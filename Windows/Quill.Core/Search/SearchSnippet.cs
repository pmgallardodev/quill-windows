namespace Quill.Core.Search;

/// <summary>Short, timestamp-free excerpt of a passage around the first query match, for result lists.</summary>
public static class SearchSnippet
{
    public static string Make(string text, string query, int maxLength = 200)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, 20);
        var plain = string.Join(' ', TextFolding.StripTimestamps(text)
            .Split(['\r', '\n', '\t', ' '], StringSplitOptions.RemoveEmptyEntries));
        if (plain.Length <= maxLength)
        {
            return plain;
        }

        // Fold keeps the length of Spanish text (one mark per letter), so folded offsets map back.
        var folded = TextFolding.Fold(plain);
        var match = folded.Length == plain.Length
            ? TextFolding.Tokens(query)
                .Select(token => folded.IndexOf(token, StringComparison.Ordinal))
                .Where(index => index >= 0)
                .DefaultIfEmpty(0)
                .Min()
            : 0;

        var start = Math.Clamp(match - (maxLength / 3), 0, plain.Length - maxLength);
        var snippet = plain.Substring(start, maxLength).Trim();
        return (start > 0 ? "…" : string.Empty) + snippet + (start + maxLength < plain.Length ? "…" : string.Empty);
    }
}
