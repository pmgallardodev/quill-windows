using System.Text.Json;
using System.Text.Json.Nodes;

namespace Quill.Core.Api;

/// <summary>One meeting in <c>GET /v1/sessions</c>.</summary>
public sealed record ApiSessionItem(string Id, string Title, DateTimeOffset Start, double DurationSeconds, bool HasSummary);

/// <summary>Speaker of a meeting with its display name ("Ana") or id ("speaker-2") when unnamed.</summary>
public sealed record ApiSpeaker(string Id, string Name, string Track);

/// <summary><c>GET /v1/sessions/{id}</c>: metadata, speakers and the final summary (null when there is none).</summary>
public sealed record ApiSessionDetail(
    string Id,
    string Title,
    DateTimeOffset Start,
    DateTimeOffset? End,
    double DurationSeconds,
    IReadOnlyList<ApiSpeaker> Speakers,
    JsonNode? Summary);

/// <summary>One transcript utterance; times are seconds from the meeting start.</summary>
public sealed record ApiSegment(double Start, double End, string SpeakerId, string Speaker, string Track, string Text);

public sealed record ApiTranscript(string SessionId, IReadOnlyList<ApiSegment> Segments);

public sealed record ApiNotes(string SessionId, string Markdown);

/// <param name="At">Seconds from the meeting start the hit comes from, when known.</param>
/// <param name="Score">Relevance, higher is better.</param>
public sealed record ApiSearchHit(string SessionId, string? Title, string Text, double? At, double Score);

/// <summary>
/// Search hook for <c>GET /v1/search</c> and the MCP <c>search_meetings</c> tool. Optional: without
/// an implementation the endpoint answers 501 and the tool is not listed. F10 (local semantic
/// search) is expected to provide the implementation.
/// </summary>
public interface IApiSearch
{
    Task<IReadOnlyList<ApiSearchHit>> SearchAsync(string query, int limit, CancellationToken cancellationToken);
}

/// <summary>
/// Transport-agnostic request: method, raw request target (path + query), body bytes and the
/// request headers (lower-case names; null when the caller has none).
/// </summary>
public sealed record ApiRequest(string Method, string Target, ReadOnlyMemory<byte> Body, IReadOnlyDictionary<string, string>? Headers = null)
{
    public string? Header(string name) => Headers?.GetValueOrDefault(name);
}

/// <summary>Status code, JSON body (null for bodiless answers such as 202) and extra response headers.</summary>
public sealed record ApiResponse(int Status, string? Json, IReadOnlyList<KeyValuePair<string, string>>? Headers = null)
{
    internal static ApiResponse Ok(object value) => new(200, JsonSerializer.Serialize(value, ApiJson.Options));

    internal static ApiResponse Error(int status, string code) =>
        new(status, new JsonObject { ["error"] = code }.ToJsonString());
}

internal static class ApiJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}
