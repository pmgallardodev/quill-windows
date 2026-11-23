using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Quill.Core.Api;

public static class ApiEventTypes
{
    /// <summary>A recording stopped and its audio is on disk.</summary>
    public const string SessionRecorded = "session.recorded";

    /// <summary>The final transcript (transcript.json) was written.</summary>
    public const string TranscriptCompleted = "transcript.completed";

    /// <summary>The final summary (summary.json) was written.</summary>
    public const string SummaryCompleted = "summary.completed";

    public static IReadOnlyList<string> All { get; } = [SessionRecorded, TranscriptCompleted, SummaryCompleted];
}

/// <summary>
/// Webhook event. Deliberately minimal: it names the meeting but carries none of its content; the
/// receiver fetches what it needs through the API with the token.
/// </summary>
public sealed record ApiEvent(string Id, string Type, string SessionId, DateTimeOffset OccurredAt)
{
    public static ApiEvent Create(string type, string sessionId, DateTimeOffset occurredAt)
    {
        if (!ApiEventTypes.All.Contains(type))
        {
            throw new ArgumentException($"unknown event type \"{type}\"", nameof(type));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return new ApiEvent(Guid.NewGuid().ToString("D"), type, sessionId, occurredAt);
    }

    public string ToJson() => new JsonObject
    {
        ["id"] = Id,
        ["type"] = Type,
        ["sessionId"] = SessionId,
        ["occurredAt"] = OccurredAt.ToString("O"),
    }.ToJsonString();
}

/// <summary>
/// <c>X-Quill-Signature: sha256=&lt;hex&gt;</c>, the HMAC-SHA256 of the exact body bytes keyed with
/// the API token, so a receiver can tell Quill's POSTs from anything else on the machine.
/// </summary>
public static class WebhookSignature
{
    public const string HeaderName = "X-Quill-Signature";

    public static string Compute(string token, ReadOnlySpan<byte> body)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        return "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(token), body));
    }
}

/// <summary>Delivery rules: 3 s per attempt, at most 2 attempts, retry only on network errors or 5xx.</summary>
public static class WebhookPolicy
{
    public const int MaxAttempts = 2;

    /// <summary>Pending deliveries kept in memory; older ones are dropped beyond this.</summary>
    public const int QueueCapacity = 64;

    public static TimeSpan Timeout { get; } = TimeSpan.FromSeconds(3);

    /// <param name="attempt">1-based number of the attempt that just failed.</param>
    /// <param name="statusCode">HTTP status received, or null for a network error or timeout.</param>
    public static bool ShouldRetry(int attempt, int? statusCode) =>
        attempt < MaxAttempts && (statusCode is null || statusCode >= 500);
}
