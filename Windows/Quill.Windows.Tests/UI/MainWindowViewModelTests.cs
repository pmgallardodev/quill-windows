using System.Globalization;
using Quill.Core.Domain;
using Quill.Core.Ports;
using Quill.Windows.Resources;
using Quill.Windows.Sessions;
using Quill.Windows.Transcription;
using Quill.Windows.UI;
using Quill.Windows.UI.Theme.Controls;
using Xunit;
using LegacySegment = Quill.Windows.Sessions.TranscriptSegment;
using TranscriptSegment = Quill.Core.Domain.TranscriptSegment;

namespace Quill.Windows.Tests.UI;

/// <summary>In-memory <see cref="ISessionStore"/> for the main window view models.</summary>
internal sealed class FakeSessionStore : ISessionStore
{
    public Dictionary<(string Id, SummaryKind Kind), MeetingSummary> Summaries { get; } = new();

    public Dictionary<string, string> Notes { get; } = new();

    public Dictionary<string, IReadOnlyList<Speaker>> Speakers { get; } = new();

    public List<(string Id, MeetingSummary Summary)> SavedSummaries { get; } = new();

    public List<(string Id, string Text)> SavedNotes { get; } = new();

    public bool FailSaves { get; set; }

    public Task<IReadOnlyList<Session>> ListSessionsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Session>>(Array.Empty<Session>());

    public Task<Session?> LoadSessionAsync(string sessionId, CancellationToken cancellationToken) =>
        Task.FromResult<Session?>(null);

    public Dictionary<string, IReadOnlyList<TranscriptSegment>> Transcripts { get; } = new();

    public List<(string Id, TranscriptInfo Info, IReadOnlyList<TranscriptSegment> Segments)> SavedTranscripts { get; } = new();

    public Task<IReadOnlyList<TranscriptSegment>> LoadTranscriptAsync(string sessionId, TranscriptPass pass, CancellationToken cancellationToken) =>
        Task.FromResult(Transcripts.TryGetValue(sessionId, out var segments) ? segments : (IReadOnlyList<TranscriptSegment>)Array.Empty<TranscriptSegment>());

    public Task SaveTranscriptAsync(string sessionId, TranscriptPass pass, TranscriptInfo info, IReadOnlyList<TranscriptSegment> segments, CancellationToken cancellationToken)
    {
        if (FailSaves)
        {
            return Task.FromException(new IOException("disk full"));
        }

        Transcripts[sessionId] = segments;
        SavedTranscripts.Add((sessionId, info, segments));
        return Task.CompletedTask;
    }

    public Task<MeetingSummary?> LoadSummaryAsync(string sessionId, SummaryKind kind, CancellationToken cancellationToken) =>
        Task.FromResult(Summaries.TryGetValue((sessionId, kind), out var summary) ? summary : null);

