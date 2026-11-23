using Quill.Core.Domain;
using Quill.Core.Live;
using Xunit;

namespace Quill.Core.Tests.Live;

public sealed class LiveTimelineTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 23, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FirstTrackDefinesTheOrigin()
    {
        var timeline = new LiveTimeline();

        Assert.Equal(TimeSpan.Zero, timeline.OffsetOf(Track.System, T0));
        Assert.Equal(TimeSpan.FromMilliseconds(250), timeline.OffsetOf(Track.Microphone, T0.AddMilliseconds(250)));
    }

    [Fact]
    public void OffsetIsFixedOnFirstSighting()
    {
        var timeline = new LiveTimeline();
        timeline.OffsetOf(Track.Microphone, T0);
        timeline.OffsetOf(Track.System, T0.AddSeconds(1));

        Assert.Equal(TimeSpan.FromSeconds(1), timeline.OffsetOf(Track.System, T0.AddSeconds(5)));
        Assert.Equal(TimeSpan.Zero, timeline.OffsetOf(Track.Microphone, T0.AddSeconds(9)));
    }

    [Fact]
    public void TrackStartingBeforeOriginIsClampedToZero()
    {
        var timeline = new LiveTimeline();
        timeline.OffsetOf(Track.Microphone, T0);

        Assert.Equal(TimeSpan.Zero, timeline.OffsetOf(Track.System, T0.AddMilliseconds(-40)));
    }
}
