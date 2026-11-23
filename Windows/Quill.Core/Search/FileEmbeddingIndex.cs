using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Quill.Core.Ports;

namespace Quill.Core.Search;

/// <summary>
/// <see cref="IEmbeddingIndex"/> stored under ".quill-index/" as a JSON manifest and little-endian
/// float32 vector file. New manifests include a digest to reject mismatched generations; legacy
/// manifests without a digest remain readable. Everything is loaded into memory on first use.
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
            var nextDimensions = _dimensions == dimensions ? _dimensions : dimensions;
            var updated = nextDimensions == _dimensions ? current.ToList() : [];
            var replaced = records.Select(record => record.Id).ToHashSet(StringComparer.Ordinal);
            updated.RemoveAll(record => replaced.Contains(record.Id));
            updated.AddRange(records.GroupBy(record => record.Id, StringComparer.Ordinal).Select(group => group.Last()));
            await SaveAsync(updated, nextDimensions, cancellationToken).ConfigureAwait(false);
            _records = updated;
            _dimensions = nextDimensions;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ReplaceSessionAsync(string sessionId, IReadOnlyList<EmbeddingRecord> records, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(records);
        if (records.Any(record => record.SessionId != sessionId))
        {
            throw new ArgumentException("all replacement records must belong to the requested session", nameof(records));
        }

        var dimensions = records.Count == 0 ? 0 : records[0].Vector.Length;
        if (records.Any(record => record.Vector.Length != dimensions) || dimensions == 0 && records.Count > 0)
        {
            throw new ArgumentException("every vector must have the same, non-zero length", nameof(records));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await LoadAsync(cancellationToken).ConfigureAwait(false);
            var nextDimensions = dimensions == 0 ? _dimensions : dimensions;
            var updated = dimensions != 0 && nextDimensions != _dimensions ? [] : current.ToList();
            updated.RemoveAll(record => record.SessionId == sessionId);
            updated.AddRange(records.GroupBy(record => record.Id, StringComparer.Ordinal).Select(group => group.Last()));
            if (updated.Count > 0 || current.Count > 0)
            {
                await SaveAsync(updated, nextDimensions, cancellationToken).ConfigureAwait(false);
            }

            _records = updated;
            _dimensions = updated.Count == 0 ? 0 : nextDimensions;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> HasSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return (await LoadAsync(cancellationToken).ConfigureAwait(false)).Any(record => record.SessionId == sessionId);
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
            var current = await LoadAsync(cancellationToken).ConfigureAwait(false);
            var records = current.Where(record => record.SessionId != sessionId).ToList();
            if (records.Count != current.Count)
            {
                var dimensions = records.Count == 0 ? 0 : _dimensions;
                await SaveAsync(records, dimensions, cancellationToken).ConfigureAwait(false);
                _records = records;
                _dimensions = dimensions;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private string ManifestPath => Path.Combine(_directory, ManifestFileName);

    private string VectorsPath => Path.Combine(_directory, VectorsFileName);

    private string ObsoleteSnapshotPath => Path.Combine(_directory, "index.bin");


    private async Task<List<EmbeddingRecord>> LoadAsync(CancellationToken cancellationToken)
    {
        if (_records is not null)
        {
            return _records;
        }

        var loaded = new List<EmbeddingRecord>();
        var dimensions = 0;
        try
        {
            if (!File.Exists(ManifestPath) || !File.Exists(VectorsPath))
            {
                _records = loaded;
                return loaded;
            }

            Manifest? manifest;
            await using (var stream = File.OpenRead(ManifestPath))
            {
                manifest = await JsonSerializer.DeserializeAsync<Manifest>(stream, Json, cancellationToken).ConfigureAwait(false);
            }

            var bytes = await File.ReadAllBytesAsync(VectorsPath, cancellationToken).ConfigureAwait(false);
            if (manifest is not { Version: FormatVersion, Dimensions: > 0, Records: not null } ||
                bytes.Length != (long)manifest.Records.Count * manifest.Dimensions * sizeof(float) ||
                manifest.VectorsSha256 is { } digest && !StringComparer.OrdinalIgnoreCase.Equals(digest, Convert.ToHexString(SHA256.HashData(bytes))))
            {
                _records = loaded;
                return loaded;
            }

            var rowBytes = manifest.Dimensions * sizeof(float);
            for (var row = 0; row < manifest.Records.Count; row++)
            {
                var entry = manifest.Records[row];
                if (entry?.Id is null || entry.SessionId is null)
                {
                    loaded.Clear();
                    _records = loaded;
                    return loaded;
                }

                var vector = new float[manifest.Dimensions];
                var span = bytes.AsSpan(row * rowBytes, rowBytes);
                for (var index = 0; index < vector.Length; index++)
                {
                    vector[index] = BinaryPrimitives.ReadSingleLittleEndian(span[(index * sizeof(float))..]);
                }

                loaded.Add(new EmbeddingRecord(
                    entry.Id,
                    entry.SessionId,
                    entry.Kind ?? string.Empty,
                    entry.Text ?? string.Empty,
                    entry.AtMs is { } ms ? TimeSpan.FromMilliseconds(ms) : null,
                    vector));
            }

            dimensions = manifest.Dimensions;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            loaded.Clear();
            dimensions = 0;
        }

        _records = loaded;
        _dimensions = dimensions;
        return loaded;
    }

    private async Task SaveAsync(List<EmbeddingRecord> records, int dimensions, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_directory);
        var vectorsTemp = VectorsPath + ".tmp";
        var manifestTemp = ManifestPath + ".tmp";
        var restoreTemp = ManifestPath + ".restore.tmp";
        var previousManifest = File.Exists(ManifestPath) ? await File.ReadAllBytesAsync(ManifestPath, cancellationToken).ConfigureAwait(false) : null;
        try
        {
            string digest;
            await using (var stream = new FileStream(vectorsTemp, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[64 * 1024];
                var buffered = 0;
                foreach (var record in records)
                {
                    foreach (var value in record.Vector)
                    {
                        BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(buffered), value);
                        buffered += sizeof(float);
                        if (buffered == buffer.Length)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            hash.AppendData(buffer);
                            await stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
                            buffered = 0;
                        }
                    }
                }

                if (buffered > 0)
                {
                    hash.AppendData(buffer.AsSpan(0, buffered));
                    await stream.WriteAsync(buffer.AsMemory(0, buffered), cancellationToken).ConfigureAwait(false);
                }

                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                digest = Convert.ToHexString(hash.GetHashAndReset());
            }

            var manifest = new Manifest(
                FormatVersion,
                dimensions,
                records.Select(record => new ManifestEntry(
                    record.Id,
                    record.SessionId,
                    record.Kind,
                    record.Text,
                    record.At is { } at ? (long)at.TotalMilliseconds : null)).ToList(),
                digest);
            var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, Json);
            await File.WriteAllBytesAsync(manifestTemp, manifestBytes, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            // Publish metadata first. A reader in the brief gap sees a digest mismatch and no
            // vectors, never a mixture of generations. Keep the old manifest for rollback until
            // the vectors rename completes.
            File.Move(manifestTemp, ManifestPath, overwrite: true);
            try
            {
                File.Move(vectorsTemp, VectorsPath, overwrite: true);
                TryDelete(ObsoleteSnapshotPath);
            }
            catch
            {
                if (previousManifest is null)
                {
                    TryDelete(ManifestPath);
                }
                else
                {
                    await File.WriteAllBytesAsync(restoreTemp, previousManifest, CancellationToken.None).ConfigureAwait(false);
                    File.Move(restoreTemp, ManifestPath, overwrite: true);
                }

                throw;
            }
        }
        finally
        {
            TryDelete(vectorsTemp);
            TryDelete(manifestTemp);
            TryDelete(restoreTemp);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record Manifest(int Version, int Dimensions, IReadOnlyList<ManifestEntry?>? Records, string? VectorsSha256 = null);

    private sealed record ManifestEntry(string? Id, string? SessionId, string? Kind, string? Text, long? AtMs);
}