    public Task SaveSummaryAsync(string sessionId, MeetingSummary summary, CancellationToken cancellationToken)
    {
        if (FailSaves)
        {
            return Task.FromException(new IOException("disk full"));
        }

        Summaries[(sessionId, summary.Meta.Kind)] = summary;
        SavedSummaries.Add((sessionId, summary));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Speaker>> LoadSpeakersAsync(string sessionId, CancellationToken cancellationToken) =>
        Task.FromResult(Speakers.TryGetValue(sessionId, out var speakers) ? speakers : (IReadOnlyList<Speaker>)Array.Empty<Speaker>());

    public Task SaveSpeakersAsync(string sessionId, IReadOnlyList<Speaker> speakers, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task<string> LoadNotesAsync(string sessionId, CancellationToken cancellationToken) =>
        Task.FromResult(Notes.TryGetValue(sessionId, out var text) ? text : string.Empty);

    public Task SaveNotesAsync(string sessionId, string markdown, CancellationToken cancellationToken)
    {
        if (FailSaves)
        {
            return Task.FromException(new IOException("disk full"));
        }

        Notes[sessionId] = markdown;
        SavedNotes.Add((sessionId, markdown));
        return Task.CompletedTask;
    }
}

internal sealed class FakeSessionActions : ISessionActions
{
    public bool IsAiAvailable { get; set; } = true;

    public IReadOnlyList<string> SummaryTemplates { get; set; } = new[] { "general", "1:1" };

    public Func<string, string, SummaryLevel, Task> OnGenerate { get; set; } = (_, _, _) => Task.CompletedTask;

    public List<(string SessionId, TimeSpan At)> Played { get; } = new();

    public Task GenerateSummaryAsync(string sessionId, string template, SummaryLevel level, CancellationToken cancellationToken) =>
        OnGenerate(sessionId, template, level);

    public Task IdentifySpeakersAsync(string sessionId, CancellationToken cancellationToken) => Task.CompletedTask;

    public void PlayAt(string sessionId, TimeSpan time) => Played.Add((sessionId, time));

    /// <summary>What <see cref="IsSummaryOutdatedAsync"/> answers (summary.state.json "outdated").</summary>
    public bool Outdated { get; set; }

    public Task<bool> IsSummaryOutdatedAsync(string sessionId, CancellationToken cancellationToken) => Task.FromResult(Outdated);

    public bool CanReassignSpeakers { get; set; }

    public List<(string SessionId, TimeSpan Start, string SpeakerId)> Reassigned { get; } = new();

    public Task ReassignSpeakerAsync(string sessionId, TimeSpan segmentStart, string speakerId, CancellationToken cancellationToken)
    {
        Reassigned.Add((sessionId, segmentStart, speakerId));
        return Task.CompletedTask;
    }
}

internal sealed class FakeUiShell : IUiShell
{
    public List<string> Copied { get; } = new();

    public bool Confirm { get; set; } = true;

    public bool TryCopyText(string text)
    {
        Copied.Add(text);
        return true;
    }

    public bool ConfirmDestructive(string title, string message) => Confirm;
}

internal static class Samples
{
    public const string SessionId = "2026-11-25_10-00-00";

    public static MeetingSummary Summary(SummaryKind kind = SummaryKind.Final) => new()
    {
        ExecutiveSummary = "Se acordó lanzar el viernes.",
        Decisions = new[] { new Decision("Lanzar el viernes", "speaker-1", TimeSpan.FromSeconds(30)) },
        ActionItems = new[]
        {
            new ActionItem("Preparar la demo", "me", "jueves", true, TimeSpan.FromSeconds(60)),
            new ActionItem("Revisar precios", "speaker-1", null, false, TimeSpan.FromSeconds(90)),
        },
        NextSteps = new[] { "Enviar el acta" },
        Meta = new SummaryMeta { Kind = kind, Template = "general" },
    };

    public static SessionSnapshot Snapshot(SessionState state, DateTimeOffset started) => new(
        Path.Combine(Path.GetTempPath(), "quill-tests-missing", SessionId),
        SessionId,
        null,
        started,
        TimeSpan.FromMinutes(12),
        state,
        null,
        state == SessionState.Failed ? "whisper crashed" : null,
        null);

    public static void UseSpanish() => Loc.Culture = CultureInfo.GetCultureInfo("es-ES");
}

public class MeetingRowViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 12, 0, 0, TimeSpan.FromHours(1));

    public MeetingRowViewModelTests() => Samples.UseSpanish();

    private static MeetingRowViewModel Row(SessionState state, params Speaker[] speakers) =>
        new(Samples.Snapshot(state, Now.AddHours(-2)), new MeetingRowDetails(speakers, false), Now);

    [Theory]
    [InlineData("Recording", "Recording")]
    [InlineData("Queued", "Transcribing")]
    [InlineData("Running", "Transcribing")]
    public void StatusPill_ShowsActivity(string state, string expected)
    {
        var row = Row(Enum.Parse<SessionState>(state));

        Assert.True(row.HasStatusPill);
        Assert.Equal(Enum.Parse<PillStatus>(expected), row.StatusPill);
    }

    [Theory]
    [InlineData("Done")]
    [InlineData("Failed")]
    [InlineData("Abandoned")]
    public void StatusPill_IsHiddenWhenIdle(string state)
    {
        Assert.False(Row(Enum.Parse<SessionState>(state)).HasStatusPill);
    }

