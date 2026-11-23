using Quill.Core.Ports;

namespace Quill.Core.Search;

/// <summary>What to search for.</summary>
/// <param name="SessionId">Only this meeting.</param>
/// <param name="From">Only meetings that started at or after this moment.</param>
/// <param name="To">Only meetings that started at or before this moment.</param>
/// <param name="Kinds">Only these <see cref="PassageKinds"/>; null means all.</param>
public sealed record SearchQuery(
    string Text,
    int TopK = 10,
    string? SessionId = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    IReadOnlyCollection<string>? Kinds = null);

/// <summary>One ranked passage.</summary>
/// <param name="Score">Reciprocal-rank-fusion score; only meaningful within one response.</param>
/// <param name="LexicalRank">1-based rank in the BM25 list, null when the words did not match.</param>
/// <param name="VectorRank">1-based rank in the vector list, null when not retrieved by meaning.</param>
public sealed record SearchResult(
    Passage Passage,
    string SessionTitle,
    DateTimeOffset Started,
    double Score,
    int? LexicalRank,
    int? VectorRank);

/// <param name="UsedVectors">False when the search ran lexical-only (no embedding model or index).</param>
public sealed record SearchResponse(IReadOnlyList<SearchResult> Results, bool UsedVectors);

/// <summary>A fused ranking entry; see <see cref="HybridSearch.Fuse"/>.</summary>
public sealed record FusedRank(string Id, double Score, int? LexicalRank, int? VectorRank);

/// <summary>
/// Search over every indexed meeting: BM25 on the words (accent and case insensitive) plus cosine
/// similarity on embeddings, merged with reciprocal rank fusion (k = 60). Without an embedding
/// model (AI pack component missing) it degrades to lexical-only. The query embedding runs at
/// <see cref="InferencePriority.Interactive"/>: the user is waiting for it.
/// </summary>
public sealed class HybridSearch
{
    public const int RrfK = 60;

    /// <summary>Vector hits at or below this cosine are noise, not matches.</summary>
    public const double MinimumCosine = 0.05;

    private readonly SearchCatalog _catalog;
    private readonly IEmbeddingIndex? _index;
    private readonly IEmbeddingModel? _model;
    private readonly IInferenceScheduler? _scheduler;
    private readonly object _gate = new();
    private (int Version, Bm25Index Index)? _lexical;

    public HybridSearch(
        SearchCatalog catalog,
        IEmbeddingIndex? index,
        IEmbeddingModel? model,
        IInferenceScheduler? scheduler = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _index = index;
        _model = index is null ? null : model;
        _scheduler = scheduler;
    }

    public SearchCatalog Catalog => _catalog;

    public async Task<SearchResponse> SearchAsync(SearchQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(query.TopK);
        if (string.IsNullOrWhiteSpace(query.Text))
        {
            return new SearchResponse([], UsedVectors: false);
        }

        var sessions = _catalog.Sessions.ToDictionary(session => session.SessionId, StringComparer.Ordinal);
        bool Include(Passage? passage) =>
            passage is not null &&
            sessions.TryGetValue(passage.SessionId, out var session) &&
            (query.SessionId is null || passage.SessionId == query.SessionId) &&
            (query.From is null || session.Started >= query.From) &&
            (query.To is null || session.Started <= query.To) &&
            (query.Kinds is null || query.Kinds.Contains(passage.Kind));

        var pool = Math.Max(query.TopK * 4, 20);
        var lexical = Lexical().Search(query.Text, pool, id => Include(_catalog.FindPassage(id)))
            .Select(hit => hit.Id)
            .ToList();
        var vector = await VectorAsync(query, pool, Include, cancellationToken).ConfigureAwait(false);

        var results = Fuse(lexical, vector ?? [], RrfK)
            .Take(query.TopK)
            .Select(rank => (Rank: rank, Passage: _catalog.FindPassage(rank.Id)))
            // Catalog entries can be removed while query embedding is in flight. Re-resolve
            // every candidate against the live catalog before building a result.
            .Where(candidate => candidate.Passage is not null && Include(candidate.Passage) &&
                _catalog.Get(candidate.Passage.SessionId) is not null)
            .Select(candidate => (candidate.Rank, Passage: candidate.Passage!, Session: _catalog.Get(candidate.Passage!.SessionId)!))
            .Select(candidate =>
            {
                var rank = candidate.Rank;
                return new SearchResult(candidate.Passage, candidate.Session.Title, candidate.Session.Started, rank.Score, rank.LexicalRank, rank.VectorRank);
            })
            .ToList();
        return new SearchResponse(results, UsedVectors: vector is not null);
    }

