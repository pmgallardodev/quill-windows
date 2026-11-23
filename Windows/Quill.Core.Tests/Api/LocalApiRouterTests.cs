using System.Text;
using System.Text.Json.Nodes;
using Quill.Core.Api;
using Xunit;

namespace Quill.Core.Tests.Api;

public sealed class LocalApiRouterTests
{
    private static LocalApiRouter Router(IApiSearch? search = null, bool mcp = false) =>
        new(new LocalApiService(new FakeApiStore(), search), mcp ? new McpServer(new LocalApiService(new FakeApiStore(), search), "1.0") : null);

    private static async Task<(int Status, JsonNode? Body)> Get(LocalApiRouter router, string target)
    {
        var response = await router.HandleAsync(new ApiRequest("GET", target, ReadOnlyMemory<byte>.Empty), CancellationToken.None);
        return (response.Status, response.Json is null ? null : JsonNode.Parse(response.Json));
    }

    [Fact]
    public async Task ListsSessionsNewestFirst()
    {
        var (status, body) = await Get(Router(), "/v1/sessions");
        var sessions = body!["sessions"]!.AsArray();

        Assert.Equal(200, status);
        Assert.Equal(2, sessions.Count);
        Assert.Equal("s2", (string?)sessions[0]!["id"]);
        Assert.Equal("s2", (string?)sessions[0]!["title"]);
        Assert.False((bool?)sessions[0]!["hasSummary"]);
        Assert.Equal("Planning", (string?)sessions[1]!["title"]);
        Assert.Equal(1800, (double?)sessions[1]!["durationSeconds"]);
        Assert.True((bool?)sessions[1]!["hasSummary"]);
        Assert.Equal(FakeApiStore.Start1, DateTimeOffset.Parse((string)sessions[1]!["start"]!));
    }

    [Fact]
    public async Task HonoursTheLimitParameter()
    {
        var (_, body) = await Get(Router(), "/v1/sessions?limit=1");

        Assert.Single(body!["sessions"]!.AsArray());
    }

    [Theory]
    [InlineData("/v1/sessions?limit=0")]
    [InlineData("/v1/sessions?limit=abc")]
    [InlineData("/v1/sessions?limit=100000")]
    public async Task RejectsBadLimits(string target) => Assert.Equal(400, (await Get(Router(), target)).Status);

    [Fact]
    public async Task GetsASessionWithSummaryAndSpeakerNames()
    {
        var (status, body) = await Get(Router(), "/v1/sessions/s1");

        Assert.Equal(200, status);
        Assert.Equal("Planning", (string?)body!["title"]);
        Assert.Equal("We agreed to ship.", (string?)body["summary"]!["executiveSummary"]);
        Assert.Equal("00:01:05", (string?)body["summary"]!["decisions"]![0]!["at"]);
        var speakers = body["speakers"]!.AsArray();
        Assert.Equal("me", (string?)speakers[0]!["name"]);
        Assert.Equal("Ana", (string?)speakers[1]!["name"]);
        Assert.Equal("system", (string?)speakers[1]!["track"]);
    }

    [Fact]
    public async Task SessionWithoutSummaryHasNullSummary()
    {
        var (status, body) = await Get(Router(), "/v1/sessions/s2");

        Assert.Equal(200, status);
        Assert.Null(body!["summary"]);
        Assert.Null(body["end"]);
    }

    [Fact]
    public async Task GetsTheTranscriptWithSpeakerLabels()
    {
        var (status, body) = await Get(Router(), "/v1/sessions/s1/transcript");
        var segments = body!["segments"]!.AsArray();

        Assert.Equal(200, status);
        Assert.Equal(3, segments.Count);
        Assert.Equal(2.5, (double?)segments[0]!["end"]);
        Assert.Equal("Ana", (string?)segments[1]!["speaker"]);
        Assert.Equal("speaker-2", (string?)segments[2]!["speaker"]);
        Assert.Equal("Vale", (string?)segments[2]!["text"]);
    }

    [Fact]
    public async Task GetsTheNotes()
    {
        var (status, body) = await Get(Router(), "/v1/sessions/s1/notes");

        Assert.Equal(200, status);
        Assert.Equal("s1", (string?)body!["sessionId"]);
        Assert.Contains("presupuesto", (string?)body["markdown"]);
    }

