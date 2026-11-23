using System.Net;

namespace Quill.Core.Api;

public enum LoopbackUrlError
{
    None,
    Empty,
    Malformed,

    /// <summary>Only plain http is allowed: the target is this computer, TLS adds nothing.</summary>
    Scheme,

    /// <summary>Host is not exactly 127.0.0.1, [::1] or localhost.</summary>
    Host,

    /// <summary>User info (user:password@) in the URL.</summary>
    Credentials,
}

/// <summary>
/// Webhook URL policy: Quill never sends data off this computer, so a webhook must be an absolute
/// http URL whose host is literally 127.0.0.1, [::1] or localhost. The dispatcher re-checks the
/// resolved address at send time (<see cref="IsLoopback"/>) so a tampered hosts file cannot
/// redirect "localhost" elsewhere.
/// </summary>
public static class LoopbackUrl
{
    private static readonly string[] AllowedHosts = ["127.0.0.1", "[::1]", "localhost"];

    public static bool TryParse(string? text, out Uri? uri, out LoopbackUrlError error)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = LoopbackUrlError.Empty;
            return false;
        }

        // On Unix "/path" parses as an implicit file URI; anything without an explicit scheme is malformed here.
        var trimmed = text.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var candidate) ||
            !trimmed.StartsWith(candidate.Scheme + ":", StringComparison.OrdinalIgnoreCase))
        {
            error = LoopbackUrlError.Malformed;
            return false;
        }

        if (candidate.Scheme != Uri.UriSchemeHttp)
        {
            error = LoopbackUrlError.Scheme;
            return false;
        }

        if (!string.IsNullOrEmpty(candidate.UserInfo))
        {
            error = LoopbackUrlError.Credentials;
            return false;
        }

        if (!AllowedHosts.Contains(candidate.Host, StringComparer.OrdinalIgnoreCase))
        {
            error = LoopbackUrlError.Host;
            return false;
        }

        uri = candidate;
        error = LoopbackUrlError.None;
        return true;
    }

    /// <summary>127.0.0.0/8 or ::1, including IPv4-mapped IPv6 forms. Null is not loopback.</summary>
    public static bool IsLoopback(IPAddress? address)
    {
        if (address is null)
        {
            return false;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        return IPAddress.IsLoopback(address);
    }
}
