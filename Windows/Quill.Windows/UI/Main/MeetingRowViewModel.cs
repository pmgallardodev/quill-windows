using Quill.Core.Domain;
using Quill.Windows.Resources;
using Quill.Windows.Sessions;
using Quill.Windows.Transcription;
using Quill.Windows.UI.Theme.Controls;

namespace Quill.Windows.UI;

/// <summary>What the list needs beyond the legacy snapshot: who spoke and whether a summary exists.</summary>
internal sealed record MeetingRowDetails(IReadOnlyList<Speaker> Speakers, bool HasSummary)
{
    public static MeetingRowDetails None { get; } = new(Array.Empty<Speaker>(), false);
}

/// <summary>One speaker chip in a meetings list row.</summary>
internal sealed record SpeakerChipViewModel(string SpeakerId, string Label);

/// <summary>
/// One meeting in the list. Port of the v1 session row (state, progress, ETA, rename) plus
/// what the redesigned list shows: start time, day group, status and issue pills, speaker
/// chips and whether a summary exists.
/// </summary>
internal sealed class MeetingRowViewModel : ObservableObject
{
    public const int MaxSpeakerChips = 3;

    private SessionState _state;
    private double _micProgress;
    private double _systemProgress;
    private TimeSpan? _microphoneDuration;
    private TimeSpan? _systemDuration;
    private TimeSpan _elapsed;
    private string? _error;
    private string? _model;
    private TimeSpan? _transcriptionElapsed;
    private string? _title;
    private string _titleDraft = string.Empty;
    private bool _isEditingTitle;
    private bool _isPaused;
    private bool _hasSummary;

