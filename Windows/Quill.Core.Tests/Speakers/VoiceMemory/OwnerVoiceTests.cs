using Quill.Core.Domain;
using Quill.Core.Ports;
using Quill.Core.Speakers;
using Quill.Core.Speakers.VoiceMemory;
using Xunit;

namespace Quill.Core.Tests.Speakers.VoiceMemory;

public sealed class OwnerVoiceTests
{
    private static readonly float[] OwnerPrint = [1f, 0f, 0f];

    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    private static MicrophoneCluster Cluster(int id, double speechSeconds, float[]? embedding = null, double? energy = null) =>
        new(id, S(speechSeconds), embedding, energy);

    [Fact]
    public void TheClusterMatchingTheOwnersVoicePrintIsTheOwnerEvenWhenItSpeaksLess()
    {
        var selection = OwnerSelector.Select(
            [Cluster(0, 300, [0f, 1f, 0f], 0.9), Cluster(1, 60, [0.95f, 0.1f, 0f], 0.2)],
            OwnerPrint);

        Assert.Equal(new OwnerSelection(1, OwnerSelectionReason.VoicePrint), selection! with { Similarity = null });
        Assert.True(selection!.Similarity > 0.9);
    }

    [Fact]
    public void WithoutAMatchingVoicePrintTheLoudestClusterIsTheOwner()
    {
        var selection = OwnerSelector.Select(
            [Cluster(0, 300, [0f, 1f, 0f], 0.1), Cluster(1, 200, [0f, 0f, 1f], 0.4)],
            OwnerPrint);

        Assert.Equal(1, selection!.ClusterId);
        Assert.Equal(OwnerSelectionReason.Loudest, selection.Reason);
    }

    [Fact]
    public void AnAmbiguousVoicePrintMatchFallsBackToTheLoudestCluster()
    {
        // Both clusters look like the owner: the voice print cannot decide.
        var selection = OwnerSelector.Select(
            [Cluster(0, 300, [0.9f, 0.1f, 0f], 0.1), Cluster(1, 200, [0.9f, 0f, 0.1f], 0.4)],
            OwnerPrint);

        Assert.Equal(OwnerSelectionReason.Loudest, selection!.Reason);
        Assert.Equal(1, selection.ClusterId);
    }

    [Fact]
    public void AShortLoudBlipIsNotTheOwner()
    {
        var selection = OwnerSelector.Select(
            [Cluster(0, 300, energy: 0.2), Cluster(1, 10, energy: 0.9)],
            ownerVoice: null);

        Assert.Equal(0, selection!.ClusterId);
    }

    [Fact]
    public void WithoutVoicePrintOrEnergyTheClusterWithMostSpeechIsTheOwner()
    {
        var selection = OwnerSelector.Select([Cluster(3, 100), Cluster(5, 250)], ownerVoice: null);

        Assert.Equal(new OwnerSelection(5, OwnerSelectionReason.MostSpeech), selection);
    }

    [Fact]
    public void NoClustersNoOwner() => Assert.Null(OwnerSelector.Select([], OwnerPrint));

    private static TranscriptSegment Mic(double start, double end) => new(S(start), S(end), "hola", SpeakerIds.Me, Track.Microphone);

    [Fact]
    public void AnOnlineMeetingTeachesTheClusterCoveringTheGenuineMicSpeech()
    {
        var diarization = new DiarizationResult(
            Track.Microphone,
            [new(S(0), S(30), 0), new(S(30), S(40), 1)],
            new Dictionary<int, float[]> { [0] = [1f, 0f], [1] = [0f, 1f] });
        // The 30-40 s segment is echo of the remote side and must not count.
        var segments = new[] { Mic(0, 15), Mic(15, 30), Mic(30, 40) };
        var verdicts = new[] { new EchoVerdict(0, false, 0.9), new EchoVerdict(1, false, 0.9), new EchoVerdict(2, true, 0.9) };

        Assert.Equal([1f, 0f], OwnerVoiceLearner.FromOnlineMeeting(diarization, segments, verdicts)!);
    }

    [Fact]
    public void ASharedMicrophoneOrTooLittleSpeechTeachesNothing()
    {
        var shared = new DiarizationResult(
            Track.Microphone,
            [new(S(0), S(20), 0), new(S(20), S(40), 1)],
            new Dictionary<int, float[]> { [0] = [1f, 0f], [1] = [0f, 1f] });
        var segments = new[] { Mic(0, 20), Mic(20, 40) };
        var genuine = new[] { new EchoVerdict(0, false, 0.9), new EchoVerdict(1, false, 0.9) };
        Assert.Null(OwnerVoiceLearner.FromOnlineMeeting(shared, segments, genuine));

        var brief = new DiarizationResult(Track.Microphone, [new(S(0), S(5), 0)], new Dictionary<int, float[]> { [0] = [1f, 0f] });
        Assert.Null(OwnerVoiceLearner.FromOnlineMeeting(brief, [Mic(0, 5)], [new EchoVerdict(0, false, 0.9)]));
    }

    [Fact]
    public void InPersonResolutionUsesTheOwnerVoicePrintToPickMe()
    {
        // Cluster 0 talks most, but cluster 1 is the owner's voice.
        var segments = new[] { Mic(0, 10), Mic(10, 40), Mic(40, 45) };
        var diarization = new DiarizationResult(
            Track.Microphone,
            [new(S(0), S(10), 1), new(S(10), S(40), 0), new(S(40), S(45), 1)],
            new Dictionary<int, float[]> { [0] = [0f, 1f, 0f], [1] = [1f, 0f, 0f] });

        var resolved = SpeakerResolver.Resolve(new SpeakerResolutionInput(
            segments,
            [new(0, false, 0.9), new(1, false, 0.9), new(2, false, 0.9)],
            new DiarizationResult(Track.System, [], new Dictionary<int, float[]>()),
            diarization,
            OwnerVoice: OwnerPrint));

        Assert.Equal([SpeakerIds.Me, "speaker-1", SpeakerIds.Me], resolved.Segments.Select(s => s.SpeakerId));
        Assert.Equal([1f, 0f, 0f], resolved.Speakers.Single(p => p.Id == SpeakerIds.Me).Embedding!);
    }

    [Fact]
    public void InPersonResolutionFallsBackToTheLoudestMicCluster()
    {
        var segments = new[] { Mic(0, 10), Mic(10, 40) };
        var diarization = new DiarizationResult(
            Track.Microphone,
            [new(S(0), S(10), 1), new(S(10), S(40), 0)],
            new Dictionary<int, float[]>());
        // 1 s frames: loud first 10 s (cluster 1), quiet afterwards (cluster 0, more speech).
        var energy = new EnergyEnvelope(S(1), Enumerable.Range(0, 40).Select(i => i < 10 ? 0.5f : 0.05f).ToList());

        var resolved = SpeakerResolver.Resolve(new SpeakerResolutionInput(
            segments,
            [new(0, false, 0.9), new(1, false, 0.9)],
            new DiarizationResult(Track.System, [], new Dictionary<int, float[]>()),
            diarization,
            MicrophoneEnergy: energy));

        Assert.Equal([SpeakerIds.Me, "speaker-1"], resolved.Segments.Select(s => s.SpeakerId));
    }
}