    /// <summary>
    /// Reciprocal rank fusion: score = Σ 1 / (k + rank) over the lists an id appears in. Ties
    /// keep the better single rank, then the lexical order.
    /// </summary>
    public static IReadOnlyList<FusedRank> Fuse(IReadOnlyList<string> lexical, IReadOnlyList<string> vector, int k = RrfK)
    {
        ArgumentNullException.ThrowIfNull(lexical);
        ArgumentNullException.ThrowIfNull(vector);
        var ranks = new Dictionary<string, (int? Lexical, int? Vector)>(StringComparer.Ordinal);
        for (var index = 0; index < lexical.Count; index++)
        {
            ranks.TryAdd(lexical[index], (index + 1, null));
        }

        for (var index = 0; index < vector.Count; index++)
        {
            var current = ranks.GetValueOrDefault(vector[index]);
            ranks[vector[index]] = (current.Lexical, current.Vector ?? index + 1);
        }

        static double Part(int? rank, int k) => rank is { } value ? 1d / (k + value) : 0d;

        return ranks
            .Select(pair => new FusedRank(pair.Key, Part(pair.Value.Lexical, k) + Part(pair.Value.Vector, k), pair.Value.Lexical, pair.Value.Vector))
            .OrderByDescending(rank => rank.Score)
            .ThenBy(rank => Math.Min(rank.LexicalRank ?? int.MaxValue, rank.VectorRank ?? int.MaxValue))
            .ThenBy(rank => rank.LexicalRank ?? int.MaxValue)
            .ToList();
    }

    private Bm25Index Lexical()
    {
        lock (_gate)
        {
            var version = _catalog.Version;
            if (_lexical is not { } cached || cached.Version != version)
            {
                cached = (version, new Bm25Index(_catalog.AllPassages().Select(passage => (passage.Id, passage.Text))));
                _lexical = cached;
            }

            return cached.Index;
        }
    }

    /// <returns>Ranked passage ids, or null when vectors are unavailable.</returns>
    private async Task<IReadOnlyList<string>?> VectorAsync(
        SearchQuery query,
        int pool,
        Func<Passage?, bool> include,
        CancellationToken cancellationToken)
    {
        if (_model is null || _index is null)
        {
            return null;
        }

        float[] embedding;
        try
        {
            var texts = new[] { query.Text.Trim() };
            var vectors = _scheduler is null
                ? await _model.EmbedAsync(texts, cancellationToken).ConfigureAwait(false)
                : await _scheduler.RunAsync(InferencePriority.Interactive, token => _model.EmbedAsync(texts, token), cancellationToken).ConfigureAwait(false);
            embedding = vectors.Count == 1 ? vectors[0] : throw new InvalidOperationException("expected one query vector");
        }
        catch (EmbeddingModelUnavailableException)
        {
            return null;
        }

        // Ask for more than needed: date and kind filters apply after the index lookup.
        var hits = await _index.SearchAsync(embedding, pool * 2, new EmbeddingFilter(query.SessionId), cancellationToken).ConfigureAwait(false);
        return hits
            .Where(hit => hit.Score > MinimumCosine && include(_catalog.FindPassage(hit.Record.Id)))
            .Select(hit => hit.Record.Id)
            .Distinct(StringComparer.Ordinal)
            .Take(pool)
            .ToList();
    }
}
