using System.Collections.Concurrent;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Quill.Core.Domain;
using Quill.Core.Ports;
using Quill.Core.Summaries;
using Quill.Windows.Sessions;
using TranscriptSegment = Quill.Core.Domain.TranscriptSegment;

namespace Quill.Core.Storage;

/// <summary>
/// <see cref="ISessionStore"/> over the recordings folder. Session format v2 is
/// strictly additive over v1:
/// <list type="bullet">
/// <item>v1 (unchanged): meta.json, mic.wav, system.wav, transcript.json, transcript.md, status.json.</item>
/// <item>v2 adds: summary.json, speakers.json, notes.md, live/transcript.json, live/summary.json,
/// and two optional per-segment fields in transcript.json ("track", "edited") that v1 readers ignore.</item>
/// </list>
/// Links (reparse points) are never followed, matching the v1 code.
/// </summary>
public sealed class FileSessionStore : ISessionStore
{
    public const string SummaryFile = "summary.json";
    public const string SpeakersFile = "speakers.json";
    public const string NotesFile = "notes.md";
    public const string LiveFolder = "live";
    public const string TranscriptJsonFile = "transcript.json";
    public const string TranscriptMarkdownFile = "transcript.md";

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _root;
    private readonly IClock _clock;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> NotesLocks = new(StringComparer.Ordinal);

