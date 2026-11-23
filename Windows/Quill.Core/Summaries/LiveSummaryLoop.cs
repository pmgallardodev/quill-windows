using Quill.Core.Domain;
using Quill.Core.Ports;
using Quill.Core.Transcripts;

namespace Quill.Core.Summaries;

public enum LiveSummaryState
{
    /// <summary>Not started.</summary>
    Idle,

    /// <summary>Accumulating new speech until the next refresh is due.</summary>
    Waiting,

    /// <summary>A refresh is running.</summary>
    Updating,

    /// <summary>The live summary reflects everything heard so far (minus speech still accumulating).</summary>
    UpToDate,

    /// <summary>A refresh was due but the scheduler was busy; it is retried with the next segments ("summary delayed").</summary>
    Delayed,

    /// <summary>No language model (AI pack missing); the loop stops trying.</summary>
    AiPackMissing,

    /// <summary>The last refresh failed; the next one waits for another interval of speech.</summary>
    Failed,
}

public sealed class LiveSummaryUpdatedEventArgs(string sessionId, MeetingSummary summary, IReadOnlyList<DroppedCitation> dropped) : EventArgs
{
    public string SessionId { get; } = sessionId;

    public MeetingSummary Summary { get; } = summary;

    public IReadOnlyList<DroppedCitation> Dropped { get; } = dropped;
}