    public MeetingRowViewModel(SessionSnapshot snapshot, MeetingRowDetails details, DateTimeOffset now)
    {
        Directory = snapshot.Directory;
        Name = snapshot.Name;
        Duration = snapshot.Duration;
        Started = snapshot.Started;
        _model = snapshot.Model;
        _title = snapshot.Title;
        _state = snapshot.State;
        _error = snapshot.Error;
        _transcriptionElapsed = snapshot.TranscriptionElapsed;
        _hasSummary = details.HasSummary;

        DayGroup = MeetingGrouping.LabelFor(snapshot.Started, now);
        StartTimeText = snapshot.Started == default
            ? string.Empty
            : UiFormat.Date(snapshot.Started, "Format_Time");

        var labels = new SpeakerLabels(details.Speakers);
        var ids = details.Speakers.Select(speaker => speaker.Id).ToList();
        if (ids.Count == 0 && _state == SessionState.Done)
        {
            // v1 sessions have no speakers.json: the two tracks are all we know.
            ids.AddRange(new[] { SpeakerIds.Me, SpeakerIds.LegacyThem });
        }

        // "me" first, then the others in their file order.
        var ordered = ids
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => string.Equals(id, SpeakerIds.Me, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ToList();
        SpeakerChips = ordered
            .Take(MaxSpeakerChips)
            .Select(id => new SpeakerChipViewModel(id, labels.Label(id)))
            .ToList();
        var more = ordered.Count - SpeakerChips.Count;
        MoreSpeakersText = more > 0 ? $"+{more}" : string.Empty;
    }

    /// <summary>Set by the main view model so row context menus reach the window commands.</summary>
    public MeetingCommands? Commands { get; set; }

    public string Directory { get; }

    /// <summary>Folder name: the session id. Never changes.</summary>
    public string Name { get; }

    public string SessionId => Name;

    public TimeSpan Duration { get; }

    public DateTimeOffset Started { get; }

    /// <summary>Header of the day group the row belongs to ("Hoy", "Ayer", ...).</summary>
    public string DayGroup { get; }

    public string StartTimeText { get; }

    public IReadOnlyList<SpeakerChipViewModel> SpeakerChips { get; }

    public string MoreSpeakersText { get; }

    public bool HasMoreSpeakers => MoreSpeakersText.Length > 0;

    public bool HasSummary
    {
        get => _hasSummary;
        set => Set(ref _hasSummary, value);
    }

    public string? Model => _model;

    public string DisplayName => string.IsNullOrWhiteSpace(_title) ? Name : _title!;

    /// <summary>The folder, as a subtitle, only when a title replaced it.</summary>
    public string FolderText => string.IsNullOrWhiteSpace(_title) ? string.Empty : Name;

    public bool HasTitle => !string.IsNullOrWhiteSpace(_title);

    public bool IsEditingTitle
    {
        get => _isEditingTitle;
        private set
        {
            if (Set(ref _isEditingTitle, value))
            {
                Raise(nameof(IsNotEditingTitle));
            }
        }
    }

    public bool IsNotEditingTitle => !_isEditingTitle;

    public string TitleDraft
    {
        get => _titleDraft;
        set => Set(ref _titleDraft, value);
    }

    public string DurationText => UiFormat.Duration(Duration);

    public SessionState State => _state;

    public bool IsRecording => _state == SessionState.Recording;

    public bool IsPaused => IsRecording && _isPaused;

    public bool IsQueued => _state == SessionState.Queued;

    public bool IsRunning => _state == SessionState.Running;

    public bool IsFailed => _state == SessionState.Failed;

    public bool IsDone => _state == SessionState.Done;

    public bool IsDisabled => _state == SessionState.Disabled;

    public bool IsAbandoned => _state == SessionState.Abandoned;

    public bool CanRetry => IsFailed || IsDisabled;

    /// <summary>Recording or transcribing: files are open, deleting would destroy work in progress.</summary>
    public bool IsBusy => IsRecording || IsQueued || IsRunning;

    public bool CanDelete => !IsBusy;

    public bool CanRename => !IsRecording && !IsAbandoned;

    /// <summary>A failed run can still leave a usable transcript behind.</summary>
    public bool HasReadableTranscript =>
        IsDone || (IsFailed && File.Exists(Path.Combine(Directory, "transcript.json")));

    public bool HasError => IsFailed && !string.IsNullOrWhiteSpace(_error);

    public string? ErrorText => _error;

    public double MicProgress => _micProgress * 100d;

    public double SystemProgress => _systemProgress * 100d;

    /// <summary>Weighted by track length, 0..1.</summary>
    public double Progress
    {
        get
        {
            var microphoneWeight = _microphoneDuration?.TotalSeconds > 0 ? _microphoneDuration.Value.TotalSeconds : 1d;
            var systemWeight = _systemDuration?.TotalSeconds > 0 ? _systemDuration.Value.TotalSeconds : 1d;
            return (_micProgress * microphoneWeight + _systemProgress * systemWeight) / (microphoneWeight + systemWeight);
        }
    }

    /// <summary>Activity pill: recording, paused or transcribing. Null when the meeting is idle.</summary>
    public PillStatus? StatusPill => IsPaused
        ? PillStatus.Paused
        : _state switch
        {
            SessionState.Recording => PillStatus.Recording,
            SessionState.Queued or SessionState.Running => PillStatus.Transcribing,
            _ => null,
        };

    public bool HasStatusPill => StatusPill is not null;

    public PillStatus StatusPillValue => StatusPill ?? PillStatus.Recording;

    /// <summary>Pill text: the default label, the queue, or the transcription percentage.</summary>
    public string StatusPillText => StatusPill switch
    {
        PillStatus.Transcribing when IsQueued => Loc.Get("Meeting_Queued"),
        PillStatus.Transcribing when Progress > 0d => Loc.Format("Meeting_TranscribingPercent", (int)Math.Round(Progress * 100d)),
        PillStatus status => Loc.Get(StatusPillLabelKey(status)),
        null => string.Empty,
    };

    /// <summary>Problem pill: failed, incomplete or transcription off. Empty when there is none.</summary>
    public string IssueText => _state switch
    {
        SessionState.Failed => Loc.Get("Meeting_IssueFailed"),
        SessionState.Abandoned => Loc.Get("Meeting_IssueAbandoned"),
        SessionState.Disabled => Loc.Get("Meeting_IssueDisabled"),
        _ => string.Empty,
    };

    public bool HasIssue => IssueText.Length > 0;

    public string ElapsedText => _transcriptionElapsed is { } elapsed
        ? Loc.Format("Meeting_TranscribedIn", UiFormat.Duration(elapsed)) +
          (Model is null ? string.Empty : $" · {Model}")
        : string.Empty;

    /// <summary>
    /// Estimate from observed progress: self-correcting, needs nothing about model or hardware.
    /// Hidden below 5 % so the first seconds do not show absurd numbers.
    /// </summary>
    public string EtaText
    {
        get
        {
            var fraction = Progress;
            if (!IsRunning || fraction < 0.05d || _elapsed <= TimeSpan.Zero)
            {
                return string.Empty;
            }

            var remaining = TimeSpan.FromSeconds(_elapsed.TotalSeconds / fraction * (1d - fraction));
            return Loc.Format("Meeting_Remaining", UiFormat.Duration(remaining));
        }
    }

    public void BeginRename()
    {
        TitleDraft = DisplayName;
        IsEditingTitle = true;
    }

    public void CancelRename() => IsEditingTitle = false;

    /// <summary>
    /// Saves the title. An empty box clears it and returns the meeting to its folder name,
    /// the only way to undo a rename.
    /// </summary>
    public bool CommitRename()
    {
        if (!IsEditingTitle)
        {
            return true;
        }

        var candidate = string.IsNullOrWhiteSpace(TitleDraft) || TitleDraft.Trim() == Name
            ? null
            : TitleDraft.Trim();

        if (candidate == _title)
        {
            IsEditingTitle = false;
            return true;
        }

        if (!SessionMetadata.TryUpdateTitle(Directory, candidate))
        {
            return false;
        }

        IsEditingTitle = false;
        _title = candidate;
        Raise(nameof(DisplayName), nameof(FolderText), nameof(HasTitle));
        return true;
    }

    public void Apply(TranscriptionUpdate update)
    {
        _state = update.State;
        if (!string.IsNullOrWhiteSpace(update.Model))
        {
            _model = update.Model;
        }

        if (_state != SessionState.Recording)
        {
            _isPaused = false;
        }

        _error = update.Error;
        _elapsed = update.Elapsed;

        if ((update.State is SessionState.Queued or SessionState.Running) && update.Tracks.Count == 0)
        {
            // A retry starts from zero; the previous failed run must not contaminate
            // the ETA or the first progress frame.
            _micProgress = 0d;
            _systemProgress = 0d;
            _microphoneDuration = null;
            _systemDuration = null;
            _transcriptionElapsed = null;
        }

        foreach (var track in update.Tracks)
        {
            if (track.Track == "mic")
            {
                _micProgress = track.Fraction;
                _microphoneDuration = track.Duration ?? _microphoneDuration;
            }
            else if (track.Track == "system")
            {
                _systemProgress = track.Fraction;
                _systemDuration = track.Duration ?? _systemDuration;
            }
        }

        if (update.State == SessionState.Done)
        {
            _micProgress = 1d;
            _systemProgress = 1d;
            _transcriptionElapsed = update.Elapsed;
        }

        RaiseState();
    }

    public void SetPaused(bool paused)
    {
        if (_isPaused == paused)
        {
            return;
        }

        _isPaused = paused;
        RaiseState();
    }

    private void RaiseState() =>
        Raise(
            nameof(State), nameof(IsRecording), nameof(IsPaused), nameof(IsQueued), nameof(IsRunning),
            nameof(IsFailed), nameof(IsDone), nameof(IsDisabled), nameof(IsAbandoned), nameof(CanRetry),
            nameof(IsBusy), nameof(CanDelete), nameof(CanRename), nameof(HasReadableTranscript),
            nameof(HasError), nameof(ErrorText), nameof(MicProgress), nameof(SystemProgress), nameof(Progress),
            nameof(StatusPill), nameof(HasStatusPill), nameof(StatusPillValue), nameof(StatusPillText),
            nameof(IssueText), nameof(HasIssue), nameof(EtaText), nameof(Model), nameof(ElapsedText));

    private static string StatusPillLabelKey(PillStatus status) => status switch
    {
        PillStatus.Recording => "Status_Recording",
        PillStatus.Paused => "Status_Paused",
        PillStatus.Transcribing => "Status_Transcribing",
        _ => "Status_Summarizing",
    };
}

/// <summary>
/// The window commands a row or the detail header can reach from a context menu (which
/// lives outside the window's visual tree). Each takes the <see cref="MeetingRowViewModel"/>.
/// </summary>
internal sealed record MeetingCommands(
    System.Windows.Input.ICommand Retry,
    System.Windows.Input.ICommand OpenFolder,
    System.Windows.Input.ICommand OpenTranscript,
    System.Windows.Input.ICommand OpenLog,
    System.Windows.Input.ICommand Rename,
    System.Windows.Input.ICommand Delete,
    System.Windows.Input.ICommand Copy,
    System.Windows.Input.ICommand ExportSrt,
    System.Windows.Input.ICommand ExportVtt);
