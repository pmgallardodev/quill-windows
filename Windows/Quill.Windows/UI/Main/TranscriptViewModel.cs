using System.Collections.ObjectModel;
using System.Windows.Input;
using Quill.Core.Domain;
using Quill.Core.Ports;
using Quill.Windows.Resources;
using Quill.Windows.Sessions;
using SpeakerIds = Quill.Core.Domain.SpeakerIds;
using TranscriptSegment = Quill.Windows.Sessions.TranscriptSegment;

namespace Quill.Windows.UI;

/// <summary>
/// One line of the Transcript tab: a spoken segment or a marker set while recording. Both
/// live in one list because reading only cares about time order.
/// </summary>
internal sealed class TranscriptEntryViewModel : ObservableObject
{
    private IReadOnlyList<HighlightRun> _runs;
    private bool _isCurrentMatch;
    private bool _isPlaying;
    private string _text;
    private bool _isEdited;
    private bool _isEditing;
    private string _editText = string.Empty;
    private IReadOnlyList<SpeakerReassignOption> _reassignOptions = Array.Empty<SpeakerReassignOption>();

    public TranscriptEntryViewModel(
        TimeSpan at,
        string speakerId,
        string speakerLabel,
        string text,
        bool isMarker,
        TimeSpan? end = null,
        int segmentIndex = -1)
    {
        SegmentIndex = isMarker ? -1 : segmentIndex;
        At = at;
        End = end is { } value && value >= at ? value : at;
        SpeakerId = speakerId;
        SpeakerLabel = speakerLabel;
        _text = text;
        IsMarker = isMarker;
        IsMe = string.Equals(speakerId, SpeakerIds.Me, StringComparison.OrdinalIgnoreCase);
        _runs = TextHighlight.Split(text, null);
    }

    public TimeSpan At { get; }

    /// <summary>End of the segment on the session clock (= <see cref="At"/> for markers).</summary>
    public TimeSpan End { get; }

    public string TimeText => TranscriptExport.Clock((long)At.TotalMilliseconds);

    public string SpeakerId { get; }

    public string SpeakerLabel { get; }

    public string Text
    {
        get => _text;
        private set => Set(ref _text, value);
    }

    /// <summary>Position of the segment in transcript.json; -1 for markers.</summary>
    public int SegmentIndex { get; }

    /// <summary>Segments of the final transcript can be corrected; markers cannot.</summary>
    public bool CanEdit => SegmentIndex >= 0;

    /// <summary>The user corrected this text ("edited" in transcript.json).</summary>
    public bool IsEdited
    {
        get => _isEdited;
        set => Set(ref _isEdited, value);
    }

    /// <summary>The inline editor is open on this line.</summary>
    public bool IsEditing
    {
        get => _isEditing;
        set => Set(ref _isEditing, value);
    }

    /// <summary>Text in the inline editor.</summary>
    public string EditText
    {
        get => _editText;
        set => Set(ref _editText, value ?? string.Empty);
    }

    /// <summary>"Cambiar interlocutor" submenu; empty when reassignment is not available.</summary>
    public IReadOnlyList<SpeakerReassignOption> ReassignOptions
    {
        get => _reassignOptions;
        set
        {
            if (Set(ref _reassignOptions, value ?? Array.Empty<SpeakerReassignOption>()))
            {
                Raise(nameof(CanReassign));
            }
        }
    }

    public bool CanReassign => _reassignOptions.Count > 0;

    public bool IsMarker { get; }

    public bool IsSegment => !IsMarker;

    public bool IsMe { get; }

    /// <summary>The text split around the current search matches.</summary>
    public IReadOnlyList<HighlightRun> Runs
    {
        get => _runs;
        private set => Set(ref _runs, value);
    }

    public bool IsCurrentMatch
    {
        get => _isCurrentMatch;
        set => Set(ref _isCurrentMatch, value);
    }

    /// <summary>The audio player is inside this segment.</summary>
    public bool IsPlaying
    {
        get => _isPlaying;
        set => Set(ref _isPlaying, value);
    }

    public bool Matches(string query) => TextHighlight.Contains(Text, query);

    public void Highlight(string? query) => Runs = TextHighlight.Split(Text, query);

    /// <summary>A saved correction: new text, marked edited, highlighted with the current query.</summary>
    public void ApplyText(string text, string? query)
    {
        Text = text;
        IsEdited = true;
        Highlight(query);
    }
}

