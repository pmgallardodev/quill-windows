using Quill.Core.Api;

namespace Quill.Core.Settings;

/// <summary>
/// config.json "api" section: the optional local API (and MCP endpoint) that only listens on
/// 127.0.0.1. Off by default so Quill stays discreet: nothing listens until the user turns it on.
/// Kept apart from <see cref="QuillSettings"/> (see <see cref="ApiSettingsDocument"/>) because the
/// token is a secret that only the API host and its settings page need.
/// </summary>
public sealed record ApiSettings
{
    public const int MinPort = 1024;
    public const int MaxPort = 65535;
    public const int MaxWebhooks = 10;

    /// <summary><c>enabled</c>: off by default.</summary>
    public bool Enabled { get; init; }

    /// <summary><c>port</c>: 0 means "pick a free port on first start and persist it".</summary>
    public int Port { get; init; }

    /// <summary><c>token</c>: bearer token (<see cref="ApiToken"/>), generated the first time the API is enabled.</summary>
    public string? Token { get; init; }

    /// <summary><c>webhooks</c>: loopback-only URLs (<see cref="LoopbackUrl"/>) that receive event POSTs.</summary>
    public IReadOnlyList<string> Webhooks { get; init; } = [];

    /// <summary><c>mcp_enabled</c>: also serve MCP (Streamable HTTP) at /mcp on the same listener.</summary>
    public bool McpEnabled { get; init; }

    public bool IsProvisioned => IsValidPort(Port) && ApiToken.IsWellFormed(Token);

    public static bool IsValidPort(int port) => port is >= MinPort and <= MaxPort;

    /// <summary>
    /// Fills a missing token and port. Returns this same instance when nothing was missing, so the
    /// caller knows whether it has to persist the result.
    /// </summary>
    /// <param name="pickFreePort">Returns a free loopback port (called only when <see cref="Port"/> is 0).</param>
    public ApiSettings Provision(Func<int> pickFreePort)
    {
        ArgumentNullException.ThrowIfNull(pickFreePort);
        if (IsProvisioned)
        {
            return this;
        }

        var port = Port;
        if (!IsValidPort(port))
        {
            port = pickFreePort();
            if (!IsValidPort(port))
            {
                throw new InvalidOperationException($"picked port {port} is outside {MinPort}-{MaxPort}");
            }
        }

        return this with
        {
            Port = port,
            Token = ApiToken.IsWellFormed(Token) ? Token : ApiToken.Generate(),
        };
    }

    public ApiSettings WithNewToken() => this with { Token = ApiToken.Generate() };
}
