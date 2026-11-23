using Quill.Core.Domain;
using Quill.Core.Summaries;
using Xunit;

namespace Quill.Core.Tests.Summaries;

public sealed class CitationValidatorTests
{
    private static readonly IReadOnlyList<TranscriptSegment> Transcript =
    [
        new(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20), "hola", SpeakerIds.Me, Track.Microphone),
        new(TimeSpan.FromSeconds(60.4), TimeSpan.FromSeconds(70), "adios", "speaker-1", Track.System),
    ];

    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    [Fact]
    public void KeepsItemsInsideSegmentsAndDropsTheRest()
    {
        var summary = new MeetingSummary
        {
            ExecutiveSummary = "x",
            Decisions = [new Decision("ok", null, S(15)), new Decision("invented", null, S(40))],
            ActionItems = [new ActionItem("ok", null, null, false, S(65)), new ActionItem("late", null, null, false, S(500))],
            OpenQuestions = [new OpenQuestion("before", S(2))],
            Topics = [new Topic("ok", S(10)), new Topic("gap", S(30))],
            Risks = [new Risk("ok", S(20))],
            KeyFacts = [new KeyFact("gap", S(45))],
            NextSteps = ["kept, not cited"],
        };

        var result = CitationValidator.Validate(summary, Transcript);

        Assert.Equal(["ok"], result.Summary.Decisions.Select(d => d.Text));
        Assert.Equal(["ok"], result.Summary.ActionItems.Select(a => a.Text));
        Assert.Empty(result.Summary.OpenQuestions);
        Assert.Equal(["ok"], result.Summary.Topics.Select(t => t.Title));
        Assert.Single(result.Summary.Risks);
        Assert.Empty(result.Summary.KeyFacts);
        Assert.Equal(summary.NextSteps, result.Summary.NextSteps);
        Assert.Equal(summary.ExecutiveSummary, result.Summary.ExecutiveSummary);

        Assert.Equal(5, result.Dropped.Count);
        Assert.Contains(result.Dropped, d => d.Section == SummarySections.Decisions && d.Text == "invented" && d.At == S(40));
        Assert.Contains(result.Dropped, d => d.Section == SummarySections.Topics && d.Text == "gap");
        Assert.True(result.HasDropped);
    }

    [Fact]
    public void SecondPrecisionCitationOfFractionalSegmentStartIsKept()
    {
        // The model only sees whole seconds: "00:01:00" cites a segment starting at 60.4 s.
        var summary = new MeetingSummary { KeyFacts = [new KeyFact("fact", S(60))] };

        var result = CitationValidator.Validate(summary, Transcript);

        Assert.Single(result.Summary.KeyFacts);
        Assert.Empty(result.Dropped);
    }

    [Fact]
    public void ZeroToleranceIsStrict()
    {
        var summary = new MeetingSummary { KeyFacts = [new KeyFact("fact", S(60))] };

        var result = CitationValidator.Validate(summary, Transcript, TimeSpan.Zero);

        Assert.Empty(result.Summary.KeyFacts);
    }

    [Fact]
    public void EmptySummaryIsValid()
    {
        var result = CitationValidator.Validate(new MeetingSummary(), Transcript);

        Assert.Empty(result.Dropped);
        Assert.False(result.HasDropped);
    }

    [Fact]
    public void EmptyTranscriptDropsEveryCitedItem()
    {
        var summary = new MeetingSummary { Decisions = [new Decision("d", null, S(1))] };

        var result = CitationValidator.Validate(summary, []);

        Assert.Empty(result.Summary.Decisions);
        Assert.Single(result.Dropped);
    }
}