/// <summary>One entry of a line's "Cambiar interlocutor" submenu.</summary>
internal sealed record SpeakerReassignOption(string SpeakerId, string Label, ICommand Command);

/// <summary>
/// Transcript tab: the lines of the meeting, search inside it with next/previous match,
/// timestamps that ask playback to start there, the line being heard, inline corrections
/// (saved to transcript.json with "edited") and "Volver a resumir" after them.
/// </summary>
internal sealed class TranscriptViewModel : ObservableObject
{
    /// <summary>A pause shorter than this keeps the previous segment highlighted.</summary>
    private static readonly TimeSpan PlayingGap = TimeSpan.FromSeconds(1.5);

    private readonly ISessionActions _actions;
    private readonly ISessionStore? _store;
    private readonly List<TranscriptEntryViewModel> _matches = new();
    private TranscriptEntryViewModel? _editing;
    private bool _hasPendingEdits;
    private bool _isBusy;
    private string _editStatus = string.Empty;
    private TranscriptEntryViewModel? _playingEntry;
    private bool _followPlayback = true;
    private string _sessionId = string.Empty;
    private string _query = string.Empty;
    private int _currentMatch = -1;
    private string _status = string.Empty;

    public TranscriptViewModel(ISessionActions actions, PlayerViewModel? player = null, ISessionStore? store = null)
    {
        _actions = actions;
        _store = store;
        Player = player;
        if (player is not null)
        {
            player.PositionChanged += OnPlayerPositionChanged;
        }

        NextMatchCommand = new RelayCommand(_ => MoveMatch(+1), _ => _matches.Count > 0);
        PreviousMatchCommand = new RelayCommand(_ => MoveMatch(-1), _ => _matches.Count > 0);
        ClearSearchCommand = new RelayCommand(_ => Query = string.Empty);
        PlayAtCommand = new RelayCommand(PlayAt);
        BeginEditCommand = new RelayCommand(
            parameter => BeginEdit(parameter as TranscriptEntryViewModel),
            parameter => CanEdit && !_isBusy && parameter is TranscriptEntryViewModel { CanEdit: true });
        SaveEditCommand = new RelayCommand(
            parameter => _ = SaveEditAsync(parameter as TranscriptEntryViewModel ?? _editing),
            _ => !_isBusy);
        CancelEditCommand = new RelayCommand(_ => CancelEdit());
        ResummarizeCommand = new RelayCommand(_ => _ = ResummarizeAsync(), _ => CanResummarize && !_isBusy);
    }

    /// <summary>Parameter: the line. Opens its inline editor.</summary>
    public ICommand BeginEditCommand { get; }

    /// <summary>Parameter: the line (or none: the open editor). Enter in the editor.</summary>
    public ICommand SaveEditCommand { get; }

    /// <summary>Esc in the editor.</summary>
    public ICommand CancelEditCommand { get; }

    /// <summary>"Volver a resumir": regenerates the summary with the corrected transcript.</summary>
    public ICommand ResummarizeCommand { get; }

    /// <summary>Set by the meeting detail: runs the summary generation, returns null or the error.</summary>
    public Func<Task<string?>>? ResummarizeHandler { get; set; }

    /// <summary>Set by the meeting detail: rereads the transcript after a speaker change.</summary>
    public Func<Task>? ReloadHandler { get; set; }

    /// <summary>Corrections are saved through the session store; without one the tab is read-only.</summary>
    public bool CanEdit => _store is not null;

    /// <summary>Corrections were saved since the summary was last generated from this tab.</summary>
    public bool HasPendingEdits
    {
        get => _hasPendingEdits;
        private set
        {
            if (Set(ref _hasPendingEdits, value))
            {
                Raise(nameof(CanResummarize));
            }
        }
    }

    public bool CanResummarize => _hasPendingEdits && _actions.IsAiAvailable && ResummarizeHandler is not null;

    public bool IsBusy
    {
        get => _isBusy;
        private set => Set(ref _isBusy, value);
    }

    /// <summary>Result of the last correction / re-summary ("No se pudo guardar...", "Resumen actualizado.").</summary>
    public string EditStatus
    {
        get => _editStatus;
        private set
        {
            if (Set(ref _editStatus, value))
            {
                Raise(nameof(HasEditStatus));
            }
        }
    }

