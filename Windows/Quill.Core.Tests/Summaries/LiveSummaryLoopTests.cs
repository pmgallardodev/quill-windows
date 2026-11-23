using Quill.Core.Domain;
using Quill.Core.Ports;
using Quill.Core.Summaries;
using Xunit;
using static Quill.Core.Tests.Summaries.Segments;

namespace Quill.Core.Tests.Summaries;

public sealed class LiveSummaryLoopTests
{
    private const string SessionId = "live-session";

    private readonly FakeSessionStore _store = new();
    private readonly FakeScheduler _scheduler = new();
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 11, 23, 10, 0, 0, TimeSpan.Zero));

    private async Task<LiveSummaryLoop> StartedLoop(FakeLanguageModel model, TimeSpan? interval = null)
    {
        var loop = new LiveSummaryLoop(model, _scheduler, _store, _clock, interval ?? TimeSpan.FromMinutes(3));
        await loop.StartAsync(SessionId, new SummaryOptions());
        return loop;
    }

    /// <summary>One minute of continuous speech starting at <paramref name="minute"/>.</summary>
    private static TranscriptSegment Minute(int minute, string text) => Line(minute * 60, (minute * 60) + 60, text);

    [Fact]
    public async Task WaitsUntilTheIntervalOfNewSpeechHasAccumulated()
    {
        var model = new FakeLanguageModel();
        var loop = await StartedLoop(model);

        var state = await loop.AddSegmentsAsync([Minute(0, "uno"), Minute(1, "dos")]);

        Assert.Equal(LiveSummaryState.Waiting, state);
        Assert.Equal(TimeSpan.FromMinutes(2), loop.PendingSpeech);
        Assert.Empty(model.JsonCalls);
    }

    [Fact]
    public async Task SummarizesAtTheIntervalWithLiveSectionsPriorityAndSaves()
    {
        var model = new FakeLanguageModel { ModelName = "m" }.Enqueue(Json(new MeetingSummary
        {
            ExecutiveSummary = "Se habló del plan.",
            Decisions = [new Decision("Plan A", null, S(65))],
            OpenQuestions = [new OpenQuestion("Inventada", S(9999))],
        }));
        var loop = await StartedLoop(model);
        LiveSummaryUpdatedEventArgs? raised = null;
        loop.Updated += (_, e) => raised = e;

        var state = await loop.AddSegmentsAsync([Minute(0, "uno"), Minute(1, "dos"), Minute(2, "tres")]);

        Assert.Equal(LiveSummaryState.UpToDate, state);
        var call = Assert.Single(model.JsonCalls);
        Assert.Equal(MeetingSummarySchema.For(SummaryKind.Live), call.Schema);
        Assert.Equal([InferencePriority.LiveSummary], _scheduler.Ran);
        var saved = _store.Summaries[(SessionId, SummaryKind.Live)];
        Assert.Equal(SummaryKind.Live, saved.Meta.Kind);
        Assert.Equal("m", saved.Meta.Model);
        Assert.Empty(saved.OpenQuestions);
        Assert.NotNull(raised);
        Assert.Same(saved, raised!.Summary);
        Assert.Same(saved, loop.Current);
        Assert.Equal(TimeSpan.Zero, loop.PendingSpeech);
    }

    [Fact]
    public async Task SkipsWhenTheSchedulerIsBusyAndCatchesUpLater()
    {
        var model = new FakeLanguageModel();
        var loop = await StartedLoop(model);
        var states = new List<LiveSummaryState>();
        loop.StateChanged += (_, s) => states.Add(s);
        _scheduler.Busy = true;

        var delayed = await loop.AddSegmentsAsync([Minute(0, "uno"), Minute(1, "dos"), Minute(2, "tres")]);

        Assert.Equal(LiveSummaryState.Delayed, delayed);
        Assert.Equal(LiveSummaryState.Delayed, loop.State);
        Assert.Contains(LiveSummaryState.Delayed, states);
        Assert.Equal([InferencePriority.LiveSummary], _scheduler.Declined);
        Assert.Empty(model.JsonCalls);
        Assert.Equal(0, _store.SaveCount);

        _scheduler.Busy = false;
        var state = await loop.AddSegmentsAsync([Minute(3, "cuatro")]);

        Assert.Equal(LiveSummaryState.UpToDate, state);
        var prompt = Assert.Single(model.JsonCalls).Request.UserPrompt;
        Assert.Contains("me: uno", prompt, StringComparison.Ordinal);
        Assert.Contains("me: cuatro", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SecondUpdateIsIncrementalPreviousSummaryPlusOnlyNewLines()
    {
        var model = new FakeLanguageModel().Enqueue(
            Json(new MeetingSummary { ExecutiveSummary = "Primer bloque.", Decisions = [new Decision("Plan A", null, S(10))] }),
            Json(new MeetingSummary { ExecutiveSummary = "Dos bloques.", Decisions = [new Decision("Plan A", null, S(10))] }));
        var loop = await StartedLoop(model, TimeSpan.FromMinutes(1));

        await loop.AddSegmentsAsync([Minute(0, "primero")]);
        await loop.AddSegmentsAsync([Minute(1, "segundo")]);

        var second = model.JsonCalls[1].Request.UserPrompt;
        Assert.Contains("Primer bloque.", second, StringComparison.Ordinal);
        Assert.Contains("me: segundo", second, StringComparison.Ordinal);
        Assert.DoesNotContain("me: primero", second, StringComparison.Ordinal);
        // The decision cites the first block: still valid against the whole live transcript.
        Assert.Single(loop.Current!.Decisions);
    }

    [Fact]
    public async Task MissingAiPackStopsTryingAndIsExposed()
    {
        var model = new FakeLanguageModel(contextTokens: 4096) { Throw = new LanguageModelUnavailableException("no pack") };
        var loop = await StartedLoop(model, TimeSpan.FromMinutes(1));

        var first = await loop.AddSegmentsAsync([Minute(0, "uno")]);
        model.Throw = null;
        var second = await loop.AddSegmentsAsync([Minute(1, "dos")]);

        Assert.Equal(LiveSummaryState.AiPackMissing, first);
        Assert.Equal(LiveSummaryState.AiPackMissing, second);
        Assert.Empty(model.JsonCalls);
    }

    [Fact]
    public async Task ResumesFromASavedLiveSummary()
    {
        _store.Summaries[(SessionId, SummaryKind.Live)] = new MeetingSummary
        {
            ExecutiveSummary = "Guardado antes.",
            Meta = new SummaryMeta { Kind = SummaryKind.Live },
        };
        _store.Transcripts[(SessionId, TranscriptPass.Live)] = [Minute(0, "viejo")];
        var model = new FakeLanguageModel();
        var loop = await StartedLoop(model, TimeSpan.FromMinutes(1));
        Assert.Equal("Guardado antes.", loop.Current?.ExecutiveSummary);

        await loop.AddSegmentsAsync([Minute(1, "nuevo")]);

        var prompt = Assert.Single(model.JsonCalls).Request.UserPrompt;
        Assert.Contains("Guardado antes.", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("me: viejo", prompt, StringComparison.Ordinal);
    }
}
