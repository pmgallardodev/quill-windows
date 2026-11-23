using Quill.Core.Domain;

namespace Quill.Windows.UI.Companion;

/// <summary>
/// Auto-scroll of the live transcript: follows new text while the user is at the bottom, stops
/// following as soon as they scroll up, and resumes when they scroll back down (or press "Ir al
/// final"). Fed with ScrollViewer numbers, no WPF types.
/// </summary>
internal sealed class AutoScrollPolicy
{
    /// <summary>Distance from the bottom (DIPs) that still counts as "at the bottom".</summary>
    public const double BottomTolerance = 24;

    public bool IsFollowing { get; private set; } = true;

    /// <summary>Content arrived while not following.</summary>
    public bool HasUnseen { get; private set; }

    /// <summary>
    /// Call from ScrollViewer.ScrollChanged. Returns true when the view should scroll to the end.
    /// </summary>
    /// <param name="extentChange">ExtentHeightChange: non-zero when content grew or shrank.</param>
    public bool OnScrollChanged(double offset, double viewport, double extent, double extentChange)
    {
        if (extentChange != 0)
        {
            // Content changed size: the user did not scroll.
            if (IsFollowing)
            {
                return true;
            }

            HasUnseen = extentChange > 0 || HasUnseen;
            return false;
        }

        IsFollowing = IsAtBottom(offset, viewport, extent);
        if (IsFollowing)
        {
            HasUnseen = false;
        }

        return false;
    }

    /// <summary>"Ir al final": follow again. The view scrolls to the end.</summary>
    public void Resume()
    {
        IsFollowing = true;
        HasUnseen = false;
    }

    public static bool IsAtBottom(double offset, double viewport, double extent) =>
        offset + viewport >= extent - BottomTolerance;
}

internal enum TranscriptSyncResult
{
    Inserted,

    /// <summary>Already present (seen by the initial snapshot).</summary>
    Duplicate,

    /// <summary>The index does not fit the rows: rebuild them from a snapshot.</summary>
    NeedsResync,
}

/// <summary>
/// Applies <see cref="Quill.Core.Live.LiveSegmentAddedEventArgs"/> to the rows shown by the panel.
/// Events are posted to the UI thread after the initial snapshot, so a segment may arrive twice
/// or before the snapshot; both cases are handled here.
/// </summary>
internal static class TranscriptRowsSync
{
    public static TranscriptSyncResult Apply<TRow>(
        IList<TRow> rows,
        TranscriptSegment segment,
        int index,
        Func<TRow, TranscriptSegment> segmentOf,
        Func<TranscriptSegment, TRow> create)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(segment);
        if (index < 0 || index > rows.Count)
        {
            return TranscriptSyncResult.NeedsResync;
        }

        if (index < rows.Count && segmentOf(rows[index]) == segment)
        {
            return TranscriptSyncResult.Duplicate;
        }

        rows.Insert(index, create(segment));
        return TranscriptSyncResult.Inserted;
    }
}
