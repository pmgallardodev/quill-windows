using Quill.Core.Domain;
using Quill.Core.Ports;
using Quill.Core.Summaries;
using Xunit;
using static Quill.Core.Tests.Summaries.Segments;

namespace Quill.Core.Tests.Summaries;

public sealed class CatchUpSummarizerTests
{
    private readonly FakeScheduler _scheduler = new();

    private static readonly IReadOnlyList<TranscriptSegment> Transcript =
    [
        Line(0, 60, "Al principio hablamos del tiempo."),
        Line(400, 460, "Decidimos contratar a Ana.", "speaker-1"),
        Line(600, 640, "¿Quién revisa el contrato?"),
    ];

    [Fact]
    public void DefaultWindowIsFiveMinutes() => Assert.Equal(TimeSpan.FromMinutes(5), CatchUpSummarizer.DefaultWindow);

    [Fact]
    public async Task RecapsOnlyTheLastMinutesAtInteractivePriority()
    {
        var model = new FakeLanguageModel().Enqueue(Json(new MeetingSummary
        {
            ExecutiveSummary = "Se decidió contratar a Ana.",
            Decisions = [new Decision("Contratar a Ana", "speaker-1", S(400)), new Decision("Vieja", null, S(10))],
            OpenQuestions = [new OpenQuestion("¿Quién revisa el contrato?", S(600))],
        }));
        var catchUp = new CatchUpSummarizer(model, _scheduler);

        var outcome = await catchUp.SummarizeAsync(Transcript, new SummaryOptions(), now: S(640));

        var call = Assert.Single(model.JsonCalls);
        Assert.Equal([InferencePriority.Interactive], _scheduler.Ran);
        Assert.Contains("[00:06:40] speaker-1: Decidimos contratar a Ana.", call.Request.UserPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Al principio", call.Request.UserPrompt, StringComparison.Ordinal);
        Assert.Contains("5 minutos", call.Request.UserPrompt, StringComparison.Ordinal);
        Assert.Equal(MeetingSummarySchema.For(SummaryKind.Live), call.Schema);

        Assert.Equal(SummaryStatus.Completed, outcome.Status);
        Assert.Equal(["Contratar a Ana"], outcome.Summary!.Decisions.Select(d => d.Text));
        Assert.Equal("Vieja", Assert.Single(outcome.Dropped).Text);
        Assert.Equal("es", outcome.Summary.Meta.Language);
    }

    [Fact]
    public async Task NothingSaidInTheWindowNeedsNoModel()
    {
        var model = new FakeLanguageModel();

        var outcome = await new CatchUpSummarizer(model, _scheduler)
            .SummarizeAsync(Transcript, new SummaryOptions(), window: TimeSpan.FromMinutes(1), now: S(2000));

        Assert.Equal(SummaryStatus.NoTranscript, outcome.Status);
        Assert.Empty(model.JsonCalls);
    }

    [Fact]
    public async Task MissingAiPackIsReported()
    {
        var model = new FakeLanguageModel { Throw = new LanguageModelUnavailableException("no pack") };

        var outcome = await new CatchUpSummarizer(model, _scheduler).SummarizeAsync(Transcript, new SummaryOptions());

        Assert.Equal(SummaryStatus.AiPackMissing, outcome.Status);
    }
}
