using Quill.Core.Domain;
using Quill.Core.Live;
using Quill.Core.Ports;
using Quill.Core.Storage;
using Xunit;

namespace Quill.Core.Tests.Live;

public sealed class LiveTranscriptBufferTests
{
    [Fact]
    public void KeepsSegmentsOrderedByStartAcrossTracks()
    {
        var buffer = new LiveTranscriptBuffer();

        buffer.Add(Segment(Track.System, 5, 8, "b"));
        buffer.Add(Segment(Track.Microphone, 1, 3, "a"));
        buffer.Add(Segment(Track.Microphone, 9, 10, "c"));

        Assert.Equal(new[] { "a", "b", "c" }, buffer.Snapshot().Select(segment => segment.Text));
    }

    [Fact]
    public void RaisesSegmentAddedWithInsertionIndex()
    {
        var buffer = new LiveTranscriptBuffer();
        var events = new List<LiveSegmentAddedEventArgs>();
        buffer.SegmentAdded += (_, args) => events.Add(args);

        buffer.Add(Segment(Track.System, 5, 8, "b"));
        buffer.Add(Segment(Track.Microphone, 1, 3, "a"));

        Assert.Equal(new[] { 0, 0 }, events.Select(args => args.Index));
        Assert.Equal("a", events[1].Segment.Text);
        Assert.Equal(2, buffer.Count);
    }

    [Fact]
    public void SegmentsAreAlwaysProvisional()
    {
        var buffer = new LiveTranscriptBuffer();

        buffer.Add(Segment(Track.Microphone, 1, 2, "hola") with { Editable = true });

        Assert.False(Assert.Single(buffer.Snapshot()).Editable);
    }

    [Fact]
    public void DropsBlankText()
    {
        var buffer = new LiveTranscriptBuffer();

        Assert.False(buffer.Add(Segment(Track.Microphone, 1, 2, "   ")));
        Assert.Empty(buffer.Snapshot());
    }

    [Fact]
    public void TrimsText()
    {
        var buffer = new LiveTranscriptBuffer();

        buffer.Add(Segment(Track.Microphone, 1, 2, "  hola  "));

        Assert.Equal("hola", Assert.Single(buffer.Snapshot()).Text);
    }

    [Fact]
    public void DropsSegmentMostlyCoveredByPreviousOnSameTrack()
    {
        var buffer = new LiveTranscriptBuffer();
        buffer.Add(Segment(Track.System, 0, 12, "primero"));

        Assert.False(buffer.Add(Segment(Track.System, 11.5, 12.2, "repetido")));
        Assert.False(buffer.Add(Segment(Track.System, 4, 6, "antiguo")));
        Assert.Single(buffer.Snapshot());
    }

    [Fact]
    public void TrimsStartOfSegmentOverlappingPreviousOnSameTrack()
    {
        var buffer = new LiveTranscriptBuffer();
        buffer.Add(Segment(Track.System, 0, 12, "primero"));

        Assert.True(buffer.Add(Segment(Track.System, 11.5, 15, "segundo")));

        Assert.Equal(TimeSpan.FromSeconds(12), buffer.Snapshot()[1].Start);
    }

    [Fact]
    public void OverlapOnOtherTrackIsKept()
    {
        var buffer = new LiveTranscriptBuffer();
        buffer.Add(Segment(Track.System, 0, 12, "ellos"));

        Assert.True(buffer.Add(Segment(Track.Microphone, 2, 4, "yo")));
        Assert.Equal(2, buffer.Count);
    }

    [Fact]
    public async Task SavesToLiveTranscriptJson()
    {
        using var temp = new TempDirectory();
        var folder = temp.Session("2026.11.23-1000");
        var store = new FileSessionStore(temp.Path);
        var buffer = new LiveTranscriptBuffer();
        buffer.Add(Segment(Track.Microphone, 1, 2, "hola"));
        buffer.Add(Segment(Track.System, 3, 4, "buenas"));

        await buffer.SaveAsync(store, "2026.11.23-1000", new TranscriptInfo("whisper.net", "small"), CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(folder, "live", "transcript.json")));
        var loaded = await store.LoadTranscriptAsync("2026.11.23-1000", TranscriptPass.Live, CancellationToken.None);
        Assert.Equal(new[] { "hola", "buenas" }, loaded.Select(segment => segment.Text));
        Assert.Equal(new[] { Track.Microphone, Track.System }, loaded.Select(segment => segment.Track));
    }

    [Fact]
    public void ProvisionalSpeakerDependsOnTrack()
    {
        Assert.Equal(SpeakerIds.Me, LiveSpeakers.ProvisionalFor(Track.Microphone));
        Assert.True(SpeakerIds.IsRemote(LiveSpeakers.ProvisionalFor(Track.System)));
    }

    internal static TranscriptSegment Segment(Track track, double start, double end, string text) => new(
        TimeSpan.FromSeconds(start),
        TimeSpan.FromSeconds(end),
        text,
        LiveSpeakers.ProvisionalFor(track),
        track,
        Editable: false);
}
