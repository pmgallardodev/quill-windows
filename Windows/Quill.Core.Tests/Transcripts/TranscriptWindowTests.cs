using Quill.Core.Domain;
using Quill.Core.Transcripts;
using Xunit;

namespace Quill.Core.Tests.Transcripts;

public sealed class TranscriptWindowTests
{
    private static TranscriptSegment Seg(double startMinutes, double endMinutes, string text = "t") =>
        new(TimeSpan.FromMinutes(startMinutes), TimeSpan.FromMinutes(endMinutes), text, SpeakerIds.Me, Track.Microphone);

    [Fact]
    public void LastReturnsSegmentsOverlappingTheWindowEndingNow()
    {
        IReadOnlyList<TranscriptSegment> segments =
        [
            Seg(0, 1, "a"), Seg(2, 4.5, "b"), Seg(6, 7, "c"), Seg(9, 10, "d"),
        ];

        var window = TranscriptWindow.Last(segments, TimeSpan.FromMinutes(5), now: TimeSpan.FromMinutes(9.5));

        // Window is [4.5, 9.5]: "b" ends exactly at 4.5 and is excluded; "d" started before now.
        Assert.Equal(["c", "d"], window.Select(s => s.Text));
    }

    [Fact]
    public void LastDefaultsNowToTheLatestSegmentEnd()
    {
        IReadOnlyList<TranscriptSegment> segments = [Seg(0, 1, "a"), Seg(20, 21, "b"), Seg(24, 25, "c")];

        var window = TranscriptWindow.Last(segments, TimeSpan.FromMinutes(5));

        Assert.Equal(["b", "c"], window.Select(s => s.Text));
    }

    [Fact]
    public void LastIsOrderedByStartEvenWhenTracksInterleave()
    {
        IReadOnlyList<TranscriptSegment> segments = [Seg(8, 9, "late"), Seg(6, 7, "early")];

        var window = TranscriptWindow.Last(segments, TimeSpan.FromMinutes(10));

        Assert.Equal(["early", "late"], window.Select(s => s.Text));
    }

    [Fact]
    public void EmptyInputOrNonPositiveWindowYieldsNothing()
    {
        Assert.Empty(TranscriptWindow.Last([], TimeSpan.FromMinutes(5)));
        Assert.Empty(TranscriptWindow.Last([Seg(0, 1)], TimeSpan.Zero));
    }

    [Fact]
    public void BetweenSelectsOverlappingRange()
    {
        IReadOnlyList<TranscriptSegment> segments = [Seg(0, 1, "a"), Seg(2, 3, "b"), Seg(5, 6, "c")];

        var range = TranscriptWindow.Between(segments, TimeSpan.FromMinutes(2.5), TimeSpan.FromMinutes(5));

        Assert.Equal(["b"], range.Select(s => s.Text));
    }
}
