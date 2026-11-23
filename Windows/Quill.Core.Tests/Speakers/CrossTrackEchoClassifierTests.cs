using Quill.Core.Domain;
using Quill.Core.Ports;
using Quill.Core.Speakers;
using Xunit;

namespace Quill.Core.Tests.Speakers;

public sealed class CrossTrackEchoClassifierTests
{
    private static readonly TimeSpan Frame = TimeSpan.FromMilliseconds(10);

    private static TranscriptSegment Mic(double start, double end, string text) =>
        new(TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end), text, SpeakerIds.Me, Track.Microphone);

    private static TranscriptSegment Sys(double start, double end, string text) =>
        new(TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end), text, SpeakerIds.LegacyThem, Track.System);

    /// <summary>Speech-like envelope: a deterministic pseudo-random syllable pattern, silence outside [start, end).</summary>
    private static float[] Speech(double totalSeconds, double start, double end, int seed)
    {
        var frames = (int)(totalSeconds * 100);
        var rms = new float[frames];
        var random = new Random(seed);
        for (var i = (int)(start * 100); i < Math.Min(frames, (int)(end * 100)); i++)
        {
            rms[i] = (float)(0.05 + 0.3 * random.NextDouble());
        }

        return rms;
    }

    /// <summary>What a laptop mic picks up from its speakers: the system signal, attenuated and delayed.</summary>
    private static float[] Leak(float[] system, int delayFrames, float gain)
    {
        var mic = new float[system.Length];
        for (var i = delayFrames; i < system.Length; i++)
        {
            mic[i] = system[i - delayFrames] * gain + 0.002f;
        }

        return mic;
    }

    private static Task<IReadOnlyList<EchoVerdict>> Classify(
        IReadOnlyList<TranscriptSegment> mic,
        IReadOnlyList<TranscriptSegment> system,
        float[]? micEnergy = null,
        float[]? systemEnergy = null) =>
        new CrossTrackEchoClassifier().ClassifyAsync(
            new EchoClassificationRequest(
                mic,
                system,
                micEnergy is null ? null : new EnergyEnvelope(Frame, micEnergy),
                systemEnergy is null ? null : new EnergyEnvelope(Frame, systemEnergy)),
            CancellationToken.None);

    [Fact]
    public async Task ReturnsOneVerdictPerMicrophoneSegmentInOrder()
    {
        var verdicts = await Classify(
            [Mic(0, 2, "hello there"), Mic(5, 7, "another thing")],
            [Sys(10, 12, "unrelated")]);

        Assert.Equal([0, 1], verdicts.Select(v => v.MicrophoneSegmentIndex));
        Assert.All(verdicts, v => Assert.InRange(v.Confidence, 0, 1));
        Assert.All(verdicts, v => Assert.False(v.IsEcho));
    }

    [Fact]
    public async Task HeadphonesCallHasNoEcho()
    {
        // Owner and remote take turns; the mic never hears the remote voice.
        var system = Speech(20, 0, 8, seed: 1);
        var mic = Speech(20, 9, 15, seed: 2);

        var verdicts = await Classify(
            [Mic(9, 15, "I think we should ship the release on Friday")],
            [Sys(0, 8, "what do you think about the release date")],
            mic,
            system);

        Assert.False(Assert.Single(verdicts).IsEcho);
    }

    [Fact]
    public async Task SpeakersCallEchoedPhraseIsEcho()
    {
        var system = Speech(10, 1, 6, seed: 3);
        var mic = Leak(system, delayFrames: 12, gain: 0.3f);

        var verdicts = await Classify(
            [Mic(1.1, 6.1, "we need the budget numbers by Monday morning")],
            [Sys(1, 6, "We need the budget numbers by Monday morning.")],
            mic,
            system);

        var verdict = Assert.Single(verdicts);
        Assert.True(verdict.IsEcho);
        Assert.True(verdict.Confidence >= 0.8, $"confidence {verdict.Confidence}");
        Assert.False(string.IsNullOrWhiteSpace(verdict.Reason));
    }

    [Fact]
    public async Task EchoedPhraseIsDetectedFromTextAloneWhenNoEnergyIsAvailable()
    {
        // Whisper often mishears the leaked copy slightly and cuts it differently.
        var verdicts = await Classify(
            [Mic(30.4, 33.0, "so the deadline is next Friday right")],
            [Sys(29.8, 34.0, "Okay, so the deadline is next Friday, right? Good.")]);

        var verdict = Assert.Single(verdicts);
        Assert.True(verdict.IsEcho);
        Assert.Contains("text", verdict.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EchoIsDetectedFromEnergyAloneWhenTextDiffers()
    {
        var system = Speech(10, 2, 7, seed: 4);
        var mic = Leak(system, delayFrames: 25, gain: 0.2f);

        var verdicts = await Classify(
            [Mic(2.2, 7.2, "garbled words nobody said")],
            [Sys(2, 7, "a completely different transcription")],
            mic,
            system);

        var verdict = Assert.Single(verdicts);
        Assert.True(verdict.IsEcho);
        Assert.Contains("energy", verdict.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LeakDelayedBeyondTheLagWindowIsNotEnergyEcho()
    {
        var system = Speech(10, 2, 7, seed: 5);
        var mic = Leak(system, delayFrames: 80, gain: 0.3f);

        var verdicts = await Classify(
            [Mic(2.8, 7.8, "garbled words nobody said")],
            [Sys(2, 7, "a completely different transcription")],
            mic,
            system);

        Assert.False(Assert.Single(verdicts).IsEcho);
    }

    [Fact]
    public async Task GenuineCrosstalkIsNotEcho()
    {
        // Both people talk at the same time, saying different things: independent envelopes.
        var system = Speech(10, 1, 6, seed: 6);
        var mic = Speech(10, 2, 5, seed: 7);

        var verdicts = await Classify(
            [Mic(2, 5, "sorry, can I jump in here for a second")],
            [Sys(1, 6, "and the third point on the agenda is hiring")],
            mic,
            system);

        Assert.False(Assert.Single(verdicts).IsEcho);
    }

    [Fact]
    public async Task ShortBackchannelMatchingRemoteWordsIsNotEchoWithoutEnergyEvidence()
    {
        var verdicts = await Classify(
            [Mic(3, 3.5, "yes")],
            [Sys(1, 5, "yes, we agree on that")]);

        Assert.False(Assert.Single(verdicts).IsEcho);
    }

    [Fact]
    public async Task SameWordsAtADifferentTimeAreNotEcho()
    {
        var verdicts = await Classify(
            [Mic(60, 63, "we need the budget numbers by Monday")],
            [Sys(10, 13, "we need the budget numbers by Monday")]);

        Assert.False(Assert.Single(verdicts).IsEcho);
    }

    [Fact]
    public void TextOverlapIgnoresCasePunctuationAndAccents()
    {
        Assert.Equal(1.0, TextOverlap.Score("¿Qué tal, Señor?", "que tal senor"), 3);
        Assert.Equal(0.0, TextOverlap.Score("", "anything"), 3);
        Assert.Equal(0.5, TextOverlap.Score("alpha beta", "alpha gamma"), 3);
    }
}
