using System.Globalization;
using System.Text;

namespace Quill.Core.Api;

/// <summary>
/// Maps an already authenticated <see cref="ApiRequest"/> to a <see cref="ApiResponse"/>. Read-only:
/// every <c>/v1</c> endpoint is GET; <c>/mcp</c> accepts POST only and exists only when MCP is on.
/// Error bodies carry a fixed code (<c>{"error":"not_found"}</c>), never exception text.
/// </summary>
/// <remarks>
/// Endpoints: <c>GET /v1/sessions?limit=</c>, <c>GET /v1/sessions/{id}</c>,
/// <c>GET /v1/sessions/{id}/transcript</c>, <c>GET /v1/sessions/{id}/notes</c>,
/// <c>GET /v1/search?q=&amp;limit=</c>, <c>POST /mcp</c> (JSON-RPC, see <see cref="McpServer"/>).
/// </remarks>
public sealed class LocalApiRouter
{
    private readonly LocalApiService _service;
    private readonly McpServer? _mcp;

    /// <param name="mcp">MCP endpoint; null keeps <c>/mcp</c> a 404.</param>
    public LocalApiRouter(LocalApiService service, McpServer? mcp = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _mcp = mcp;
    }

    public async Task<ApiResponse> HandleAsync(ApiRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var queryStart = request.Target.IndexOf('?');
        var path = queryStart < 0 ? request.Target : request.Target[..queryStart];
        var query = ParseQuery(queryStart < 0 ? string.Empty : request.Target[(queryStart + 1)..]);
        if (!TrySplit(path, out var segments))
        {
            return ApiResponse.Error(404, "not_found");
        }

        if (segments is ["mcp"] && _mcp is not null)
        {
            if (request.Method != "POST")
            {
                return ApiResponse.Error(405, "method_not_allowed");
            }

            var reply = await _mcp.HandleAsync(Encoding.UTF8.GetString(request.Body.Span), cancellationToken).ConfigureAwait(false);
            return reply is null ? new ApiResponse(202, null) : new ApiResponse(200, reply);
        }

        if (segments is not ["v1", ..] || !IsKnown(segments))
        {
            return ApiResponse.Error(404, "not_found");
        }

        if (request.Method != "GET")
        {
            return ApiResponse.Error(405, "method_not_allowed");
        }

        if (!TryLimit(query, out var limit))
        {
            return ApiResponse.Error(400, "invalid_limit");
        }

        return segments switch
        {
            ["v1", "sessions"] => ApiResponse.Ok(new { sessions = await _service.ListSessionsAsync(limit, cancellationToken).ConfigureAwait(false) }),
            ["v1", "sessions", var id] => Found(await _service.GetSessionAsync(id, cancellationToken).ConfigureAwait(false)),
            ["v1", "sessions", var id, "transcript"] => Found(await _service.GetTranscriptAsync(id, cancellationToken).ConfigureAwait(false)),
            ["v1", "sessions", var id, "notes"] => Found(await _service.GetNotesAsync(id, cancellationToken).ConfigureAwait(false)),
            _ => await SearchAsync(query.GetValueOrDefault("q"), limit, cancellationToken).ConfigureAwait(false),
        };
    }

    private async Task<ApiResponse> SearchAsync(string? query, int limit, CancellationToken cancellationToken)
    {
        if (!_service.SupportsSearch)
        {
            return ApiResponse.Error(501, "search_unavailable");
        }

        try
        {
            return ApiResponse.Ok(new { hits = await _service.SearchAsync(query, limit, cancellationToken).ConfigureAwait(false) });
        }
        catch (ArgumentException)
        {
            return ApiResponse.Error(400, "invalid_query");
        }
    }

    private static ApiResponse Found(object? value) => value is null ? ApiResponse.Error(404, "not_found") : ApiResponse.Ok(value);

    private static bool IsKnown(string[] segments) => segments is
        ["v1", "sessions"] or ["v1", "sessions", _] or ["v1", "sessions", _, "transcript" or "notes"] or ["v1", "search"];

    private static bool TryLimit(Dictionary<string, string> query, out int limit)
    {
        limit = LocalApiService.DefaultLimit;
        if (!query.TryGetValue("limit", out var text))
        {
            return true;
        }

        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out limit) && limit is >= 1 and <= LocalApiService.MaxLimit;
    }

    /// <summary>Splits and percent-decodes the path. Decoded segments must not contain separators or be dot segments.</summary>
    private static bool TrySplit(string path, out string[] segments)
    {
        segments = [];
        if (!path.StartsWith('/'))
        {
            return false;
        }

        var parts = path[1..].Split('/');
        if (parts is [""])
        {
            return false;
        }

        segments = new string[parts.Length];
        for (var index = 0; index < parts.Length; index++)
        {
            var decoded = Uri.UnescapeDataString(parts[index]);
            if (decoded.Length == 0 || decoded is "." or ".." || decoded.IndexOfAny(['/', '\\', '\0']) >= 0)
            {
                return false;
            }

            segments[index] = decoded;
        }

        return true;
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            var key = Decode(separator < 0 ? pair : pair[..separator]);
            var value = separator < 0 ? string.Empty : Decode(pair[(separator + 1)..]);
            values.TryAdd(key, value);
        }

        return values;
    }

    private static string Decode(string text) => Uri.UnescapeDataString(text.Replace('+', ' '));
}
