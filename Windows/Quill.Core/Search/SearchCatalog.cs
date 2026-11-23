using System.Text.Json;
using System.Text.Json.Serialization;

namespace Quill.Core.Search;

/// <summary>What the index knows about one meeting.</summary>
/// <param name="ContentHash">Hash of the passages and title; a different hash means "reindex".</param>
/// <param name="EmbeddingModel">Model that embedded the passages, or null when only lexical search covers them.</param>
public sealed record IndexedSession(
    string SessionId,
    string Title,
    DateTimeOffset Started,
    string ContentHash,
    string? EmbeddingModel,
    IReadOnlyList<Passage> Passages);

/// <summary>
/// The passages of every indexed meeting, for lexical search, filters and Ask context. Persisted
/// as JSON next to the vector index (".quill-index/catalog.json") so the app does not rebuild it
/// on every start; a missing or corrupt file just means "index everything again". Thread-safe.
/// </summary>
public sealed class SearchCatalog
{
    public const string FileName = "catalog.json";
    private const int FormatVersion = 1;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly object _gate = new();
    private readonly Dictionary<string, IndexedSession> _sessions = new(StringComparer.Ordinal);
    private readonly string? _path;
    private Dictionary<string, Passage>? _passagesById;

    /// <param name="path">JSON file used by <see cref="SaveAsync"/>; null keeps the catalog in memory only.</param>
    public SearchCatalog(string? path = null) => _path = path;

    /// <summary>Incremented on every change, so callers can cache derived data (e.g. a BM25 index).</summary>
    public int Version { get; private set; }

    public IReadOnlyList<IndexedSession> Sessions
    {
        get
        {
            lock (_gate)
            {
                return _sessions.Values.OrderByDescending(session => session.Started).ToList();
            }
        }
    }

    public static async Task<SearchCatalog> LoadAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var catalog = new SearchCatalog(path);
        try
        {
            if (!File.Exists(path))
            {
                return catalog;
            }

            await using var stream = File.OpenRead(path);
            var document = await JsonSerializer.DeserializeAsync<CatalogDocument>(stream, Json, cancellationToken).ConfigureAwait(false);
            if (document?.Version != FormatVersion || document.Sessions is null)
            {
                return catalog;
            }

            foreach (var session in document.Sessions.Where(IsValid))
            {
                catalog._sessions[session.SessionId] = session;
            }
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            catalog._sessions.Clear();
        }

        return catalog;
    }

    public IndexedSession? Get(string sessionId)
    {
        lock (_gate)
        {
            return _sessions.GetValueOrDefault(sessionId);
        }
    }

    public void Set(IndexedSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (_gate)
        {
            _sessions[session.SessionId] = session;
            Changed();
        }
    }

    public bool Remove(string sessionId)
    {
        lock (_gate)
        {
            if (!_sessions.Remove(sessionId))
            {
                return false;
            }

            Changed();
            return true;
        }
    }

    public IReadOnlyList<Passage> AllPassages()
    {
        lock (_gate)
        {
            return _sessions.Values.SelectMany(session => session.Passages).ToList();
        }
    }

    public Passage? FindPassage(string id)
    {
        lock (_gate)
        {
            _passagesById ??= _sessions.Values
                .SelectMany(session => session.Passages)
                .GroupBy(passage => passage.Id, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            return _passagesById.GetValueOrDefault(id);
        }
    }

    /// <summary>Writes the file atomically (temp file + rename). No-op for an in-memory catalog.</summary>
    public async Task SaveAsync(CancellationToken cancellationToken)
    {
        if (_path is null)
        {
            return;
        }

        CatalogDocument document;
        lock (_gate)
        {
            document = new CatalogDocument(FormatVersion, _sessions.Values.ToList());
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        var temp = _path + ".tmp";
        await using (var stream = File.Create(temp))
        {
            await JsonSerializer.SerializeAsync(stream, document, Json, cancellationToken).ConfigureAwait(false);
        }

        File.Move(temp, _path, overwrite: true);
    }

    private static bool IsValid(IndexedSession? session) =>
        session is { SessionId.Length: > 0, Passages: not null, ContentHash: not null } &&
        session.Passages.All(passage => passage is { Id: not null, Text: not null, Speakers: not null });

    private void Changed()
    {
        Version++;
        _passagesById = null;
    }

    private sealed record CatalogDocument(int Version, IReadOnlyList<IndexedSession>? Sessions);
}
