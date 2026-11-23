using Quill.Core.Domain;
using Quill.Core.Summaries;
using Xunit;
using static Quill.Core.Tests.Summaries.Segments;

namespace Quill.Core.Tests.Summaries;

public sealed class SummaryUserStateMergerTests
{
    private static ActionItem Item(string text, string? owner = null, bool done = false, double at = 10) =>
        new(text, owner, null, false, S(at), done);

    private static MeetingSummary With(params ActionItem[] items) => new() { ActionItems = items };

    [Fact]
    public void NoPreviousSummaryKeepsTheNewOneAsIs()
    {
        var next = With(Item("Preparar demo"));

        var result = SummaryUserStateMerger.Merge(null, next);

        Assert.Same(next, result.Summary);
        Assert.Equal(0, result.CarriedDone);
        Assert.Empty(result.UnmatchedDone);
    }

    [Fact]
    public void ExactMatchCarriesTheDoneFlag()
    {
        var previous = With(Item("Preparar demo", "speaker-1", done: true), Item("Reservar la sala"));
        var next = With(Item("Reservar la sala", at: 50), Item("Preparar demo", "speaker-1", at: 30));

        var result = SummaryUserStateMerger.Merge(previous, next);

        Assert.Equal([false, true], result.Summary.ActionItems.Select(a => a.Done));
        Assert.Equal(1, result.CarriedDone);
        Assert.Empty(result.UnmatchedDone);
        // Everything else comes from the new summary.
        Assert.Equal(S(30), result.Summary.ActionItems[1].At);
    }

    [Fact]
    public void RewordedItemStillMatchesIgnoringCaseAccentsAndPunctuation()
    {
        var previous = With(Item("Enviar el presupuesto a Ana.", done: true));
        var next = With(Item("Reservar sala para el jueves"), Item("enviar presupuesto a Ana antes del viernes"));

        var result = SummaryUserStateMerger.Merge(previous, next);

        Assert.Equal([false, true], result.Summary.ActionItems.Select(a => a.Done));
    }

    [Fact]
    public void DifferentOwnersNeverMatch()
    {
        var previous = With(Item("Preparar demo", "speaker-1", done: true));
        var next = With(Item("Preparar demo", "speaker-2"));

        var result = SummaryUserStateMerger.Merge(previous, next);

        Assert.False(Assert.Single(result.Summary.ActionItems).Done);
        Assert.Equal("Preparar demo", Assert.Single(result.UnmatchedDone).Text);
    }

    [Fact]
    public void OwnerMissingOnOneSideStillMatchesByText()
    {
        var previous = With(Item("Preparar demo", "speaker-1", done: true));
        var next = With(Item("Preparar demo"));

        Assert.True(Assert.Single(SummaryUserStateMerger.Merge(previous, next).Summary.ActionItems).Done);
    }

    [Fact]
    public void UnrelatedItemsDoNotMatch()
    {
        var previous = With(Item("Enviar el presupuesto a Ana", done: true));
        var next = With(Item("Reservar la sala del jueves"));

        var result = SummaryUserStateMerger.Merge(previous, next);

        Assert.False(Assert.Single(result.Summary.ActionItems).Done);
        Assert.Single(result.UnmatchedDone);
    }

    [Fact]
    public void MatchingIsOneToOneAndPrefersTheBestPair()
    {
        // Only one old item was done; the closer of the two new rewordings gets the flag.
        var previous = With(Item("Revisar el contrato con Luis", done: true));
        var next = With(Item("Revisar contrato"), Item("Revisar el contrato con Luis"));

        var result = SummaryUserStateMerger.Merge(previous, next);

        Assert.Equal([false, true], result.Summary.ActionItems.Select(a => a.Done));
        Assert.Equal(1, result.CarriedDone);
    }

    [Fact]
    public void NewItemAlreadyDoneStaysDone()
    {
        var previous = With(Item("Preparar demo"));
        var next = With(Item("Preparar demo", done: true));

        Assert.True(Assert.Single(SummaryUserStateMerger.Merge(previous, next).Summary.ActionItems).Done);
    }
}