    public FileSessionStore(string recordingsRoot, IClock? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordingsRoot);
        _root = recordingsRoot;
        _clock = clock ?? SystemClock.Instance;
    }

    public string RecordingsRoot => _root;

    /// <summary>Absolute folder of a session. Throws <see cref="ArgumentException"/> for unsafe ids.</summary>
    public string FolderOf(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || sessionId is "." or ".." ||
            sessionId.IndexOfAny(['/', '\\', ':']) >= 0 ||
            sessionId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException($"invalid session id \"{sessionId}\"", nameof(sessionId));
        }

        return Path.Combine(_root, sessionId);
    }

    public async Task<IReadOnlyList<Session>> ListSessionsAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_root))
        {
            return [];
        }

        var sessions = new List<Session>();
        IEnumerable<string> directories;
        try
        {
            directories = Directory.EnumerateDirectories(_root).ToList();
        }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            return [];
        }

        foreach (var directory in directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var session = await LoadSessionAsync(Path.GetFileName(directory), cancellationToken).ConfigureAwait(false);
            if (session is not null)
            {
                sessions.Add(session);
            }
        }

        return sessions
            .OrderByDescending(session => session.Started)
            .ThenByDescending(session => session.Id, StringComparer.Ordinal)
            .ToList();
    }

    public Task<Session?> LoadSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var folder = FolderOf(sessionId);
        if (!Directory.Exists(folder) || IsLink(folder))
        {
            return Task.FromResult<Session?>(null);
        }

        var metadata = SessionMetadata.TryRead(folder);
        if (metadata is null)
        {
            return Task.FromResult<Session?>(null);
        }

        var tracks = new List<SessionTrack>();
        foreach (var (name, fileName) in metadata.Files)
        {
            if (TrackNames.TryParse(name, out var track))
            {
                var offset = metadata.StartOffsetMs.TryGetValue(name, out var ms) ? ms : 0;
                tracks.Add(new SessionTrack(track, fileName, TimeSpan.FromMilliseconds(offset)));
            }
        }

        var session = new Session(
            sessionId,
            folder,
            string.IsNullOrWhiteSpace(metadata.Title) ? null : metadata.Title.Trim(),
            metadata.Started,
            metadata.Ended,
            tracks.OrderBy(track => track.Track).ToList(),
            metadata.Markers.Select(ms => new Marker(TimeSpan.FromMilliseconds(ms))).ToList());
        return Task.FromResult<Session?>(session);
    }

    public async Task<IReadOnlyList<TranscriptSegment>> LoadTranscriptAsync(
        string sessionId,
        TranscriptPass pass,
        CancellationToken cancellationToken)
    {
        var text = await ReadAsync(TranscriptPath(sessionId, pass), cancellationToken).ConfigureAwait(false);
        if (text is null)
        {
            return [];
        }

        try
        {
            var document = JsonSerializer.Deserialize<TranscriptFile>(text);
            if (document?.Segments is null)
            {
                return [];
            }

            var segments = new List<TranscriptSegment>(document.Segments.Count);
            foreach (var row in document.Segments)
            {
                if (row is null || string.IsNullOrWhiteSpace(row.Speaker) || row.Text is null ||
                    row.StartMs < 0 || row.EndMs < row.StartMs)
                {
                    return [];
                }

                var track = TrackNames.TryParse(row.Track, out var parsed) ? parsed : SpeakerIds.LegacyTrack(row.Speaker);
                segments.Add(new TranscriptSegment(
                    TimeSpan.FromMilliseconds(row.StartMs),
                    TimeSpan.FromMilliseconds(row.EndMs),
                    row.Text,
                    row.Speaker,
                    track,
                    Editable: pass == TranscriptPass.Final,
                    Edited: row.Edited ?? false));
            }

            return segments;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or OverflowException or ArgumentException)
        {
            return [];
        }
    }

    public Task SaveTranscriptAsync(
        string sessionId,
        TranscriptPass pass,
        TranscriptInfo info,
        IReadOnlyList<TranscriptSegment> segments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(segments);
        cancellationToken.ThrowIfCancellationRequested();
        if (segments.Any(segment => segment is null || string.IsNullOrWhiteSpace(segment.SpeakerId) ||
                segment.Text is null || segment.Start < TimeSpan.Zero || segment.End < segment.Start))
        {
            throw new ArgumentException("transcript contains an invalid segment", nameof(segments));
        }

        var folder = ExistingFolder(sessionId);
        var file = new TranscriptFile
        {
            Engine = info.Engine,
            Model = info.Model,
            CreatedAt = _clock.Now,
            Segments = segments.Select(segment => (TranscriptRow?)new TranscriptRow
            {
                Speaker = segment.SpeakerId,
                StartMs = (long)segment.Start.TotalMilliseconds,
                EndMs = (long)segment.End.TotalMilliseconds,
                Text = segment.Text,
                Track = TrackNames.ToName(segment.Track),
                Edited = segment.Edited ? true : null,
            }).ToList(),
        };
        var json = JsonSerializer.Serialize(file, WriteOptions);

        if (pass == TranscriptPass.Live)
        {
            Directory.CreateDirectory(Path.Combine(folder, LiveFolder));
            AtomicFile.Write(Path.Combine(folder, LiveFolder, TranscriptJsonFile), json);
            return Task.CompletedTask;
        }

        // Same order as v1: transcript.json marks completion, so it goes last.
        AtomicFile.Write(Path.Combine(folder, TranscriptMarkdownFile), RenderMarkdown(sessionId, info, segments));
        AtomicFile.Write(Path.Combine(folder, TranscriptJsonFile), json);
        return Task.CompletedTask;
    }

    public async Task<MeetingSummary?> LoadSummaryAsync(string sessionId, SummaryKind kind, CancellationToken cancellationToken)
    {
        var text = await ReadAsync(SummaryPath(sessionId, kind), cancellationToken).ConfigureAwait(false);
        return text is not null && MeetingSummaryJson.TryDeserialize(text, out var summary) ? summary : null;
    }

    public Task SaveSummaryAsync(string sessionId, MeetingSummary summary, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(summary);
        cancellationToken.ThrowIfCancellationRequested();
        ExistingFolder(sessionId);
        var path = SummaryPath(sessionId, summary.Meta.Kind);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AtomicFile.Write(path, MeetingSummaryJson.Serialize(summary));
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<Speaker>> LoadSpeakersAsync(string sessionId, CancellationToken cancellationToken)
    {
        var text = await ReadAsync(Path.Combine(FolderOf(sessionId), SpeakersFile), cancellationToken).ConfigureAwait(false);
        if (text is null)
        {
            return [];
        }

        try
        {
            var file = JsonSerializer.Deserialize<SpeakersFileDto>(text);
            return file?.Speakers?
                .Where(row => row is not null && !string.IsNullOrWhiteSpace(row.Id))
                .Select(row => new Speaker(
                    row!.Id!,
                    TrackNames.TryParse(row.Track, out var track) ? track : SpeakerIds.LegacyTrack(row.Id!),
                    string.IsNullOrWhiteSpace(row.DisplayName) ? null : row.DisplayName))
                .ToList() ?? [];
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            return [];
        }
    }

    public Task SaveSpeakersAsync(string sessionId, IReadOnlyList<Speaker> speakers, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(speakers);
        cancellationToken.ThrowIfCancellationRequested();
        var folder = ExistingFolder(sessionId);
        var file = new SpeakersFileDto
        {
            Version = 1,
            Speakers = speakers.Select(speaker => (SpeakerRow?)new SpeakerRow
            {
                Id = speaker.Id,
                Track = TrackNames.ToName(speaker.Track),
                DisplayName = speaker.DisplayName,
            }).ToList(),
        };
        AtomicFile.Write(Path.Combine(folder, SpeakersFile), JsonSerializer.Serialize(file, WriteOptions));
        return Task.CompletedTask;
    }

    public async Task<string> LoadNotesAsync(string sessionId, CancellationToken cancellationToken)
    {
        var path = Path.Combine(FolderOf(sessionId), NotesFile);
        try
        {
            var parent = Path.GetDirectoryName(path)!;
            if (IsLink(parent))
            {
                throw new IOException("notes folder is a link");
            }

            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("notes file is a link");
            }

            if ((attributes & FileAttributes.Directory) != 0)
            {
                throw new IOException("notes path is not a file");
            }

            return await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            return string.Empty;
        }
        catch (DirectoryNotFoundException)
        {
            return string.Empty;
        }
    }

    public Task SaveNotesAsync(string sessionId, string markdown, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        cancellationToken.ThrowIfCancellationRequested();
        AtomicFile.Write(Path.Combine(ExistingFolder(sessionId), NotesFile), markdown);
        return Task.CompletedTask;
    }

    public async Task SaveNotesAsync(string sessionId, string expectedMarkdown, string markdown, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expectedMarkdown);
        ArgumentNullException.ThrowIfNull(markdown);
        cancellationToken.ThrowIfCancellationRequested();
        var path = Path.Combine(ExistingFolder(sessionId), NotesFile);
        var gate = NotesLocks.GetOrAdd(Path.GetFullPath(path), static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await LoadNotesAsync(sessionId, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(current, expectedMarkdown, StringComparison.Ordinal))
            {
                throw new NotesConflictException(sessionId);
            }

            AtomicFile.Write(path, markdown);
        }
        finally
        {
            gate.Release();
        }
    }

    private string TranscriptPath(string sessionId, TranscriptPass pass) => pass == TranscriptPass.Live
        ? Path.Combine(FolderOf(sessionId), LiveFolder, TranscriptJsonFile)
        : Path.Combine(FolderOf(sessionId), TranscriptJsonFile);

    private string SummaryPath(string sessionId, SummaryKind kind) => kind == SummaryKind.Live
        ? Path.Combine(FolderOf(sessionId), LiveFolder, SummaryFile)
        : Path.Combine(FolderOf(sessionId), SummaryFile);

    private string ExistingFolder(string sessionId)
    {
        var folder = FolderOf(sessionId);
        if (!Directory.Exists(folder))
        {
            throw new DirectoryNotFoundException($"session folder not found: {folder}");
        }

        if (IsLink(folder))
        {
            throw new IOException($"session folder is a link: {folder}");
        }

        return folder;
    }

    /// <summary>File text, or null when missing, linked or unreadable.</summary>
    private static async Task<string?> ReadAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(path) || IsLink(path) || IsLink(Path.GetDirectoryName(path)!))
            {
                return null;
            }

            return await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            return null;
        }
    }

    private static bool IsLink(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            return true;
        }
    }

    private static bool IsReadFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException;

    private static string RenderMarkdown(string sessionId, TranscriptInfo info, IReadOnlyList<TranscriptSegment> segments)
    {
        var output = new StringBuilder();
        output.Append("# ").Append(sessionId).Append("\n\n");
        output.Append("engine: ").Append(info.Engine).Append(" (").Append(info.Model).Append(")\n\n");
        foreach (var segment in segments)
        {
            output.Append("**[").Append(TranscriptExport.Clock((long)segment.Start.TotalMilliseconds)).Append("] ")
                .Append(segment.SpeakerId).Append(":** ").Append(segment.Text.Trim()).Append("\n\n");
        }

        return output.ToString();
    }

    /// <summary>transcript.json, v1 fields plus optional v2 fields.</summary>
    private sealed class TranscriptFile
    {
        [JsonPropertyName("engine")]
        public string? Engine { get; init; }

        [JsonPropertyName("model")]
        public string? Model { get; init; }

        [JsonPropertyName("created_at")]
        public DateTimeOffset CreatedAt { get; init; }

        [JsonPropertyName("segments")]
        public List<TranscriptRow?>? Segments { get; init; }
    }

    private sealed class TranscriptRow
    {
        [JsonPropertyName("speaker")]
        public string? Speaker { get; init; }

        [JsonPropertyName("start_ms")]
        public long StartMs { get; init; }

        [JsonPropertyName("end_ms")]
        public long EndMs { get; init; }

        [JsonPropertyName("text")]
        public string? Text { get; init; }

        [JsonPropertyName("track")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Track { get; init; }

        [JsonPropertyName("edited")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? Edited { get; init; }
    }

    private sealed class SpeakersFileDto
    {
        [JsonPropertyName("version")]
        public int Version { get; init; }

        [JsonPropertyName("speakers")]
        public List<SpeakerRow?>? Speakers { get; init; }
    }

    private sealed class SpeakerRow
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("track")]
        public string? Track { get; init; }

        [JsonPropertyName("displayName")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? DisplayName { get; init; }
    }
}
