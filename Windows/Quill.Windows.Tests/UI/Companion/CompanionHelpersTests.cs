using Quill.Core.Domain;
using Quill.Windows.UI.Companion;
using Xunit;

namespace Quill.Windows.Tests.UI.Companion;

public class AutoScrollPolicyTests
{
    [Fact]
    public void FollowsNewContentWhileAtTheBottom()
    {
        var policy = new AutoScrollPolicy();

        Assert.True(policy.OnScrollChanged(offset: 0, viewport: 300, extent: 400, extentChange: 100));
        Assert.True(policy.IsFollowing);
    }

    [Fact]
    public void ScrollingUpPausesAndMarksUnseenContent()
    {
        var policy = new AutoScrollPolicy();

        policy.OnScrollChanged(offset: 50, viewport: 300, extent: 1000, extentChange: 0);
        var scroll = policy.OnScrollChanged(offset: 50, viewport: 300, extent: 1100, extentChange: 100);

        Assert.False(policy.IsFollowing);
        Assert.False(scroll);
        Assert.True(policy.HasUnseen);
    }

    [Fact]
    public void ScrollingBackToTheBottomResumes()
    {
        var policy = new AutoScrollPolicy();
        policy.OnScrollChanged(offset: 0, viewport: 300, extent: 1000, extentChange: 0);
        policy.OnScrollChanged(offset: 0, viewport: 300, extent: 1100, extentChange: 100);

        policy.OnScrollChanged(offset: 790, viewport: 300, extent: 1100, extentChange: 0);

        Assert.True(policy.IsFollowing);
        Assert.False(policy.HasUnseen);
    }

    [Fact]
    public void ResumeFollowsAgain()
    {
        var policy = new AutoScrollPolicy();
        policy.OnScrollChanged(offset: 0, viewport: 300, extent: 1000, extentChange: 0);

        policy.Resume();

        Assert.True(policy.IsFollowing);
    }
}

public class TranscriptRowsSyncTests
{
    private static TranscriptSegment Segment(int second, string text) =>
        new(TimeSpan.FromSeconds(second), TimeSpan.FromSeconds(second + 2), text, SpeakerIds.Me, Track.Microphone, Editable: false);

    private static TranscriptSyncResult Apply(List<TranscriptSegment> rows, TranscriptSegment segment, int index) =>
        TranscriptRowsSync.Apply(rows, segment, index, row => row, segment => segment);

    [Fact]
    public void InsertsAtTheGivenIndex()
    {
        var rows = new List<TranscriptSegment> { Segment(0, "a"), Segment(10, "c") };

        Assert.Equal(TranscriptSyncResult.Inserted, Apply(rows, Segment(5, "b"), 1));
        Assert.Equal(["a", "b", "c"], rows.Select(row => row.Text));
    }

    [Fact]
    public void SkipsASegmentAlreadyTakenFromTheSnapshot()
    {
        var rows = new List<TranscriptSegment> { Segment(0, "a") };

        Assert.Equal(TranscriptSyncResult.Duplicate, Apply(rows, Segment(0, "a"), 0));
        Assert.Single(rows);
    }

    [Fact]
    public void AskForResyncWhenTheIndexDoesNotFit()
    {
        var rows = new List<TranscriptSegment>();

        Assert.Equal(TranscriptSyncResult.NeedsResync, Apply(rows, Segment(0, "a"), 3));
        Assert.Empty(rows);
    }
}

public class CompanionPlacementTests
{
    private static readonly PanelRect WorkArea = new(0, 0, 1920, 1040);

    [Fact]
    public void DocksToTheTopRightInsideTheMargin()
    {
        var (left, top) = CompanionPlacement.DockRight(WorkArea, 360);

        Assert.Equal(1920 - 360 - CompanionPlacement.Margin, left);
        Assert.Equal(CompanionPlacement.Margin, top);
    }

    [Fact]
    public void ClampKeepsThePanelOnScreen()
    {
        Assert.Equal((1560d, 440d), CompanionPlacement.Clamp(5000, 900, 360, 600, WorkArea));
        Assert.Equal((0d, 0d), CompanionPlacement.Clamp(-50, -10, 360, 600, WorkArea));
        Assert.Equal((100d, 100d), CompanionPlacement.Clamp(100, 100, 360, 600, WorkArea));
    }

    [Fact]
    public void KeepRightEdgeMovesLeftByTheWidthDifference()
    {
        Assert.Equal(1340, CompanionPlacement.KeepRightEdge(1500, 200, 360));
    }

    [Fact]
    public void StoreRoundTripsAndToleratesGarbage()
    {
        using var temp = new TempDirectory();
        var store = new CompanionPlacementStore(System.IO.Path.Combine(temp.Path, "nested", "companion-panel.json"));

        Assert.Null(store.Load());
        store.Save(new CompanionPanelState(12.5, 40, Collapsed: true));
        Assert.Equal(new CompanionPanelState(12.5, 40, true), store.Load());

        File.WriteAllText(store.FilePath, "{ not json");
        Assert.Null(store.Load());
    }
}

public class NotesAutosaverTests
{
    [Fact]
    public async Task SavesOnlyTheLatestTextAfterTheDelay()
    {
        var saved = new List<string>();
        var autosaver = new NotesAutosaver((text, _) => { lock (saved) { saved.Add(text); } return Task.CompletedTask; }, TimeSpan.FromMilliseconds(50));

        autosaver.Update("a");
        autosaver.Update("ab");
        autosaver.Update("abc");
        Assert.Equal(NotesSaveState.Pending, autosaver.State);

        await WaitUntil(() => autosaver.State == NotesSaveState.Saved);
        Assert.Equal(["abc"], saved);
    }

    [Fact]
    public async Task FlushSavesImmediatelyAndOnlyOnce()
    {
        var saved = new List<string>();
        var autosaver = new NotesAutosaver((text, _) => { saved.Add(text); return Task.CompletedTask; }, TimeSpan.FromMinutes(5));

        autosaver.Update("nota");
        await autosaver.FlushAsync();
        await autosaver.FlushAsync();

        Assert.Equal(["nota"], saved);
        Assert.Equal(NotesSaveState.Saved, autosaver.State);
    }

    [Fact]
    public async Task LegacySaveCallbackReceivesTheDraftRatherThanItsBaseline()
    {
        var saved = new List<string>();
        var autosaver = new NotesAutosaver((text, _) => { saved.Add(text); return Task.CompletedTask; }, TimeSpan.FromMinutes(5));
        autosaver.SetBaseline("original");

        autosaver.Update("edited draft");
        await autosaver.FlushAsync();

        Assert.Equal(["edited draft"], saved);
    }

    [Fact]
    public async Task AFailedSaveIsRetriedOnFlush()
    {
        var attempts = 0;
        var autosaver = new NotesAutosaver(
            (_, _) => ++attempts == 1 ? throw new IOException("locked") : Task.CompletedTask,
            TimeSpan.FromMinutes(5));

        autosaver.Update("nota");
        await autosaver.FlushAsync();
        Assert.Equal(NotesSaveState.Failed, autosaver.State);

        await autosaver.FlushAsync();
        Assert.Equal(NotesSaveState.Saved, autosaver.State);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task NothingToSaveWithoutChanges()
    {
        var attempts = 0;
        var autosaver = new NotesAutosaver((_, _) => { attempts++; return Task.CompletedTask; });

        await autosaver.FlushAsync();

        Assert.Equal(0, attempts);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }
}
