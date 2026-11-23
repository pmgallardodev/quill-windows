using Quill.Core.Domain;
using Quill.Core.Ports;

namespace Quill.Core.Live;

/// <summary>Provisional speaker ids for live segments, before speaker resolution (F5) runs.</summary>
public static class LiveSpeakers
{
    /// <summary>"me" for the microphone, "speaker-1" for the system track.</summary>
    public static string ProvisionalFor(Track track) =>
        track == Track.Microphone ? SpeakerIds.Me : SpeakerIds.Remote(1);
}

/// <summary>Raised when a live segment is accepted.</summary>
/// <param name="Segment">The stored segment (trimmed, provisional).</param>
/// <param name="Index">Position it was inserted at in <see cref="LiveTranscriptBuffer.Snapshot"/>.</param>
public sealed record LiveSegmentAddedEventArgs(TranscriptSegment Segment, int Index);

/// <summary>
/// Live transcript of one recording: segments from both tracks ordered by start time. Consumers
/// (live panel, live summary loop) read <see cref="Snapshot"/> and subscribe to
/// <see cref="SegmentAdded"/>, which is raised on the adding thread, outside the lock.
/// Thread-safe.
/// </summary>
/// <remarks>
/// Chunks cut on continuous speech overlap a little, so the same words can come back twice on one
/// track. A segment whose midpoint is before the end of the previous segment of its track is a
/// repeat and is dropped; one that only starts earlier is trimmed to start at that end.
/// </remarks>
public sealed class LiveTranscriptBuffer
{
    private readonly object _lock = new();
    private readonly List<TranscriptSegment> _segments = new();
    private readonly Dictionary<Track, TimeSpan> _lastEnd = new();
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    public event EventHandler<LiveSegmentAddedEventArgs>? SegmentAdded;

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _segments.Count;
            }
        }
    }

    /// <returns>False when the segment was dropped (blank text or overlap repeat).</returns>
    public bool Add(TranscriptSegment segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        var text = segment.Text?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            return false;
        }

        TranscriptSegment stored;
        int index;
        lock (_lock)
        {
            var start = segment.Start;
            var end = segment.End < start ? start : segment.End;
            if (_lastEnd.TryGetValue(segment.Track, out var lastEnd))
            {
                var midpoint = start + ((end - start) / 2);
                if (end <= lastEnd || midpoint < lastEnd)
                {
                    return false;
                }

                if (start < lastEnd)
                {
                    start = lastEnd;
                }
            }

            stored = segment with { Start = start, End = end, Text = text, Editable = false };
            index = _segments.FindLastIndex(existing => existing.Start <= start) + 1;
            _segments.Insert(index, stored);
            _lastEnd[segment.Track] = end;
        }

        SegmentAdded?.Invoke(this, new LiveSegmentAddedEventArgs(stored, index));
        return true;
    }

    public IReadOnlyList<TranscriptSegment> Snapshot()
    {
        lock (_lock)
        {
            return _segments.ToArray();
        }
    }

    /// <summary>
    /// Writes the current snapshot to live/transcript.json. Concurrent saves are serialized and
    /// each takes its snapshot after acquiring the lock, so the last write is always the newest.
    /// </summary>
    public async Task SaveAsync(
        ISessionStore store,
        string sessionId,
        TranscriptInfo info,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        await _saveLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await store.SaveTranscriptAsync(sessionId, TranscriptPass.Live, info, Snapshot(), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _saveLock.Release();
        }
    }
}
