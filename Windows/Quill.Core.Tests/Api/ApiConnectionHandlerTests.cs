using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Quill.Core.Api;
using Xunit;

namespace Quill.Core.Tests.Api;

public sealed class ApiConnectionHandlerTests
{
    private const int Port = 48300;
    private static readonly string Token = ApiToken.Generate();

    private static ApiConnectionHandler Handler(bool mcp = false)
    {
        var service = new LocalApiService(new FakeApiStore());
        return new ApiConnectionHandler(new LocalApiRouter(service, mcp ? new McpServer(service, "1.0") : null), Port, Token);
    }

    private static async Task<RawResponse> Send(string raw, IPAddress? remote = null, bool mcp = false)
    {
        var stream = new DuplexStream(Encoding.Latin1.GetBytes(raw));
        await Handler(mcp).HandleAsync(stream, remote ?? IPAddress.Loopback, CancellationToken.None);
        return RawResponse.Parse(stream.Written);
    }

    private static string Get(string target, string? extraHeaders = null, string host = "127.0.0.1:48300") =>
        $"GET {target} HTTP/1.1\r\nHost: {host}\r\nAuthorization: Bearer {Token}\r\n{extraHeaders}\r\n";

    [Fact]
    public async Task ServesAnAuthenticatedRequestAsJson()
    {
        var response = await Send(Get("/v1/sessions"));

        Assert.Equal(200, response.Status);
        Assert.Equal("application/json; charset=utf-8", response.Headers["content-type"]);
        Assert.Equal("no-store", response.Headers["cache-control"]);
        Assert.Equal("nosniff", response.Headers["x-content-type-options"]);
        Assert.Equal("close", response.Headers["connection"]);
        Assert.False(response.Headers.ContainsKey("access-control-allow-origin"));
        Assert.Equal(response.Body.Length.ToString(), response.Headers["content-length"]);
        Assert.Equal(2, JsonNode.Parse(response.Body)!["sessions"]!.AsArray().Count);
    }

    [Fact]
    public async Task MissingTokenIsUnauthorized()
    {
        var response = await Send("GET /v1/sessions HTTP/1.1\r\nHost: 127.0.0.1:48300\r\n\r\n");

        Assert.Equal(401, response.Status);
        Assert.Equal("Bearer", response.Headers["www-authenticate"]);
        Assert.DoesNotContain("sessions", response.Body);
    }

    [Fact]
    public async Task RemotePeerIsForbidden() =>
        Assert.Equal(403, (await Send(Get("/v1/sessions"), IPAddress.Parse("192.168.1.30"))).Status);

    [Fact]
    public async Task ForeignHostHeaderIsForbidden() =>
        Assert.Equal(403, (await Send(Get("/v1/sessions", host: "rebind.example:48300"))).Status);

    [Fact]
    public async Task CrossSiteOriginIsForbidden() =>
        Assert.Equal(403, (await Send(Get("/v1/sessions", "Origin: http://evil.example\r\n"))).Status);

