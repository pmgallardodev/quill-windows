using Quill.Core.Api;
using Quill.Core.Search;
using Xunit;

namespace Quill.Core.Tests.Api;

public sealed class HybridApiSearchTests
{
    [Fact]
    public async Task MapsRankedPassagesToApiHits()
    {
        SearchQuery? asked = null;
        var passage = new Passage("s1:transcript:0", "s1", PassageKinds.Transcript, TimeSpan.FromSeconds(65), TimeSpan.FromSeconds(90), ["me"], "[00:01:05] me: ship on friday");
        var note = new Passage("s2:note:0", "s2", PassageKinds.Note, null, null, [], "remember friday");
        IApiSearch search = new HybridApiSearch((query, _) =>
        {
            asked = query;
            return Task.FromResult(new SearchResponse(
                [
                    new SearchResult(passage, "Planning", DateTimeOffset.UnixEpoch, 0.03, 1, 2),
                    new SearchResult(note, "Retro", DateTimeOffset.UnixEpoch, 0.01, 2, null),
                ],
                UsedVectors: true));
        });

        var hits = await search.SearchAsync("friday", 7, CancellationToken.None);

        Assert.Equal("friday", asked!.Text);
        Assert.Equal(7, asked.TopK);
        Assert.Equal(2, hits.Count);
        Assert.Equal(new ApiSearchHit("s1", "Planning", "[00:01:05] me: ship on friday", 65, 0.03), hits[0]);
        Assert.Null(hits[1].At);
        Assert.Equal("s2", hits[1].SessionId);
    }

    [Fact]
    public async Task ServesTheToolThroughMcp()
    {
        var passage = new Passage("s1:summary:0", "s1", PassageKinds.Summary, TimeSpan.FromSeconds(3), null, [], "Decision: ship");
        var search = new HybridApiSearch((_, _) => Task.FromResult(new SearchResponse([new SearchResult(passage, "Planning", DateTimeOffset.UnixEpoch, 1, 1, 1)], false)));
        var server = new McpServer(new LocalApiService(new FakeApiStore(), search), "1.0");

        var reply = await server.HandleAsync("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"search_meetings","arguments":{"query":"ship"}}}""", CancellationToken.None);

        Assert.Contains("Decision: ship", reply);
    }
}
