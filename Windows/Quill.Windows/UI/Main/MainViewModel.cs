using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Input;
using System.Windows.Threading;
using Quill.Core.Ports;
using Quill.Core.Storage;
using Quill.Windows.Import;
using Quill.Windows.Playback;
using Quill.Windows.Resources;
using Quill.Windows.Sessions;
using Quill.Windows.Transcription;
using Speaker = Quill.Core.Domain.Speaker;

namespace Quill.Windows.UI;

/// <summary>A transcript fragment found by the cross-meeting search, with its meeting.</summary>
internal sealed class MeetingSearchHitViewModel
{
    public MeetingSearchHitViewModel(TranscriptHit hit, string query)
    {
        Directory = hit.Directory;
        SessionLabel = hit.SessionLabel;
        At = TimeSpan.FromMilliseconds(hit.StartMs);
        TimeText = TranscriptExport.Clock(hit.StartMs);
        SpeakerText = SpeakerLabels.DefaultLabel(hit.Speaker);
        Runs = TextHighlight.Split(hit.Text, query);
    }

    public string Directory { get; }

    public string SessionLabel { get; }

    public TimeSpan At { get; }

    public string TimeText { get; }

    public string SpeakerText { get; }

    public IReadOnlyList<HighlightRun> Runs { get; }
}

/// <summary>
/// Main window: meetings grouped by day, the open meeting's detail, the cross-meeting
/// search and "Mis tareas". Replaces the v1 sessions view model and keeps all of its
/// behaviour: live progress, delete, copy, SRT/VTT export, retry, open folder/log, rename
/// and the active (recording) session.
/// </summary>
internal sealed class MainViewModel : ObservableObject, IDisposable
{
    /// <summary>Enough to find what is being looked for without flooding the list.</summary>
    private const int MaxSearchResults = 300;

    private readonly string _recordingsRoot;
    private readonly TranscriptionCoordinator _transcription;
    private readonly Dispatcher _dispatcher;
    private readonly ISessionStore _store;
    private readonly ISessionActions _actions;
    private readonly IUiShell _shell;
    private readonly IClock _clock;
    private readonly IRecordingImporter _importer;
    private readonly DispatcherTimer _pump;
    private readonly DispatcherTimer _searchDebounce;
    private readonly ConcurrentDictionary<string, TranscriptionUpdate> _inFlight = new(StringComparer.OrdinalIgnoreCase);

    private string? _activeSessionDirectory;
    private bool _activeSessionPaused;
    private MeetingRowViewModel? _selectedMeeting;
    private MeetingDetailViewModel? _detail;
    private bool _restoringSelection;
    private string? _pendingSelectionDirectory;
    private string _searchQuery = string.Empty;
    private string _searchStatus = string.Empty;
    private string _statusText = string.Empty;
    private bool _isMyTasksVisible;
    private CancellationTokenSource? _searchCancellation;
    private CancellationTokenSource? _refreshCancellation;
    private CancellationTokenSource? _myTasksCancellation;
    private int _refreshGeneration;
    private bool _disposed;

    // Tab (and transcript query) to show once the requested meeting's detail exists. Opening
    // a detail is asynchronous: it first saves the notes of the previous one.
    private (int Tab, string? Query)? _pendingOpen;

