using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Quill.Core.Domain;
using Quill.Core.Live;
using Quill.Core.Summaries;
using Quill.Windows.Resources;
using Quill.Windows.UI.Theme.Controls;

namespace Quill.Windows.UI.Companion;

/// <summary>One live transcript line.</summary>
internal sealed record TranscriptRow(TranscriptSegment Segment)
{
    public string TimeLabel => TimestampLink.Format(Segment.Start);

    public string SpeakerId => Segment.SpeakerId;

    public string Text => Segment.Text;
}

/// <summary>
/// State and commands of the companion panel. Everything here runs on the UI thread: events
/// from the live transcript, the live summary and the autosaver arrive on background threads
/// and are marshalled through the <c>post</c> callback. <see cref="Tick"/> is polled by the
/// window (timer, levels, pause state, toast expiry).
/// </summary>
internal sealed class CompanionViewModel : INotifyPropertyChanged
{
    /// <summary>Notices from the host (notifications kept in-app) stay longer than confirmations.</summary>
    private static readonly TimeSpan NoticeMinimumDuration = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan ToastDuration = TimeSpan.FromSeconds(3);

    private readonly CompanionContext _context;
    private readonly ILiveSummaryFeed? _summary;
    private readonly Action<Action> _post;
    private readonly Action<string> _copyText;
    private readonly Func<DateTimeOffset> _now;
    private readonly NotesAutosaver _notes;
    private CancellationTokenSource? _catchUp;
    private DateTimeOffset _toastUntil;
    private bool _closed;

    private string _elapsedLabel = CompanionPresenter.ElapsedLabel(TimeSpan.Zero);
    private bool _isPaused;
    private double _microphoneLevel;
    private double _systemLevel;
    private bool _isCollapsed;
    private int _selectedTab;
    private CompanionSummaryView _summaryView = CompanionSummaryView.Empty;
    private LiveSummaryBanner _banner;
    private CatchUpState _catchUpState = CatchUpState.Hidden;
    private CompanionSummaryView _catchUpView = CompanionSummaryView.Empty;
    private string _notesText = string.Empty;
    private bool _notesLoaded;
    private bool _notesLoadFailed;
    private NotesSaveState _notesSaveState = NotesSaveState.Saved;
    private bool _notesTouched;
    private string _quickNoteText = string.Empty;
    private string? _toast;

