using Quill.Core.Domain;
using Quill.Core.Live;
using Quill.Core.Ports;
using Quill.Core.Summaries;
using Quill.Windows.UI.Companion;
using Quill.Windows.UI.Theme.Controls;
using Xunit;

namespace Quill.Windows.Tests.UI.Companion;

public class CompanionViewModelTests
{
    private const string SessionId = "2026-11-23_10-00-00";

    [Fact]
    public async Task WithoutAiPackTheLiveTabAsksToInstallIt()
    {
        var vm = Create(new Harness());
        await vm.InitializeAsync();

        Assert.Equal(LiveSummaryBanner.AiPackMissing, vm.Banner);
        Assert.True(vm.IsAiPackMissing);
        Assert.False(vm.HasSummary);
    }

    [Fact]
    public async Task LiveSummaryUpdatesAndDelayedStateReachTheView()
    {
        var harness = new Harness { Installed = true };
        var feed = new FakeFeed { State = LiveSummaryState.Waiting };
        var vm = Create(harness, feed);
        await vm.InitializeAsync();
        Assert.Equal(LiveSummaryBanner.Waiting, vm.Banner);

        feed.Publish(new MeetingSummary { Decisions = [new Decision("Lanzar en enero", null, TimeSpan.FromMinutes(2))] });
        Assert.True(vm.HasSummary);
        Assert.False(vm.HasNoDecisions);
        Assert.Equal(LiveSummaryBanner.None, vm.Banner);

        feed.SetState(LiveSummaryState.Delayed);
        Assert.Equal(LiveSummaryBanner.Delayed, vm.Banner);
        Assert.True(vm.IsBannerAttention);
    }

    [Fact]
    public async Task TranscriptShowsTheSnapshotThenNewSegments()
    {
        var buffer = new LiveTranscriptBuffer();
        buffer.Add(Segment(0, "hola", Track.Microphone));
        var harness = new Harness { Transcript = buffer };
        var vm = Create(harness);
        await vm.InitializeAsync();
        Assert.Single(vm.TranscriptRows);

        buffer.Add(Segment(5, "buenas", Track.System));

        Assert.Equal(["hola", "buenas"], vm.TranscriptRows.Select(row => row.Text));
        Assert.Equal(SpeakerIds.Remote(1), vm.TranscriptRows[1].SpeakerId);
        Assert.False(vm.IsTranscriptEmpty);
    }

    [Fact]
    public async Task WithoutLiveTranscriptTheTabExplainsIt()
    {
        var vm = Create(new Harness());
        await vm.InitializeAsync();

        Assert.False(vm.HasLiveTranscript);
        Assert.False(vm.IsTranscriptEmpty);
    }

    [Fact]
    public async Task QuickNoteIsAppendedWithTheRecordingTimeAndSavedOnShutdown()
    {
        var harness = new Harness { Elapsed = TimeSpan.FromSeconds(95) };
        harness.Store.Notes = "- nota previa\n";
        var vm = Create(harness);
        await vm.InitializeAsync();

        vm.QuickNoteText = "Pedir presupuesto";
        vm.AddQuickNoteCommand.Execute(null);
        await vm.ShutdownAsync();

        Assert.Equal("- nota previa\n- [00:01:35] Pedir presupuesto\n", harness.Store.Notes);
        Assert.Equal(string.Empty, vm.QuickNoteText);
    }

    [Fact]
    public async Task NotesThatFailToLoadAreNeverOverwritten()
    {
        var harness = new Harness();
        harness.Store.FailLoad = true;
        var vm = Create(harness);
        await vm.InitializeAsync();

        vm.NotesText = "esto no debe guardarse";
        await vm.ShutdownAsync();

        Assert.False(vm.IsNotesEditable);
        Assert.True(vm.IsNotesProblem);
        Assert.Equal(0, harness.Store.Saves);
    }