    public MainViewModel(
        string recordingsRoot,
        TranscriptionCoordinator transcription,
        Dispatcher dispatcher,
        ISessionStore? store = null,
        ISessionActions? actions = null,
        IUiShell? shell = null,
        IClock? clock = null,
        IRecordingImporter? importer = null)
    {
        _importer = importer ?? new RecordingImporter(recordingsRoot);
        _recordingsRoot = recordingsRoot;
        _transcription = transcription;
        _dispatcher = dispatcher;
        _store = store ?? new FileSessionStore(recordingsRoot);
        // One player for the window; every timestamp link (transcript and summary) plays through it.
        Player = new PlayerViewModel(new SessionAudioSource(_store), dispatcher);
        _actions = new PlaybackSessionActions(actions ?? AiPackMissingSessionActions.Instance, Player);
        _shell = shell ?? WpfUiShell.Instance;
        _clock = clock ?? SystemClock.Instance;

        Commands = new MeetingCommands(
            Retry: new RelayCommand(parameter => WithRow(parameter, Retry), parameter => RowOf(parameter)?.CanRetry == true),
            OpenFolder: new RelayCommand(parameter => WithRow(parameter, row => Open(row.Directory))),
            OpenTranscript: new RelayCommand(parameter => WithRow(parameter, row => Open(Path.Combine(row.Directory, "transcript.md")))),
            OpenLog: new RelayCommand(parameter => WithRow(parameter, row => Open(Path.Combine(row.Directory, "transcribe.log")))),
            Rename: new RelayCommand(parameter => WithRow(parameter, BeginRename), parameter => RowOf(parameter)?.CanRename == true),
            Delete: new RelayCommand(parameter => WithRow(parameter, Delete), parameter => RowOf(parameter)?.CanDelete == true),
            Copy: new RelayCommand(parameter => WithRow(parameter, Copy)),
            ExportSrt: new RelayCommand(parameter => WithRow(parameter, row => Export(row, srt: true))),
            ExportVtt: new RelayCommand(parameter => WithRow(parameter, row => Export(row, srt: false))));

        OpenHitCommand = new RelayCommand(parameter =>
        {
            if (parameter is MeetingSearchHitViewModel hit)
            {
                OpenHit(hit);
            }
        });
        ClearSearchCommand = new RelayCommand(_ => SearchQuery = string.Empty);
        RefreshCommand = new RelayCommand(_ => Refresh());
        ShowMeetingsCommand = new RelayCommand(_ => IsMyTasksVisible = false);
        ShowMyTasksCommand = new RelayCommand(_ => IsMyTasksVisible = true);
        MyTasks = new MyTasksViewModel(_store, OpenMeeting);

        // The coordinator publishes without limit. Accumulate here and flush to the view
        // at a fixed rate: marshalling every segment to the dispatcher would choke it.
        _pump = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };
        _pump.Tick += (_, _) => Drain();