/// <summary>
/// Rolling summary during a recording (design §7.2). Feed it the live transcript segments;
/// once <see cref="Interval"/> of new speech has accumulated it asks the model to update the
/// previous live summary with the new lines only (live sections: executive summary,
/// decisions, action items, open questions). Refreshes go through
/// <see cref="IInferenceScheduler.TryRunAsync{T}"/> at <see cref="InferencePriority.LiveSummary"/>:
/// when the scheduler is busy the refresh is skipped (state <see cref="LiveSummaryState.Delayed"/>)
/// and the pending lines are kept for the next attempt, never queued.
/// Thread-safe: segments may arrive from any thread; at most one refresh runs at a time.
/// </summary>
public sealed class LiveSummaryLoop
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(3);

    private readonly ILanguageModel _model;
    private readonly IInferenceScheduler _scheduler;
    private readonly ISessionStore _store;
    private readonly IClock _clock;
    private readonly object _gate = new();
    private readonly List<TranscriptSegment> _summarized = [];
    private readonly List<TranscriptSegment> _pending = [];

    private string? _sessionId;
    private SummaryOptions _options = new();
    private LiveSummaryState _state = LiveSummaryState.Idle;
    private MeetingSummary? _current;
    private bool _refreshing;
    private TimeSpan _retryAfterSpeech;

    public LiveSummaryLoop(
        ILanguageModel model,
        IInferenceScheduler scheduler,
        ISessionStore store,
        IClock? clock = null,
        TimeSpan? interval = null)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _clock = clock ?? SystemClock.Instance;
        Interval = interval ?? DefaultInterval;
        if (Interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval), interval, "interval must be positive");
        }
    }

    /// <summary>Speech (sum of segment durations) that triggers a refresh.</summary>
    public TimeSpan Interval { get; }

    /// <summary>Raised after a refresh is saved. May fire on a background thread.</summary>
    public event EventHandler<LiveSummaryUpdatedEventArgs>? Updated;

    /// <summary>Raised on every state change. May fire on a background thread.</summary>
    public event EventHandler<LiveSummaryState>? StateChanged;

    public LiveSummaryState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    public MeetingSummary? Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>Speech received since the last successful refresh.</summary>
    public TimeSpan PendingSpeech
    {
        get
        {
            lock (_gate)
            {
                return Speech(_pending);
            }
        }
    }

    /// <summary>
    /// Starts (or restarts) the loop for a session. An existing live/summary.json and
    /// live/transcript.json are picked up, so a restarted app continues where it left off.
    /// </summary>
    public async Task StartAsync(string sessionId, SummaryOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(options);
        var previous = await _store.LoadSummaryAsync(sessionId, SummaryKind.Live, cancellationToken).ConfigureAwait(false);
        var heard = previous is null
            ? []
            : await _store.LoadTranscriptAsync(sessionId, TranscriptPass.Live, cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            _sessionId = sessionId;
            _options = options;
            _current = previous;
            _summarized.Clear();
            _summarized.AddRange(heard);
            _pending.Clear();
            _refreshing = false;
            _retryAfterSpeech = TimeSpan.Zero;
        }

        SetState(LiveSummaryState.Waiting);
    }

    /// <summary>Stops reacting to segments. The saved live summary stays on disk.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            _sessionId = null;
            _pending.Clear();
        }

        SetState(LiveSummaryState.Idle);
    }

    /// <summary>
    /// Adds live transcript segments and refreshes the summary when due. Returns the state
    /// after any refresh this call ran (or <see cref="LiveSummaryState.Updating"/> when another
    /// refresh is already in progress).
    /// </summary>
    public Task<LiveSummaryState> AddSegmentsAsync(IReadOnlyList<TranscriptSegment> segments, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(segments);
        lock (_gate)
        {
            if (_sessionId is null)
            {
                return Task.FromResult(_state);
            }

            _pending.AddRange(segments.Where(segment => segment is not null && !string.IsNullOrWhiteSpace(segment.Text)));
        }

        return RefreshIfDueAsync(force: false, cancellationToken);
    }

    /// <summary>Refreshes now if there is any new speech, regardless of the interval (still skippable when busy).</summary>
    public Task<LiveSummaryState> RefreshNowAsync(CancellationToken cancellationToken = default) =>
        RefreshIfDueAsync(force: true, cancellationToken);

    private async Task<LiveSummaryState> RefreshIfDueAsync(bool force, CancellationToken cancellationToken)
    {
        string sessionId;
        SummaryOptions options;
        MeetingSummary? previous;
        List<TranscriptSegment> batch;
        List<TranscriptSegment> heard;
        lock (_gate)
        {
            if (_sessionId is null || _state == LiveSummaryState.AiPackMissing || _refreshing)
            {
                return _state;
            }

            var speech = Speech(_pending);
            var due = _pending.Count > 0 && (force || (speech >= Interval && speech >= _retryAfterSpeech));
            if (!due)
            {
                return _state;
            }

            _refreshing = true;
            sessionId = _sessionId;
            options = _options;
            previous = _current;
            batch = _pending.ToList();
            heard = _summarized.Concat(batch).ToList();
        }

        try
        {
            var (ran, result) = await _scheduler.TryRunAsync(
                InferencePriority.LiveSummary,
                token => UpdateAsync(previous, batch, heard, options, token),
                cancellationToken).ConfigureAwait(false);

            if (!ran)
            {
                return Finish(LiveSummaryState.Delayed);
            }

            var summary = result!.Summary with { Meta = SummaryGeneration.Meta(_model, options, _clock, SummaryKind.Live) };
            await _store.SaveSummaryAsync(sessionId, summary, cancellationToken).ConfigureAwait(false);

            lock (_gate)
            {
                if (_sessionId != sessionId)
                {
                    _refreshing = false;
                    return _state;
                }

                _current = summary;
                _summarized.AddRange(batch);
                _pending.RemoveRange(0, Math.Min(batch.Count, _pending.Count));
                _retryAfterSpeech = TimeSpan.Zero;
            }

            Updated?.Invoke(this, new LiveSummaryUpdatedEventArgs(sessionId, summary, result.Dropped));
            return Finish(LiveSummaryState.UpToDate);
        }
        catch (LanguageModelUnavailableException)
        {
            return Finish(LiveSummaryState.AiPackMissing);
        }
        catch (SummaryGenerationException)
        {
            lock (_gate)
            {
                // Back off: wait for another interval of speech before retrying.
                _retryAfterSpeech = Speech(_pending) + Interval;
            }

            return Finish(LiveSummaryState.Failed);
        }
        catch
        {
            lock (_gate)
            {
                _refreshing = false;
            }

            throw;
        }
    }

    /// <summary>Runs inside the scheduler: one model call per chunk of new lines that fits the context.</summary>
    private async Task<CitationValidationResult> UpdateAsync(
        MeetingSummary? previous,
        IReadOnlyList<TranscriptSegment> batch,
        IReadOnlyList<TranscriptSegment> heard,
        SummaryOptions options,
        CancellationToken cancellationToken)
    {
        SetState(LiveSummaryState.Updating);
        var system = SummaryPrompts.System(SummaryPromptKind.Live, options, SummarySections.Live, []);
        var maxOutput = SummaryPrompts.MaxOutputTokens(SummaryPromptKind.Live, SummaryLevel.Brief);
        var schema = MeetingSummarySchema.For(SummaryKind.Live);
        var previousTokens = previous is null
            ? 0
            : TranscriptChunker.EstimateTokens(SummaryPrompts.ModelJson(previous, SummarySections.Live));
        var budget = Math.Max(256, SummaryGeneration.ContentBudget(_model, system, maxOutput) - previousTokens);

        var current = previous;
        foreach (var chunk in TranscriptChunker.Chunk(batch, budget))
        {
            var request = new LanguageModelRequest(system, SummaryPrompts.Live(current, chunk.Text), maxOutput);
            current = await SummaryGeneration.GenerateAsync(_model, request, schema, cancellationToken).ConfigureAwait(false);
        }

        return CitationValidator.Validate(SummaryGeneration.Deduplicate(current ?? new MeetingSummary()), heard);
    }

    private LiveSummaryState Finish(LiveSummaryState state)
    {
        lock (_gate)
        {
            _refreshing = false;
            if (_sessionId is null)
            {
                // Stopped while the refresh ran: stay idle.
                return _state;
            }
        }

        SetState(state);
        return state;
    }

    private void SetState(LiveSummaryState state)
    {
        bool changed;
        lock (_gate)
        {
            changed = _state != state;
            _state = state;
        }

        if (changed)
        {
            StateChanged?.Invoke(this, state);
        }
    }

    private static TimeSpan Speech(IEnumerable<TranscriptSegment> segments) =>
        segments.Aggregate(TimeSpan.Zero, (total, segment) => total + segment.Duration);
}
