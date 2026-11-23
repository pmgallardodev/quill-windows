using System.Runtime.CompilerServices;
using Quill.Core.Domain;
using Quill.Core.Ports;
using Quill.Core.Summaries;

namespace Quill.Core.Tests.Summaries;

/// <summary>Records every request and answers JSON calls from a scripted queue or a callback.</summary>
internal sealed class FakeLanguageModel : ILanguageModel
{
    private readonly Queue<string> _answers = new();

    public FakeLanguageModel(int contextTokens = 8192) => ContextTokens = contextTokens;

    public string ModelName { get; init; } = "fake-model";

    public int ContextTokens { get; }

    public List<(LanguageModelRequest Request, string Schema)> JsonCalls { get; } = [];

    /// <summary>Used when the queue is empty.</summary>
    public Func<LanguageModelRequest, string, string>? Respond { get; set; }

    /// <summary>When set, every call throws it.</summary>
    public Exception? Throw { get; set; }

    public FakeLanguageModel Enqueue(params string[] answers)
    {
        foreach (var answer in answers)
        {
            _answers.Enqueue(answer);
        }

        return this;
    }

    public async IAsyncEnumerable<string> GenerateAsync(
        LanguageModelRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        yield return await GenerateJsonAsync(request, "{}", cancellationToken);
    }

    public Task<string> GenerateJsonAsync(LanguageModelRequest request, string jsonSchema, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Throw is not null)
        {
            throw Throw;
        }

        JsonCalls.Add((request, jsonSchema));
        if (_answers.Count > 0)
        {
            return Task.FromResult(_answers.Dequeue());
        }

        return Task.FromResult(Respond?.Invoke(request, jsonSchema) ?? "{\"executiveSummary\":\"\"}");
    }
}

/// <summary>Runs jobs inline and records priorities. <see cref="Busy"/> makes TryRunAsync decline.</summary>
internal sealed class FakeScheduler : IInferenceScheduler
{
    public List<InferencePriority> Ran { get; } = [];

    public List<InferencePriority> Declined { get; } = [];

    public bool Busy { get; set; }

    public int PendingCount => Busy ? 1 : 0;

    public async Task<T> RunAsync<T>(
        InferencePriority priority,
        Func<CancellationToken, Task<T>> job,
        CancellationToken cancellationToken = default)
    {
        Ran.Add(priority);
        return await job(cancellationToken);
    }

    public async Task<(bool Ran, T? Result)> TryRunAsync<T>(
        InferencePriority priority,
        Func<CancellationToken, Task<T>> job,
        CancellationToken cancellationToken = default)
    {
        if (Busy)
        {
            Declined.Add(priority);
            return (false, default);
        }

        Ran.Add(priority);
        return (true, await job(cancellationToken));
    }
}

internal sealed class FixedClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset Now { get; set; } = now;
}

/// <summary>In-memory store with just what the summary use cases need.</summary>
internal sealed class FakeSessionStore : ISessionStore
{
    public Dictionary<(string, TranscriptPass), IReadOnlyList<TranscriptSegment>> Transcripts { get; } = [];

    public Dictionary<(string, SummaryKind), MeetingSummary> Summaries { get; } = [];

    public Dictionary<string, string> Notes { get; } = [];

    public int SaveCount { get; private set; }

    public Task<IReadOnlyList<Session>> ListSessionsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Session>>([]);

    public Task<Session?> LoadSessionAsync(string sessionId, CancellationToken cancellationToken) =>
        Task.FromResult<Session?>(null);

    public Task<IReadOnlyList<TranscriptSegment>> LoadTranscriptAsync(
        string sessionId,
        TranscriptPass pass,
        CancellationToken cancellationToken) =>
        Task.FromResult(Transcripts.GetValueOrDefault((sessionId, pass)) ?? []);

    public Task SaveTranscriptAsync(
        string sessionId,
        TranscriptPass pass,
        TranscriptInfo info,
        IReadOnlyList<TranscriptSegment> segments,
        CancellationToken cancellationToken)
    {
        Transcripts[(sessionId, pass)] = segments;
        return Task.CompletedTask;
    }

    public Task<MeetingSummary?> LoadSummaryAsync(string sessionId, SummaryKind kind, CancellationToken cancellationToken) =>
        Task.FromResult(Summaries.GetValueOrDefault((sessionId, kind)));

    public Task SaveSummaryAsync(string sessionId, MeetingSummary summary, CancellationToken cancellationToken)
    {
        SaveCount++;
        Summaries[(sessionId, summary.Meta.Kind)] = summary;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Speaker>> LoadSpeakersAsync(string sessionId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Speaker>>([]);

    public Task SaveSpeakersAsync(string sessionId, IReadOnlyList<Speaker> speakers, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task<string> LoadNotesAsync(string sessionId, CancellationToken cancellationToken) =>
        Task.FromResult(Notes.GetValueOrDefault(sessionId) ?? string.Empty);

    public Task SaveNotesAsync(string sessionId, string markdown, CancellationToken cancellationToken)
    {
        Notes[sessionId] = markdown;
        return Task.CompletedTask;
    }
}

internal static class Segments
{
    public static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    public static TranscriptSegment Line(double start, double end, string text, string speaker = SpeakerIds.Me) =>
        new(S(start), S(end), text, speaker, speaker == SpeakerIds.Me ? Track.Microphone : Track.System);

    /// <summary>Summary JSON as a model would answer it.</summary>
    public static string Json(MeetingSummary summary) => MeetingSummaryJson.Serialize(summary);
}
