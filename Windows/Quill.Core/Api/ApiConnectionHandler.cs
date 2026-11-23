using System.Net;
using System.Text.Json.Nodes;

namespace Quill.Core.Api;

/// <summary>
/// Serves exactly one request on an accepted connection: strict parse (<see cref="HttpWire"/>),
/// security checks (<see cref="ApiRequestGuard"/>) before the body is read, JSON-only POSTs, then
/// <see cref="LocalApiRouter"/>. Transport-agnostic (any <see cref="Stream"/>), so the socket
/// listener stays a thin host. Never answers with CORS headers and never echoes exception text.
/// </summary>
public sealed class ApiConnectionHandler
{
    private static readonly KeyValuePair<string, string>[] Challenge = [new("WWW-Authenticate", "Bearer")];

    private readonly LocalApiRouter _router;
    private readonly int _port;
    private readonly string _token;

    public ApiConnectionHandler(LocalApiRouter router, int port, string token)
    {
        _router = router ?? throw new ArgumentNullException(nameof(router));
        _port = port;
        _token = ApiToken.IsWellFormed(token) ? token : throw new ArgumentException("token is not well formed", nameof(token));
    }

    public async Task HandleAsync(Stream stream, IPAddress? remote, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ApiResponse response;
        IEnumerable<KeyValuePair<string, string>>? headers = null;
        try
        {
            if (!LoopbackUrl.IsLoopback(remote))
            {
                response = ApiResponse.Error(403, "forbidden");
            }
            else
            {
                var head = await HttpWire.ReadHeadAsync(stream, cancellationToken).ConfigureAwait(false);
                var verdict = ApiRequestGuard.Check(remote, head.Header("host"), head.Header("origin"), head.Header("authorization"), _port, _token);
                if (verdict != ApiGuardVerdict.Allowed)
                {
                    response = ApiResponse.Error(ApiRequestGuard.StatusFor(verdict), verdict == ApiGuardVerdict.Unauthorized ? "unauthorized" : "forbidden");
                    headers = verdict == ApiGuardVerdict.Unauthorized ? Challenge : null;
                }
                else
                {
                    response = await RouteAsync(stream, head, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (HttpWireException exception)
        {
            response = ApiResponse.Error(exception.Status, "bad_request");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Deliberately generic: paths and messages stay inside the process.
            response = ApiResponse.Error(500, "internal_error");
        }

        await HttpWire.WriteResponseAsync(stream, response.Status, response.Json, headers, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ApiResponse> RouteAsync(Stream stream, HttpRequestHead head, CancellationToken cancellationToken)
    {
        if (head.Method is not ("GET" or "POST"))
        {
            return ApiResponse.Error(405, "method_not_allowed");
        }

        var body = ReadOnlyMemory<byte>.Empty;
        if (head.Method == "POST")
        {
            var contentType = head.Header("content-type");
            if (contentType is null || !contentType.Split(';')[0].Trim().Equals("application/json", StringComparison.OrdinalIgnoreCase))
            {
                return ApiResponse.Error(415, "json_only");
            }

            body = await HttpWire.ReadBodyAsync(stream, head, cancellationToken).ConfigureAwait(false);
        }

        return await _router.HandleAsync(new ApiRequest(head.Method, head.Target, body), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Body for a 500 written by the host when even the handler failed.</summary>
    public static string InternalErrorJson { get; } = new JsonObject { ["error"] = "internal_error" }.ToJsonString();
}