    [Fact]
    public void StatusPill_ShowsPausedWhileRecordingIsPaused()
    {
        var row = Row(SessionState.Recording);

        row.SetPaused(true);

        Assert.Equal(PillStatus.Paused, row.StatusPill);
    }

    [Theory]
    [InlineData("Failed", "Error")]
    [InlineData("Abandoned", "Incompleta")]
    [InlineData("Disabled", "Sin transcripción")]
    [InlineData("Done", "")]
    public void IssuePill_NamesTheProblem(string state, string expected)
    {
        var row = Row(Enum.Parse<SessionState>(state));

        Assert.Equal(expected, row.IssueText);
        Assert.Equal(expected.Length > 0, row.HasIssue);
    }

    [Fact]
    public void SpeakerChips_ShowMeFirst_UpToThree_ThenACount()
    {
        var row = Row(
            SessionState.Done,
            new Speaker("speaker-1", Track.System, "Ana"),
            new Speaker("speaker-2", Track.System),
            new Speaker("me", Track.Microphone),
            new Speaker("speaker-3", Track.System),
            new Speaker("speaker-4", Track.System));

        Assert.Equal(new[] { "Yo", "Ana", "Interlocutor 2" }, row.SpeakerChips.Select(chip => chip.Label));
        Assert.Equal("+2", row.MoreSpeakersText);
        Assert.True(row.HasMoreSpeakers);
    }

    [Fact]
    public void SpeakerChips_FallBackToTheTwoTracks_ForV1Sessions()
    {
        var row = Row(SessionState.Done);

        Assert.Equal(new[] { "Yo", "Otros" }, row.SpeakerChips.Select(chip => chip.Label));
        Assert.False(row.HasMoreSpeakers);
    }

    [Fact]
    public void Row_KnowsItsDayGroupAndStartTime()
    {
        var row = Row(SessionState.Done);

        Assert.Equal("Hoy", row.DayGroup);
        Assert.Equal(Now.AddHours(-2).ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture), row.StartTimeText);
    }

    [Fact]
    public void Apply_DoneUpdate_ClearsThePillAndAllowsDelete()
    {
        var row = Row(SessionState.Running);

        row.Apply(new TranscriptionUpdate(row.Directory, SessionState.Done, 0, Array.Empty<TrackProgress>(), null, TimeSpan.FromMinutes(2)));

        Assert.False(row.HasStatusPill);
        Assert.True(row.CanDelete);
        Assert.Equal("Transcrito en 2:00", row.ElapsedText);
    }
}

public class SummaryViewModelTests
{
    private readonly FakeSessionStore _store = new();
    private readonly FakeSessionActions _actions = new();
    private readonly FakeUiShell _shell = new();

    public SummaryViewModelTests() => Samples.UseSpanish();

    private async Task<SummaryViewModel> LoadAsync()
    {
        var summary = new SummaryViewModel(_store, _actions, _shell);
        await summary.LoadAsync(Samples.SessionId, "Kickoff", SpeakerLabels.Empty, CancellationToken.None);
        return summary;
    }

    [Fact]
    public async Task NoSummary_WithoutAiPack_ShowsAiPackMissing()
    {
        _actions.IsAiAvailable = false;

        var summary = await LoadAsync();

        Assert.Equal(SummaryState.AiPackMissing, summary.State);
        Assert.False(summary.CanGenerateNow);
        Assert.False(summary.ShowGenerateBar);
    }

    [Fact]
    public async Task NoSummary_WithAiPack_OffersGeneration()
    {
        var summary = await LoadAsync();

        Assert.Equal(SummaryState.NoSummary, summary.State);
        Assert.True(summary.CanGenerateNow);
        Assert.Equal("general", summary.SelectedTemplate);
    }