    [Fact]
    public async Task MarkCallsTheHostAndShowsAToastThatExpires()
    {
        var harness = new Harness { Elapsed = TimeSpan.FromMinutes(12) };
        var vm = Create(harness);
        await vm.InitializeAsync();

        vm.MarkCommand.Execute(null);
        Assert.Equal(1, harness.Markers);
        Assert.Equal("Momento marcado en 12:00", vm.Toast);

        harness.Now += TimeSpan.FromSeconds(4);
        vm.Tick();
        Assert.False(vm.HasToast);
    }

    [Fact]
    public async Task HostCallbackFailuresDoNotEscape()
    {
        var harness = new Harness { ThrowFromHost = true };
        var vm = Create(harness);
        await vm.InitializeAsync();

        vm.MarkCommand.Execute(null);
        vm.StopCommand.Execute(null);
        vm.TogglePauseCommand.Execute(null);

        Assert.Null(vm.Toast);
    }

    [Fact]
    public async Task TickReflectsPauseTimerAndLevels()
    {
        var harness = new Harness { Elapsed = TimeSpan.FromSeconds(61), Microphone = 2.0, SystemAudio = 0.4 };
        var vm = Create(harness);
        await vm.InitializeAsync();

        Assert.Equal("01:01", vm.ElapsedLabel);
        Assert.Equal(1.0, vm.MicrophoneLevel);
        Assert.Equal(0.4, vm.SystemLevel);
        Assert.Equal(PillStatus.Recording, vm.Status);

        vm.TogglePauseCommand.Execute(null);
        Assert.True(vm.IsPaused);
        Assert.Equal(PillStatus.Paused, vm.Status);
        Assert.Equal(0, vm.MicrophoneLevel);
    }

    [Fact]
    public async Task CatchUpWithoutModelExplainsTheAiPack()
    {
        var vm = Create(new Harness());
        await vm.InitializeAsync();

        await vm.CatchUpAsync();

        Assert.Equal(CatchUpState.AiPackMissing, vm.CatchUpState);
        Assert.True(vm.IsCatchUpVisible);
    }

    [Fact]
    public async Task CatchUpShowsTheRecapForTheDefaultWindow()
    {
        TimeSpan? asked = null;
        var recap = new MeetingSummary { ActionItems = [new ActionItem("Enviar acta", "me", null, true, TimeSpan.FromMinutes(9))] };
        var harness = new Harness
        {
            CatchUp = (window, _) =>
            {
                asked = window;
                return Task.FromResult(new SummaryOutcome(SummaryStatus.Completed, recap, []));
            },
        };
        var vm = Create(harness);
        await vm.InitializeAsync();

        await vm.CatchUpAsync();

        Assert.Equal(TimeSpan.FromMinutes(5), asked);
        Assert.Equal(CatchUpState.Ready, vm.CatchUpState);
        Assert.Single(vm.CatchUpView.ActionItems);

        vm.CloseCatchUpCommand.Execute(null);
        Assert.False(vm.IsCatchUpVisible);
    }

    [Fact]
    public async Task CatchUpErrorsBecomeTheFailedState()
    {
        var harness = new Harness { CatchUp = (_, _) => throw new InvalidOperationException("model crashed") };
        var vm = Create(harness);
        await vm.InitializeAsync();

        await vm.CatchUpAsync();

        Assert.Equal(CatchUpState.Failed, vm.CatchUpState);
        Assert.NotNull(vm.CatchUpMessage);
    }

    [Fact]
    public async Task CopyNoticeUsesTheClipboardCallbackOnly()
    {
        var harness = new Harness();
        var vm = Create(harness);
        await vm.InitializeAsync();

        vm.CopyNoticeCommand.Execute(null);

        Assert.Equal("Aviso: estoy tomando notas de esta reunión con una herramienta local.", harness.Copied);
        Assert.True(vm.HasToast);
    }

