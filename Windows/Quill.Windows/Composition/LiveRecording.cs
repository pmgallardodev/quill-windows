using System.Diagnostics;
using Quill.Core.Domain;
using Quill.Core.Live;
using Quill.Core.Ports;
using Quill.Core.Summaries;
using Quill.Windows.Audio;
using Quill.Windows.Transcription;

namespace Quill.Windows.Composition;

/// <summary>
/// The live side of one recording: live transcription (when the AI pack has the live model) and
/// the rolling live summary fed from its segments (when <see cref="Core.Pipeline.LiveSummaryPolicy"/>
/// allows it). Either part may be missing; nothing here ever affects capture.
/// </summary>
/// <remarks>
/// Lifecycle: create before <see cref="RecordingSession.Start"/> (it needs <see cref="Tap"/>),
/// <see cref="Start"/> once the session folder exists, <see cref="StopAsync"/> after capture stopped.
/// </remarks>
internal sealed class LiveRecording : IAsyncDisposable
{
    private readonly LiveTranscriptionModule? _transcription;
    private readonly object _forwardGate = new();
    private readonly HashSet<TranscriptSegment> _forwarded = new(ReferenceEqualityComparer.Instance);
    private bool _forwarding;
    private int _stopped;

    public LiveRecording(LiveTranscriptionModule? transcription, LiveSummaryLoop? summary)
    {
        _transcription = transcription;
        // A summary without live text would never receive a segment.
        Summary = transcription is null ? null : summary;
    }

    /// <summary>Handed to <see cref="RecordingSession.Start"/>; null without live transcription.</summary>
    public LiveCaptureTap? Tap => _transcription?.Tap;

    public LiveTranscriptBuffer? Transcript => _transcription?.Buffer;

    public LiveSummaryLoop? Summary { get; }

    /// <summary>Starts live text and the summary loop for the session. Never throws for a live failure.</summary>
    public void Start(ISessionStore store, string sessionId, SummaryOptions summaryOptions)
    {
        if (_transcription is null)
        {
            return;
        }

        _transcription.Start(store, sessionId);
        if (Summary is { } loop)
        {
            _ = StartSummaryAsync(loop, sessionId, summaryOptions);
        }
    }

    /// <summary>Stops the summary first (no refresh competes with the last live chunks), then live text. Never throws.</summary>
    public async Task StopAsync(TimeSpan timeout)
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
        {
            return;
        }

        StopForwarding();
        if (_transcription is not null)
        {
            await _transcription.StopAsync(timeout).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _stopped, 1);
        StopForwarding();
        if (_transcription is not null)
        {
            await _transcription.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task StartSummaryAsync(LiveSummaryLoop loop, string sessionId, SummaryOptions options)
    {
        try
        {
            await loop.StartAsync(sessionId, options).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Trace.TraceWarning($"quill: live summary is off: {exception.Message}");
            return;
        }

        lock (_forwardGate)
        {
            if (Volatile.Read(ref _stopped) != 0)
            {
                loop.Stop();
                return;
            }

            _forwarding = true;
            Transcript!.SegmentAdded += OnSegmentAdded;
        }

        // Segments heard while the loop was starting.
        Forward();
    }

    private void OnSegmentAdded(object? sender, LiveSegmentAddedEventArgs e) => Forward();

    /// <summary>
    /// Sends every segment not yet forwarded, in order. Reading from the buffer instead of the
    /// event payload means none is lost or sent twice around the subscription.
    /// </summary>
    private void Forward()
    {
        lock (_forwardGate)
        {
            if (!_forwarding || Summary is null || Transcript is null)
            {
                return;
            }

            var batch = SelectUnforwarded(Transcript.Snapshot(), _forwarded);
            if (batch.Count == 0)
            {
                return;
            }

            // Called under the lock so batches keep their order. Cheap: the loop appends
            // synchronously and a due refresh runs on the scheduler's worker, not here.
            _ = ForwardAsync(Summary, batch);
        }
    }

    internal static IReadOnlyList<TranscriptSegment> SelectUnforwarded(
        IReadOnlyList<TranscriptSegment> snapshot,
        HashSet<TranscriptSegment> forwarded)
    {
        var batch = new List<TranscriptSegment>();
        foreach (var segment in snapshot)
        {
            if (forwarded.Add(segment))
            {
                batch.Add(segment);
            }
        }

        return batch;
    }

    private static async Task ForwardAsync(LiveSummaryLoop loop, IReadOnlyList<TranscriptSegment> batch)
    {
        try
        {
            await loop.AddSegmentsAsync(batch).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Trace.TraceWarning($"quill: live summary refresh failed: {exception.Message}");
        }
    }

    private void StopForwarding()
    {
        lock (_forwardGate)
        {
            if (_forwarding && Transcript is not null)
            {
                Transcript.SegmentAdded -= OnSegmentAdded;
            }

            _forwarding = false;
        }

        Summary?.Stop();
    }
}