    public bool HasEditStatus => _editStatus.Length > 0;

    public ObservableCollection<TranscriptEntryViewModel> Entries { get; } = new();

    /// <summary>The window's player (null in tests that do not need it).</summary>
    public PlayerViewModel? Player { get; }

    public bool HasPlayer => Player is not null;

    /// <summary>The segment being heard. The view scrolls to it while <see cref="FollowPlayback"/> is on.</summary>
    public TranscriptEntryViewModel? PlayingEntry
    {
        get => _playingEntry;
        private set
        {
            if (ReferenceEquals(_playingEntry, value))
            {
                return;
            }

            if (_playingEntry is not null)
            {
                _playingEntry.IsPlaying = false;
            }

            _playingEntry = value;
            if (value is not null)
            {
                value.IsPlaying = true;
            }

            Raise(nameof(PlayingEntry));
        }
    }

    public bool FollowPlayback
    {
        get => _followPlayback;
        set => Set(ref _followPlayback, value);
    }

    public ICommand NextMatchCommand { get; }

    public ICommand PreviousMatchCommand { get; }

    public ICommand ClearSearchCommand { get; }

    /// <summary>Parameter: a <see cref="TimeSpan"/> or a <see cref="TranscriptEntryViewModel"/>.</summary>
    public ICommand PlayAtCommand { get; }

    /// <summary>What fills the tab when there are no lines ("Cargando...", "Sin transcript", ...).</summary>
    public string Status
    {
        get => _status;
        private set
        {
            if (Set(ref _status, value))
            {
                Raise(nameof(HasStatus));
            }
        }
    }

    public bool HasStatus => _status.Length > 0;

    public string Query
    {
        get => _query;
        set
        {
            if (Set(ref _query, value ?? string.Empty))
            {
                Search();
            }
        }
    }

    public bool IsSearching => !string.IsNullOrWhiteSpace(_query);

    public int MatchCount => _matches.Count;

    public int CurrentMatchIndex => _currentMatch;

    /// <summary>The line to scroll to. The view watches this property.</summary>
    public TranscriptEntryViewModel? CurrentMatch => _currentMatch >= 0 ? _matches[_currentMatch] : null;

    /// <summary>"3 de 12", "Sin resultados", or empty when not searching.</summary>
    public string MatchText => !IsSearching
        ? string.Empty
        : _matches.Count == 0
            ? Loc.Get("Search_NoResults")
            : Loc.Format("Search_MatchPosition", _currentMatch + 1, _matches.Count);

    public void ShowStatus(string status)
    {
        Entries.Clear();
        _matches.Clear();
        _currentMatch = -1;
        Status = status;
        RaiseSearch();
    }

    public void Load(string sessionId, IReadOnlyList<TranscriptEntryViewModel> entries, string emptyStatus)
    {
        if (!string.Equals(sessionId, _sessionId, StringComparison.Ordinal))
        {
            HasPendingEdits = false;
            EditStatus = string.Empty;
        }

        _sessionId = sessionId;
        _editing = null;
        Entries.Clear();
        foreach (var entry in entries)
        {
            Entries.Add(entry);
        }

        AttachReassignOptions();

        Status = Entries.Count == 0 ? emptyStatus : string.Empty;
        Search();
        _playingEntry = null;
        UpdatePlaying();
    }

    /// <summary>Marks the lines whose segment carries "edited" in transcript.json.</summary>
    public void ApplyEditedFlags(IReadOnlyList<Quill.Core.Domain.TranscriptSegment> segments)
    {
        foreach (var entry in Entries)
        {
            if (entry.SegmentIndex >= 0 && entry.SegmentIndex < segments.Count &&
                segments[entry.SegmentIndex].Edited &&
                (segments[entry.SegmentIndex].Start - entry.At).Duration() <= TimeSpan.FromMilliseconds(1))
            {
                entry.IsEdited = true;
            }
        }
    }

    /// <summary>The detail closed: stop following the shared player.</summary>
    public void Detach()
    {
        if (Player is not null)
        {
            Player.PositionChanged -= OnPlayerPositionChanged;
        }
    }

