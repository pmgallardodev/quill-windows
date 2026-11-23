using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Quill.Core.Api;
using Xunit;

namespace Quill.Core.Tests.Api;

/// <summary>
/// Replays, over raw HTTP, what the TypeScript MCP SDK's Streamable HTTP client (used by Claude
/// Code and OpenCode) sends: initialize, notifications/initialized, the optional GET for a
/// server stream, tools/list, tools/call and ping, with their real headers.
/// </summary>
public sealed class McpHandshakeTests
{
    private const int Port = 48400;
    private static readonly string Token = ApiToken.Generate();

    private static ApiConnectionHandler Handler()
    {
        var service = new LocalApiService(new FakeApiStore(), new FakeSearch());
        return new ApiConnectionHandler(new LocalApiRouter(service, new McpServer(service, "1.0")), Port, Token);
    }

    private static async Task<RawResponse> Send(string raw)
    {
        var stream = new DuplexStream(Encoding.UTF8.GetBytes(raw));
        await Handler().HandleAsync(stream, IPAddress.Loopback, CancellationToken.None);
        return RawResponse.Parse(stream.Written);
    }

    private static string Post(string body, string? protocolVersion = McpServer.LatestProtocolVersion, string accept = "application/json, text/event-stream", string? extra = null)
    {
        var builder = new StringBuilder()
            .Append("POST /mcp HTTP/1.1\r\n")
            .Append("Host: 127.0.0.1:48400\r\n")
            .Append("Connection: keep-alive\r\n")
            .Append(CultureInvariant($"Authorization: Bearer {Token}\r\n"))
            .Append("Content-Type: application/json\r\n")
            .Append(CultureInvariant($"Accept: {accept}\r\n"))
            .Append("User-Agent: node\r\n")
            .Append("Accept-Encoding: gzip, deflate\r\n")
            .Append("Sec-Fetch-Mode: cors\r\n");
        if (protocolVersion is not null)
        {
            builder.Append(CultureInvariant($"MCP-Protocol-Version: {protocolVersion}\r\n"));
        }

        builder.Append(extra)
            .Append(CultureInvariant($"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\n\r\n"))
            .Append(body);
        return builder.ToString();
    }

    private static string CultureInvariant(FormattableString text) => FormattableString.Invariant(text);

    private static JsonObject Json(RawResponse response) => JsonNode.Parse(response.Body)!.AsObject();

    [Fact]
    public async Task ARealClientHandshakeSucceeds()
    {
        var initialize = await Send(Post(
            """{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{"roots":{}},"clientInfo":{"name":"claude-code","version":"2.0.0"}}}""",
            protocolVersion: null));
        Assert.Equal(200, initialize.Status);
        Assert.StartsWith("application/json", initialize.Headers["content-type"]);
        Assert.Equal("2025-11-25", (string?)Json(initialize)["result"]!["protocolVersion"]);
        Assert.NotNull(Json(initialize)["result"]!["capabilities"]!["tools"]);
        // Stateless server: no session to resume or expire across app restarts.
        Assert.False(initialize.Headers.ContainsKey("mcp-session-id"));

        var initialized = await Send(Post("""{"jsonrpc":"2.0","method":"notifications/initialized"}"""));
        Assert.Equal(202, initialized.Status);
        Assert.Equal(string.Empty, initialized.Body);
        Assert.False(initialized.Headers.ContainsKey("content-type"));

        var stream = await Send($"GET /mcp HTTP/1.1\r\nHost: 127.0.0.1:48400\r\nAuthorization: Bearer {Token}\r\nAccept: text/event-stream\r\nMCP-Protocol-Version: 2025-11-25\r\n\r\n");
        Assert.Equal(405, stream.Status);
        Assert.Equal("POST", stream.Headers["allow"]);

        var list = await Send(Post("""{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}"""));
        Assert.Equal(200, list.Status);
        var names = Json(list)["result"]!["tools"]!.AsArray().Select(tool => (string?)tool!["name"]).ToList();
        Assert.Contains("search_meetings", names);
        Assert.Contains("list_meetings", names);

        var call = await Send(Post("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"search_meetings","arguments":{"query":"friday"}}}"""));
        Assert.Equal(200, call.Status);
        var result = Json(call)["result"]!;
        Assert.False((bool?)result["isError"]);
        Assert.Contains("Ship on Friday", (string?)result["content"]![0]!["text"]);

        var ping = await Send(Post("""{"jsonrpc":"2.0","id":3,"method":"ping"}"""));
        Assert.Equal(200, ping.Status);
        Assert.Equal(3, (int?)Json(ping)["id"]);
        Assert.Empty(Json(ping)["result"]!.AsObject());
    }