    [Fact]
    public async Task FinalSummary_ShowsEverySection_InDesignOrder()
    {
        _store.Summaries[(Samples.SessionId, SummaryKind.Final)] = Samples.Summary();

        var summary = await LoadAsync();

        Assert.Equal(SummaryState.Ready, summary.State);
        Assert.Equal("Se acordó lanzar el viernes.", summary.ExecutiveSummary);
        Assert.Equal(
            new[]
            {
                "Summary_Decisions", "Summary_ActionItems", "Summary_OpenQuestions", "Summary_Topics",
                "Summary_Risks", "Summary_KeyFacts", "Summary_NextSteps",
            }.Select(Loc.Get),
            summary.Sections.Select(section => section.Title));
        Assert.Equal(new[] { 1, 2, 0, 0, 0, 0, 1 }, summary.Sections.Select(section => section.Count));
        Assert.True(summary.Sections[2].IsEmpty);
    }

    [Fact]
    public async Task OutdatedFinalSummary_ShowsTheRegenerateBanner()
    {
        _store.Summaries[(Samples.SessionId, SummaryKind.Final)] = Samples.Summary();
        _actions.Outdated = true;

        var summary = await LoadAsync();

        Assert.True(summary.IsOutdated);
        Assert.True(summary.ShowOutdatedBanner);
        Assert.True(summary.GenerateCommand.CanExecute(null));
    }

    [Fact]
    public async Task Regenerating_ClearsTheOutdatedBanner()
    {
        _store.Summaries[(Samples.SessionId, SummaryKind.Final)] = Samples.Summary();
        _actions.Outdated = true;
        _actions.OnGenerate = (_, _, _) =>
        {
            _actions.Outdated = false;
            return Task.CompletedTask;
        };
        var summary = await LoadAsync();

        await summary.GenerateAsync();

        Assert.False(summary.ShowOutdatedBanner);
    }

    [Fact]
    public async Task UpToDateSummary_HasNoBanner()
    {
        _store.Summaries[(Samples.SessionId, SummaryKind.Final)] = Samples.Summary();

        var summary = await LoadAsync();

        Assert.False(summary.ShowOutdatedBanner);
    }

    [Fact]
    public async Task LiveSummary_NeverShowsTheOutdatedBanner()
    {
        _store.Summaries[(Samples.SessionId, SummaryKind.Live)] = Samples.Summary(SummaryKind.Live);
        _actions.Outdated = true;

        var summary = await LoadAsync();

        Assert.False(summary.ShowOutdatedBanner);
    }

    [Fact]
    public async Task OnlyLiveSummary_ShowsTheLiveState()
    {
        _store.Summaries[(Samples.SessionId, SummaryKind.Live)] = Samples.Summary(SummaryKind.Live);

        var summary = await LoadAsync();

        Assert.Equal(SummaryState.Live, summary.State);
        Assert.True(summary.HasSummary);
    }

    [Fact]
    public async Task TickingAnActionItem_SavesItAndKeepsTheRest()
    {
        _store.Summaries[(Samples.SessionId, SummaryKind.Final)] = Samples.Summary();
        var summary = await LoadAsync();

        summary.Sections[1].Items[1].IsDone = true;

        var saved = Assert.Single(_store.SavedSummaries).Summary;
        Assert.True(saved.ActionItems[1].Done);
        Assert.False(saved.ActionItems[0].Done);
        Assert.Equal(Samples.Summary().ExecutiveSummary, saved.ExecutiveSummary);
        Assert.Equal(SummaryKind.Final, saved.Meta.Kind);
    }

    [Fact]
    public async Task TickingAnActionItem_RevertsWhenTheSaveFails()
    {
        _store.Summaries[(Samples.SessionId, SummaryKind.Final)] = Samples.Summary();
        var summary = await LoadAsync();
        _store.FailSaves = true;
        var item = summary.Sections[1].Items[0];

        item.IsDone = true;

        Assert.False(item.IsDone);
        Assert.NotEmpty(summary.StatusText);
    }

