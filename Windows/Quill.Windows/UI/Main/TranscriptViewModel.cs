using System.Collections.ObjectModel;
using System.Windows.Input;
using Quill.Windows.Resources;
using Quill.Windows.Sessions;
using SpeakerIds = Quill.Core.Domain.SpeakerIds;

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

    public TranscriptEntryViewModel(
        TimeSpan at,
        string speakerId,
        string speakerLabel,
        string text,
        bool isMarker,
        TimeSpan? end = null)
    {
        At = at;
        End = end is { } value && value >= at ? value : at;
        SpeakerId = speakerId;
        SpeakerLabel = speakerLabel;
        Text = text;
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

    public string Text { get; }

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
}

/// <summary>
/// Transcript tab: the lines of the meeting, search inside it with next/previous match,
/// and timestamps that ask playback to start there.
/// </summary>
internal sealed class TranscriptViewModel : ObservableObject
{
    /// <summary>A pause shorter than this keeps the previous segment highlighted.</summary>
    private static readonly TimeSpan PlayingGap = TimeSpan.FromSeconds(1.5);

    private readonly ISessionActions _actions;
    private readonly List<TranscriptEntryViewModel> _matches = new();
    private TranscriptEntryViewModel? _playingEntry;
    private bool _followPlayback = true;
    private string _sessionId = string.Empty;
    private string _query = string.Empty;
    private int _currentMatch = -1;
    private string _status = string.Empty;

    public TranscriptViewModel(ISessionActions actions, PlayerViewModel? player = null)
    {
        _actions = actions;
        Player = player;
        if (player is not null)
        {
            player.PositionChanged += OnPlayerPositionChanged;
        }

        NextMatchCommand = new RelayCommand(_ => MoveMatch(+1), _ => _matches.Count > 0);
        PreviousMatchCommand = new RelayCommand(_ => MoveMatch(-1), _ => _matches.Count > 0);
        ClearSearchCommand = new RelayCommand(_ => Query = string.Empty);
        PlayAtCommand = new RelayCommand(PlayAt);
    }

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
        _sessionId = sessionId;
        Entries.Clear();
        foreach (var entry in entries)
        {
            Entries.Add(entry);
        }

        Status = Entries.Count == 0 ? emptyStatus : string.Empty;
        Search();
        _playingEntry = null;
        UpdatePlaying();
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
        foreach (var segment in segments)
        {
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
                end: TimeSpan.FromMilliseconds(segment.EndMs)));
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