    [Theory]
    [InlineData("2025-11-25")]
    [InlineData("2025-06-18")]
    [InlineData("2025-03-26")]
    [InlineData("2024-11-05")]
    public async Task NegotiatesEveryVersionItSupports(string version)
    {
        var response = await Send(Post(
            """{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"protocolVersion":"VERSION","capabilities":{},"clientInfo":{"name":"opencode","version":"1"}}}""".Replace("VERSION", version, StringComparison.Ordinal),
            protocolVersion: null));

        Assert.Equal(version, (string?)Json(response)["result"]!["protocolVersion"]);
    }

    [Fact]
    public async Task UnsupportedProtocolVersionHeaderIsABadRequest()
    {
        var response = await Send(Post("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", protocolVersion: "1999-01-01"));

        Assert.Equal(400, response.Status);
        Assert.Equal(-32600, (int?)Json(response)["error"]!["code"]);
    }

    [Fact]
    public async Task MissingProtocolVersionHeaderIsAccepted() =>
        Assert.Equal(200, (await Send(Post("""{"jsonrpc":"2.0","id":1,"method":"ping"}""", protocolVersion: null))).Status);

    [Fact]
    public async Task ASessionIdFromAnEarlierServerIsIgnored() =>
        Assert.Equal(200, (await Send(Post("""{"jsonrpc":"2.0","id":1,"method":"ping"}""", extra: "Mcp-Session-Id: abc123\r\n"))).Status);

    [Fact]
    public async Task UnknownMethodIsMethodNotFound()
    {
        var response = await Send(Post("""{"jsonrpc":"2.0","id":9,"method":"resources/list"}"""));

        Assert.Equal(200, response.Status);
        Assert.Equal(-32601, (int?)Json(response)["error"]!["code"]);
        Assert.Equal(9, (int?)Json(response)["id"]);
    }

    [Fact]
    public async Task BatchesAreRejected()
    {
        var response = await Send(Post("""[{"jsonrpc":"2.0","id":1,"method":"ping"},{"jsonrpc":"2.0","id":2,"method":"ping"}]"""));

        Assert.Equal(400, response.Status);
        var error = Json(response);
        Assert.Equal(-32600, (int?)error["error"]!["code"]);
        Assert.Null(error["id"]);
    }

    [Fact]
    public async Task AClientResponseIsAccepted()
    {
        var response = await Send(Post("""{"jsonrpc":"2.0","id":"s-1","result":{}}"""));

        Assert.Equal(202, response.Status);
        Assert.Equal(string.Empty, response.Body);
    }

    [Theory]
    [InlineData("application/json")]
    [InlineData("*/*")]
    [InlineData("application/*")]
    public async Task AcceptsAnyAcceptThatAllowsJson(string accept) =>
        Assert.Equal(200, (await Send(Post("""{"jsonrpc":"2.0","id":1,"method":"ping"}""", accept: accept))).Status);

    [Fact]
    public async Task AcceptWithoutJsonIsNotAcceptable() =>
        Assert.Equal(406, (await Send(Post("""{"jsonrpc":"2.0","id":1,"method":"ping"}""", accept: "text/event-stream"))).Status);

    [Fact]
    public async Task TheBearerTokenIsStillRequired()
    {
        var raw = Post("""{"jsonrpc":"2.0","id":1,"method":"ping"}""").Replace($"Authorization: Bearer {Token}\r\n", string.Empty, StringComparison.Ordinal);

        Assert.Equal(401, (await Send(raw)).Status);
    }
}
