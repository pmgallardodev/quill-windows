using Quill.Core.Domain;
using Quill.Windows.Composition;
using Xunit;

namespace Quill.Windows.Tests.Composition;

public sealed class LiveRecordingTests
{
    [Fact]
    public void SnapshotForwardingIncludesLateSegmentsExactlyOnce()
    {
        var forwarded = new HashSet<TranscriptSegment>(ReferenceEqualityComparer.Instance);
        var first = Segment(TimeSpan.FromSeconds(10), "first");
        var late = Segment(TimeSpan.FromSeconds(2), "late");

        var initial = LiveRecording.SelectUnforwarded([first], forwarded);
        var afterLateArrival = LiveRecording.SelectUnforwarded([late, first], forwarded);

        Assert.Equal([first], initial);
        Assert.Equal([late], afterLateArrival);
        Assert.Empty(LiveRecording.SelectUnforwarded([late, first], forwarded));
    }

    private static TranscriptSegment Segment(TimeSpan start, string text) =>
        new(start, start + TimeSpan.FromSeconds(1), text, "speaker-1", Track.System);
}
