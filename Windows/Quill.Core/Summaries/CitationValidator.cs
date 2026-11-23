using Quill.Core.Domain;

namespace Quill.Core.Summaries;

/// <summary>An item removed because its timestamp is not inside any transcript segment.</summary>
/// <param name="Section">Section key from <see cref="SummarySections"/>.</param>
public sealed record DroppedCitation(string Section, string Text, TimeSpan At);

public sealed record CitationValidationResult(MeetingSummary Summary, IReadOnlyList<DroppedCitation> Dropped)
{
    public bool HasDropped => Dropped.Count > 0;
}

/// <summary>
/// Anti-hallucination gate (design §4, §7.1): every cited item must point at a
/// moment where somebody actually spoke. Items whose <c>at</c> (or a topic's
/// <c>start</c>) falls outside every segment are removed and returned so the
/// caller can log them. Uncited fields (executive summary, next steps) are kept.
/// </summary>
public static class CitationValidator
{
    /// <summary>
    /// Default slack around each segment. The model only sees whole seconds
    /// ("[00:01:00]" for a segment starting at 60.4 s), so a strict check would
    /// reject correct citations.
    /// </summary>
    public static readonly TimeSpan DefaultTolerance = TimeSpan.FromSeconds(1);

    public static CitationValidationResult Validate(
        MeetingSummary summary,
        IReadOnlyList<TranscriptSegment> transcript,
        TimeSpan? tolerance = null)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(transcript);

        var slack = tolerance ?? DefaultTolerance;
        if (slack < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(tolerance), tolerance, "tolerance cannot be negative");
        }

        var dropped = new List<DroppedCitation>();

        bool Cited(TimeSpan at) => transcript.Any(segment => segment is not null && segment.Contains(at, slack));

        IReadOnlyList<T> Keep<T>(IReadOnlyList<T> items, string section, Func<T, (string Text, TimeSpan At)> describe)
        {
            var kept = new List<T>(items.Count);
            foreach (var item in items)
            {
                var (text, at) = describe(item);
                if (Cited(at))
                {
                    kept.Add(item);
                }
                else
                {
                    dropped.Add(new DroppedCitation(section, text, at));
                }
            }

            return kept;
        }

        var validated = summary with
        {
            Decisions = Keep(summary.Decisions, SummarySections.Decisions, d => (d.Text, d.At)),
            ActionItems = Keep(summary.ActionItems, SummarySections.ActionItems, a => (a.Text, a.At)),
            OpenQuestions = Keep(summary.OpenQuestions, SummarySections.OpenQuestions, q => (q.Text, q.At)),
            Topics = Keep(summary.Topics, SummarySections.Topics, t => (t.Title, t.Start)),
            Risks = Keep(summary.Risks, SummarySections.Risks, r => (r.Text, r.At)),
            KeyFacts = Keep(summary.KeyFacts, SummarySections.KeyFacts, f => (f.Text, f.At)),
        };

        return new CitationValidationResult(validated, dropped);
    }
}
