using Quill.Core.Domain;
using Quill.Core.Live;
using Quill.Core.Ports;
using Xunit;
using static Quill.Core.Tests.Live.SignalFactory;

namespace Quill.Core.Tests.Live;

public sealed class SpeechChunkerTests
{
    private static readonly TimeSpan Tolerance = TimeSpan.FromMilliseconds(80);

    [Fact]
    public void SilenceNeverProducesChunks()
    {
        var chunker = new SpeechChunker(Track.Microphone, TimeSpan.Zero);

        var chunks = chunker.Push(Silence(30)).Concat(chunker.Flush()).ToList();

        Assert.Empty(chunks);
        Assert.Equal(TimeSpan.FromSeconds(30), chunker.Position);
    }

    [Fact]
    public void CutsUtteranceOnSilenceAfterMinimumLength()
    {
        var chunker = new SpeechChunker(Track.System, TimeSpan.Zero);

        var chunks = chunker.Push(Concat(Speech(4), Silence(1)));

        var chunk = Assert.Single(chunks);
        Assert.Equal(Track.System, chunk.Track);
        AssertClose(TimeSpan.Zero, chunk.Start);
        Assert.InRange(chunk.Duration, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(5));
        Assert.Empty(chunker.Flush());
    }

    [Fact]
    public void TrimsLeadingSilenceToPreRollAndAppliesTrackOffset()
    {
        var options = new SpeechChunkerOptions();
        var chunker = new SpeechChunker(Track.Microphone, TimeSpan.FromSeconds(2), options);

        var chunk = Assert.Single(chunker.Push(Concat(Silence(5), Speech(4), Silence(1))));

        AssertClose(TimeSpan.FromSeconds(2 + 5) - options.PreRoll, chunk.Start);
        Assert.True(chunk.Duration < TimeSpan.FromSeconds(5.5));
    }

    [Fact]
    public void ShortUtteranceIsEmittedAfterLongerSilence()
    {
        var options = new SpeechChunkerOptions();
        var chunker = new SpeechChunker(Track.Microphone, TimeSpan.Zero, options);

        Assert.Empty(chunker.Push(Concat(Speech(1), Silence(1))));
        var chunk = Assert.Single(chunker.Push(Silence(1)));

        Assert.InRange(chunk.Duration, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1) + options.ShortUtteranceSilence + Tolerance);
    }

    [Fact]
    public void ShortPauseMergesUtterancesBelowMinimumLength()
    {
        var chunker = new SpeechChunker(Track.Microphone, TimeSpan.Zero);

        var chunks = chunker.Push(Concat(Speech(1), Silence(0.8), Speech(3), Silence(1)));

        var chunk = Assert.Single(chunks);
        AssertClose(TimeSpan.Zero, chunk.Start);
        Assert.True(chunk.Duration >= TimeSpan.FromSeconds(4.8));
    }

    [Fact]
    public void ContinuousSpeechIsCutAtMaximumWithOverlap()
    {
        var options = new SpeechChunkerOptions();
        var chunker = new SpeechChunker(Track.System, TimeSpan.Zero, options);

        var chunks = chunker.Push(Speech(30)).Concat(chunker.Flush()).ToList();

        Assert.True(chunks.Count >= 3);
        Assert.All(chunks, chunk => Assert.True(chunk.Duration <= options.MaxChunk));
        for (var index = 1; index < chunks.Count; index++)
        {
            AssertClose(chunks[index - 1].End - options.Overlap, chunks[index].Start);
        }

        AssertClose(TimeSpan.FromSeconds(30), chunks[^1].End);
    }

    [Fact]
    public void FlushEmitsPendingSpeech()
    {
        var chunker = new SpeechChunker(Track.Microphone, TimeSpan.Zero);

        Assert.Empty(chunker.Push(Speech(2)));
        var chunk = Assert.Single(chunker.Flush());

        AssertClose(TimeSpan.FromSeconds(2), chunk.Duration);
        Assert.Empty(chunker.Flush());
    }

    [Fact]
    public void FlushDropsSpeckOfSpeechShorterThanMinimum()
    {
        var chunker = new SpeechChunker(Track.Microphone, TimeSpan.Zero);

        chunker.Push(Concat(Silence(1), Speech(0.06)));

        Assert.Empty(chunker.Flush());
    }

    [Fact]
    public void PushingInOddPiecesMatchesPushingAtOnce()
    {
        var signal = Concat(Silence(1.3), Speech(4.1), Silence(1.2), Speech(2), Silence(2));
        var whole = new SpeechChunker(Track.Microphone, TimeSpan.Zero);
        var pieces = new SpeechChunker(Track.Microphone, TimeSpan.Zero);

        var expected = whole.Push(signal).Concat(whole.Flush()).ToList();
        var actual = new List<AudioChunk>();
        for (var offset = 0; offset < signal.Length; offset += 777)
        {
            actual.AddRange(pieces.Push(signal.AsSpan(offset, Math.Min(777, signal.Length - offset))));
        }

        actual.AddRange(pieces.Flush());

        Assert.Equal(expected.Count, actual.Count);
        for (var index = 0; index < expected.Count; index++)
        {
            Assert.Equal(expected[index].Start, actual[index].Start);
            Assert.Equal(expected[index].Samples.ToArray(), actual[index].Samples.ToArray());
        }
    }

    [Fact]
    public void SteadyBackgroundNoiseIsNotSpeech()
    {
        var chunker = new SpeechChunker(Track.System, TimeSpan.Zero);

        var chunks = chunker.Push(Concat(Noise(6, 0.02f), Speech(4, 0.3f), Noise(2, 0.02f)));

        var chunk = Assert.Single(chunks);
        Assert.InRange(chunk.Start, TimeSpan.FromSeconds(5.5), TimeSpan.FromSeconds(6));
    }

    [Fact]
    public void ChunkSamplesAreIndependentCopies()
    {
        var chunker = new SpeechChunker(Track.System, TimeSpan.Zero);
        var first = Assert.Single(chunker.Push(Concat(Speech(4), Silence(1))));
        var snapshot = first.Samples.ToArray();

        chunker.Push(Concat(Speech(4, 0.9f), Silence(1)));

        Assert.Equal(snapshot, first.Samples.ToArray());
    }

    [Fact]
    public void RejectsInconsistentOptions()
    {
        Assert.Throws<ArgumentException>(() => new SpeechChunker(
            Track.Microphone,
            TimeSpan.Zero,
            new SpeechChunkerOptions { MinChunk = TimeSpan.FromSeconds(10), MaxChunk = TimeSpan.FromSeconds(5) }));
        Assert.Throws<ArgumentException>(() => new SpeechChunker(
            Track.Microphone,
            TimeSpan.Zero,
            new SpeechChunkerOptions { Overlap = TimeSpan.FromSeconds(12) }));
    }

    private static void AssertClose(TimeSpan expected, TimeSpan actual) =>
        Assert.InRange(actual, expected - Tolerance, expected + Tolerance);
}
