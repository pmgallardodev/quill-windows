using System.Net;

namespace Quill.Core.Api;

public enum ApiGuardVerdict
{
    Allowed,

    /// <summary>The TCP peer is not a loopback address.</summary>
    ForbiddenRemote,

    /// <summary>Host header is not 127.0.0.1:port or localhost:port (DNS rebinding).</summary>
    BadHost,

    /// <summary>A browser Origin other than the API itself (cross-site request).</summary>
    BadOrigin,

    /// <summary>Missing, malformed or wrong bearer token.</summary>
    Unauthorized,
}

/// <summary>
/// Checks every request before routing, in this order: loopback peer, Host header, Origin header,
/// bearer token (constant time, <see cref="ApiToken.Matches"/>). The listener already binds to
/// 127.0.0.1 only; the peer check is defence in depth.
/// </summary>
public static class ApiRequestGuard
{
    public static ApiGuardVerdict Check(
        IPAddress? remote,
        string? host,
        string? origin,
        string? authorization,
        int port,
        string? token)
    {
        if (!LoopbackUrl.IsLoopback(remote))
        {
            return ApiGuardVerdict.ForbiddenRemote;
        }

        if (!IsSelf(host?.Trim(), port, scheme: null))
        {
            return ApiGuardVerdict.BadHost;
        }

        if (origin is not null && !IsSelf(origin.Trim(), port, scheme: "http://"))
        {
            return ApiGuardVerdict.BadOrigin;
        }

        const string scheme = "Bearer ";
        var presented = authorization is not null && authorization.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)
            ? authorization[scheme.Length..].Trim()
            : null;
        return ApiToken.Matches(token, presented) ? ApiGuardVerdict.Allowed : ApiGuardVerdict.Unauthorized;
    }

    public static int StatusFor(ApiGuardVerdict verdict) => verdict switch
    {
        ApiGuardVerdict.Allowed => 200,
        ApiGuardVerdict.Unauthorized => 401,
        _ => 403,
    };

    private static bool IsSelf(string? value, int port, string? scheme)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        var prefix = scheme ?? string.Empty;
        return string.Equals(value, $"{prefix}127.0.0.1:{port}", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, $"{prefix}localhost:{port}", StringComparison.OrdinalIgnoreCase);
    }
}
