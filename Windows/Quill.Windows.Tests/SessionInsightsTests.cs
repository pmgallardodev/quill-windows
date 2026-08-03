using Quill.Windows.Sessions;
using Xunit;

namespace Quill.Windows.Tests;

public class SessionInsightsTests
{
    [Fact]
    public void SpeakingTime_AddsUpTheSegmentsOfOneSpeaker()
    {
        var segments = new[]
        {
            new TranscriptSegment("me", 0, 2_000, "uno"),
            new TranscriptSegment("them", 2_000, 5_000, "dos"),
            new TranscriptSegment("me", 6_000, 7_500, "tres"),
        };

        Assert.Equal(TimeSpan.FromMilliseconds(3_500), SessionInsights.SpeakingTime(segments, "me"));
        Assert.Equal(TimeSpan.FromMilliseconds(3_000), SessionInsights.SpeakingTime(segments, "them"));
    }

    [Fact]
    public void SpeakingTime_DoesNotCountOverlapWithinTheSameTrack_Twice()
    {
        // Whisper puede solapar segmentos contiguos; sumarlos inflaria el total.
        var segments = new[]
        {
            new TranscriptSegment("me", 0, 5_000, "uno"),
            new TranscriptSegment("me", 3_000, 8_000, "dos"),
        };

        Assert.Equal(TimeSpan.FromMilliseconds(8_000), SessionInsights.SpeakingTime(segments, "me"));
    }

    [Fact]
    public void SpeakingTime_IgnoresSegmentsThatEndBeforeTheyStart()
    {
        var segments = new[] { new TranscriptSegment("me", 5_000, 1_000, "roto") };

        Assert.Equal(TimeSpan.Zero, SessionInsights.SpeakingTime(segments, "me"));
    }

    [Fact]
    public void Summarize_SplitsTheShareOverSpeechAndNotOverTheSessionLength()
    {
        // Tres minutos de silencio entre medias no pertenecen a nadie.
        var segments = new[]
        {
            new TranscriptSegment("me", 0, 30_000, "uno"),
            new TranscriptSegment("them", 200_000, 210_000, "dos"),
        };

        var summary = SessionInsights.Summarize(segments);

        Assert.Equal(0.75d, summary.MicrophoneShare, precision: 3);
        Assert.Equal(0.25d, summary.SystemShare, precision: 3);
        Assert.Equal(TimeSpan.FromMilliseconds(40_000), summary.Total);
    }

    [Fact]
    public void Summarize_ReportsNoSpeech_ForAnEmptyTranscript()
    {
        var summary = SessionInsights.Summarize(Array.Empty<TranscriptSegment>());

        Assert.False(summary.HasSpeech);
        Assert.Equal(0d, summary.MicrophoneShare);
    }

    [Fact]
    public void SilentTracks_FlagsTheMicrophone_WhenOnlyTheOtherSideWasTranscribed()
    {
        // El caso del micro silenciado: hoy el transcript sale a medias sin avisar.
        var segments = new[] { new TranscriptSegment("them", 0, 5_000, "solo ellos") };

        Assert.Equal(new[] { "me" }, SessionInsights.SilentTracks(segments));
    }

    [Fact]
    public void SilentTracks_FlagsSystemAudio_WhenTheLoopbackCapturedNothing()
    {
        var segments = new[] { new TranscriptSegment("me", 0, 5_000, "solo yo") };

        Assert.Equal(new[] { "them" }, SessionInsights.SilentTracks(segments));
    }

    [Fact]
    public void SilentTracks_FlagsNothing_WhenBothTracksHaveSpeech()
    {
        var segments = new[]
        {
            new TranscriptSegment("me", 0, 1_000, "yo"),
            new TranscriptSegment("them", 1_000, 2_000, "ellos"),
        };

        Assert.Empty(SessionInsights.SilentTracks(segments));
    }

    [Fact]
    public void SilentTracks_FlagsNothing_WhenThereIsNoTranscriptAtAll()
    {
        // Sin segmentos no se puede distinguir un fallo de una sesion vacia.
        Assert.Empty(SessionInsights.SilentTracks(Array.Empty<TranscriptSegment>()));
    }

    [Fact]
    public void Timeline_MarksTheSlotsWhereEachSideSpoke()
    {
        var segments = new[]
        {
            new TranscriptSegment("me", 0, 1_000, "principio"),
            new TranscriptSegment("them", 9_000, 10_000, "final"),
        };

        var timeline = SessionInsights.Timeline(segments, slots: 10);

        Assert.Equal(10, timeline.Count);
        Assert.True(timeline[0].Microphone);
        Assert.False(timeline[0].System);
        Assert.True(timeline[9].System);
        Assert.False(timeline[5].Microphone);
        Assert.False(timeline[5].System);
    }

    [Fact]
    public void Timeline_TreatsSegmentEndAsExclusiveAtSlotBoundaries()
    {
        // El segundo segmento fija la escala en 10 s, de modo que cada tramo dura
        // uno: sin el, el primer segmento seria toda la reunion y los ocuparia todos.
        var segments = new[]
        {
            new TranscriptSegment("me", 0, 1_000, "solo el primer slot"),
            new TranscriptSegment("them", 9_000, 10_000, "final"),
        };

        var timeline = SessionInsights.Timeline(segments, slots: 10);

        Assert.True(timeline[0].Microphone);
        Assert.False(timeline[1].Microphone);
    }

    [Fact]
    public void Timeline_FillsEverySlot_WhenOneSegmentSpansTheWholeSession()
    {
        var segments = new[] { new TranscriptSegment("me", 0, 1_000, "toda la reunion") };

        var timeline = SessionInsights.Timeline(segments, slots: 10);

        Assert.All(timeline, slot => Assert.True(slot.Microphone));
    }

    [Fact]
    public void Timeline_IgnoresUnknownSpeakers()
    {
        var segments = new[] { new TranscriptSegment("unknown", 0, 1_000, "no asignar") };

        var timeline = SessionInsights.Timeline(segments, slots: 10);

        Assert.All(timeline, slot =>
        {
            Assert.False(slot.Microphone);
            Assert.False(slot.System);
        });
    }

    [Fact]
    public void Timeline_IsEmpty_ForAnEmptyTranscript()
    {
        Assert.Empty(SessionInsights.Timeline(Array.Empty<TranscriptSegment>(), slots: 10));
    }

    [Fact]
    public void Timeline_IsEmpty_WhenAskedForNoSlots()
    {
        var segments = new[] { new TranscriptSegment("me", 0, 1_000, "uno") };

        Assert.Empty(SessionInsights.Timeline(segments, slots: 0));
    }
}