    /// <summary>
    /// The segment that contains <paramref name="position"/>, or the one just before it when
    /// the gap is short (pauses between sentences). Markers are skipped. Entries are sorted.
    /// </summary>
    public static TranscriptEntryViewModel? SegmentAt(IReadOnlyList<TranscriptEntryViewModel> entries, TimeSpan position)
    {
        int low = 0, high = entries.Count - 1, found = -1;
        while (low <= high)
        {
            var middle = (low + high) / 2;
            if (entries[middle].At <= position)
            {
                found = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        for (var index = found; index >= 0; index--)
        {
            var entry = entries[index];
            if (entry.IsSegment)
            {
                return position <= entry.End + PlayingGap ? entry : null;
            }
        }

        return null;
    }

    /// <summary>
    /// Segments with text plus markers, in time order. OrderBy is stable, so a marker that
    /// falls exactly on a segment start reads after it.
    /// </summary>
    public static IReadOnlyList<TranscriptEntryViewModel> BuildEntries(
        IReadOnlyList<TranscriptSegment> segments,
        IEnumerable<long> markersMs,
        SpeakerLabels labels,
        CancellationToken cancellationToken = default)
    {
        var entries = new List<TranscriptEntryViewModel>(segments.Count);
        for (var index = 0; index < segments.Count; index++)
        {
            var segment = segments[index];
            cancellationToken.ThrowIfCancellationRequested();
            var text = segment.Text?.Trim() ?? string.Empty;
            if (text.Length == 0)
            {
                continue;
            }

            var speaker = string.IsNullOrWhiteSpace(segment.Speaker) ? string.Empty : segment.Speaker.Trim();
            entries.Add(new TranscriptEntryViewModel(
                TimeSpan.FromMilliseconds(segment.StartMs),
                speaker,
                labels.Label(speaker),
                text,
                isMarker: false,
                end: TimeSpan.FromMilliseconds(segment.EndMs),
                segmentIndex: index));
        }

        var number = 0;
        foreach (var marker in markersMs.Where(value => value >= 0).OrderBy(value => value))
        {
            entries.Add(new TranscriptEntryViewModel(
                TimeSpan.FromMilliseconds(marker),
                string.Empty,
                string.Empty,
                Loc.Format("Transcript_Marker", ++number),
                isMarker: true));
        }

        return entries.OrderBy(entry => entry.At).ToList();
    }

    private void Search()
    {
        var previous = CurrentMatch;
        if (previous is not null)
        {
            previous.IsCurrentMatch = false;
        }

        _matches.Clear();
        var query = IsSearching ? _query.Trim() : null;
        foreach (var entry in Entries)
        {
            entry.Highlight(query);
            if (query is not null && entry.Matches(query))
            {
                _matches.Add(entry);
            }
        }

        _currentMatch = _matches.Count > 0 ? 0 : -1;
        if (CurrentMatch is { } current)
        {
            current.IsCurrentMatch = true;
        }

        RaiseSearch();
    }

    private void MoveMatch(int step)
    {
        if (_matches.Count == 0)
        {
            return;
        }

        _matches[_currentMatch].IsCurrentMatch = false;
        _currentMatch = ((_currentMatch + step) % _matches.Count + _matches.Count) % _matches.Count;
        _matches[_currentMatch].IsCurrentMatch = true;
        Raise(nameof(CurrentMatchIndex), nameof(CurrentMatch), nameof(MatchText));
    }

    private void PlayAt(object? parameter)
    {
        var at = parameter switch
        {
            TimeSpan time => time,
            TranscriptEntryViewModel entry => entry.At,
            _ => (TimeSpan?)null,
        };

        if (at is { } time2 && _sessionId.Length > 0)
        {
            _actions.PlayAt(_sessionId, time2);
        }
    }

    private void BeginEdit(TranscriptEntryViewModel? entry)
    {
        if (entry is null || !entry.CanEdit || !CanEdit)
        {
            return;
        }

        CancelEdit();
        entry.EditText = entry.Text;
        entry.IsEditing = true;
        _editing = entry;
        EditStatus = string.Empty;
    }

    private void CancelEdit()
    {
        if (_editing is not null)
        {
            _editing.IsEditing = false;
            _editing = null;
        }
    }

    private async Task SaveEditAsync(TranscriptEntryViewModel? entry)
    {
        if (entry is null || !entry.IsEditing || _store is null || _isBusy)
        {
            return;
        }

        var text = TranscriptEdits.Normalize(entry.EditText);
        if (text is null || string.Equals(text, entry.Text, StringComparison.Ordinal))
        {
            CancelEdit();
            return;
        }

        var sessionId = _sessionId;
        IsBusy = true;
        try
        {
            var segments = await _store.LoadTranscriptAsync(sessionId, TranscriptPass.Final, CancellationToken.None).ConfigureAwait(true);
            var updated = TranscriptEdits.Apply(segments, entry.SegmentIndex, entry.At, text);
            var info = await TranscriptInfoAsync(sessionId).ConfigureAwait(true);
            await _store.SaveTranscriptAsync(sessionId, TranscriptPass.Final, info, updated, CancellationToken.None).ConfigureAwait(true);
            if (!string.Equals(sessionId, _sessionId, StringComparison.Ordinal))
            {
                return;
            }

            entry.ApplyText(text, IsSearching ? _query.Trim() : null);
            CancelEdit();
            HasPendingEdits = true;
            EditStatus = string.Empty;
            Search();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            EditStatus = Loc.Format("Edit_SaveFailed", exception.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Engine and model of the transcript being corrected; they stay in its header.</summary>
    private async Task<TranscriptInfo> TranscriptInfoAsync(string sessionId)
    {
        var session = await _store!.LoadSessionAsync(sessionId, CancellationToken.None).ConfigureAwait(true);
        return session is not null && TranscriptDocument.TryRead(session.Folder, out var document) && document is not null
            ? new TranscriptInfo(document.Engine, document.Model)
            : new TranscriptInfo("unknown", "unknown");
    }

    private async Task ResummarizeAsync()
    {
        if (ResummarizeHandler is not { } handler || _isBusy)
        {
            return;
        }

        IsBusy = true;
        EditStatus = Loc.Get("Edit_Resummarizing");
        try
        {
            var error = await handler().ConfigureAwait(true);
            if (error is null)
            {
                HasPendingEdits = false;
                EditStatus = Loc.Get("Edit_Resummarized");
            }
            else
            {
                EditStatus = Loc.Format("Edit_ResummarizeFailed", error);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            EditStatus = Loc.Format("Edit_ResummarizeFailed", exception.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void AttachReassignOptions()
    {
        if (!_actions.CanReassignSpeakers)
        {
            return;
        }

        var choices = TranscriptEdits.SpeakerChoices(Entries);
        foreach (var entry in Entries.Where(entry => entry.CanEdit))
        {
            entry.ReassignOptions = choices
                .Where(choice => !string.Equals(choice.Id, entry.SpeakerId, StringComparison.OrdinalIgnoreCase))
                .Select(choice => new SpeakerReassignOption(
                    choice.Id,
                    choice.Label,
                    new RelayCommand(_ => _ = ReassignAsync(entry, choice.Id), _ => !_isBusy)))
                .ToList();
        }
    }

    private async Task ReassignAsync(TranscriptEntryViewModel entry, string speakerId)
    {
        if (_isBusy)
        {
            return;
        }

        IsBusy = true;
        EditStatus = string.Empty;
        try
        {
            await _actions.ReassignSpeakerAsync(
                _sessionId,
                entry.SegmentIndex,
                entry.At,
                entry.SpeakerId,
                entry.Text,
                speakerId,
                CancellationToken.None).ConfigureAwait(true);
            HasPendingEdits = true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            EditStatus = Loc.Format("Edit_ReassignFailed", exception.Message);
            return;
        }
        finally
        {
            IsBusy = false;
        }

        if (ReloadHandler is { } reload)
        {
            await reload().ConfigureAwait(true);
        }
    }

    private void OnPlayerPositionChanged(object? sender, EventArgs e) => UpdatePlaying();

    private void UpdatePlaying()
    {
        if (Player is null || _sessionId.Length == 0 ||
            !string.Equals(Player.SessionId, _sessionId, StringComparison.Ordinal))
        {
            PlayingEntry = null;
            return;
        }

        PlayingEntry = Player.Position <= TimeSpan.Zero && !Player.IsPlaying ? null : SegmentAt(Entries, Player.Position);
    }

    private void RaiseSearch() =>
        Raise(nameof(IsSearching), nameof(MatchCount), nameof(CurrentMatchIndex), nameof(CurrentMatch), nameof(MatchText));
}
