using System.Net;
using System.Text.Json.Nodes;
using Quill.Core.Api;
using Quill.Core.Settings;
using Xunit;

namespace Quill.Core.Tests.Settings;

public sealed class ApiSettingsTests
{
    private static readonly string ValidToken = ApiToken.Generate();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{ \"api\": 3 }")]
    [InlineData("{ \"api\": {} }")]
    public void MissingApiSectionIsOffByDefault(string? json)
    {
        var api = ApiSettingsDocument.Parse(json);

        Assert.False(api.Enabled);
        Assert.Equal(0, api.Port);
        Assert.Null(api.Token);
        Assert.Empty(api.Webhooks);
        Assert.False(api.McpEnabled);
        Assert.False(api.IsProvisioned);
    }

    [Fact]
    public void ReadsEveryApiKey()
    {
        var json = $$"""
            { "api": { "enabled": true, "port": 48123, "token": "{{ValidToken}}", "mcp_enabled": true,
              "webhooks": ["http://127.0.0.1:9000/hook", "http://localhost:9001/", "http://[::1]:9002/x"] } }
            """;

        var api = ApiSettingsDocument.Parse(json);

        Assert.True(api.Enabled);
        Assert.Equal(48123, api.Port);
        Assert.Equal(ValidToken, api.Token);
        Assert.True(api.McpEnabled);
        Assert.Equal(["http://127.0.0.1:9000/hook", "http://localhost:9001/", "http://[::1]:9002/x"], api.Webhooks);
        Assert.True(api.IsProvisioned);
    }

    [Fact]
    public void InvalidValuesFallBackOneByOne()
    {
        const string json = """
            { "api": { "enabled": "yes", "port": 80, "token": "short", "mcp_enabled": true,
              "webhooks": ["https://example.com/hook", "http://10.0.0.5/hook", 7, "http://127.0.0.1:9000/ok", "http://127.0.0.1:9000/ok"] } }
            """;

        var api = ApiSettingsDocument.Parse(json);

        Assert.False(api.Enabled);
        Assert.Equal(0, api.Port);
        Assert.Null(api.Token);
        Assert.True(api.McpEnabled);
        Assert.Equal(["http://127.0.0.1:9000/ok"], api.Webhooks);
    }

    [Theory]
    [InlineData(1023)]
    [InlineData(65536)]
    [InlineData(-1)]
    public void OutOfRangePortsMeanPickOne(int port)
    {
        var api = ApiSettingsDocument.Parse($$"""{ "api": { "port": {{port}} } }""");

        Assert.Equal(0, api.Port);
    }

    [Fact]
    public void KeepsAtMostTheWebhookLimit()
    {
        var urls = Enumerable.Range(0, ApiSettings.MaxWebhooks + 5).Select(index => $"\"http://127.0.0.1:{9000 + index}/\"");
        var api = ApiSettingsDocument.Parse($$"""{ "api": { "webhooks": [{{string.Join(',', urls)}}] } }""");

        Assert.Equal(ApiSettings.MaxWebhooks, api.Webhooks.Count);
    }

    [Fact]
    public void MergeWritesTheApiSectionKeepsOtherKeysAndRoundTrips()
    {
        var settings = new ApiSettings
        {
            Enabled = true,
            Port = 48500,
            Token = ValidToken,
            Webhooks = ["http://127.0.0.1:9000/hook"],
            McpEnabled = true,
        };

        var merged = ApiSettingsDocument.Merge("""{ "privacy": { "discreet_mode": true }, "api": { "future": 1 } }""", settings);
        var root = JsonNode.Parse(merged)!.AsObject();
        var api = root["api"]!.AsObject();
        var parsed = ApiSettingsDocument.Parse(merged);

        Assert.True((bool?)root["privacy"]!["discreet_mode"]);
        Assert.Equal(1, (int?)api["future"]);
        Assert.True((bool?)api["enabled"]);
        Assert.Equal(48500, (int?)api["port"]);
        Assert.Equal(ValidToken, (string?)api["token"]);
        Assert.True((bool?)api["mcp_enabled"]);
        Assert.Equal(settings.Enabled, parsed.Enabled);
        Assert.Equal(settings.Port, parsed.Port);
        Assert.Equal(settings.Token, parsed.Token);
        Assert.Equal(settings.Webhooks, parsed.Webhooks);
        Assert.Equal(settings.McpEnabled, parsed.McpEnabled);
    }

    [Fact]
    public void MergeRemovesUnsetPortAndToken()
    {
        var merged = ApiSettingsDocument.Merge("""{ "api": { "port": 48500, "token": "x" } }""", new ApiSettings());
        var api = JsonNode.Parse(merged)!["api"]!.AsObject();

        Assert.False(api.ContainsKey("port"));
        Assert.False(api.ContainsKey("token"));
        Assert.False((bool?)api["enabled"]);
        Assert.Empty(api["webhooks"]!.AsArray());
    }

    [Fact]
    public void MergeRefusesABrokenConfigFile()
    {
        Assert.Throws<FormatException>(() => ApiSettingsDocument.Merge("[1, 2]", new ApiSettings()));
        Assert.Throws<FormatException>(() => ApiSettingsDocument.Merge("{ broken", new ApiSettings()));
    }

    [Fact]
    public void ProvisionFillsTokenAndPortOnlyWhenMissing()
    {
        var provisioned = new ApiSettings { Enabled = true }.Provision(() => 49000);

        Assert.True(provisioned.IsProvisioned);
        Assert.Equal(49000, provisioned.Port);
        Assert.True(ApiToken.IsWellFormed(provisioned.Token));

        var again = provisioned.Provision(() => throw new InvalidOperationException("must not pick a port"));
        Assert.Same(provisioned, again);
    }

    [Fact]
    public void ProvisionRejectsAnInvalidPickedPort() =>
        Assert.Throws<InvalidOperationException>(() => new ApiSettings().Provision(() => 80));

    [Fact]
    public void RegenerateTokenAlwaysChangesIt()
    {
        var settings = new ApiSettings { Token = ValidToken };

        Assert.NotEqual(ValidToken, settings.WithNewToken().Token);
    }
}

