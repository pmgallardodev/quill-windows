using System.Text.Json.Nodes;
using Quill.Core.Api;
using Xunit;

namespace Quill.Core.Tests.Api;

public sealed class McpServerTests
{
    private static McpServer Server(IApiSearch? search = null) => new(new LocalApiService(new FakeApiStore(), search), "1.2.3");

    private static async Task<JsonObject> Call(McpServer server, string json)
    {
        var response = await server.HandleAsync(json, CancellationToken.None);
        Assert.NotNull(response);
        return JsonNode.Parse(response!)!.AsObject();
    }

    [Fact]
    public async Task InitializeNegotiatesAKnownProtocolVersion()
    {
        var response = await Call(Server(), """
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"t","version":"0"}}}
            """);

        Assert.Equal("2.0", (string?)response["jsonrpc"]);
        Assert.Equal(1, (int?)response["id"]);
        Assert.Equal("2025-03-26", (string?)response["result"]!["protocolVersion"]);
        Assert.Equal("quill", (string?)response["result"]!["serverInfo"]!["name"]);
        Assert.Equal("1.2.3", (string?)response["result"]!["serverInfo"]!["version"]);
        Assert.NotNull(response["result"]!["capabilities"]!["tools"]);
    }

    [Fact]
    public async Task InitializeFallsBackToTheLatestVersionItSpeaks()
    {
        var response = await Call(Server(), """{"jsonrpc":"2.0","id":"a","method":"initialize","params":{"protocolVersion":"1999-01-01"}}""");

        Assert.Equal("a", (string?)response["id"]);
        Assert.Equal(McpServer.LatestProtocolVersion, (string?)response["result"]!["protocolVersion"]);
    }

    [Fact]
    public async Task NotificationsGetNoResponse()
    {
        Assert.Null(await Server().HandleAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""", CancellationToken.None));
    }

    [Fact]
    public async Task ListsReadOnlyToolsAndHidesSearchWithoutAProvider()
    {
        var without = await Call(Server(), """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");
        var with = await Call(Server(new FakeSearch()), """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");

        var names = without["result"]!["tools"]!.AsArray().Select(tool => (string?)tool!["name"]).ToList();
        Assert.Equal(["list_meetings", "get_meeting_summary", "get_transcript"], names);
        Assert.Contains("search_meetings", with["result"]!["tools"]!.AsArray().Select(tool => (string?)tool!["name"]));
        Assert.All(with["result"]!["tools"]!.AsArray(), tool =>
        {
            Assert.Equal("object", (string?)tool!["inputSchema"]!["type"]);
            Assert.True((bool?)tool["annotations"]!["readOnlyHint"]);
        });
    }

    [Fact]
    public async Task ListMeetingsReturnsJsonText()
    {
        var response = await Call(Server(), """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"list_meetings","arguments":{"limit":1}}}""");
        var content = response["result"]!["content"]![0]!;
        var payload = JsonNode.Parse((string)content["text"]!)!;

        Assert.Equal("text", (string?)content["type"]);
        Assert.False((bool?)response["result"]!["isError"]);
        Assert.Single(payload["sessions"]!.AsArray());
    }

    [Fact]
    public async Task GetMeetingSummaryAndTranscript()
    {
        var summary = await Call(Server(), """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"get_meeting_summary","arguments":{"id":"s1"}}}""");
        var transcript = await Call(Server(), """{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"get_transcript","arguments":{"id":"s1"}}}""");

        Assert.Contains("We agreed to ship.", (string?)summary["result"]!["content"]![0]!["text"]);
        Assert.Contains("Buenas", (string?)transcript["result"]!["content"]![0]!["text"]);
    }

    [Fact]
    public async Task UnknownMeetingIsAToolError()
    {
        var response = await Call(Server(), """{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"get_transcript","arguments":{"id":"../x"}}}""");

        Assert.True((bool?)response["result"]!["isError"]);
    }

    [Fact]
    public async Task SearchMeetingsUsesTheProvider()
    {
        var search = new FakeSearch();
        var response = await Call(Server(search), """{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"search_meetings","arguments":{"query":"ship"}}}""");

        Assert.Equal("ship", search.LastQuery);
        Assert.Contains("Ship on Friday", (string?)response["result"]!["content"]![0]!["text"]);
    }

    [Theory]
    [InlineData("""{"jsonrpc":"2.0","id":8,"method":"tools/call","params":{"name":"delete_meeting","arguments":{}}}""", -32602)]
    [InlineData("""{"jsonrpc":"2.0","id":8,"method":"tools/call","params":{"name":"get_transcript","arguments":{}}}""", -32602)]
    [InlineData("""{"jsonrpc":"2.0","id":8,"method":"tools/call","params":{"name":"search_meetings","arguments":{"query":"x"}}}""", -32602)]
    [InlineData("""{"jsonrpc":"2.0","id":8,"method":"resources/list"}""", -32601)]
    [InlineData("""{"jsonrpc":"1.0","id":8,"method":"ping"}""", -32600)]
    [InlineData("""{"jsonrpc":"2.0","id":8}""", -32600)]
    [InlineData("""[{"jsonrpc":"2.0","id":8,"method":"ping"}]""", -32600)]
    [InlineData("""{ broken""", -32700)]
    public async Task ReportsJsonRpcErrors(string json, int code)
    {
        var response = await Call(Server(), json);

        Assert.Equal(code, (int?)response["error"]!["code"]);
        Assert.Null(response["result"]);
    }

    [Fact]
    public async Task PingReturnsAnEmptyResult()
    {
        var response = await Call(Server(), """{"jsonrpc":"2.0","id":9,"method":"ping"}""");

        Assert.Empty(response["result"]!.AsObject());
    }
}
