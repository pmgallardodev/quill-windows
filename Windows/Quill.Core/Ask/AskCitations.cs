using System.Text.RegularExpressions;
using Quill.Core.Search;
using Quill.Core.Summaries;

namespace Quill.Core.Ask;

/// <summary>
/// Turns raw model output into an <see cref="AskAnswer"/>. Same idea as
/// <see cref="CitationValidator"/>: a citation counts only if it points at a line the model
/// was actually given (same meeting, timestamp within one second). Invented markers are
/// removed from the text instead of being shown as links to nowhere.
/// </summary>
public static partial class AskCitations
{
    public static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(1);

    /// <param name="defaultSessionId">Meeting for markers without a session ("[00:01:05]"), when there is one.</param>
    public static AskAnswer Parse(string raw, IReadOnlyList<AskSource> sources, string? defaultSessionId, string language)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var text = (raw ?? string.Empty).Trim();
        if (text.Length == 0 || IsNotFound(text, language))
        {
            return AskAnswer.NothingFound(language, sources);
        }

        var lines = sources
            .SelectMany(source => CitableLines(source.Passage))
            .ToList();
        var single = defaultSessionId ??
            (sources.Select(source => source.Passage.SessionId).Distinct(StringComparer.Ordinal).Count() == 1
                ? sources[0].Passage.SessionId
                : null);

        var citations = new List<AskCitation>();
        var cleaned = Marker().Replace(text, match =>
        {
            var session = match.Groups["session"].Success ? match.Groups["session"].Value : single;
            if (session is null || !TimestampJsonConverter.TryParse(match.Groups["time"].Value, out var at))
            {
                return string.Empty;
            }

            var line = lines
                .Where(candidate => candidate.SessionId == session && (candidate.At - at).Duration() <= Tolerance)
                .OrderBy(candidate => (candidate.At - at).Duration())
                .Cast<AskCitation?>()
                .FirstOrDefault();
            if (line is null)
            {
                return string.Empty;
            }

            if (!citations.Contains(line))
            {
                citations.Add(line);
            }

            return $" [{line.SessionId}@{TimestampJsonConverter.Format(line.At)}]";
        });

        var withMarkers = Tidy(cleaned);
        var plain = Tidy(Marker().Replace(withMarkers, string.Empty));
        return new AskAnswer(withMarkers, plain, citations, NotFound: false, sources);
    }

    private static bool IsNotFound(string text, string language)
    {
        var folded = TextFolding.Fold(text).Trim(' ', '.', '«', '»', '"', '\'');
        return folded.StartsWith(TextFolding.Fold(AskPrompts.NotFoundText(language)).TrimEnd('.'), StringComparison.Ordinal) ||
            folded.StartsWith(TextFolding.Fold(AskPrompts.NotFoundText("es")).TrimEnd('.'), StringComparison.Ordinal);
    }

    private static IEnumerable<AskCitation> CitableLines(Passage passage)
    {
        foreach (var line in passage.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var match = AskPrompts.LineTimestamp().Match(line);
            if (match.Success && TimestampJsonConverter.TryParse(match.Groups[1].Value, out var at))
            {
                yield return new AskCitation(passage.SessionId, at, line[match.Length..].Trim());
            }
        }
    }

    /// <summary>Collapses the spaces left by removed markers and before punctuation.</summary>
    private static string Tidy(string text) =>
        SpaceBeforePunctuation().Replace(Spaces().Replace(text, " "), "$1").Trim();

    [GeneratedRegex(@"\s*\[(?:(?<session>[^\[\]@\s]+)@)?(?<time>\d{1,3}:\d{2}:\d{2})\]")]
    private static partial Regex Marker();

    [GeneratedRegex(@"[ \t]{2,}")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"[ \t]+([.,;:!?)])")]
    private static partial Regex SpaceBeforePunctuation();
}
