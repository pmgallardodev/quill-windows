namespace Quill.Core.Search;

/// <summary>Lexical hit; <see cref="Score"/> is BM25, only comparable within one query.</summary>
public sealed record LexicalHit(string Id, double Score);

/// <summary>
/// In-memory Okapi BM25 (k1 = 1.2, b = 0.75) over <see cref="TextFolding.Tokens"/>. Immutable
/// once built; rebuilding for a few thousand passages takes milliseconds.
/// </summary>
public sealed class Bm25Index
{
    private const double K1 = 1.2;
    private const double B = 0.75;

    private readonly List<(string Id, Dictionary<string, int> Terms, int Length)> _documents = [];
    private readonly Dictionary<string, int> _documentFrequency = new(StringComparer.Ordinal);
    private readonly double _averageLength;

    public Bm25Index(IEnumerable<(string Id, string Text)> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);
        foreach (var (id, text) in documents)
        {
            var tokens = TextFolding.Tokens(text);
            var terms = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var token in tokens)
            {
                terms[token] = terms.GetValueOrDefault(token) + 1;
            }

            foreach (var term in terms.Keys)
            {
                _documentFrequency[term] = _documentFrequency.GetValueOrDefault(term) + 1;
            }

            _documents.Add((id, terms, tokens.Count));
        }

        _averageLength = _documents.Count == 0 ? 0 : _documents.Average(document => document.Length);
    }

    public int Count => _documents.Count;

    /// <summary>Documents that contain at least one query term, best first.</summary>
    /// <param name="include">Optional filter on document ids, applied before ranking.</param>
    public IReadOnlyList<LexicalHit> Search(string query, int topK, Func<string, bool>? include = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(topK);
        var terms = TextFolding.Tokens(query).Distinct(StringComparer.Ordinal).ToList();
        if (terms.Count == 0 || _documents.Count == 0)
        {
            return [];
        }

        var hits = new List<LexicalHit>();
        foreach (var (id, documentTerms, length) in _documents)
        {
            if (include is not null && !include(id))
            {
                continue;
            }

            var score = 0d;
            foreach (var term in terms)
            {
                if (!documentTerms.TryGetValue(term, out var frequency))
                {
                    continue;
                }

                var documentFrequency = _documentFrequency[term];
                var idf = Math.Log(1 + ((_documents.Count - documentFrequency + 0.5) / (documentFrequency + 0.5)));
                var norm = K1 * (1 - B + (B * length / Math.Max(1d, _averageLength)));
                score += idf * (frequency * (K1 + 1)) / (frequency + norm);
            }

            if (score > 0)
            {
                hits.Add(new LexicalHit(id, score));
            }
        }

        return hits
            .OrderByDescending(hit => hit.Score)
            .ThenBy(hit => hit.Id, StringComparer.Ordinal)
            .Take(topK)
            .ToList();
    }
}
