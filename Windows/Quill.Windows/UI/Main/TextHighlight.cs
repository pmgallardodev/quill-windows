using System.Globalization;

namespace Quill.Windows.UI;

/// <summary>A run of text, flagged when it matches the search query.</summary>
internal readonly record struct HighlightRun(string Text, bool IsMatch);

/// <summary>
/// Splits text around the occurrences of a query, ignoring case and accents so "sesion"
/// highlights "Sesión". The runs always concatenate back to the original text.
/// </summary>
internal static class TextHighlight
{
    private const CompareOptions Options = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;

    private static readonly CompareInfo Compare = CultureInfo.InvariantCulture.CompareInfo;

    public static bool Contains(string? text, string? query) =>
        !string.IsNullOrEmpty(text) &&
        !string.IsNullOrWhiteSpace(query) &&
        Compare.IndexOf(text, query.Trim(), Options) >= 0;

    public static IReadOnlyList<HighlightRun> Split(string? text, string? query)
    {
        if (string.IsNullOrEmpty(text))
        {
            return Array.Empty<HighlightRun>();
        }

        var needle = query?.Trim() ?? string.Empty;
        if (needle.Length == 0)
        {
            return new[] { new HighlightRun(text, false) };
        }

        var runs = new List<HighlightRun>();
        var position = 0;
        while (position < text.Length)
        {
            var index = Compare.IndexOf(text.AsSpan(position), needle.AsSpan(), Options, out var length);
            if (index < 0 || length == 0)
            {
                break;
            }

            if (index > 0)
            {
                runs.Add(new HighlightRun(text.Substring(position, index), false));
            }

            runs.Add(new HighlightRun(text.Substring(position + index, length), true));
            position += index + length;
        }

        if (position < text.Length)
        {
            runs.Add(new HighlightRun(text[position..], false));
        }

        return runs;
    }
}