    public CompanionViewModel(
        CompanionContext context,
        ILiveSummaryFeed? summary,
        Action<Action> post,
        Action<string> copyText,
        Func<DateTimeOffset>? now = null,
        TimeSpan? notesDelay = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _summary = summary;
        _post = post ?? throw new ArgumentNullException(nameof(post));
        _copyText = copyText ?? throw new ArgumentNullException(nameof(copyText));
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _notes = new NotesAutosaver(
            (baseline, markdown, token) => _context.Store.SaveNotesAsync(_context.SessionId, baseline, markdown, token),
            notesDelay);

        TogglePauseCommand = new CompanionCommand(TogglePause, () => !_closed);
        StopCommand = new CompanionCommand(Stop, () => !_closed);
        MarkCommand = new CompanionCommand(Mark, () => !_closed);
        WhatDidIMissCommand = new CompanionCommand(() => _ = CatchUpAsync(), () => _catchUpState != CatchUpState.Loading);
        CloseCatchUpCommand = new CompanionCommand(CloseCatchUp);
        CopyNoticeCommand = new CompanionCommand(CopyNotice);
        AddQuickNoteCommand = new CompanionCommand(AddQuickNote, () => IsNotesEditable && !string.IsNullOrWhiteSpace(_quickNoteText));
        ToggleCollapsedCommand = new CompanionCommand(() => IsCollapsed = !IsCollapsed);
        ExpandCommand = new CompanionCommand(() => IsCollapsed = false);

        _banner = BannerNow();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public CompanionCommand TogglePauseCommand { get; }

    public CompanionCommand StopCommand { get; }

    public CompanionCommand MarkCommand { get; }

    public CompanionCommand WhatDidIMissCommand { get; }

    public CompanionCommand CloseCatchUpCommand { get; }

    public CompanionCommand CopyNoticeCommand { get; }

    public CompanionCommand AddQuickNoteCommand { get; }

    public CompanionCommand ToggleCollapsedCommand { get; }

    /// <summary>Expands the panel (pill click).</summary>
    public CompanionCommand ExpandCommand { get; }

    // Header

    public string ElapsedLabel
    {
        get => _elapsedLabel;
        private set => Set(ref _elapsedLabel, value);
    }

    public bool IsPaused
    {
        get => _isPaused;
        private set
        {
            if (Set(ref _isPaused, value))
            {
                OnPropertyChanged(nameof(Status));
                OnPropertyChanged(nameof(PauseLabel));
            }
        }
    }

    public PillStatus Status => _isPaused ? PillStatus.Paused : PillStatus.Recording;

    public string PauseLabel => Loc.Get(_isPaused ? "Action_Resume" : "Action_Pause");

    public bool HasMicrophoneLevel => _context.MicrophoneLevel is not null;

    public bool HasSystemLevel => _context.SystemLevel is not null;

    public double MicrophoneLevel
    {
        get => _microphoneLevel;
        private set => Set(ref _microphoneLevel, value);
    }

    public double SystemLevel
    {
        get => _systemLevel;
        private set => Set(ref _systemLevel, value);
    }

    public bool IsCollapsed
    {
        get => _isCollapsed;
        set => Set(ref _isCollapsed, value);
    }

    public int SelectedTab
    {
        get => _selectedTab;
        set => Set(ref _selectedTab, value);
    }

    /// <summary>Short confirmation ("Momento marcado en 12:04"), cleared after a few seconds.</summary>
    public string? Toast
    {
        get => _toast;
        private set
        {
            if (Set(ref _toast, value))
            {
                OnPropertyChanged(nameof(HasToast));
            }
        }
    }

    public bool HasToast => !string.IsNullOrEmpty(_toast);

    // Live summary

    public CompanionSummaryView SummaryView
    {
        get => _summaryView;
        private set
        {
            if (Set(ref _summaryView, value))
            {
                OnPropertyChanged(nameof(HasSummary));
                OnPropertyChanged(nameof(HasExecutiveSummary));
                OnPropertyChanged(nameof(HasNoDecisions));
                OnPropertyChanged(nameof(HasNoActionItems));
                OnPropertyChanged(nameof(HasNoOpenQuestions));
            }
        }
    }

    public bool HasSummary => !_summaryView.IsEmpty;

    public bool HasExecutiveSummary => !string.IsNullOrWhiteSpace(_summaryView.ExecutiveSummary);

    public bool HasNoDecisions => _summaryView.Decisions.Count == 0;

    public bool HasNoActionItems => _summaryView.ActionItems.Count == 0;

    public bool HasNoOpenQuestions => _summaryView.OpenQuestions.Count == 0;

    public LiveSummaryBanner Banner
    {
        get => _banner;
        private set
        {
            if (Set(ref _banner, value))
            {
                OnPropertyChanged(nameof(BannerText));
                OnPropertyChanged(nameof(HasBanner));
                OnPropertyChanged(nameof(IsBannerAttention));
                OnPropertyChanged(nameof(IsAiPackMissing));
            }
        }
    }

    public string? BannerText => CompanionPresenter.BannerKey(_banner) is { } key ? Loc.Get(key) : null;

    public bool HasBanner => _banner != LiveSummaryBanner.None;

    /// <summary>Delayed or failed: shown with the warning color.</summary>
    public bool IsBannerAttention => _banner is LiveSummaryBanner.Delayed or LiveSummaryBanner.Failed;

    public bool IsAiPackMissing => _banner == LiveSummaryBanner.AiPackMissing;

    // Catch-up

    public CatchUpState CatchUpState
    {
        get => _catchUpState;
        private set
        {
            if (Set(ref _catchUpState, value))
            {
                OnPropertyChanged(nameof(IsCatchUpVisible));
                OnPropertyChanged(nameof(IsCatchUpLoading));
                OnPropertyChanged(nameof(IsCatchUpReady));
                OnPropertyChanged(nameof(CatchUpMessage));
                OnPropertyChanged(nameof(HasCatchUpMessage));
                WhatDidIMissCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public CompanionSummaryView CatchUpView
    {
        get => _catchUpView;
        private set => Set(ref _catchUpView, value);
    }

    public string CatchUpTitle => Loc.Format("Companion_CatchUp_Title", (int)CatchUpWindow.TotalMinutes);

    /// <summary>Window of "¿Qué me perdí?" for this recording.</summary>
    public TimeSpan CatchUpWindow => _context.CatchUpWindow > TimeSpan.Zero ? _context.CatchUpWindow : CatchUpSummarizer.DefaultWindow;

    public bool IsCatchUpVisible => _catchUpState != CatchUpState.Hidden;

    public bool IsCatchUpLoading => _catchUpState == CatchUpState.Loading;

    public bool IsCatchUpReady => _catchUpState == CatchUpState.Ready;

    public string? CatchUpMessage => _catchUpState switch
    {
        CatchUpState.Loading => Loc.Get("Companion_CatchUp_Loading"),
        CatchUpState.Empty => Loc.Get("Companion_CatchUp_Empty"),
        CatchUpState.AiPackMissing => Loc.Get("Companion_CatchUp_AiPackMissing"),
        CatchUpState.Failed => Loc.Get("Companion_CatchUp_Failed"),
        _ => null,
    };

    public bool HasCatchUpMessage => CatchUpMessage is not null;

    // Transcript

    public ObservableCollection<TranscriptRow> TranscriptRows { get; } = [];

    public bool HasLiveTranscript => _context.Transcript is not null;

    public bool IsTranscriptEmpty => _context.Transcript is not null && TranscriptRows.Count == 0;

    // Notes

    /// <summary>Raw notes.md. Edits are saved after a short pause in typing.</summary>
    public string NotesText
    {
        get => _notesText;
        set
        {
            if (!IsNotesEditable)
            {
                OnPropertyChanged();
                return;
            }

            if (Set(ref _notesText, value ?? string.Empty))
            {
                _notesTouched = true;
                _notes.Update(_notesText);
            }
        }
    }

    public bool IsNotesEditable => _notesLoaded && !_notesLoadFailed && !_closed;

    public string QuickNoteText
    {
        get => _quickNoteText;
        set
        {
            if (Set(ref _quickNoteText, value ?? string.Empty))
            {
                AddQuickNoteCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string NotesStatusText
    {
        get
        {
            if (_notesLoadFailed)
            {
                return Loc.Get("Companion_Notes_LoadFailed");
            }

            if (!_notesLoaded)
            {
                return Loc.Get("Companion_Notes_Loading");
            }

            if (!_notesTouched)
            {
                return Loc.Get("Companion_Notes_Hint");
            }

            return Loc.Get(_notesSaveState switch
            {
                NotesSaveState.Pending => "Companion_Notes_Pending",
                NotesSaveState.Saving => "Companion_Notes_Saving",
                NotesSaveState.Failed => "Companion_Notes_Failed",
                _ => "Companion_Notes_Saved",
            });
        }
    }

    public bool IsNotesProblem => _notesLoadFailed || _notesSaveState == NotesSaveState.Failed;

    /// <summary>Subscribes to the live sources and loads notes.md. Call once on the UI thread.</summary>
    public async Task InitializeAsync()
    {
        if (_summary is not null)
        {
            _summary.Updated += OnSummaryUpdated;
            _summary.StateChanged += OnSummaryStateChanged;
            SummaryView = CompanionPresenter.View(_summary.Current);
            Banner = BannerNow();
        }

        if (_context.Transcript is { } transcript)
        {
            transcript.SegmentAdded += OnSegmentAdded;
            ResyncTranscript();
        }

        _notes.StateChanged += OnNotesStateChanged;
        Tick();

        try
        {
            var markdown = await _context.Store.LoadNotesAsync(_context.SessionId, CancellationToken.None);
            _notesText = markdown ?? string.Empty;
            _notes.SetBaseline(_notesText);
            _notesLoaded = true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Saving now could overwrite notes the user already has: keep editing off.
            Trace.TraceWarning($"quill: notes not loaded: {exception.Message}");
            _notesLoadFailed = true;
        }

        OnPropertyChanged(nameof(NotesText));
        OnNotesChanged();
    }

    /// <summary>Polled by the window: timer, pause state, levels, toast expiry.</summary>
    public void Tick()
    {
        if (_closed)
        {
            return;
        }

        ElapsedLabel = CompanionPresenter.ElapsedLabel(SafeElapsed());
        IsPaused = Safe(_context.IsPaused, false);
        MicrophoneLevel = _isPaused ? 0 : LevelMeter.ClampUnit(Safe(_context.MicrophoneLevel, 0));
        SystemLevel = _isPaused ? 0 : LevelMeter.ClampUnit(Safe(_context.SystemLevel, 0));
        if (_toast is not null && _now() >= _toastUntil)
        {
            Toast = null;
        }
    }

    /// <summary>Unsubscribes, cancels a running catch-up and writes pending notes. Never throws.</summary>
    public async Task<bool> ShutdownAsync()
    {
        if (_closed)
        {
            return true;
        }

        await _notes.FlushAsync().ConfigureAwait(false);
        if (_notes.State == NotesSaveState.Failed)
        {
            OnNotesChanged();
            return false;
        }

        _closed = true;
        if (_summary is not null)
        {
            _summary.Updated -= OnSummaryUpdated;
            _summary.StateChanged -= OnSummaryStateChanged;
        }

        if (_context.Transcript is { } transcript)
        {
            transcript.SegmentAdded -= OnSegmentAdded;
        }

        _catchUp?.Cancel();
        _notes.StateChanged -= OnNotesStateChanged;
        return true;
    }

    /// <summary>"¿Qué me perdí?": recap of the last <see cref="CatchUpWindow"/>.</summary>
    public async Task CatchUpAsync()
    {
        if (_catchUpState == CatchUpState.Loading || _closed)
        {
            return;
        }

        if (_context.CatchUp is not { } catchUp)
        {
            CatchUpState = CatchUpState.AiPackMissing;
            return;
        }

        var cancellation = new CancellationTokenSource();
        _catchUp = cancellation;
        CatchUpView = CompanionSummaryView.Empty;
        CatchUpState = CatchUpState.Loading;
        SummaryOutcome outcome;
        try
        {
            // Off the UI thread: the model may load synchronously on first use.
            outcome = await Task.Run(() => catchUp(CatchUpWindow, cancellation.Token), cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Trace.TraceWarning($"quill: catch-up failed: {exception.Message}");
            outcome = SummaryOutcome.Failed(exception.Message);
        }

        if (cancellation.IsCancellationRequested || _closed)
        {
            return;
        }

        CatchUpView = CompanionPresenter.View(outcome.Summary);
        CatchUpState = CompanionPresenter.CatchUpStateOf(outcome);
    }

    private void TogglePause()
    {
        Invoke(_context.TogglePause);
        Tick();
    }

    private void Stop() => Invoke(_context.Stop);

    private void Mark()
    {
        var at = SafeElapsed();
        if (Invoke(_context.AddMarker))
        {
            ShowToast(Loc.Format("Companion_MarkerAdded", CompanionPresenter.ElapsedLabel(at)));
        }
    }

    private void CloseCatchUp()
    {
        _catchUp?.Cancel();
        CatchUpState = CatchUpState.Hidden;
    }

    private void CopyNotice()
    {
        try
        {
            _copyText(Loc.Get("Companion_Notice_Text"));
            ShowToast(Loc.Get("Companion_Notice_Copied"));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The clipboard can be locked by another process for a moment.
            Trace.TraceWarning($"quill: recording notice not copied: {exception.Message}");
            ShowToast(Loc.Get("Companion_Notice_CopyFailed"));
        }
    }

    private void AddQuickNote()
    {
        if (!IsNotesEditable || string.IsNullOrWhiteSpace(_quickNoteText))
        {
            return;
        }

        NotesText = CompanionPresenter.AppendNote(_notesText, _quickNoteText, SafeElapsed());
        QuickNoteText = string.Empty;
    }

    private void ShowToast(string message)
    {
        _toastUntil = _now() + ToastDuration;
        Toast = message;
    }

    /// <summary>
    /// Shows a host notice (a notification kept in-app in discreet mode) in the toast area for
    /// <paramref name="duration"/>, at least a few seconds. UI thread.
    /// </summary>
    public void ShowNotice(string message, TimeSpan duration)
    {
        if (_closed || string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        _toastUntil = _now() + (duration > NoticeMinimumDuration ? duration : NoticeMinimumDuration);
        Toast = message;
    }

    private LiveSummaryBanner BannerNow() =>
        CompanionPresenter.Banner(
            _summary is not null,
            _context.IsAiPackInstalled,
            _summary?.State ?? LiveSummaryState.Idle,
            !_summaryView.IsEmpty);

    private void OnSummaryUpdated(object? sender, MeetingSummary summary) => _post(() =>
    {
        if (_closed)
        {
            return;
        }

        SummaryView = CompanionPresenter.View(summary);
        Banner = BannerNow();
    });

    private void OnSummaryStateChanged(object? sender, LiveSummaryState state) => _post(() =>
    {
        if (!_closed)
        {
            Banner = BannerNow();
        }
    });

    private void OnSegmentAdded(object? sender, LiveSegmentAddedEventArgs e) => _post(() =>
    {
        if (_closed)
        {
            return;
        }

        var result = TranscriptRowsSync.Apply(TranscriptRows, e.Segment, e.Index, row => row.Segment, segment => new TranscriptRow(segment));
        if (result == TranscriptSyncResult.NeedsResync)
        {
            ResyncTranscript();
        }

        OnPropertyChanged(nameof(IsTranscriptEmpty));
    });

    private void OnNotesStateChanged(object? sender, NotesSaveState state) => _post(() =>
    {
        _notesSaveState = state;
        OnNotesChanged();
    });

    private void ResyncTranscript()
    {
        if (_context.Transcript is not { } transcript)
        {
            return;
        }

        TranscriptRows.Clear();
        foreach (var segment in transcript.Snapshot())
        {
            TranscriptRows.Add(new TranscriptRow(segment));
        }

        OnPropertyChanged(nameof(IsTranscriptEmpty));
    }

    private void OnNotesChanged()
    {
        OnPropertyChanged(nameof(IsNotesEditable));
        OnPropertyChanged(nameof(NotesStatusText));
        OnPropertyChanged(nameof(IsNotesProblem));
        AddQuickNoteCommand.RaiseCanExecuteChanged();
    }

    private TimeSpan SafeElapsed() => Safe(_context.Elapsed, TimeSpan.Zero);

    /// <summary>Host callbacks must never take the panel down with them.</summary>
    private static bool Invoke(Action action)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Trace.TraceWarning($"quill: companion action failed: {exception.Message}");
            return false;
        }
    }

    private static T Safe<T>(Func<T>? read, T fallback)
    {
        if (read is null)
        {
            return fallback;
        }

        try
        {
            return read();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Trace.TraceWarning($"quill: companion read failed: {exception.Message}");
            return fallback;
        }
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