    [Theory]
    [InlineData("/v1/sessions/missing")]
    [InlineData("/v1/sessions/missing/transcript")]
    [InlineData("/v1/sessions/missing/notes")]
    [InlineData("/v1/sessions/..")]
    [InlineData("/v1/sessions/%2E%2E/notes")]
    [InlineData("/v1/sessions/a%2Fb")]
    [InlineData("/v1/sessions/s1/unknown")]
    [InlineData("/v1/nope")]
    [InlineData("/")]
    [InlineData("/mcp")]
    public async Task UnknownOrUnsafeTargetsAreNotFound(string target)
    {
        var (status, body) = await Get(Router(), target);

        Assert.Equal(404, status);
        Assert.Equal("not_found", (string?)body!["error"]);
    }

    [Fact]
    public async Task WriteMethodsAreNotAllowed()
    {
        var response = await Router().HandleAsync(new ApiRequest("DELETE", "/v1/sessions/s1", ReadOnlyMemory<byte>.Empty), CancellationToken.None);

        Assert.Equal(405, response.Status);
    }

    [Fact]
    public async Task SearchIsNotImplementedWithoutAProvider() =>
        Assert.Equal(501, (await Get(Router(), "/v1/search?q=ship")).Status);

    [Fact]
    public async Task SearchDelegatesToTheProvider()
    {
        var search = new FakeSearch();
        var (status, body) = await Get(Router(search), "/v1/search?q=ship%20friday&limit=5");

        Assert.Equal(200, status);
        Assert.Equal("ship friday", search.LastQuery);
        Assert.Equal(5, search.LastLimit);
        Assert.Equal("s1", (string?)body!["hits"]![0]!["sessionId"]);
    }

    [Theory]
    [InlineData("/v1/search")]
    [InlineData("/v1/search?q=")]
    [InlineData("/v1/search?q=%20%20")]
    public async Task SearchNeedsAQuery(string target) => Assert.Equal(400, (await Get(Router(new FakeSearch()), target)).Status);

    [Fact]
    public async Task SearchRejectsOverlongQueries() =>
        Assert.Equal(400, (await Get(Router(new FakeSearch()), "/v1/search?q=" + new string('a', LocalApiService.MaxQueryLength + 1))).Status);

    [Fact]
    public async Task McpIsRoutedOnlyWhenEnabled()
    {
        var body = Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","id":1,"method":"ping"}""");

        var off = await Router().HandleAsync(new ApiRequest("POST", "/mcp", body), CancellationToken.None);
        var on = await Router(mcp: true).HandleAsync(new ApiRequest("POST", "/mcp", body), CancellationToken.None);
        var get = await Router(mcp: true).HandleAsync(new ApiRequest("GET", "/mcp", ReadOnlyMemory<byte>.Empty), CancellationToken.None);

        Assert.Equal(404, off.Status);
        Assert.Equal(200, on.Status);
        Assert.Equal(405, get.Status);
    }
}

public sealed class ApiRequestGuardTests
{
    private const int Port = 48200;
    private static readonly string Token = ApiToken.Generate();

    private static ApiGuardVerdict Check(
        string remote = "127.0.0.1",
        string? host = "127.0.0.1:48200",
        string? origin = null,
        string? authorization = "default") =>
        ApiRequestGuard.Check(
            System.Net.IPAddress.Parse(remote),
            host,
            origin,
            authorization == "default" ? "Bearer " + Token : authorization,
            Port,
            Token);

    [Fact]
    public void AllowsALoopbackRequestWithTheToken()
    {
        Assert.Equal(ApiGuardVerdict.Allowed, Check());
        Assert.Equal(ApiGuardVerdict.Allowed, Check(remote: "::1", host: "localhost:48200"));
        Assert.Equal(ApiGuardVerdict.Allowed, Check(host: "LOCALHOST:48200", authorization: "bearer " + Token));
        Assert.Equal(ApiGuardVerdict.Allowed, Check(origin: "http://127.0.0.1:48200"));
    }

