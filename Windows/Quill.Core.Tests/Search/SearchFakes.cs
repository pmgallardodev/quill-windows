using Quill.Core.Domain;
using Quill.Core.Ports;
using Quill.Core.Search;

namespace Quill.Core.Tests.Search;

/// <summary>
/// Deterministic bag-of-words embedding: each folded token hashes into one of
/// <see cref="Dimensions"/> buckets, then the vector is L2-normalized. Texts that share
/// words get a high cosine, which is all the search tests need.
/// </summary>
internal sealed class FakeEmbeddingModel : IEmbeddingModel
{
    public FakeEmbeddingModel(int dimensions = 64) => Dimensions = dimensions;

    public string ModelName { get; init; } = "fake-embed";

    public int Dimensions { get; }

    public List<string> Embedded { get; } = [];

    /// <summary>When set, every call throws it (e.g. a missing AI pack component).</summary>
    public Exception? Throw { get; set; }

    /// <summary>Extra words treated as the same bucket, to simulate synonyms the lexical side misses.</summary>
    public Dictionary<string, string> Synonyms { get; } = new(StringComparer.Ordinal);

    public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Throw is not null)
        {
            throw Throw;
        }

        Embedded.AddRange(texts);
        return Task.FromResult<IReadOnlyList<float[]>>(texts.Select(Vector).ToList());
    }

    private float[] Vector(string text)
    {
        var vector = new float[Dimensions];
        foreach (var token in TextFolding.Tokens(text))
        {
            var word = Synonyms.GetValueOrDefault(token, token);
            var bucket = (int)((uint)StableHash(word) % (uint)Dimensions);
            vector[bucket] += 1f;
        }

        var norm = MathF.Sqrt(vector.Sum(value => value * value));
        if (norm > 0)
        {
            for (var index = 0; index < vector.Length; index++)
            {
                vector[index] /= norm;
            }
        }

        return vector;
    }

    private static int StableHash(string text)
    {
        unchecked
        {
            var hash = 17;
            foreach (var character in text)
            {
                hash = (hash * 31) + character;
            }

            return hash;
        }
    }
}

/// <summary>In-memory <see cref="IEmbeddingIndex"/> with brute-force cosine.</summary>
internal sealed class MemoryEmbeddingIndex : IEmbeddingIndex
{
    public Dictionary<string, EmbeddingRecord> Records { get; } = new(StringComparer.Ordinal);

    public int UpsertCalls { get; private set; }

    public Task UpsertAsync(IReadOnlyList<EmbeddingRecord> records, CancellationToken cancellationToken)
    {
        UpsertCalls++;
        foreach (var record in records)
        {
            Records[record.Id] = record;
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<EmbeddingHit>> SearchAsync(
        float[] query,
        int topK,
        EmbeddingFilter? filter,
        CancellationToken cancellationToken)
    {
        var hits = Records.Values
            .Where(record => filter?.SessionId is null || record.SessionId == filter.SessionId)
            .Where(record => filter?.Kind is null || record.Kind == filter.Kind)
            .Select(record => new EmbeddingHit(record, VectorMath.Cosine(query, record.Vector)))
            .OrderByDescending(hit => hit.Score)
            .Take(topK)
            .ToList();
        return Task.FromResult<IReadOnlyList<EmbeddingHit>>(hits);
    }

    public Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        foreach (var id in Records.Values.Where(record => record.SessionId == sessionId).Select(record => record.Id).ToList())
        {
            Records.Remove(id);
        }

        return Task.CompletedTask;
    }
}

internal static class SearchData
{
    public static Session Session(string id, string? title = null, DateTimeOffset? started = null) =>
        new(id, $"/rec/{id}", title, started ?? new DateTimeOffset(2026, 11, 20, 10, 0, 0, TimeSpan.Zero), null, [], []);
}