    [Fact]
    public async Task Generate_ReloadsTheSavedFinalSummary()
    {
        _actions.OnGenerate = (id, template, level) =>
        {
            _store.Summaries[(id, SummaryKind.Final)] = Samples.Summary() with
            {
                Meta = new SummaryMeta { Template = template, Level = level },
            };
            return Task.CompletedTask;
        };
        var summary = await LoadAsync();
        summary.SelectedTemplate = "1:1";
        summary.SelectedLevel = SummaryLevel.Detailed;

        await summary.GenerateAsync();

        Assert.Equal(SummaryState.Ready, summary.State);
        Assert.Equal("1:1", summary.Summary!.Meta.Template);
        Assert.Equal(SummaryLevel.Detailed, summary.Summary.Meta.Level);
    }

    [Fact]
    public async Task Generate_Failure_ShowsTheError()
    {
        _actions.OnGenerate = (_, _, _) => Task.FromException(new InvalidOperationException("model crashed"));
        var summary = await LoadAsync();

        await summary.GenerateAsync();

        Assert.Equal(SummaryState.Failed, summary.State);
        Assert.Contains("model crashed", summary.ErrorText);
        Assert.True(summary.CanGenerateNow);
    }

    [Fact]
    public async Task Generate_IsNotOfferedWhileTheMeetingIsNotTranscribed()
    {
        var summary = await LoadAsync();

        summary.CanGenerate = false;

        Assert.False(summary.CanGenerateNow);
    }

    [Fact]
    public async Task CopyAsMarkdown_UsesTheMeetingTitle()
    {
        _store.Summaries[(Samples.SessionId, SummaryKind.Final)] = Samples.Summary();
        var summary = await LoadAsync();

        summary.CopyMarkdownCommand.Execute(null);

        Assert.StartsWith("# Kickoff", Assert.Single(_shell.Copied));
    }

    [Fact]
    public async Task CitationClick_AsksPlaybackToStartThere()
    {
        _store.Summaries[(Samples.SessionId, SummaryKind.Final)] = Samples.Summary();
        var summary = await LoadAsync();

        summary.PlayAtCommand.Execute(TimeSpan.FromSeconds(30));

        Assert.Equal((Samples.SessionId, TimeSpan.FromSeconds(30)), Assert.Single(_actions.Played));
    }
}

public class TranscriptViewModelTests
{
    private readonly FakeSessionActions _actions = new();

    public TranscriptViewModelTests() => Samples.UseSpanish();

    private static IReadOnlyList<TranscriptEntryViewModel> Entries() =>
        TranscriptViewModel.BuildEntries(
            new[]
            {
                new LegacySegment("me", 0, 2_000, "Hola, empezamos la sesión"),
                new LegacySegment("speaker-1", 3_000, 5_000, "   "),
                new LegacySegment("speaker-1", 6_000, 8_000, "Vale, la Sesion de hoy"),
                new LegacySegment("me", 9_000, 10_000, "Perfecto"),
            },
            new long[] { 6_000, -1 },
            new SpeakerLabels(new[] { new Speaker("speaker-1", Track.System, "Ana") }));

    [Fact]
    public void BuildEntries_SkipsBlankLines_AndPlacesMarkersAfterTheSegmentTheyStartWith()
    {
        var entries = Entries();

        Assert.Equal(
            new[] { "Hola, empezamos la sesión", "Vale, la Sesion de hoy", "Marca 1", "Perfecto" },
            entries.Select(entry => entry.Text));
        Assert.Equal(new[] { "Yo", "Ana", "", "Yo" }, entries.Select(entry => entry.SpeakerLabel));
        Assert.True(entries[2].IsMarker);
    }

    [Fact]
    public void Search_IgnoresAccents_AndWrapsAround()
    {
        var transcript = new TranscriptViewModel(_actions);
        transcript.Load(Samples.SessionId, Entries(), "vacío");

        transcript.Query = "sesion";

        Assert.Equal(2, transcript.MatchCount);
        Assert.Equal("1 de 2", transcript.MatchText);
        Assert.Same(transcript.Entries[0], transcript.CurrentMatch);
        Assert.True(transcript.Entries[0].IsCurrentMatch);
        Assert.Contains(transcript.Entries[0].Runs, run => run.IsMatch && run.Text == "sesión");

        transcript.NextMatchCommand.Execute(null);
        Assert.Same(transcript.Entries[1], transcript.CurrentMatch);
        Assert.False(transcript.Entries[0].IsCurrentMatch);

        transcript.NextMatchCommand.Execute(null);
        Assert.Same(transcript.Entries[0], transcript.CurrentMatch);

        transcript.PreviousMatchCommand.Execute(null);
        Assert.Equal("2 de 2", transcript.MatchText);
    }

