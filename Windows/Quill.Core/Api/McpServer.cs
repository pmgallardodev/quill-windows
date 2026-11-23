using System.Text.Json;
using System.Text.Json.Nodes;

namespace Quill.Core.Api;

/// <summary>
/// Minimal Model Context Protocol server (JSON-RPC 2.0 over Streamable HTTP, JSON responses only)
/// exposing read-only tools over <see cref="LocalApiService"/>: <c>list_meetings</c>,
/// <c>get_meeting_summary</c>, <c>get_transcript</c> and, when a search provider exists,
/// <c>search_meetings</c>. Supports <c>initialize</c>, <c>ping</c>, <c>tools/list</c>,
/// <c>tools/call</c> and ignores notifications. No batches (removed in 2025-06-18), no
/// resources, prompts or sampling: nothing here can write, run code or reach the network.
/// </summary>
/// <remarks>
/// Hand-written instead of the ModelContextProtocol NuGet package: that package pulls in
/// ASP.NET Core hosting for HTTP transport and a dependency tree far larger than the four
/// methods Quill needs, and it keeps changing with the spec.
/// </remarks>
public sealed class McpServer
{
    public const string LatestProtocolVersion = "2025-06-18";

    private static readonly string[] SupportedProtocolVersions = [LatestProtocolVersion, "2025-03-26", "2024-11-05"];

    private readonly LocalApiService _service;
    private readonly string _version;

    public McpServer(LocalApiService service, string version)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _version = string.IsNullOrWhiteSpace(version) ? "0.0.0" : version;
    }

    /// <summary>Returns the JSON-RPC response, or null when the message is a notification (HTTP 202).</summary>
    public async Task<string?> HandleAsync(string json, CancellationToken cancellationToken)
    {
        JsonNode? message;
        try
        {
            message = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return Error(null, -32700, "parse error");
        }

        if (message is not JsonObject request || AsString(request["jsonrpc"]) != "2.0" || AsString(request["method"]) is not { } method)
        {
            return Error(Id(message), -32600, "invalid request");
        }

        var id = Id(request);
        if (!request.ContainsKey("id"))
        {
            return null;
        }

        var parameters = request["params"] as JsonObject;
        return method switch
        {
            "initialize" => Result(id, Initialize(parameters)),
            "ping" => Result(id, new JsonObject()),
            "tools/list" => Result(id, new JsonObject { ["tools"] = Tools() }),
            "tools/call" => await CallAsync(id, parameters, cancellationToken).ConfigureAwait(false),
            _ => Error(id, -32601, "method not found"),
        };
    }

    private JsonObject Initialize(JsonObject? parameters)
    {
        var requested = AsString(parameters?["protocolVersion"]);
        return new JsonObject
        {
            ["protocolVersion"] = SupportedProtocolVersions.Contains(requested) ? requested : LatestProtocolVersion,
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
            ["serverInfo"] = new JsonObject { ["name"] = "quill", ["version"] = _version },
            ["instructions"] = "Read-only access to the meetings recorded with Quill on this computer.",
        };
    }

    private JsonArray Tools()
    {
        var tools = new JsonArray
        {
            Tool("list_meetings", "List recorded meetings, newest first.", Schema(("limit", "integer", "Maximum meetings (1-200, default 50)."), required: null)),
            Tool("get_meeting_summary", "Get a meeting's metadata, speakers and final summary.", Schema(("id", "string", "Meeting id from list_meetings."), required: "id")),
            Tool("get_transcript", "Get a meeting's final transcript with speaker names and times in seconds.", Schema(("id", "string", "Meeting id from list_meetings."), required: "id")),
        };
        if (_service.SupportsSearch)
        {
            tools.Add(Tool("search_meetings", "Search across all meetings.", Schema(("query", "string", "What to look for."), required: "query")));
        }

        return tools;
    }

    private async Task<string> CallAsync(JsonNode? id, JsonObject? parameters, CancellationToken cancellationToken)
    {
        var arguments = parameters?["arguments"] as JsonObject ?? [];
        var name = AsString(parameters?["name"]);
        var meetingId = AsString(arguments["id"]);
        object? payload;
        switch (name)
        {
            case "list_meetings":
                var limit = arguments["limit"] is JsonValue value && value.TryGetValue<int>(out var number) ? number : LocalApiService.DefaultLimit;
                payload = new { sessions = await _service.ListSessionsAsync(limit, cancellationToken).ConfigureAwait(false) };
                break;
            case "get_meeting_summary" when meetingId is not null:
                payload = await _service.GetSessionAsync(meetingId, cancellationToken).ConfigureAwait(false);
                break;
            case "get_transcript" when meetingId is not null:
                payload = await _service.GetTranscriptAsync(meetingId, cancellationToken).ConfigureAwait(false);
                break;
            case "search_meetings" when _service.SupportsSearch && AsString(arguments["query"]) is { } query:
                try
                {
                    payload = new { hits = await _service.SearchAsync(query, 20, cancellationToken).ConfigureAwait(false) };
                }
                catch (ArgumentException)
                {
                    return Result(id, ToolResult("The query must have between 1 and 500 characters.", isError: true));
                }

                break;
            default:
                return Error(id, -32602, "unknown tool or missing arguments");
        }

        return payload is null
            ? Result(id, ToolResult("Meeting not found.", isError: true))
            : Result(id, ToolResult(JsonSerializer.Serialize(payload, ApiJson.Options), isError: false));
    }

    private static JsonObject ToolResult(string text, bool isError) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        ["isError"] = isError,
    };

    private static JsonObject Tool(string name, string description, JsonObject schema) => new()
    {
        ["name"] = name,
        ["description"] = description,
        ["inputSchema"] = schema,
        ["annotations"] = new JsonObject { ["readOnlyHint"] = true, ["openWorldHint"] = false },
    };

    private static JsonObject Schema((string Name, string Type, string Description) property, string? required)
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                [property.Name] = new JsonObject { ["type"] = property.Type, ["description"] = property.Description },
            },
        };
        if (required is not null)
        {
            schema["required"] = new JsonArray(JsonValue.Create(required));
        }

        return schema;
    }

    private static string Result(JsonNode? id, JsonNode result) =>
        new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result }.ToJsonString();

    private static string Error(JsonNode? id, int code, string message) =>
        new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } }.ToJsonString();

    /// <summary>A copy of a string or number id; anything else (including a missing id) is null.</summary>
    private static JsonNode? Id(JsonNode? message) =>
        message is JsonObject request && request["id"] is JsonValue value && value.GetValueKind() is JsonValueKind.String or JsonValueKind.Number
            ? value.DeepClone()
            : null;

    private static string? AsString(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
