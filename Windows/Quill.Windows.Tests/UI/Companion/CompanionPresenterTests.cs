using Quill.Core.Domain;
using Quill.Core.Storage;
using Quill.Core.Summaries;
using Quill.Windows.UI.Companion;
using Xunit;

namespace Quill.Windows.Tests.UI.Companion;

public class CompanionPresenterTests
{
    [Theory]
    [InlineData(false, false, LiveSummaryState.Idle, false, LiveSummaryBanner.AiPackMissing)]
    [InlineData(false, true, LiveSummaryState.Idle, false, LiveSummaryBanner.Off)]
    [InlineData(true, true, LiveSummaryState.Waiting, false, LiveSummaryBanner.Waiting)]
    [InlineData(true, true, LiveSummaryState.Waiting, true, LiveSummaryBanner.None)]
    [InlineData(true, true, LiveSummaryState.UpToDate, true, LiveSummaryBanner.None)]
    [InlineData(true, true, LiveSummaryState.Updating, true, LiveSummaryBanner.Updating)]
    [InlineData(true, true, LiveSummaryState.Delayed, true, LiveSummaryBanner.Delayed)]
    [InlineData(true, true, LiveSummaryState.Failed, false, LiveSummaryBanner.Failed)]
    [InlineData(true, true, LiveSummaryState.AiPackMissing, false, LiveSummaryBanner.AiPackMissing)]
    internal void BannerFollowsFeedAndState(bool hasFeed, bool installed, LiveSummaryState state, bool hasSummary, LiveSummaryBanner expected)
    {
        Assert.Equal(expected, CompanionPresenter.Banner(hasFeed, installed, state, hasSummary));
    }

    [Fact]
    public void EveryVisibleBannerHasAString()
    {
        foreach (var banner in Enum.GetValues<LiveSummaryBanner>().Where(banner => banner != LiveSummaryBanner.None))
        {
            var key = CompanionPresenter.BannerKey(banner);
            Assert.NotNull(key);
            Assert.DoesNotContain("[", Quill.Windows.Resources.Loc.Get(key!));
        }

        Assert.Null(CompanionPresenter.BannerKey(LiveSummaryBanner.None));
    }

    [Fact]
    public void ViewMapsLiveSectionsWithSpeakerLabels()
    {
        var summary = new MeetingSummary
        {
            ExecutiveSummary = "  Revisamos precios.  ",
            Decisions = [new Decision("Subir el plan pro", "speaker-2", TimeSpan.FromSeconds(75))],
            ActionItems =
            [
                new ActionItem("Enviar propuesta", "me", "el viernes", true, TimeSpan.FromMinutes(3)),
                new ActionItem("Revisar contrato", null, null, false, TimeSpan.FromMinutes(4)),
            ],
            OpenQuestions = [new OpenQuestion("¿Descuento anual?", TimeSpan.FromMinutes(5))],
        };

        var view = CompanionPresenter.View(summary);

        Assert.Equal("Revisamos precios.", view.ExecutiveSummary);
        Assert.Equal("Interlocutor 2", view.Decisions.Single().Detail);
        Assert.Equal("01:15", view.Decisions.Single().AtLabel);
        Assert.Equal("Yo · Para: el viernes", view.ActionItems[0].Detail);
        Assert.False(view.ActionItems[1].HasDetail);
        Assert.Equal(SummaryItemKind.OpenQuestion, view.OpenQuestions.Single().Kind);
        Assert.False(view.IsEmpty);
    }

    [Fact]
    public void NullSummaryIsEmpty()
    {
        Assert.True(CompanionPresenter.View(null).IsEmpty);
    }

    [Fact]
    public void CatchUpStateFollowsOutcome()
    {
        var recap = new MeetingSummary { OpenQuestions = [new OpenQuestion("¿Fecha?", TimeSpan.FromMinutes(1))] };
        Assert.Equal(CatchUpState.Ready, CompanionPresenter.CatchUpStateOf(new SummaryOutcome(SummaryStatus.Completed, recap, [])));
        Assert.Equal(CatchUpState.Empty, CompanionPresenter.CatchUpStateOf(new SummaryOutcome(SummaryStatus.Completed, new MeetingSummary(), [])));
        Assert.Equal(CatchUpState.Empty, CompanionPresenter.CatchUpStateOf(SummaryOutcome.NoTranscript));
        Assert.Equal(CatchUpState.AiPackMissing, CompanionPresenter.CatchUpStateOf(SummaryOutcome.AiPackMissing()));
        Assert.Equal(CatchUpState.Failed, CompanionPresenter.CatchUpStateOf(SummaryOutcome.Failed("boom")));
    }

    [Theory]
    [InlineData("me", "Yo")]
    [InlineData("speaker-3", "Interlocutor 3")]
    [InlineData("Juan", "Juan")]
    public void SpeakerLabels(string id, string expected)
    {
        Assert.Equal(expected, CompanionPresenter.SpeakerLabel(id));
    }

    [Fact]
    public void ElapsedLabelNeverNegative()
    {
        Assert.Equal("00:00", CompanionPresenter.ElapsedLabel(TimeSpan.FromSeconds(-3)));
        Assert.Equal("1:02:03", CompanionPresenter.ElapsedLabel(new TimeSpan(1, 2, 3)));
    }

    [Fact]
    public void AppendNoteWritesTheNotesConvention()
    {
        var once = CompanionPresenter.AppendNote(string.Empty, "Pedir el presupuesto", TimeSpan.FromSeconds(90));
        var twice = CompanionPresenter.AppendNote("- nota libre", "Otra", TimeSpan.FromMinutes(2));

        Assert.Equal("- [00:01:30] Pedir el presupuesto\n", once);
        Assert.Equal("- nota libre\n- [00:02:00] Otra\n", twice);
        Assert.Equal("x", CompanionPresenter.AppendNote("x", "   ", TimeSpan.Zero));
        Assert.Equal(2, NotesDocument.Parse(twice).Count);
    }
}