    [Fact]
    public void Search_WithoutMatches_SaysSo_AndClearingRestoresPlainText()
    {
        var transcript = new TranscriptViewModel(_actions);
        transcript.Load(Samples.SessionId, Entries(), "vacío");

        transcript.Query = "presupuesto";
        Assert.Equal("Sin resultados", transcript.MatchText);
        Assert.Null(transcript.CurrentMatch);

        transcript.ClearSearchCommand.Execute(null);
        Assert.Equal(string.Empty, transcript.MatchText);
        Assert.All(transcript.Entries, entry => Assert.DoesNotContain(entry.Runs, run => run.IsMatch));
    }

    [Fact]
    public void TimestampClick_AsksPlaybackToStartThere()
    {
        var transcript = new TranscriptViewModel(_actions);
        transcript.Load(Samples.SessionId, Entries(), "vacío");

        transcript.PlayAtCommand.Execute(transcript.Entries[1]);

        Assert.Equal((Samples.SessionId, TimeSpan.FromSeconds(6)), Assert.Single(_actions.Played));
    }

    [Fact]
    public void Load_WithoutLines_ShowsTheEmptyStatus()
    {
        var transcript = new TranscriptViewModel(_actions);

        transcript.Load(Samples.SessionId, Array.Empty<TranscriptEntryViewModel>(), "vacío");

        Assert.Equal("vacío", transcript.Status);
        Assert.True(transcript.HasStatus);
    }
}

public class NotesViewModelTests
{
    private readonly FakeSessionStore _store = new();
    private TaskCompletionSource _delay = new();

    public NotesViewModelTests() => Samples.UseSpanish();

    private NotesViewModel Notes() => new(_store, (_, token) =>
    {
        _delay = new TaskCompletionSource();
        token.Register(() => _delay.TrySetCanceled());
        return _delay.Task;
    });

    [Fact]
    public async Task Typing_SavesOnlyAfterThePause()
    {
        _store.Notes[Samples.SessionId] = "- antes";
        var notes = Notes();
        await notes.LoadAsync(Samples.SessionId, CancellationToken.None);

        notes.Text = "- antes\n- después";
        Assert.Empty(_store.SavedNotes);
        Assert.True(notes.IsDirty);

        _delay.SetResult();

        Assert.Equal((Samples.SessionId, "- antes\n- después"), Assert.Single(_store.SavedNotes));
        Assert.False(notes.IsDirty);
        Assert.Equal("Guardado", notes.Status);
    }

    [Fact]
    public async Task KeepTyping_RestartsThePause()
    {
        var notes = Notes();
        await notes.LoadAsync(Samples.SessionId, CancellationToken.None);

        notes.Text = "a";
        var first = _delay;
        notes.Text = "ab";

        Assert.True(first.Task.IsCanceled);
        _delay.SetResult();
        Assert.Equal("ab", Assert.Single(_store.SavedNotes).Text);
    }

    [Fact]
    public async Task Flush_SavesImmediately()
    {
        var notes = Notes();
        await notes.LoadAsync(Samples.SessionId, CancellationToken.None);
        notes.Text = "- idea";

        await notes.FlushAsync();

        Assert.Equal("- idea", Assert.Single(_store.SavedNotes).Text);
    }

    [Fact]
    public async Task Flush_WithoutChanges_DoesNotWrite()
    {
        _store.Notes[Samples.SessionId] = "- igual";
        var notes = Notes();
        await notes.LoadAsync(Samples.SessionId, CancellationToken.None);

        await notes.FlushAsync();

        Assert.Empty(_store.SavedNotes);
    }

