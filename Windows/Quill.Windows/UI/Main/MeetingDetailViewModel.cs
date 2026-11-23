using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using Quill.Core.Ports;
using Quill.Windows.Resources;
using Quill.Windows.Sessions;
using Quill.Windows.Transcription;
using Speaker = Quill.Core.Domain.Speaker;
using SpeakerIds = Quill.Core.Domain.SpeakerIds;

namespace Quill.Windows.UI;

/// <summary>One slot of the participation timeline under the header.</summary>
internal sealed class ParticipationSlotViewModel
{
    public ParticipationSlotViewModel(TimelineSlot slot)
    {
        Me = slot.Microphone;
        Others = slot.System;
        var time = TranscriptExport.Clock((long)slot.At.TotalMilliseconds);
        Tooltip = Loc.Format(
            Me && Others ? "Participation_SlotBoth" : Me ? "Participation_SlotMe" : Others ? "Participation_SlotOthers" : "Participation_SlotSilence",
            time);
    }

    public bool Me { get; }

    public bool Others { get; }

    public string Tooltip { get; }
}

/// <summary>Who spoke how much, computed from the transcript the detail already read.</summary>
internal sealed record ParticipationResult(
    double MeShare,
    TimeSpan Me,
    TimeSpan Others,
    IReadOnlyList<TimelineSlot> Timeline,
    IReadOnlyList<string> Warnings)
{
    public static ParticipationResult Empty { get; } =
        new(0d, TimeSpan.Zero, TimeSpan.Zero, Array.Empty<TimelineSlot>(), Array.Empty<string>());

    public bool HasSpeech => Me + Others > TimeSpan.Zero;

    /// <summary>
    /// "Me" versus everybody else. Remote speakers ("speaker-N", v1 "them") are folded into
    /// one side, which is what the two-colour bar and timeline can show.
    /// </summary>
    public static ParticipationResult Compute(IReadOnlyList<TranscriptSegment> segments, TimeSpan sessionDuration)
    {
        var folded = segments
            .Select(segment => string.Equals(segment.Speaker?.Trim(), SpeakerIds.Me, StringComparison.OrdinalIgnoreCase)
                ? segment with { Speaker = SessionInsights.MicrophoneSpeaker }
                : segment with { Speaker = SessionInsights.SystemSpeaker })
            .ToList();
        var summary = SessionInsights.Summarize(folded);
        if (!summary.HasSpeech)
        {
            return Empty;
        }

        var warnings = new List<string>();
        var silent = SessionInsights.SilentTracks(folded);
        if (silent.Contains(SessionInsights.MicrophoneSpeaker))
        {
            warnings.Add(Loc.Get("Warning_MicrophoneSilent"));
        }
        else if (silent.Count > 0)
        {
            warnings.Add(Loc.Get("Warning_SystemSilent"));
        }

        return new ParticipationResult(
            summary.MicrophoneShare,
            summary.Microphone,
            summary.System,
            SessionInsights.Timeline(folded, slots: 48, sessionDuration),
            warnings);
    }
}

/// <summary>
/// Right-hand side of the main window for one meeting: header (title, date, duration,
/// participation bar and timeline, warnings) and one view model per tab.
/// </summary>
internal sealed class MeetingDetailViewModel : ObservableObject
{
    public const int SummaryTab = 0;
    public const int TranscriptTab = 1;
    public const int NotesTab = 2;

    private readonly ISessionStore _store;
    private readonly ISessionActions _actions;
    private readonly CancellationTokenSource _lifetime = new();
    private int _selectedTab;
    private double _meShare;
    private string _participationText = string.Empty;
    private bool _isIdentifyingSpeakers;
    private string _statusText = string.Empty;
    private int _loadGeneration;

