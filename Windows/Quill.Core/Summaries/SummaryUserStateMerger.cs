using System.Globalization;
using System.Text;
using Quill.Core.Domain;

namespace Quill.Core.Summaries;

/// <param name="Summary">The new summary with the user's state carried over.</param>
/// <param name="CarriedDone">How many new action items received a done flag from the previous summary.</param>
/// <param name="UnmatchedDone">
/// Previous action items the user had ticked that no new item matched. summary.json has no field
/// for them (the schema is closed), so they only survive in summary.prev.json.
/// </param>
public sealed record SummaryMergeResult(MeetingSummary Summary, int CarriedDone, IReadOnlyList<ActionItem> UnmatchedDone);

/// <summary>
/// Carries the user's state from a previous final summary to a regenerated one. Today that state is
/// the action-item "done" flag: a new item inherits it from a previous done item whose normalized
/// text is similar enough (Sørensen–Dice over character bigrams, ignoring case, accents and
/// punctuation) and whose owner is the same when both have one. Pairs are assigned one-to-one,
/// best score first. Everything else comes from the new summary.
/// </summary>
public static class SummaryUserStateMerger
{
    /// <summary>Minimum similarity (0..1) for two action items to count as the same task.</summary>
    public const double DefaultThreshold = 0.6;

    public static SummaryMergeResult Merge(MeetingSummary? previous, MeetingSummary next, double threshold = DefaultThreshold)
    {
        ArgumentNullException.ThrowIfNull(next);
        var done = previous?.ActionItems.Where(item => item.Done).ToList() ?? [];
        if (done.Count == 0)
        {
            return new SummaryMergeResult(next, 0, []);
        }

        var candidates = new List<(int Old, int New, double Score)>();
        for (var o = 0; o < done.Count; o++)
        {
            for (var n = 0; n < next.ActionItems.Count; n++)
            {
                if (!SameOwner(done[o].Owner, next.ActionItems[n].Owner))
                {
                    continue;
                }

                var score = Similarity(done[o].Text, next.ActionItems[n].Text);
                if (score >= threshold)
                {
                    candidates.Add((o, n, score));
                }
            }
        }

        var oldTaken = new bool[done.Count];
        var newTaken = new bool[next.ActionItems.Count];
        foreach (var (o, n, _) in candidates.OrderByDescending(pair => pair.Score).ThenBy(pair => pair.Old).ThenBy(pair => pair.New))
        {
            if (!oldTaken[o] && !newTaken[n])
            {
                oldTaken[o] = true;
                newTaken[n] = true;
            }
        }

        var carried = 0;
        var items = next.ActionItems
            .Select((item, index) =>
            {
                if (!newTaken[index] || item.Done)
                {
                    return item;
                }

                carried++;
                return item with { Done = true };
            })
            .ToList();
        var unmatched = done.Where((_, index) => !oldTaken[index]).ToList();
        return new SummaryMergeResult(next with { ActionItems = items }, carried, unmatched);
    }

    /// <summary>Sørensen–Dice coefficient over character bigrams of the normalized texts, 0..1.</summary>
    internal static double Similarity(string? a, string? b)
    {
        var left = Normalize(a);
        var right = Normalize(b);
        if (left.Length == 0 || right.Length == 0)
        {
            return 0;
        }

        if (string.Equals(left, right, StringComparison.Ordinal))
        {
            return 1;
        }

        if (left.Length < 2 || right.Length < 2)
        {
            return 0;
        }

        var bigrams = new Dictionary<(char, char), int>();
        for (var i = 0; i < left.Length - 1; i++)
        {
            var key = (left[i], left[i + 1]);
            bigrams[key] = bigrams.GetValueOrDefault(key) + 1;
        }

        var shared = 0;
        for (var i = 0; i < right.Length - 1; i++)
        {
            var key = (right[i], right[i + 1]);
            if (bigrams.TryGetValue(key, out var count) && count > 0)
            {
                bigrams[key] = count - 1;
                shared++;
            }
        }

        return 2.0 * shared / (left.Length - 1 + right.Length - 1);
    }

    /// <summary>Lower case, no accents, letters and digits separated by single spaces.</summary>
    internal static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(c))
            {
                if (pendingSpace && builder.Length > 0)
                {
                    builder.Append(' ');
                }

                pendingSpace = false;
                builder.Append(char.ToLowerInvariant(c));
            }
            else
            {
                pendingSpace = true;
            }
        }

        return builder.ToString();
    }

    private static bool SameOwner(string? a, string? b) =>
        string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b) ||
        string.Equals(Normalize(a), Normalize(b), StringComparison.Ordinal);
}
