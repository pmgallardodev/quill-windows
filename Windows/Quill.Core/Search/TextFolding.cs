using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Quill.Core.Search;

/// <summary>
/// Normalization for lexical search: lower case, accents removed ("Reunión" = "reunion",
/// "camión" = "CAMION"), Spanish and English stopwords dropped. Bracketed timestamps
/// ("[00:01:05]") are never tokens.
/// </summary>
public static partial class TextFolding
{
    private static readonly HashSet<string> Stopwords = new(StringComparer.Ordinal)
    {
        // Spanish (already folded).
        "el", "la", "los", "las", "un", "una", "unos", "unas", "de", "del", "al", "a", "en", "y", "o", "u", "e",
        "que", "se", "lo", "le", "les", "me", "te", "nos", "su", "sus", "mi", "mis", "tu", "tus", "es", "son",
        "por", "para", "con", "sin", "sobre", "como", "pero", "mas", "ya", "muy", "si", "no", "fue", "ha", "han",
        "hay", "este", "esta", "esto", "ese", "esa", "eso", "estos", "estas", "aqui", "alli", "cuando", "donde",
        "quien", "cual", "cuales", "porque", "pues", "entonces", "tambien", "solo", "todo", "todos",
        "toda", "todas", "algo", "nada", "ser", "estar", "era", "eran", "habia", "dijo", "dice",
        // English.
        "the", "an", "and", "or", "of", "to", "in", "on", "at", "for", "with", "is", "are", "was", "were", "be",
        "it", "this", "that", "these", "those", "we", "you", "they", "he", "she", "i", "my", "our", "your",
        "what", "who", "when", "where", "how", "why", "did", "do", "does", "about",
    };

    /// <summary>Lower case without diacritics; "ñ" becomes "n".</summary>
    public static string Fold(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var decomposed = text.Normalize(NormalizationForm.FormD);
        var output = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                output.Append(char.ToLowerInvariant(character));
            }
        }

        return output.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>Folded words of two or more characters, timestamps and stopwords removed, in order.</summary>
    public static IReadOnlyList<string> Tokens(string? text)
    {
        var folded = Fold(StripTimestamps(text));
        var tokens = new List<string>();
        var start = -1;
        for (var index = 0; index <= folded.Length; index++)
        {
            var isWord = index < folded.Length && char.IsLetterOrDigit(folded[index]);
            if (isWord && start < 0)
            {
                start = index;
            }
            else if (!isWord && start >= 0)
            {
                var token = folded[start..index];
                if (token.Length >= 2 && !Stopwords.Contains(token))
                {
                    tokens.Add(token);
                }

                start = -1;
            }
        }

        return tokens;
    }

    /// <summary>Removes "[hh:mm:ss]" (and "[session@hh:mm:ss]") markers.</summary>
    public static string StripTimestamps(string? text) =>
        string.IsNullOrEmpty(text) ? string.Empty : TimestampPattern().Replace(text, string.Empty);

    [GeneratedRegex(@"\[(?:[^\[\]\s@]+@)?\d{1,3}:\d{2}:\d{2}\]\s?")]
    private static partial Regex TimestampPattern();
}
