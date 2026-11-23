using Quill.Core.Api;

namespace Quill.Core.Search;

/// <summary>
/// <see cref="IApiSearch"/> (local API <c>GET /v1/search</c> and the MCP <c>search_meetings</c> tool)
/// over the F10 hybrid search. Takes the search as a delegate so the app can pass the lazily
/// loaded index (<c>AskModule.SearchAsync</c>) without building it at startup.
/// </summary>
public sealed class HybridApiSearch : IApiSearch
{
    private readonly Func<SearchQuery, CancellationToken, Task<SearchResponse>> _search;

    public HybridApiSearch(Func<SearchQuery, CancellationToken, Task<SearchResponse>> search)
    {
        _search = search ?? throw new ArgumentNullException(nameof(search));
    }

    /// <summary>One hit per ranked passage; <see cref="ApiSearchHit.At"/> is null for uncited text (notes, executive summary).</summary>
    public async Task<IReadOnlyList<ApiSearchHit>> SearchAsync(string query, int limit, CancellationToken cancellationToken)
    {
        var response = await _search(new SearchQuery(query, TopK: limit), cancellationToken).ConfigureAwait(false);
        return response.Results
            .Select(result => new ApiSearchHit(
                result.Passage.SessionId,
                result.SessionTitle,
                result.Passage.Text,
                result.Passage.Start?.TotalSeconds,
                result.Score))
            .ToList();
    }
}