    [Fact]
    public async Task PreflightGetsNoCorsHeaders()
    {
        var response = await Send($"OPTIONS /v1/sessions HTTP/1.1\r\nHost: 127.0.0.1:48300\r\nAuthorization: Bearer {Token}\r\n\r\n");

        Assert.Equal(405, response.Status);
        Assert.DoesNotContain(response.Headers.Keys, key => key.StartsWith("access-control", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("GET /v1/sessions HTTP/2.0\r\nHost: 127.0.0.1:48300\r\n\r\n", 505)]
    [InlineData("GET http://127.0.0.1:48300/v1/sessions HTTP/1.1\r\nHost: 127.0.0.1:48300\r\n\r\n", 400)]
    [InlineData("get /v1/sessions HTTP/1.1\r\nHost: 127.0.0.1:48300\r\n\r\n", 400)]
    [InlineData("GET /v1/sessions HTTP/1.1\r\nHost: 127.0.0.1:48300\r\nHost: evil:1\r\n\r\n", 400)]
    [InlineData("GET /v1/sessions HTTP/1.1\r\nHost: 127.0.0.1:48300\r\n folded\r\n\r\n", 400)]
    [InlineData("GET /v1/sessions HTTP/1.1\r\nHost 127.0.0.1:48300\r\n\r\n", 400)]
    [InlineData("POST /mcp HTTP/1.1\r\nHost: 127.0.0.1:48300\r\nTransfer-Encoding: chunked\r\n\r\n", 501)]
    [InlineData("POST /mcp HTTP/1.1\r\nHost: 127.0.0.1:48300\r\nContent-Length: abc\r\n\r\n", 400)]
    [InlineData("POST /mcp HTTP/1.1\r\nHost: 127.0.0.1:48300\r\nContent-Length: 1\r\nContent-Length: 2\r\n\r\n", 400)]
    [InlineData("POST /mcp HTTP/1.1\r\nHost: 127.0.0.1:48300\r\nContent-Length: 99999999\r\n\r\n", 413)]
    [InlineData("GET /v1/sessions HTTP/1.1\r\nHost: 127.0.0.1:48300\r\n", 400)]
    [InlineData("", 400)]
    public async Task RejectsMalformedOrUnsupportedRequests(string raw, int status) =>
        Assert.Equal(status, (await Send(raw)).Status);

    [Fact]
    public async Task RejectsOversizedHeaders()
    {
        var raw = "GET /v1/sessions HTTP/1.1\r\nHost: 127.0.0.1:48300\r\nX-Big: " + new string('a', HttpWire.MaxHeaderBytes) + "\r\n\r\n";

        Assert.Equal(431, (await Send(raw)).Status);
    }

    [Fact]
    public async Task McpNeedsJsonContentType()
    {
        const string body = """{"jsonrpc":"2.0","id":1,"method":"ping"}""";
        var raw = $"POST /mcp HTTP/1.1\r\nHost: localhost:48300\r\nAuthorization: Bearer {Token}\r\nContent-Type: text/plain\r\nContent-Length: {body.Length}\r\n\r\n{body}";

        Assert.Equal(415, (await Send(raw, mcp: true)).Status);
    }

    [Fact]
    public async Task McpRoundTripsOverHttp()
    {
        const string body = """{"jsonrpc":"2.0","id":1,"method":"ping"}""";
        var raw = $"POST /mcp HTTP/1.1\r\nHost: localhost:48300\r\nAuthorization: Bearer {Token}\r\n" +
                  $"Content-Type: application/json\r\nAccept: application/json, text/event-stream\r\nContent-Length: {body.Length}\r\n\r\n{body}";

        var response = await Send(raw, mcp: true);

        Assert.Equal(200, response.Status);
        Assert.Equal(1, (int?)JsonNode.Parse(response.Body)!["id"]);
    }

    [Fact]
    public async Task McpNotificationIsAcceptedWithoutBody()
    {
        const string body = """{"jsonrpc":"2.0","method":"notifications/initialized"}""";
        var raw = $"POST /mcp HTTP/1.1\r\nHost: localhost:48300\r\nAuthorization: Bearer {Token}\r\n" +
                  $"Content-Type: application/json\r\nContent-Length: {body.Length}\r\n\r\n{body}";

        var response = await Send(raw, mcp: true);

        Assert.Equal(202, response.Status);
        Assert.Equal("0", response.Headers["content-length"]);
    }

    [Fact]
    public async Task UnexpectedFailuresBecomeAGeneric500()
    {
        var handler = new ApiConnectionHandler(new LocalApiRouter(new LocalApiService(new ThrowingStore())), Port, Token);
        var stream = new DuplexStream(Encoding.Latin1.GetBytes(Get("/v1/sessions")));

        await handler.HandleAsync(stream, IPAddress.Loopback, CancellationToken.None);
        var response = RawResponse.Parse(stream.Written);

        Assert.Equal(500, response.Status);
        Assert.DoesNotContain("secret", response.Body);
    }

    private sealed class ThrowingStore : Ports.ISessionStore
    {
        public Task<IReadOnlyList<Domain.Session>> ListSessionsAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("secret path C:\\Users\\me");

        public Task<Domain.Session?> LoadSessionAsync(string sessionId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<Domain.TranscriptSegment>> LoadTranscriptAsync(string sessionId, Domain.TranscriptPass pass, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task SaveTranscriptAsync(string sessionId, Domain.TranscriptPass pass, Ports.TranscriptInfo info, IReadOnlyList<Domain.TranscriptSegment> segments, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Domain.MeetingSummary?> LoadSummaryAsync(string sessionId, Domain.SummaryKind kind, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task SaveSummaryAsync(string sessionId, Domain.MeetingSummary summary, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<Domain.Speaker>> LoadSpeakersAsync(string sessionId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task SaveSpeakersAsync(string sessionId, IReadOnlyList<Domain.Speaker> speakers, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<string> LoadNotesAsync(string sessionId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task SaveNotesAsync(string sessionId, string markdown, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}

/// <summary>Reads from fixed bytes, records everything written.</summary>
internal sealed class DuplexStream(byte[] input) : Stream
{
    private readonly MemoryStream _input = new(input);
    private readonly MemoryStream _output = new();

    public byte[] Written => _output.ToArray();

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => _input.Read(buffer, offset, count);

    public override void Write(byte[] buffer, int offset, int count) => _output.Write(buffer, offset, count);

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();
}

internal sealed record RawResponse(int Status, Dictionary<string, string> Headers, string Body)
{
    public static RawResponse Parse(byte[] bytes)
    {
        var text = Encoding.UTF8.GetString(bytes);
        var split = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        Assert.True(split > 0, "response has no header terminator: " + text);
        var lines = text[..split].Split("\r\n");
        var status = int.Parse(lines[0].Split(' ')[1]);
        var headers = lines.Skip(1)
            .Select(line => line.Split(':', 2))
            .ToDictionary(parts => parts[0].Trim().ToLowerInvariant(), parts => parts[1].Trim());
        return new RawResponse(status, headers, text[(split + 4)..]);
    }
}