        // Searching twenty transcripts costs something: not on every key.
        _searchDebounce = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(300),
        };
        _searchDebounce.Tick += (_, _) => RunSearch();

        _transcription.StateChanged += OnUpdate;
        _transcription.ProgressChanged += OnUpdate;
    }

    /// <summary>Newest first. The view groups them by <see cref="MeetingRowViewModel.DayGroup"/>.</summary>
    public ObservableCollection<MeetingRowViewModel> Meetings { get; } = new();

    public ObservableCollection<MeetingSearchHitViewModel> SearchResults { get; } = new();

    public MeetingCommands Commands { get; }

    public MyTasksViewModel MyTasks { get; }

    /// <summary>Audio of the open meeting (player bar of the Transcript tab).</summary>
    public PlayerViewModel Player { get; }

    public ICommand OpenHitCommand { get; }

    public ICommand ClearSearchCommand { get; }

    public ICommand RefreshCommand { get; }

    public ICommand ShowMeetingsCommand { get; }

    public ICommand ShowMyTasksCommand { get; }

    public bool IsAiAvailable => _actions.IsAiAvailable;

    public bool HasMeetings => Meetings.Count > 0;

    public MeetingRowViewModel? SelectedMeeting
    {
        get => _selectedMeeting;
        set
        {
            if (ReferenceEquals(_selectedMeeting, value))
            {
                return;
            }

            _selectedMeeting = value;
            Raise();
            if (!_restoringSelection)
            {
                _ = OpenDetailAsync(value);
            }
        }
    }

    /// <summary>The open meeting. Null when nothing is selected.</summary>
    public MeetingDetailViewModel? Detail
    {
        get => _detail;
        private set
        {
            if (Set(ref _detail, value))
            {
                Raise(nameof(HasDetail));
            }
        }
    }

    public bool HasDetail => _detail is not null;

    public bool IsMyTasksVisible
    {
        get => _isMyTasksVisible;
        set
        {
            if (!Set(ref _isMyTasksVisible, value))
            {
                return;
            }

            Raise(nameof(IsMeetingsVisible));
            if (value)
            {
                _ = LoadMyTasksAsync();
            }
            else if (_detail is not null)
            {
                // A task ticked in "Mis tareas" changed a summary the detail may show.
                _ = _detail.LoadAsync();
            }
        }
    }

    public bool IsMeetingsVisible => !_isMyTasksVisible;

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (!Set(ref _searchQuery, value ?? string.Empty))
            {
                return;
            }

            Raise(nameof(IsSearching));
            _searchDebounce.Stop();
            if (IsSearching)
            {
                SearchStatus = Loc.Get("Search_Searching");
                _searchDebounce.Start();
            }
            else
            {
                CancelSearch();
                SearchResults.Clear();
                SearchStatus = string.Empty;
            }
        }
    }

    /// <summary>With an active search the list shows results instead of meetings.</summary>
    public bool IsSearching => !string.IsNullOrWhiteSpace(_searchQuery);

    public string SearchStatus
    {
        get => _searchStatus;
        private set => Set(ref _searchStatus, value);
    }

    /// <summary>Result of the last action: export, copy, delete.</summary>
    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    public void Start()
    {
        _pump.Start();
        if (IsSearching)
        {
            _searchDebounce.Stop();
            _searchDebounce.Start();
        }
    }

    /// <summary>Window hidden: stop timers and pending reads, save the notes being edited.</summary>
    public void Stop()
    {
        if (Player.IsPlaying)
        {
            _ = Player.PlayPauseAsync();
        }

        _pump.Stop();
        _searchDebounce.Stop();
        CancelSearch();
        _refreshGeneration++;
        _refreshCancellation?.Cancel();
        _refreshCancellation = null;
        _myTasksCancellation?.Cancel();
        if (_detail is not null)
        {
            _ = _detail.Notes.FlushAsync();
        }
    }

    public void SetActiveSession(string? sessionDirectory, bool isPaused = false)
    {
        _activeSessionDirectory = sessionDirectory;
        _activeSessionPaused = isPaused;
        Refresh();
    }

    /// <summary>Rebuilds the list from disk. Only when the window opens or something changed the set.</summary>
    public void Refresh()
    {
        if (_disposed)
        {
            return;
        }

        if (!_dispatcher.CheckAccess())
        {
            try
            {
                _dispatcher.BeginInvoke(Refresh);
            }
            catch (InvalidOperationException)
            {
                // The window can disappear while a recording is stopping.
            }

            return;
        }

        var generation = ++_refreshGeneration;
        var cancellation = new CancellationTokenSource();
        _refreshCancellation?.Cancel();
        _refreshCancellation = cancellation;
        _ = RefreshAsync(_activeSessionDirectory, generation, cancellation);
    }

    public void Dispose()
    {
        _disposed = true;
        Player.Dispose();
        _transcription.StateChanged -= OnUpdate;
        _transcription.ProgressChanged -= OnUpdate;
        _pump.Stop();
        _searchDebounce.Stop();
        CancelSearch();
        _refreshCancellation?.Cancel();
        _refreshCancellation = null;
        _myTasksCancellation?.Cancel();
        if (_detail is not null)
        {
            // Last chance to keep what was typed in the notes.
            _ = _detail.CloseAsync();
        }

        _inFlight.Clear();
    }

    private async Task RefreshAsync(string? activeSession, int generation, CancellationTokenSource cancellation)
    {
        try
        {
            IReadOnlyList<(SessionSnapshot Snapshot, MeetingRowDetails Details)> rows;
            try
            {
                rows = await Task.Run(() => ScanAsync(activeSession, cancellation.Token), cancellation.Token)
                    .ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                if (generation == _refreshGeneration)
                {
                    StatusText = Loc.Format("Meetings_ReadFailed", exception.Message);
                }

                return;
            }

            if (_disposed || generation != _refreshGeneration || cancellation.IsCancellationRequested)
            {
                return;
            }

            ApplyRows(rows);
        }
        finally
        {
            if (ReferenceEquals(_refreshCancellation, cancellation))
            {
                _refreshCancellation = null;
            }

            cancellation.Dispose();
        }
    }

    private async Task<IReadOnlyList<(SessionSnapshot, MeetingRowDetails)>> ScanAsync(
        string? activeSession,
        CancellationToken cancellationToken)
    {
        var snapshots = SessionCatalog.Scan(_recordingsRoot, activeSession, cancellationToken);
        var rows = new List<(SessionSnapshot, MeetingRowDetails)>(snapshots.Count);
        foreach (var snapshot in snapshots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<Speaker> speakers;
            try
            {
                speakers = await _store.LoadSpeakersAsync(snapshot.Name, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                speakers = Array.Empty<Speaker>();
            }

            var hasSummary =
                File.Exists(Path.Combine(snapshot.Directory, "summary.json")) ||
                File.Exists(Path.Combine(snapshot.Directory, "live", "summary.json"));
            rows.Add((snapshot, new MeetingRowDetails(speakers, hasSummary)));
        }

        return rows;
    }

    private void ApplyRows(IReadOnlyList<(SessionSnapshot Snapshot, MeetingRowDetails Details)> rows)
    {
        var now = _clock.Now;

        // Rebuilding the list goes through SelectedItem = null. Without this flag the
        // detail would be closed and reopened on every refresh.
        _restoringSelection = true;
        try
        {
            var selectionToRestore = _pendingSelectionDirectory ?? _selectedMeeting?.Directory;
            _pendingSelectionDirectory = null;
            Meetings.Clear();
            foreach (var (snapshot, details) in rows)
            {
                var row = new MeetingRowViewModel(snapshot, details, now) { Commands = Commands };
                if (string.Equals(row.Directory, _activeSessionDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    row.SetPaused(_activeSessionPaused);
                }

                Meetings.Add(row);
            }

            SelectedMeeting = Meetings.FirstOrDefault(row =>
                string.Equals(row.Directory, selectionToRestore, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _restoringSelection = false;
        }

        Raise(nameof(HasMeetings));

        var selected = _selectedMeeting;
        if (selected is not null &&
            _detail is not null &&
            string.Equals(_detail.Row.Directory, selected.Directory, StringComparison.OrdinalIgnoreCase))
        {
            _detail.Rebind(selected);
            _ = _detail.LoadAsync();
        }
        else
        {
            _ = OpenDetailAsync(selected);
        }

        Drain();
    }

    private async Task OpenDetailAsync(MeetingRowViewModel? row)
    {
        var previous = _detail;
        if (previous is not null && row is not null &&
            string.Equals(previous.Row.Directory, row.Directory, StringComparison.OrdinalIgnoreCase))
        {
            previous.Rebind(row);
            ApplyPendingOpen(previous);
            return;
        }

        Detail = null;
        if (previous is not null)
        {
            await previous.CloseAsync().ConfigureAwait(true);
        }

        if (row is null || !ReferenceEquals(row, _selectedMeeting) || _disposed)
        {
            return;
        }

        var detail = new MeetingDetailViewModel(row, _store, _actions, _shell, Commands, player: Player)
        {
            // Keep the tab the user was reading when moving between meetings.
            SelectedTab = previous?.SelectedTab ?? MeetingDetailViewModel.SummaryTab,
        };
        ApplyPendingOpen(detail);
        Detail = detail;
        await detail.LoadAsync().ConfigureAwait(true);
    }

    private void ApplyPendingOpen(MeetingDetailViewModel detail)
    {
        if (_pendingOpen is not { } pending)
        {
            return;
        }

        _pendingOpen = null;
        detail.SelectedTab = pending.Tab;
        if (pending.Query is not null)
        {
            detail.Transcript.Query = pending.Query;
        }
    }

    private void OpenMeeting(string sessionId)
    {
        IsMyTasksVisible = false;
        var target = Meetings.FirstOrDefault(row => string.Equals(row.SessionId, sessionId, StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            return;
        }

        _pendingOpen = (MeetingDetailViewModel.SummaryTab, null);
        if (ReferenceEquals(target, _selectedMeeting) && _detail is not null)
        {
            ApplyPendingOpen(_detail);
            return;
        }

        SelectedMeeting = target;
    }

    private async Task LoadMyTasksAsync()
    {
        _myTasksCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _myTasksCancellation = cancellation;
        try
        {
            await MyTasks.LoadAsync(Meetings.ToList(), cancellation.Token).ConfigureAwait(true);
        }
        finally
        {
            if (ReferenceEquals(_myTasksCancellation, cancellation))
            {
                _myTasksCancellation = null;
            }

            cancellation.Dispose();
        }
    }

    private void OnUpdate(TranscriptionUpdate update) =>
        _inFlight[update.SessionDirectory] = update;

    /// <summary>
    /// The "Importar grabación" dialog for the picked files. A finished import is queued for
    /// transcription right away and opened in the list.
    /// </summary>
    public ImportViewModel CreateImport(IReadOnlyList<string> paths) =>
        new(paths, _importer, OnImported);

    private void OnImported(string sessionDirectory)
    {
        _transcription.Enqueue(sessionDirectory);
        IsMyTasksVisible = false;
        _pendingSelectionDirectory = sessionDirectory;
        StatusText = Loc.Format("Import_Done", Path.GetFileName(sessionDirectory));
        Refresh();
    }

    private void Retry(MeetingRowViewModel row)
    {
        _transcription.Retry(row.Directory);
        Refresh();
    }

    private void Drain()
    {
        foreach (var row in Meetings)
        {
            if (!_inFlight.TryGetValue(row.Directory, out var update))
            {
                continue;
            }

            var wasDone = row.IsDone;
            row.Apply(update);

            // The meeting being read just finished: now there is a transcript.
            if (!wasDone && row.IsDone && _detail is not null && ReferenceEquals(row, _detail.Row))
            {
                _ = _detail.LoadAsync();
            }

            if (update.State is SessionState.Done or SessionState.Failed or SessionState.Disabled)
            {
                _inFlight.TryRemove(row.Directory, out _);
            }
        }
    }

    private void RunSearch()
    {
        _searchDebounce.Stop();
        CancelSearch();

        var query = _searchQuery;
        if (string.IsNullOrWhiteSpace(query))
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        _searchCancellation = cancellation;
        var token = cancellation.Token;
        var root = _recordingsRoot;
        var active = _activeSessionDirectory;

        _ = Task.Run(
            () =>
            {
                var sessions = SessionCatalog.Scan(root, active, token);
                return TranscriptSearch.Search(sessions, query, token).Take(MaxSearchResults + 1).ToList();
            },
            token).ContinueWith(
            task =>
            {
                if (!ReferenceEquals(_searchCancellation, cancellation))
                {
                    return;
                }

                SearchResults.Clear();
                if (task.Status != TaskStatus.RanToCompletion)
                {
                    SearchStatus = Loc.Get("Search_Failed");
                    return;
                }

                var hasMore = task.Result.Count > MaxSearchResults;
                foreach (var hit in task.Result.Take(MaxSearchResults))
                {
                    SearchResults.Add(new MeetingSearchHitViewModel(hit, query));
                }

                SearchStatus = SearchResults.Count switch
                {
                    0 => Loc.Get("Search_NoResults"),
                    1 => Loc.Get("Search_OneResult"),
                    _ when hasMore => Loc.Format("Search_MoreThan", MaxSearchResults),
                    var count => Loc.Format("Search_Results", count),
                };
            },
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void CancelSearch()
    {
        var previous = _searchCancellation;
        _searchCancellation = null;
        if (previous is null)
        {
            return;
        }

        previous.Cancel();
        previous.Dispose();
    }

    /// <summary>Opens the meeting on the Transcript tab, searching for the same text.</summary>
    private void OpenHit(MeetingSearchHitViewModel hit)
    {
        var query = _searchQuery.Trim();
        var target = Meetings.FirstOrDefault(row =>
            string.Equals(row.Directory, hit.Directory, StringComparison.OrdinalIgnoreCase));

        SearchQuery = string.Empty;
        IsMyTasksVisible = false;
        _pendingOpen = (MeetingDetailViewModel.TranscriptTab, query);
        if (target is null)
        {
            _pendingSelectionDirectory = hit.Directory;
            Refresh();
            return;
        }

        if (ReferenceEquals(target, _selectedMeeting) && _detail is not null)
        {
            ApplyPendingOpen(_detail);
            return;
        }

        SelectedMeeting = target;
    }

    private void BeginRename(MeetingRowViewModel row)
    {
        if (!row.CanRename)
        {
            return;
        }

        // The title editor lives in the detail header.
        IsMyTasksVisible = false;
        SelectedMeeting = row;
        row.BeginRename();
    }

    /// <summary>
    /// Deleting is irreversible and takes the original WAVs with it, so it always asks
    /// and never touches a meeting with open files.
    /// </summary>
    private void Delete(MeetingRowViewModel row)
    {
        if (row.IsBusy ||
            _transcription.IsBusy(row.Directory) ||
            string.Equals(row.Directory, _activeSessionDirectory, StringComparison.OrdinalIgnoreCase))
        {
            StatusText = Loc.Get("Meeting_DeleteBusy");
            return;
        }

        if (!_shell.ConfirmDestructive(Loc.Get("Meeting_DeleteTitle"), Loc.Format("Meeting_DeleteMessage", row.DisplayName)))
        {
            return;
        }

        var directory = row.Directory;
        var displayName = row.DisplayName;
        _ = Task.Run(() => _transcription.TryDeleteSession(directory)).ContinueWith(
            task =>
            {
                if (task.Status != TaskStatus.RanToCompletion)
                {
                    StatusText = Loc.Get("Meeting_DeleteFailed");
                    return;
                }

                if (task.Result is { } failure)
                {
                    StatusText = Loc.Format("Meeting_DeleteFailedReason", failure);
                    return;
                }

                StatusText = Loc.Format("Meeting_Deleted", displayName);
                _inFlight.TryRemove(directory, out _);
                if (string.Equals(_selectedMeeting?.Directory, directory, StringComparison.OrdinalIgnoreCase))
                {
                    SelectedMeeting = null;
                }

                Refresh();
            },
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void Copy(MeetingRowViewModel row)
    {
        var path = row.Directory;
        var displayName = row.DisplayName;
        _ = Task.Run(() => TranscriptExport.ToPlainText(TranscriptExport.TryReadSegments(path))).ContinueWith(
            task =>
            {
                var text = task.Status == TaskStatus.RanToCompletion ? task.Result : string.Empty;
                if (string.IsNullOrEmpty(text))
                {
                    StatusText = Loc.Get("Meeting_NothingToCopy");
                    return;
                }

                StatusText = _shell.TryCopyText(text)
                    ? Loc.Format("Meeting_Copied", displayName)
                    : Loc.Get("Status_ClipboardBusy");
            },
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void Export(MeetingRowViewModel row, bool srt)
    {
        var path = row.Directory;
        var fileName = srt ? TranscriptExport.SrtFileName : TranscriptExport.VttFileName;

        _ = Task.Run(() =>
        {
            var segments = TranscriptExport.TryReadSegments(path);
            if (segments.Count == 0)
            {
                return Loc.Get("Meeting_NothingToExport");
            }

            try
            {
                AtomicFile.Write(
                    Path.Combine(path, fileName),
                    srt ? TranscriptExport.ToSrt(segments) : TranscriptExport.ToVtt(segments));
                return Loc.Format("Meeting_Exported", fileName);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return Loc.Format("Meeting_ExportFailedReason", fileName, exception.Message);
            }
        }).ContinueWith(
            task => StatusText = task.Status == TaskStatus.RanToCompletion
                ? task.Result
                : Loc.Format("Meeting_ExportFailed", fileName),
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void Open(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            StatusText = Loc.Get("Meeting_PathMissing");
            return;
        }

        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                StatusText = Loc.Get("Meeting_PathLinked");
                return;
            }

            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception exception) when (
            exception is Win32Exception or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        {
            StatusText = Loc.Format("Meeting_OpenFailed", exception.Message);
        }
    }

    private static MeetingRowViewModel? RowOf(object? parameter) => parameter as MeetingRowViewModel;

    private static void WithRow(object? parameter, Action<MeetingRowViewModel> action)
    {
        if (RowOf(parameter) is { } row)
        {
            action(row);
        }
    }
}
