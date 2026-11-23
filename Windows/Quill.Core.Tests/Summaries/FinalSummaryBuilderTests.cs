using Quill.Core.Domain;
using Quill.Core.Ports;
using Quill.Core.Summaries;
using Xunit;
using static Quill.Core.Tests.Summaries.Segments;

namespace Quill.Core.Tests.Summaries;

public sealed class FinalSummaryBuilderTests
{
    private const string SessionId = "2026-11-23_10-00-00";
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 11, 0, 0, TimeSpan.FromHours(1));

    private readonly FakeSessionStore _store = new();
    private readonly FakeScheduler _scheduler = new();
    private readonly FixedClock _clock = new(Now);

    private FinalSummaryBuilder Builder(FakeLanguageModel model) => new(model, _scheduler, _store, _clock);

    private void GivenTranscript(params TranscriptSegment[] segments) =>
        _store.Transcripts[(SessionId, TranscriptPass.Final)] = segments;

    [Fact]
    public async Task ShortTranscriptIsOneBackgroundCallValidatedAndSavedWithMeta()
    {
        GivenTranscript(
            Line(10, 20, "Decidimos lanzar el lunes."),
            Line(30, 40, "Yo preparo la demo.", "speaker-1"));
        var model = new FakeLanguageModel { ModelName = "qwen3-4b" }.Enqueue(Json(new MeetingSummary
        {
            ExecutiveSummary = "Se decide lanzar el lunes.",
            Decisions = [new Decision("Lanzar el lunes", SpeakerIds.Me, S(10))],
            ActionItems = [new ActionItem("Preparar demo", "speaker-1", null, false, S(30)), new ActionItem("Inventada", null, null, false, S(500))],
        }));
        var template = new Template("cliente", "", null, SummaryLevel.Detailed, [SummarySections.Decisions, SummarySections.ActionItems], "Cuerpo");

        var outcome = await Builder(model).BuildAsync(SessionId, new SummaryOptions { Template = template });

        Assert.Equal(SummaryStatus.Completed, outcome.Status);
        var call = Assert.Single(model.JsonCalls);
        Assert.Equal(MeetingSummarySchema.For(template.Sections), call.Schema);
        Assert.Contains("[00:00:10] me: Decidimos lanzar el lunes.", call.Request.UserPrompt, StringComparison.Ordinal);
        Assert.Equal([InferencePriority.Background], _scheduler.Ran);

        var saved = _store.Summaries[(SessionId, SummaryKind.Final)];
        Assert.Same(outcome.Summary, saved);
        Assert.Equal(["Preparar demo"], saved.ActionItems.Select(a => a.Text));
        Assert.Equal("Inventada", Assert.Single(outcome.Dropped).Text);
        Assert.Equal(
            new SummaryMeta { Template = "cliente", Language = "es", Level = SummaryLevel.Detailed, Model = "qwen3-4b", GeneratedAt = Now, Kind = SummaryKind.Final },
            saved.Meta);
    }

    [Fact]
    public async Task OwnerNotesFromTheSessionReachThePrompt()
    {
        GivenTranscript(Line(10, 20, "Hola"));
        _store.Notes[SessionId] = "- [00:00:12] Importante: precio cerrado\n";
        var model = new FakeLanguageModel();

        await Builder(model).BuildAsync(SessionId, new SummaryOptions());

        Assert.Contains("[00:00:12] Importante: precio cerrado", model.JsonCalls[0].Request.SystemPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LongTranscriptIsSplitSummarizedPerPartThenMergedAndDeduplicated()
    {
        // ~40 tokens per line and a tiny context: forces several map chunks.
        var lines = Enumerable.Range(0, 40)
            .Select(i => Line(i * 10, (i * 10) + 9, $"Frase número {i} " + new string('x', 120)))
            .ToArray();
        GivenTranscript(lines);
        var model = new FakeLanguageModel(contextTokens: 2600)
        {
            Respond = (request, _) => request.SystemPrompt.Contains("Fusiona", StringComparison.Ordinal)
                ? Json(new MeetingSummary
                {
                    ExecutiveSummary = "Resumen fusionado.",
                    Decisions = [new Decision("Usar Postgres", null, S(0)), new Decision("usar postgres.", null, S(200))],
                })
                : Json(new MeetingSummary { ExecutiveSummary = "Parcial.", Decisions = [new Decision("Usar Postgres", null, S(0))] }),
        };

        var outcome = await Builder(model).BuildAsync(SessionId, new SummaryOptions());

        var partials = model.JsonCalls.Where(c => !c.Request.SystemPrompt.Contains("Fusiona", StringComparison.Ordinal)).ToList();
        var merges = model.JsonCalls.Except(partials).ToList();
        Assert.True(partials.Count >= 2, $"expected several parts, got {partials.Count}");
        Assert.NotEmpty(merges);
        Assert.All(partials, c => Assert.Contains($"de {partials.Count}", c.Request.UserPrompt, StringComparison.Ordinal));
        Assert.Contains("Parcial.", merges[^1].Request.UserPrompt, StringComparison.Ordinal);
        Assert.All(_scheduler.Ran, p => Assert.Equal(InferencePriority.Background, p));
        Assert.Equal(model.JsonCalls.Count, _scheduler.Ran.Count);

        // Every line of the transcript was sent exactly once across the parts.
        var sent = string.Concat(partials.Select(c => c.Request.UserPrompt));
        Assert.All(lines, l => Assert.Single(System.Text.RegularExpressions.Regex.Matches(sent, $"Frase número {l.Text.Split(' ')[2]} ")));

        Assert.Equal(SummaryStatus.Completed, outcome.Status);
        Assert.Equal("Resumen fusionado.", outcome.Summary!.ExecutiveSummary);
        Assert.Equal("Usar Postgres", Assert.Single(outcome.Summary.Decisions).Text);
    }

    [Fact]
    public async Task EveryRequestFitsTheModelContext()
    {
        var lines = Enumerable.Range(0, 200).Select(i => Line(i * 5, (i * 5) + 4, new string('a', 200))).ToArray();
        GivenTranscript(lines);
        var model = new FakeLanguageModel(contextTokens: 4096);

        await Builder(model).BuildAsync(SessionId, new SummaryOptions());

        Assert.All(model.JsonCalls, c =>
        {
            var used = Quill.Core.Transcripts.TranscriptChunker.EstimateTokens(c.Request.SystemPrompt + c.Request.UserPrompt) + c.Request.MaxOutputTokens;
            Assert.True(used <= model.ContextTokens, $"request uses {used} of {model.ContextTokens}");
        });
    }

    [Fact]
    public async Task EmptyTranscriptReportsNoTranscriptWithoutCallingTheModel()
    {
        var model = new FakeLanguageModel();

        var outcome = await Builder(model).BuildAsync(SessionId, new SummaryOptions());

        Assert.Equal(SummaryStatus.NoTranscript, outcome.Status);
        Assert.Empty(model.JsonCalls);
        Assert.Equal(0, _store.SaveCount);
    }

    [Fact]
    public async Task MissingAiPackIsReportedNotThrown()
    {
        GivenTranscript(Line(0, 5, "Hola"));
        var model = new FakeLanguageModel { Throw = new LanguageModelUnavailableException("no pack") };

        var outcome = await Builder(model).BuildAsync(SessionId, new SummaryOptions());

        Assert.Equal(SummaryStatus.AiPackMissing, outcome.Status);
        Assert.Equal(0, _store.SaveCount);
    }

    [Fact]
    public async Task InvalidJsonIsRetriedOnceThenReportedAsFailure()
    {
        GivenTranscript(Line(0, 5, "Hola"));
        var recovered = new FakeLanguageModel().Enqueue("not json", Json(new MeetingSummary { ExecutiveSummary = "ok" }));
        var broken = new FakeLanguageModel().Enqueue("not json", "{\"decisions\":[{\"text\":\"x\",\"at\":\"later\"}]}");

        var ok = await Builder(recovered).BuildAsync(SessionId, new SummaryOptions());
        var failed = await Builder(broken).BuildAsync(SessionId, new SummaryOptions());

        Assert.Equal(SummaryStatus.Completed, ok.Status);
        Assert.Equal(2, recovered.JsonCalls.Count);
        Assert.Equal(SummaryStatus.Failed, failed.Status);
        Assert.NotNull(failed.Error);
        Assert.Equal(1, _store.SaveCount);
    }

    [Fact]
    public async Task RegeneratingKeepsDoneFlagsBacksUpThePreviousSummaryAndClearsOutdated()
    {
        GivenTranscript(Line(30, 40, "Yo preparo la demo.", "speaker-1"), Line(50, 60, "Y reservo la sala."));
        var previous = new MeetingSummary
        {
            ExecutiveSummary = "Antes",
            ActionItems = [new ActionItem("Preparar la demo", "speaker-1", null, false, S(30), Done: true)],
        };
        _store.Summaries[(SessionId, SummaryKind.Final)] = previous;
        var state = new FakeSummaryStateStore();
        state.States[SessionId] = new SummaryState { Outdated = true, Reason = "retry" };
        var model = new FakeLanguageModel().Enqueue(Json(new MeetingSummary
        {
            ExecutiveSummary = "Después",
            ActionItems =
            [
                new ActionItem("Preparar demo", "speaker-1", null, false, S(30)),
                new ActionItem("Reservar sala", SpeakerIds.Me, null, true, S(50)),
            ],
        }));

        var outcome = await new FinalSummaryBuilder(model, _scheduler, _store, _clock, state)
            .BuildAsync(SessionId, new SummaryOptions());

        Assert.Equal(SummaryStatus.Completed, outcome.Status);
        var saved = _store.Summaries[(SessionId, SummaryKind.Final)];
        Assert.Same(outcome.Summary, saved);
        Assert.Equal("Después", saved.ExecutiveSummary);
        Assert.Equal([true, false], saved.ActionItems.Select(a => a.Done));
        Assert.Same(previous, state.Backups[SessionId]);
        Assert.False((await state.LoadStateAsync(SessionId, CancellationToken.None)).Outdated);
    }

    [Fact]
    public async Task FirstSummaryMakesNoBackup()
    {
        GivenTranscript(Line(10, 20, "Hola"));
        var state = new FakeSummaryStateStore();
        var model = new FakeLanguageModel().Enqueue(Json(new MeetingSummary { ExecutiveSummary = "ok" }));

        var outcome = await new FinalSummaryBuilder(model, _scheduler, _store, _clock, state)
            .BuildAsync(SessionId, new SummaryOptions());

        Assert.Equal(SummaryStatus.Completed, outcome.Status);
        Assert.Empty(state.Backups);
    }
}