    [Theory]
    [InlineData("192.168.1.20")]
    [InlineData("10.0.0.1")]
    [InlineData("::ffff:192.168.1.20")]
    public void RejectsRemoteCallers(string remote) => Assert.Equal(ApiGuardVerdict.ForbiddenRemote, Check(remote: remote));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("127.0.0.1")]
    [InlineData("127.0.0.1:1234")]
    [InlineData("evil.example:48200")]
    [InlineData("localhost.evil.example:48200")]
    [InlineData("192.168.1.2:48200")]
    public void RejectsUnexpectedHostHeaders(string? host) => Assert.Equal(ApiGuardVerdict.BadHost, Check(host: host));

    [Theory]
    [InlineData("http://evil.example")]
    [InlineData("null")]
    [InlineData("https://127.0.0.1:48200")]
    [InlineData("http://127.0.0.1:1")]
    public void RejectsBrowserOriginsOtherThanItself(string origin) => Assert.Equal(ApiGuardVerdict.BadOrigin, Check(origin: origin));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Bearer")]
    [InlineData("Bearer ")]
    [InlineData("Basic dXNlcjpwYXNz")]
    [InlineData("Bearer wrong-token")]
    public void RejectsMissingOrWrongTokens(string? authorization) =>
        Assert.Equal(ApiGuardVerdict.Unauthorized, Check(authorization: authorization));

    [Fact]
    public void NothingIsAllowedWithoutAConfiguredToken() =>
        Assert.Equal(
            ApiGuardVerdict.Unauthorized,
            ApiRequestGuard.Check(System.Net.IPAddress.Loopback, "127.0.0.1:48200", null, "Bearer ", Port, null));

    [Fact]
    public void MapsVerdictsToStatusCodes()
    {
        Assert.Equal(403, ApiRequestGuard.StatusFor(ApiGuardVerdict.ForbiddenRemote));
        Assert.Equal(403, ApiRequestGuard.StatusFor(ApiGuardVerdict.BadHost));
        Assert.Equal(403, ApiRequestGuard.StatusFor(ApiGuardVerdict.BadOrigin));
        Assert.Equal(401, ApiRequestGuard.StatusFor(ApiGuardVerdict.Unauthorized));
    }
}

public sealed class ApiEventTests
{
    [Fact]
    public void SerializesAMinimalPayloadWithoutMeetingContent()
    {
        var at = new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);
        var json = JsonNode.Parse(ApiEvent.Create(ApiEventTypes.SummaryCompleted, "s1", at).ToJson())!.AsObject();

        Assert.Equal("summary.completed", (string?)json["type"]);
        Assert.Equal("s1", (string?)json["sessionId"]);
        Assert.Equal(at, DateTimeOffset.Parse((string)json["occurredAt"]!));
        Assert.True(Guid.TryParse((string?)json["id"], out _));
        Assert.Equal(4, json.Count);
    }

    [Fact]
    public void RejectsUnknownEventTypes() =>
        Assert.Throws<ArgumentException>(() => ApiEvent.Create("session.deleted", "s1", DateTimeOffset.Now));

    [Fact]
    public void SignsBodiesWithHmacSha256OfTheToken()
    {
        // HMAC-SHA256("key", "The quick brown fox jumps over the lazy dog"), RFC test vector.
        var signature = WebhookSignature.Compute("key", Encoding.UTF8.GetBytes("The quick brown fox jumps over the lazy dog"));

        Assert.Equal("sha256=f7bc83f430538424b13298e6aa6fb143ef4d59a14946175997479dbc2d1a3cd8", signature);
        Assert.Equal("X-Quill-Signature", WebhookSignature.HeaderName);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, false)]
    public void RetriesAtMostOnce(int attempt, bool retry)
    {
        Assert.Equal(retry, WebhookPolicy.ShouldRetry(attempt, statusCode: null));
        Assert.Equal(retry, WebhookPolicy.ShouldRetry(attempt, statusCode: 503));
        Assert.False(WebhookPolicy.ShouldRetry(attempt, statusCode: 404));
        Assert.False(WebhookPolicy.ShouldRetry(attempt, statusCode: 200));
        Assert.Equal(2, WebhookPolicy.MaxAttempts);
        Assert.Equal(TimeSpan.FromSeconds(3), WebhookPolicy.Timeout);
    }
}