    private static TranscriptSegment Segment(int second, string text, Track track) =>
        new(TimeSpan.FromSeconds(second), TimeSpan.FromSeconds(second + 3), text, LiveSpeakers.ProvisionalFor(track), track, Editable: false);

    private static CompanionViewModel Create(Harness harness, ILiveSummaryFeed? feed = null)
    {
        var context = new CompanionContext
        {
            SessionId = SessionId,
            Store = harness.Store,
            Elapsed = () => harness.Elapsed,
            IsPaused = () => harness.Paused,
            TogglePause = () =>
            {
                if (harness.ThrowFromHost)
                {
                    throw new InvalidOperationException("host");
                }

                harness.Paused = !harness.Paused;
            },
            Stop = () => { if (harness.ThrowFromHost) { throw new InvalidOperationException("host"); } },
            AddMarker = () =>
            {
                if (harness.ThrowFromHost)
                {
                    throw new InvalidOperationException("host");
                }

                harness.Markers++;
            },
            Transcript = harness.Transcript,
            IsAiPackInstalled = harness.Installed,
            CatchUp = harness.CatchUp,
            MicrophoneLevel = () => harness.Microphone,
            SystemLevel = () => harness.SystemAudio,
        };

        return new CompanionViewModel(
            context,
            feed,
            action => action(),
            text => harness.Copied = text,
            () => harness.Now,
            TimeSpan.FromMinutes(5));
    }

    private sealed class Harness
    {
        public NotesStore Store { get; } = new();

        public TimeSpan Elapsed { get; set; }

        public bool Paused { get; set; }

        public bool Installed { get; set; }

        public bool ThrowFromHost { get; set; }

        public int Markers { get; set; }

        public double Microphone { get; set; }

        public double SystemAudio { get; set; }

        public string? Copied { get; set; }

        public DateTimeOffset Now { get; set; } = new(2026, 11, 23, 10, 0, 0, TimeSpan.Zero);

        public LiveTranscriptBuffer? Transcript { get; set; }

        public CatchUpHandler? CatchUp { get; set; }
    }

    private sealed class FakeFeed : ILiveSummaryFeed
    {
        public event EventHandler<MeetingSummary>? Updated;

        public event EventHandler<LiveSummaryState>? StateChanged;

        public LiveSummaryState State { get; set; }

        public MeetingSummary? Current { get; private set; }

        public void Publish(MeetingSummary summary)
        {
            Current = summary;
            State = LiveSummaryState.UpToDate;
            Updated?.Invoke(this, summary);
            StateChanged?.Invoke(this, State);
        }

        public void SetState(LiveSummaryState state)
        {
            State = state;
            StateChanged?.Invoke(this, state);
        }
    }

    /// <summary>Only notes.md is used by the panel.</summary>
    private sealed class NotesStore : ISessionStore
    {
        public string Notes { get; set; } = string.Empty;

        public bool FailLoad { get; set; }

        public int Saves { get; private set; }

        public Task<string> LoadNotesAsync(string sessionId, CancellationToken cancellationToken) =>
            FailLoad ? throw new IOException("locked") : Task.FromResult(Notes);

        public Task SaveNotesAsync(string sessionId, string markdown, CancellationToken cancellationToken)
        {
            Saves++;
            Notes = markdown;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<Session>> ListSessionsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Session?> LoadSessionAsync(string sessionId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<TranscriptSegment>> LoadTranscriptAsync(string sessionId, TranscriptPass pass, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task SaveTranscriptAsync(string sessionId, TranscriptPass pass, TranscriptInfo info, IReadOnlyList<TranscriptSegment> segments, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<MeetingSummary?> LoadSummaryAsync(string sessionId, SummaryKind kind, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task SaveSummaryAsync(string sessionId, MeetingSummary summary, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Speaker>> LoadSpeakersAsync(string sessionId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task SaveSpeakersAsync(string sessionId, IReadOnlyList<Speaker> speakers, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
