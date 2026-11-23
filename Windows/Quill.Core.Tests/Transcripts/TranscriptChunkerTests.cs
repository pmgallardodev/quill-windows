using Quill.Core.Domain;
using Quill.Core.Transcripts;
using Xunit;

namespace Quill.Core.Tests.Transcripts;

public sealed class TranscriptChunkerTests
{
    private static TranscriptSegment Seg(int second, string text, string speaker = SpeakerIds.Me) =>
        new(TimeSpan.FromSeconds(second), TimeSpan.FromSeconds(second + 1), text, speaker, Track.Microphone);

    [Theory]
    [InlineData("", 0)]
    [InlineData("abc", 1)]
    [InlineData("abcd", 1)]
    [InlineData("abcde", 2)]
    public void EstimatesFourCharactersPerToken(string text, int tokens) =>
        Assert.Equal(tokens, TranscriptChunker.EstimateTokens(text));

    [Fact]
    public void FormatsLinesWithCitableTimestamp()
    {
        var line = TranscriptChunker.FormatLine(
            new TranscriptSegment(TimeSpan.FromSeconds(3725.6), TimeSpan.FromSeconds(3730), "  hola\nmundo ", "speaker-2", Track.System));

        Assert.Equal("[01:02:05] speaker-2: hola mundo", line);
    }

    [Fact]
    public void PacksSegmentsUpToTheBudgetWithoutSplittingOne()
    {
        // Each formatted line "[00:00:0N] me: xxxxxxxxxxxxxxxx" is 31 chars plus a newline = 32 chars -> 8 tokens.
        var segments = Enumerable.Range(0, 5).Select(i => Seg(i, new string('x', 16))).ToList();

        var chunks = TranscriptChunker.Chunk(segments, maxTokens: 20);

        Assert.Equal([2, 2, 1], chunks.Select(c => c.Segments.Count));
        Assert.All(chunks, c => Assert.True(c.EstimatedTokens <= 20));
        Assert.Equal(segments, chunks.SelectMany(c => c.Segments));
        Assert.Equal(TimeSpan.FromSeconds(2), chunks[1].Start);
        Assert.Equal(TimeSpan.FromSeconds(4), chunks[1].End);
        Assert.StartsWith("[00:00:02] me:", chunks[1].Text);
    }

    [Fact]
    public void OversizedSegmentGetsItsOwnChunk()
    {
        IReadOnlyList<TranscriptSegment> segments = [Seg(0, "a"), Seg(1, new string('y', 400)), Seg(2, "b")];

        var chunks = TranscriptChunker.Chunk(segments, maxTokens: 30);

        Assert.Equal(3, chunks.Count);
        Assert.Single(chunks[1].Segments);
        Assert.True(chunks[1].EstimatedTokens > 30);
    }

    [Fact]
    public void SkipsBlankSegmentsAndHandlesEmptyInput()
    {
        Assert.Empty(TranscriptChunker.Chunk([], 100));

        var chunks = TranscriptChunker.Chunk([Seg(0, "  "), Seg(1, "hola")], 100);

        Assert.Single(chunks);
        Assert.Single(chunks[0].Segments);
    }

    [Fact]
    public void RejectsNonPositiveBudget() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => TranscriptChunker.Chunk([Seg(0, "a")], 0));
}
