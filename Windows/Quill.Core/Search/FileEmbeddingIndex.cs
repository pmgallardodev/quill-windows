using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using Quill.Core.Ports;

namespace Quill.Core.Search;

/// <summary>
/// <see cref="IEmbeddingIndex"/> in two files under the recordings root ".quill-index/":
/// <list type="bullet">
/// <item><c>manifest.json</c>: format version, dimensions and one entry per record (id, session,
/// kind, text, offset), in row order.</item>
/// <item><c>vectors.bin</c>: the vectors as little-endian float32 rows of <c>dimensions</c> values,
/// in the manifest's order (about 4 KB per passage at 1024 dimensions).</item>
/// </list>
/// Everything is loaded into memory on first use and searched by brute-force cosine, which is
/// fast enough for tens of thousands of passages. Each change rewrites both files atomically
/// (temp file + rename, vectors first). Files that are corrupt or disagree in length load as an
/// empty index; the indexer then rebuilds it. A record with a different dimension count (the
/// embedding model changed) clears the index first. No native dependency. Thread-safe.
/// </summary>
public sealed class FileEmbeddingIndex : IEmbeddingIndex
{
    public const string DirectoryName = ".quill-index";
    public const string ManifestFileName = "manifest.json";
    public const string VectorsFileName = "vectors.bin";
    private const int FormatVersion = 1;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _directory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<EmbeddingRecord>? _records;
    private int _dimensions;

    /// <param name="directory">Folder of the two files, usually <c>Path.Combine(recordingsRoot, DirectoryName)</c>.</param>
    public FileEmbeddingIndex(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = directory;
    }

    public async Task UpsertAsync(IReadOnlyList<EmbeddingRecord> records, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (records.Count == 0)
        {
            return;
        }

        var dimensions = records[0].Vector.Length;
        if (dimensions == 0 || records.Any(record => record.Vector.Length != dimensions))
        {
            throw new ArgumentException("every vector must have the same, non-zero length", nameof(records));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await LoadAsync(cancellationToken).ConfigureAwait(false);
            if (_dimensions != dimensions)
            {
                current.Clear();
                _dimensions = dimensions;
            }

            var replaced = records.Select(record => record.Id).ToHashSet(StringComparer.Ordinal);
            current.RemoveAll(record => replaced.Contains(record.Id));
            current.AddRange(records.GroupBy(record => record.Id, StringComparer.Ordinal).Select(group => group.Last()));
            await SaveAsync(current, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<EmbeddingHit>> SearchAsync(
        float[] query,
        int topK,
        EmbeddingFilter? filter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(topK);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var records = await LoadAsync(cancellationToken).ConfigureAwait(false);
            if (query.Length != _dimensions)
            {
                return [];
            }

            return records
                .Where(record => filter?.SessionId is null || record.SessionId == filter.SessionId)
                .Where(record => filter?.Kind is null || record.Kind == filter.Kind)
                .Select(record => new EmbeddingHit(record, VectorMath.Cosine(query, record.Vector)))
                .OrderByDescending(hit => hit.Score)
                .Take(topK)
                .ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var records = await LoadAsync(cancellationToken).ConfigureAwait(false);
            if (records.RemoveAll(record => record.SessionId == sessionId) > 0)
            {
                await SaveAsync(records, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private string ManifestPath => Path.Combine(_directory, ManifestFileName);

    private string VectorsPath => Path.Combine(_directory, VectorsFileName);

    private async Task<List<EmbeddingRecord>> LoadAsync(CancellationToken cancellationToken)
    {
        if (_records is not null)
        {
            return _records;
        }

        _records = [];
        _dimensions = 0;
        try
        {
            if (!File.Exists(ManifestPath) || !File.Exists(VectorsPath))
            {
                return _records;
            }

            Manifest? manifest;
            await using (var stream = File.OpenRead(ManifestPath))
            {
                manifest = await JsonSerializer.DeserializeAsync<Manifest>(stream, Json, cancellationToken).ConfigureAwait(false);
            }

            var bytes = await File.ReadAllBytesAsync(VectorsPath, cancellationToken).ConfigureAwait(false);
            if (manifest is not { Version: FormatVersion, Dimensions: > 0, Records: not null } ||
                bytes.Length != (long)manifest.Records.Count * manifest.Dimensions * sizeof(float))
            {
                return _records;
            }

            var rowBytes = manifest.Dimensions * sizeof(float);
            for (var row = 0; row < manifest.Records.Count; row++)
            {
                var entry = manifest.Records[row];
                if (entry?.Id is null || entry.SessionId is null)
                {
                    _records.Clear();
                    return _records;
                }

                var vector = new float[manifest.Dimensions];
                var span = bytes.AsSpan(row * rowBytes, rowBytes);
                for (var index = 0; index < vector.Length; index++)
                {
                    vector[index] = BinaryPrimitives.ReadSingleLittleEndian(span[(index * sizeof(float))..]);
                }

                _records.Add(new EmbeddingRecord(
                    entry.Id,
                    entry.SessionId,
                    entry.Kind ?? string.Empty,
                    entry.Text ?? string.Empty,
                    entry.AtMs is { } ms ? TimeSpan.FromMilliseconds(ms) : null,
                    vector));
            }

            _dimensions = manifest.Dimensions;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _records.Clear();
            _dimensions = 0;
        }

        return _records;
    }

    private async Task SaveAsync(List<EmbeddingRecord> records, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_directory);
        var bytes = new byte[records.Count * _dimensions * sizeof(float)];
        var offset = 0;
        foreach (var record in records)
        {
            foreach (var value in record.Vector)
            {
                BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(offset), value);
                offset += sizeof(float);
            }
        }

        var manifest = new Manifest(
            FormatVersion,
            _dimensions,
            records.Select(record => new ManifestEntry(
                record.Id,
                record.SessionId,
                record.Kind,
                record.Text,
                record.At is { } at ? (long)at.TotalMilliseconds : null)).ToList());

        // The two files are not replaced atomically together; the length check on load turns a
        // crash between the two renames into an empty index that is rebuilt, never wrong hits.
        await File.WriteAllBytesAsync(VectorsPath + ".tmp", bytes, cancellationToken).ConfigureAwait(false);
        await using (var stream = File.Create(ManifestPath + ".tmp"))
        {
            await JsonSerializer.SerializeAsync(stream, manifest, Json, cancellationToken).ConfigureAwait(false);
        }

        File.Move(VectorsPath + ".tmp", VectorsPath, overwrite: true);
        File.Move(ManifestPath + ".tmp", ManifestPath, overwrite: true);
    }

    private sealed record Manifest(int Version, int Dimensions, IReadOnlyList<ManifestEntry?>? Records);

    private sealed record ManifestEntry(string? Id, string? SessionId, string? Kind, string? Text, long? AtMs);
}