    [Fact]
    public async Task SwitchingMeeting_SavesThePreviousNotesFirst()
    {
        var notes = Notes();
        await notes.LoadAsync("a", CancellationToken.None);
        notes.Text = "- de a";

        await notes.LoadAsync("b", CancellationToken.None);

        Assert.Equal(("a", "- de a"), Assert.Single(_store.SavedNotes));
        Assert.Equal(string.Empty, notes.Text);
    }
}

public class MyTasksViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 12, 0, 0, TimeSpan.FromHours(1));
    private readonly FakeSessionStore _store = new();

    public MyTasksViewModelTests() => Samples.UseSpanish();

    private static MeetingRowViewModel Row(bool hasSummary) =>
        new(Samples.Snapshot(SessionState.Done, Now), new MeetingRowDetails(Array.Empty<Speaker>(), hasSummary), Now);

    [Fact]
    public async Task Load_ListsOnlyMyActionItems()
    {
        _store.Summaries[(Samples.SessionId, SummaryKind.Final)] = Samples.Summary();
        var tasks = new MyTasksViewModel(_store, _ => { });

        await tasks.LoadAsync(new[] { Row(hasSummary: true) }, CancellationToken.None);

        var task = Assert.Single(tasks.Pending);
        Assert.Equal("Preparar la demo", task.Text);
        Assert.Equal("vence jueves", task.Due);
        Assert.Empty(tasks.Done);
        Assert.False(tasks.IsEmpty);
    }

    [Fact]
    public async Task TickingATask_SavesTheSummary_AndMovesItToDone()
    {
        _store.Summaries[(Samples.SessionId, SummaryKind.Final)] = Samples.Summary();
        var tasks = new MyTasksViewModel(_store, _ => { });
        await tasks.LoadAsync(new[] { Row(hasSummary: true) }, CancellationToken.None);

        tasks.Pending[0].IsDone = true;

        Assert.True(Assert.Single(_store.SavedSummaries).Summary.ActionItems[0].Done);
        Assert.Empty(tasks.Pending);
        Assert.Single(tasks.Done);
    }

    [Fact]
    public async Task OpenMeeting_ReportsTheSession()
    {
        _store.Summaries[(Samples.SessionId, SummaryKind.Final)] = Samples.Summary();
        string? opened = null;
        var tasks = new MyTasksViewModel(_store, id => opened = id);
        await tasks.LoadAsync(new[] { Row(hasSummary: true) }, CancellationToken.None);

        tasks.OpenMeetingCommand.Execute(tasks.Pending[0]);

        Assert.Equal(Samples.SessionId, opened);
    }

    [Fact]
    public async Task MeetingsWithoutSummary_AreNotRead()
    {
        _store.Summaries[(Samples.SessionId, SummaryKind.Final)] = Samples.Summary();
        var tasks = new MyTasksViewModel(_store, _ => { });

        await tasks.LoadAsync(new[] { Row(hasSummary: false) }, CancellationToken.None);

        Assert.True(tasks.IsEmpty);
    }
}

public class ParticipationResultTests
{
    public ParticipationResultTests() => Samples.UseSpanish();

    [Fact]
    public void Compute_FoldsEveryRemoteSpeakerIntoOthers()
    {
        var result = ParticipationResult.Compute(
            new[]
            {
                new LegacySegment("me", 0, 10_000, "uno"),
                new LegacySegment("speaker-1", 10_000, 20_000, "dos"),
                new LegacySegment("speaker-2", 20_000, 40_000, "tres"),
            },
            TimeSpan.FromSeconds(40));

        Assert.Equal(TimeSpan.FromSeconds(10), result.Me);
        Assert.Equal(TimeSpan.FromSeconds(30), result.Others);
        Assert.Equal(0.25d, result.MeShare, 3);
        Assert.Empty(result.Warnings);
        Assert.NotEmpty(result.Timeline);
    }

    [Fact]
    public void Compute_WarnsWhenTheMicrophoneHeardNothing()
    {
        var result = ParticipationResult.Compute(
            new[] { new LegacySegment("them", 0, 10_000, "hola") },
            TimeSpan.FromSeconds(10));

        Assert.Equal("El micrófono no captó nada.", Assert.Single(result.Warnings));
    }
}