public sealed class LoopbackUrlTests
{
    [Theory]
    [InlineData("http://127.0.0.1:9000/hook")]
    [InlineData("http://localhost/")]
    [InlineData("HTTP://LOCALHOST:8080/a?b=c")]
    [InlineData("http://[::1]:9000/")]
    [InlineData("  http://127.0.0.1:9000/hook  ")]
    public void AcceptsLoopbackHttpUrls(string text)
    {
        Assert.True(LoopbackUrl.TryParse(text, out var uri, out var error));
        Assert.NotNull(uri);
        Assert.Equal(LoopbackUrlError.None, error);
    }

    [Theory]
    [InlineData(null, LoopbackUrlError.Empty)]
    [InlineData("   ", LoopbackUrlError.Empty)]
    [InlineData("not a url", LoopbackUrlError.Malformed)]
    [InlineData("/relative/path", LoopbackUrlError.Malformed)]
    [InlineData("https://127.0.0.1:9000/", LoopbackUrlError.Scheme)]
    [InlineData("ftp://127.0.0.1/", LoopbackUrlError.Scheme)]
    [InlineData("file:///C:/x", LoopbackUrlError.Scheme)]
    [InlineData("http://example.com/hook", LoopbackUrlError.Host)]
    [InlineData("http://10.0.0.5/hook", LoopbackUrlError.Host)]
    [InlineData("http://127.0.0.2/hook", LoopbackUrlError.Host)]
    [InlineData("http://localhost.evil.com/", LoopbackUrlError.Host)]
    [InlineData("http://0.0.0.0/", LoopbackUrlError.Host)]
    [InlineData("http://user:pass@127.0.0.1/", LoopbackUrlError.Credentials)]
    public void RejectsEverythingElse(string? text, LoopbackUrlError expected)
    {
        Assert.False(LoopbackUrl.TryParse(text, out var uri, out var error));
        Assert.Null(uri);
        Assert.Equal(expected, error);
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("127.8.9.10", true)]
    [InlineData("::1", true)]
    [InlineData("::ffff:127.0.0.1", true)]
    [InlineData("10.0.0.1", false)]
    [InlineData("192.168.1.10", false)]
    [InlineData("::ffff:10.0.0.1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("0.0.0.0", false)]
    public void ClassifiesLoopbackAddresses(string address, bool expected) =>
        Assert.Equal(expected, LoopbackUrl.IsLoopback(IPAddress.Parse(address)));

    [Fact]
    public void NullAddressIsNotLoopback() => Assert.False(LoopbackUrl.IsLoopback(null));
}

public sealed class ApiTokenTests
{
    [Fact]
    public void GeneratesDistinctUrlSafe256BitTokens()
    {
        var first = ApiToken.Generate();
        var second = ApiToken.Generate();

        Assert.NotEqual(first, second);
        Assert.Equal(43, first.Length);
        Assert.Matches("^[A-Za-z0-9_-]{43}$", first);
        Assert.True(ApiToken.IsWellFormed(first));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA+")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    public void RejectsMalformedTokens(string? token) => Assert.False(ApiToken.IsWellFormed(token));

    [Fact]
    public void MatchesOnlyTheExactToken()
    {
        var token = ApiToken.Generate();

        Assert.True(ApiToken.Matches(token, token));
        Assert.False(ApiToken.Matches(token, token[..^1]));
        Assert.False(ApiToken.Matches(token, token + "x"));
        Assert.False(ApiToken.Matches(token, token.ToUpperInvariant() == token ? token.ToLowerInvariant() : token.ToUpperInvariant()));
        Assert.False(ApiToken.Matches(token, null));
        Assert.False(ApiToken.Matches(null, token));
        Assert.False(ApiToken.Matches(null, null));
    }
}