    public MeetingDetailViewModel(
        MeetingRowViewModel row,
        ISessionStore store,
        ISessionActions actions,
        IUiShell shell,
        MeetingCommands commands,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        PlayerViewModel? player = null)
    {
        Row = row;
        _store = store;
        _actions = actions;
        Commands = commands;
        Summary = new SummaryViewModel(store, actions, shell);
        player?.SetSession(row.SessionId);
        Transcript = new TranscriptViewModel(actions, player);
        Notes = new NotesViewModel(store, delay);
        DateText = row.Started == default ? string.Empty : UiFormat.Date(row.Started, "Format_MeetingDate");
        IdentifySpeakersCommand = new RelayCommand(
            _ => _ = IdentifySpeakersAsync(),
            _ => _actions.IsAiAvailable && !_isIdentifyingSpeakers && Row.IsDone);
        Row.PropertyChanged += OnRowPropertyChanged;
        Summary.CanGenerate = row.IsDone;
    }

    public MeetingRowViewModel Row { get; private set; }

    public MeetingCommands Commands { get; }

    /// <summary>
    /// A list refresh recreates the rows. The open detail moves to the new row of the same
    /// meeting instead of being rebuilt, so the notes being typed and the open tab survive.
    /// </summary>
    public void Rebind(MeetingRowViewModel row)
    {
        if (ReferenceEquals(row, Row))
        {
            return;
        }

        Row.PropertyChanged -= OnRowPropertyChanged;
        Row = row;
        Row.PropertyChanged += OnRowPropertyChanged;
        Summary.CanGenerate = row.IsDone;
        Summary.SetMeetingTitle(row.DisplayName);
        Raise(nameof(Row), nameof(Title), nameof(DurationText));
    }

    public SummaryViewModel Summary { get; }

    public TranscriptViewModel Transcript { get; }

    public NotesViewModel Notes { get; }

    public ICommand IdentifySpeakersCommand { get; }

    public bool IsAiAvailable => _actions.IsAiAvailable;

    public string Title => Row.DisplayName;

    public string DateText { get; }

    public string DurationText => Row.DurationText;

    public int SelectedTab
    {
        get => _selectedTab;
        set => Set(ref _selectedTab, Math.Clamp(value, SummaryTab, NotesTab));
    }

    /// <summary>My share of the speech, 0..100, for the participation bar.</summary>
    public double MeShare
    {
        get => _meShare;
        private set
        {
            if (Set(ref _meShare, value))
            {
                Raise(nameof(OthersShare));
            }
        }
    }

    public double OthersShare => 100d - _meShare;

    public string ParticipationText
    {
        get => _participationText;
        private set
        {
            if (Set(ref _participationText, value))
            {
                Raise(nameof(HasParticipation));
            }
        }
    }

    public bool HasParticipation => _participationText.Length > 0;

    public ObservableCollection<ParticipationSlotViewModel> Timeline { get; } = new();

    public bool HasTimeline => Timeline.Count > 0;

    public ObservableCollection<string> Warnings { get; } = new();

    public bool HasWarnings => Warnings.Count > 0;

