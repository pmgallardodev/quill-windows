using Quill.Core.Api;
using Quill.Core.Domain;
using Quill.Core.Ports;

namespace Quill.Core.Tests.Api;

/// <summary>In-memory <see cref="ISessionStore"/> with two sessions: "s1" (summary, speakers, notes) and "s2" (bare).</summary>
internal sealed class FakeApiStore : ISessionStore
{
    public static readonly DateTimeOffset Start1 = new(2026, 11, 20, 10, 0, 0, TimeSpan.Zero);
    public static readonly DateTimeOffset Start2 = new(2026, 11, 21, 9, 0, 0, TimeSpan.Zero);

    private readonly Dictionary<string, Session> _sessions = new()
    {
        ["s1"] = new Session("s1", "/r/s1", "Planning", Start1, Start1.AddMinutes(30), [], []),
        ["s2"] = new Session("s2", "/r/s2", null, Start2, null, [], []),
    };

    public static MeetingSummary Summary { get; } = new()
    {
        ExecutiveSummary = "We agreed to ship.",
        Decisions = [new Decision("Ship on Friday", "Ana", TimeSpan.FromSeconds(65))],
    };

    public Task<IReadOnlyList<Session>> ListSessionsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Session>>(_sessions.Values.OrderByDescending(session => session.Started).ToList());

    public Task<Session?> LoadSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        Validate(sessionId);
        return Task.FromResult(_sessions.GetValueOrDefault(sessionId));
    }

    public Task<IReadOnlyList<TranscriptSegment>> LoadTranscriptAsync(string sessionId, TranscriptPass pass, CancellationToken cancellationToken)
    {
        Validate(sessionId);
        IReadOnlyList<TranscriptSegment> segments = sessionId == "s1" && pass == TranscriptPass.Final
            ? [
                new TranscriptSegment(TimeSpan.Zero, TimeSpan.FromSeconds(2.5), "Hola", "me", Track.Microphone),
                new TranscriptSegment(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5), "Buenas", "speaker-1", Track.System),
                new TranscriptSegment(TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(7), "Vale", "speaker-2", Track.System),
            ]
            : [];
        return Task.FromResult(segments);
    }

    public Task<MeetingSummary?> LoadSummaryAsync(string sessionId, SummaryKind kind, CancellationToken cancellationToken)
    {
        Validate(sessionId);
        return Task.FromResult(sessionId == "s1" && kind == SummaryKind.Final ? Summary : null);
    }

    public Task<IReadOnlyList<Speaker>> LoadSpeakersAsync(string sessionId, CancellationToken cancellationToken)
    {
        Validate(sessionId);
        IReadOnlyList<Speaker> speakers = sessionId == "s1"
            ? [new Speaker("me", Track.Microphone), new Speaker("speaker-1", Track.System, "Ana")]
            : [];
        return Task.FromResult(speakers);
    }

    public Task<string> LoadNotesAsync(string sessionId, CancellationToken cancellationToken)
    {
        Validate(sessionId);
        return Task.FromResult(sessionId == "s1" ? "- [00:00:10] revisar presupuesto\n" : string.Empty);
    }

    public Task SaveTranscriptAsync(string sessionId, TranscriptPass pass, TranscriptInfo info, IReadOnlyList<TranscriptSegment> segments, CancellationToken cancellationToken) =>
        throw new NotSupportedException("the API is read-only");

    public Task SaveSummaryAsync(string sessionId, MeetingSummary summary, CancellationToken cancellationToken) =>
        throw new NotSupportedException("the API is read-only");

    public Task SaveSpeakersAsync(string sessionId, IReadOnlyList<Speaker> speakers, CancellationToken cancellationToken) =>
        throw new NotSupportedException("the API is read-only");

    public Task SaveNotesAsync(string sessionId, string markdown, CancellationToken cancellationToken) =>
        throw new NotSupportedException("the API is read-only");

    private static void Validate(string sessionId)
    {
        if (sessionId.Contains('/') || sessionId.Contains('\\') || sessionId is "." or "..")
        {
            throw new ArgumentException($"invalid session id \"{sessionId}\"", nameof(sessionId));
        }
    }
}

internal sealed class FakeSearch : IApiSearch
{
    public string? LastQuery { get; private set; }

    public int LastLimit { get; private set; }

    public Task<IReadOnlyList<ApiSearchHit>> SearchAsync(string query, int limit, CancellationToken cancellationToken)
    {
        LastQuery = query;
        LastLimit = limit;
        return Task.FromResult<IReadOnlyList<ApiSearchHit>>([new ApiSearchHit("s1", "Planning", "Ship on Friday", 65, 0.9)]);
    }
}
