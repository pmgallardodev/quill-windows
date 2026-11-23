using Quill.Core.Domain;

namespace Quill.Core.Transcripts;

/// <summary>Time-window selection over a transcript, e.g. for "What did I miss?".</summary>
public static class TranscriptWindow
{
    /// <summary>
    /// Segments overlapping the open interval (now - window, now), ordered by start. <paramref name="now"/>
    /// is the current recording offset; it defaults to the latest segment end.
    /// A segment ending exactly at the window start is excluded.
    /// </summary>
    public static IReadOnlyList<TranscriptSegment> Last(
        IReadOnlyList<TranscriptSegment> segments,
        TimeSpan window,
        TimeSpan? now = null)
    {
        ArgumentNullException.ThrowIfNull(segments);
        if (segments.Count == 0 || window <= TimeSpan.Zero)
        {
            return [];
        }

        var end = now ?? segments.Max(segment => segment.End);
        return Between(segments, end - window, end);
    }

    /// <summary>Segments overlapping the open interval (from, to), ordered by start (ties by end).</summary>
    public static IReadOnlyList<TranscriptSegment> Between(
        IReadOnlyList<TranscriptSegment> segments,
        TimeSpan from,
        TimeSpan to)
    {
        ArgumentNullException.ThrowIfNull(segments);
        if (to <= from)
        {
            return [];
        }

        return segments
            .Where(segment => segment is not null && segment.End > from && segment.Start < to)
            .OrderBy(segment => segment.Start)
            .ThenBy(segment => segment.End)
            .ToList();
    }
}
