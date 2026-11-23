using System.Globalization;
using System.Text;

namespace Quill.Core.Api;

/// <summary>Request line and headers of one HTTP/1.x request. Header names are lower-case.</summary>
public sealed record HttpRequestHead(
    string Method,
    string Target,
    IReadOnlyDictionary<string, string> Headers,
    int ContentLength,
    byte[] Prefetched)
{
    public string? Header(string name) => Headers.GetValueOrDefault(name);
}

/// <summary>A request the server refuses with <see cref="Status"/> before it reaches the router.</summary>
public sealed class HttpWireException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>
/// Deliberately small and strict HTTP/1.x reader/writer for the loopback API: origin-form targets
/// only, Content-Length bodies only (no chunked, no Transfer-Encoding at all), no header folding,
/// single Host/Content-Length/Authorization/Origin/Content-Type, 8 KiB of headers, 256 KiB of body,
/// one request per connection (<c>Connection: close</c>). Anything outside that is rejected rather
/// than interpreted, which removes request smuggling and parser ambiguity by construction.
/// </summary>
public static class HttpWire
{
    public const int MaxHeaderBytes = 8 * 1024;
    public const int MaxBodyBytes = 256 * 1024;
    public const int MaxHeaders = 64;

    private static readonly string[] SingletonHeaders = ["host", "content-length", "authorization", "origin", "content-type"];
    private static readonly byte[] HeaderEnd = "\r\n\r\n"u8.ToArray();

    public static async Task<HttpRequestHead> ReadHeadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxHeaderBytes];
        var filled = 0;
        int end;
        while ((end = buffer.AsSpan(0, filled).IndexOf(HeaderEnd)) < 0)
        {
            if (filled == buffer.Length)
            {
                throw new HttpWireException(431, "headers too large");
            }

            var read = await stream.ReadAsync(buffer.AsMemory(filled), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new HttpWireException(400, "connection closed before the end of the headers");
            }

            filled += read;
        }

        var lines = Encoding.Latin1.GetString(buffer, 0, end).Split("\r\n");
        var (method, target) = ParseRequestLine(lines[0]);
        var headers = ParseHeaders(lines.AsSpan(1));
        if (headers.ContainsKey("transfer-encoding"))
        {
            throw new HttpWireException(501, "transfer-encoding is not supported");
        }

        var contentLength = 0;
        if (headers.TryGetValue("content-length", out var lengthText) &&
            !int.TryParse(lengthText, NumberStyles.None, CultureInfo.InvariantCulture, out contentLength))
        {
            throw lengthText.Length > 0 && lengthText.All(char.IsAsciiDigit)
                ? new HttpWireException(413, "body too large")
                : new HttpWireException(400, "invalid content-length");
        }

        if (contentLength > MaxBodyBytes)
        {
            throw new HttpWireException(413, "body too large");
        }

        var prefetched = buffer.AsSpan(end + HeaderEnd.Length, filled - end - HeaderEnd.Length).ToArray();
        return new HttpRequestHead(method, target, headers, contentLength, prefetched);
    }

    public static async Task<byte[]> ReadBodyAsync(Stream stream, HttpRequestHead head, CancellationToken cancellationToken)
    {
        var body = new byte[head.ContentLength];
        var filled = Math.Min(head.Prefetched.Length, body.Length);
        head.Prefetched.AsSpan(0, filled).CopyTo(body);
        while (filled < body.Length)
        {
            var read = await stream.ReadAsync(body.AsMemory(filled), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new HttpWireException(400, "connection closed before the end of the body");
            }

            filled += read;
        }

        return body;
    }

    /// <summary>Writes a complete response and flushes. <paramref name="json"/> null sends an empty body.</summary>
    public static async Task WriteResponseAsync(
        Stream stream,
        int status,
        string? json,
        IEnumerable<KeyValuePair<string, string>>? extraHeaders,
        CancellationToken cancellationToken)
    {
        var body = json is null ? [] : Encoding.UTF8.GetBytes(json);
        var head = new StringBuilder()
            .Append(CultureInfo.InvariantCulture, $"HTTP/1.1 {status} {Reason(status)}\r\n")
            .Append(CultureInfo.InvariantCulture, $"Content-Length: {body.Length}\r\n")
            .Append("Cache-Control: no-store\r\n")
            .Append("X-Content-Type-Options: nosniff\r\n")
            .Append("Connection: close\r\n");
        if (json is not null)
        {
            head.Append("Content-Type: application/json; charset=utf-8\r\n");
        }

        foreach (var (name, value) in extraHeaders ?? [])
        {
            head.Append(CultureInfo.InvariantCulture, $"{name}: {value}\r\n");
        }

        head.Append("\r\n");
        await stream.WriteAsync(Encoding.Latin1.GetBytes(head.ToString()), cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static (string Method, string Target) ParseRequestLine(string line)
    {
        var parts = line.Split(' ');
        if (parts.Length != 3 || parts[0].Length is 0 or > 16 || !parts[0].All(char.IsAsciiLetterUpper))
        {
            throw new HttpWireException(400, "invalid request line");
        }

        if (parts[2] is not ("HTTP/1.1" or "HTTP/1.0"))
        {
            throw new HttpWireException(505, "only HTTP/1.x is supported");
        }

        var target = parts[1];
        if (!target.StartsWith('/') || target.Any(character => character <= ' ' || character >= 0x7F || character == '#'))
        {
            throw new HttpWireException(400, "only origin-form request targets are accepted");
        }

        return (parts[0], target);
    }

    private static Dictionary<string, string> ParseHeaders(ReadOnlySpan<string> lines)
    {
        if (lines.Length > MaxHeaders)
        {
            throw new HttpWireException(431, "too many headers");
        }

        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            var colon = line.IndexOf(':');
            if (colon <= 0 || !line.AsSpan(0, colon).ContainsOnlyTokenChars())
            {
                throw new HttpWireException(400, "invalid header line");
            }

            var name = line[..colon].ToLowerInvariant();
            var value = line[(colon + 1)..].Trim(' ', '\t');
            if (value.Any(character => character < ' ' && character != '\t' || character == 0x7F))
            {
                throw new HttpWireException(400, "invalid header value");
            }

            if (headers.TryGetValue(name, out var existing))
            {
                if (SingletonHeaders.Contains(name))
                {
                    throw new HttpWireException(400, $"duplicate {name} header");
                }

                value = existing + ", " + value;
            }

            headers[name] = value;
        }

        return headers;
    }

    private static bool ContainsOnlyTokenChars(this ReadOnlySpan<char> text)
    {
        foreach (var character in text)
        {
            if (!char.IsAsciiLetterOrDigit(character) && "!#$%&'*+-.^_`|~".IndexOf(character) < 0)
            {
                return false;
            }
        }

        return true;
    }

    private static string Reason(int status) => status switch
    {
        200 => "OK",
        202 => "Accepted",
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        405 => "Method Not Allowed",
        408 => "Request Timeout",
        413 => "Content Too Large",
        415 => "Unsupported Media Type",
        431 => "Request Header Fields Too Large",
        501 => "Not Implemented",
        505 => "HTTP Version Not Supported",
        _ => "Internal Server Error",
    };
}
