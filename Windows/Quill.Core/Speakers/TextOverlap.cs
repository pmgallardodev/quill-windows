using System.Globalization;
using System.Text;

namespace Quill.Core.Speakers;

/// <summary>
/// Normalized token overlap between two utterances, used to spot the same phrase
/// transcribed twice (once on the system track, once leaking into the microphone).
/// </summary>
public static class TextOverlap
{
    /// <summary>
    /// Overlap coefficient over token multisets: |A ∩ B| / min(|A|, |B|), in [0, 1].
    /// Case, punctuation and diacritics are ignored. Empty input scores 0.
    /// The minimum in the denominator makes a partial echo (the mic caught only part
    /// of a longer remote sentence) still score high.
    /// </summary>
    public static double Score(string? a, string? b)
    {
        var left = Tokens(a);
        var right = Tokens(b);
        if (left.Count == 0 || right.Count == 0)
        {
            return 0;
        }

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var token in right)
        {
            counts[token] = counts.GetValueOrDefault(token) + 1;
        }

        var common = 0;
        foreach (var token in left)
        {
            if (counts.TryGetValue(token, out var count) && count > 0)
            {
                counts[token] = count - 1;
                common++;
            }
        }

        return (double)common / Math.Min(left.Count, right.Count);
    }

    /// <summary>Lower-cased, accent-free words; everything that is not a letter or digit separates tokens.</summary>
    public static IReadOnlyList<string> Tokens(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var decomposed = text.Normalize(NormalizationForm.FormD);
        var tokens = new List<string>();
        var current = new StringBuilder();
        foreach (var character in decomposed)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(character);
            if (category == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(character))
            {
                current.Append(char.ToLowerInvariant(character));
            }
            else if (current.Length > 0)
            {
                tokens.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }
}