    public bool IsIdentifyingSpeakers
    {
        get => _isIdentifyingSpeakers;
        private set => Set(ref _isIdentifyingSpeakers, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    /// <summary>Reads everything the tabs show. Called on selection and when transcription finishes.</summary>
    public async Task LoadAsync()
    {
        var generation = ++_loadGeneration;
        var token = _lifetime.Token;
        var sessionId = Row.SessionId;
        Summary.CanGenerate = Row.IsDone;

        IReadOnlyList<Speaker> speakers;
        try
        {
            speakers = await _store.LoadSpeakersAsync(sessionId, token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            speakers = Array.Empty<Speaker>();
        }

        if (generation != _loadGeneration || token.IsCancellationRequested)
        {
            return;
        }

        var labels = new SpeakerLabels(speakers);
        var summaryLoad = Summary.LoadAsync(sessionId, Title, labels, token);
        var notesLoad = Notes.IsLoaded ? Task.CompletedTask : Notes.LoadAsync(sessionId, token);
        await LoadTranscriptAsync(generation, labels, token).ConfigureAwait(true);
        await Task.WhenAll(summaryLoad, notesLoad).ConfigureAwait(true);
    }

    /// <summary>Saves the notes and stops pending loads. The detail is not used afterwards.</summary>
    public async Task CloseAsync()
    {
        Row.PropertyChanged -= OnRowPropertyChanged;
        Transcript.Detach();
        _lifetime.Cancel();
        await Notes.FlushAsync().ConfigureAwait(true);
    }

    private async Task LoadTranscriptAsync(int generation, SpeakerLabels labels, CancellationToken token)
    {
        Timeline.Clear();
        Warnings.Clear();
        MeShare = 0d;
        ParticipationText = string.Empty;
        if (Row.HasError && Row.ErrorText is { } error)
        {
            Warnings.Add(error);
        }

        RaiseCollections();

        if (!Row.HasReadableTranscript)
        {
            Transcript.ShowStatus(Loc.Get(Row.IsBusy ? "Transcript_InProgress" : "Transcript_NotYet"));
            return;
        }

        Transcript.ShowStatus(Loc.Get("Common_Loading"));
        var directory = Row.Directory;
        var duration = Row.Duration;
        TranscriptLoad load;
        try
        {
            load = await Task.Run(() => ReadTranscript(directory, duration, labels, token), token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (generation == _loadGeneration)
            {
                Transcript.ShowStatus(Loc.Get("Transcript_ReadFailed"));
            }

            return;
        }

        if (generation != _loadGeneration || token.IsCancellationRequested)
        {
            return;
        }

        Transcript.Load(Row.SessionId, load.Entries, load.EmptyStatus);

        var participation = load.Participation;
        if (participation.HasSpeech)
        {
            MeShare = participation.MeShare * 100d;
            var others = 1d - participation.MeShare;
            ParticipationText = Loc.Format(
                "Participation_Text",
                participation.MeShare.ToString("P0", Loc.Culture),
                UiFormat.Duration(participation.Me),
                others.ToString("P0", Loc.Culture),
                UiFormat.Duration(participation.Others));
        }

        foreach (var slot in participation.Timeline)
        {
            Timeline.Add(new ParticipationSlotViewModel(slot));
        }

        foreach (var warning in participation.Warnings)
        {
            Warnings.Add(warning);
        }

        RaiseCollections();
    }

    private static TranscriptLoad ReadTranscript(
        string directory,
        TimeSpan duration,
        SpeakerLabels labels,
        CancellationToken token)
    {
        var transcript = TranscriptExport.Read(directory);
        var emptyStatus = transcript.Status switch
        {
            TranscriptReadStatus.Missing => Loc.Get("Transcript_Missing"),
            TranscriptReadStatus.Invalid => Loc.Get("Transcript_ReadFailed"),
            TranscriptReadStatus.Empty => Loc.Get("Transcript_Empty"),
            _ => Loc.Get("Transcript_NoVisibleLines"),
        };

        if (transcript.Status is TranscriptReadStatus.Missing or TranscriptReadStatus.Invalid)
        {
            return new TranscriptLoad(Array.Empty<TranscriptEntryViewModel>(), emptyStatus, ParticipationResult.Empty);
        }

        var markers = SessionMetadata.TryRead(directory)?.Markers ?? new List<long>();
        var entries = TranscriptViewModel.BuildEntries(transcript.Segments, markers, labels, token);
        return new TranscriptLoad(entries, emptyStatus, ParticipationResult.Compute(transcript.Segments, duration));
    }

    private async Task IdentifySpeakersAsync()
    {
        IsIdentifyingSpeakers = true;
        StatusText = Loc.Get("Speakers_Identifying");
        try
        {
            await _actions.IdentifySpeakersAsync(Row.SessionId, _lifetime.Token).ConfigureAwait(true);
            StatusText = string.Empty;
            await LoadAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            StatusText = string.Empty;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            StatusText = exception is AiPackMissingException
                ? Loc.Get("Status_AiPackMissing")
                : Loc.Format("Speakers_Failed", exception.Message);
        }
        finally
        {
            IsIdentifyingSpeakers = false;
        }
    }

    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        switch (args.PropertyName)
        {
            case nameof(MeetingRowViewModel.DisplayName):
                Raise(nameof(Title));
                Summary.SetMeetingTitle(Title);
                break;
            case nameof(MeetingRowViewModel.IsDone):
                Summary.CanGenerate = Row.IsDone;
                break;
        }
    }

    private void RaiseCollections() => Raise(nameof(HasTimeline), nameof(HasWarnings));

    private sealed record TranscriptLoad(
        IReadOnlyList<TranscriptEntryViewModel> Entries,
        string EmptyStatus,
        ParticipationResult Participation);
}
