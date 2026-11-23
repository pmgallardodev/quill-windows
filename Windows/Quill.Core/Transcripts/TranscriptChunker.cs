using System.Text;
using Quill.Core.Domain;
using Quill.Core.Summaries;

namespace Quill.Core.Transcripts;

/// <summary>A contiguous run of segments that fits a token budget (map step of map-reduce).</summary>
/// <param name="Text">The prompt text: one formatted line per segment, each ending in "\n".</param>
/// <param name="EstimatedTokens">Sum of <see cref="TranscriptChunker.EstimateTokens"/> over the lines.</param>
public sealed record TranscriptChunk(IReadOnlyList<TranscriptSegment> Segments, string Text, int EstimatedTokens)
{
    public TimeSpan Start => Segments[0].Start;

    public TimeSpan End => Segments.Max(segment => segment.End);
}

/// <summary>
/// Turns a transcript into prompt text and splits long transcripts into chunks
/// for map-reduce summarization. Token counts are an approximation
/// (characters / 4, rounded up) that needs no tokenizer; keep a safety margin
/// below the model context when choosing the budget.
/// </summary>
public static class TranscriptChunker
{
    public const int CharactersPerToken = 4;

    public static int EstimateTokens(string text) =>
        string.IsNullOrEmpty(text) ? 0 : (text.Length + CharactersPerToken - 1) / CharactersPerToken;

    /// <summary>
    /// "[hh:mm:ss] speakerId: text" on one line. The bracketed timestamp is the
    /// exact format the model must cite back in <c>at</c>.
    /// </summary>
    public static string FormatLine(TranscriptSegment segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        return $"[{TimestampJsonConverter.Format(segment.Start)}] {segment.SpeakerId}: {Flatten(segment.Text)}";
    }

    /// <summary>
    /// Greedy, order-preserving packing of segments into chunks of at most
    /// <paramref name="maxTokens"/>. A segment is never split: one larger than the
    /// budget becomes a chunk on its own. Blank segments are skipped.
    /// </summary>
    public static IReadOnlyList<TranscriptChunk> Chunk(
        IReadOnlyList<TranscriptSegment> segments,
        int maxTokens,
        Func<TranscriptSegment, string>? format = null)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxTokens);
        format ??= FormatLine;

        var chunks = new List<TranscriptChunk>();
        var current = new List<TranscriptSegment>();
        var text = new StringBuilder();
        var tokens = 0;

        void Flush()
        {
            if (current.Count == 0)
            {
                return;
            }

            chunks.Add(new TranscriptChunk(current.ToList(), text.ToString(), tokens));
            current.Clear();
            text.Clear();
            tokens = 0;
        }

        foreach (var segment in segments)
        {
            if (segment is null || string.IsNullOrWhiteSpace(segment.Text))
            {
                continue;
            }

            var line = format(segment) + "\n";
            var lineTokens = EstimateTokens(line);
            if (current.Count > 0 && tokens + lineTokens > maxTokens)
            {
                Flush();
            }

            current.Add(segment);
            text.Append(line);
            tokens += lineTokens;
        }

        Flush();
        return chunks;
    }

    private static string Flatten(string? text) =>
        string.Join(' ', (text ?? string.Empty).Split(['\r', '\n', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries));
}
