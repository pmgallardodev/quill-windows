using Quill.Core.Domain;

namespace Quill.Windows.UI;

/// <summary>Pure rules behind transcript corrections in the Transcript tab.</summary>
internal static class TranscriptEdits
{
    /// <summary>A segment found by index must still start where the line says (same file, not re-transcribed).</summary>
    private static readonly TimeSpan StartTolerance = TimeSpan.FromMilliseconds(1);

    /// <summary>
    /// What gets saved for an edit: trimmed, line breaks folded into spaces (one segment is
    /// one line in transcript.md). Null when nothing is left.
    /// </summary>
    public static string? Normalize(string? text)
    {
        if (text is null)
        {
            return null;
        }

        var folded = string.Join(' ', text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return folded.Length == 0 ? null : folded;
    }

    /// <summary>
    /// The transcript with segment <paramref name="index"/> replaced by <paramref name="text"/>
    /// and marked edited. The segment must still start at <paramref name="start"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The transcript changed since it was shown.</exception>
    public static IReadOnlyList<TranscriptSegment> Apply(
        IReadOnlyList<TranscriptSegment> segments,
        int index,
        TimeSpan start,
        string text)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (index < 0 || index >= segments.Count || (segments[index].Start - start).Duration() > StartTolerance)
        {
            throw new InvalidOperationException("The transcript changed on disk. Reload the meeting and try again.");
        }

        var updated = segments.ToList();
        updated[index] = updated[index] with { Text = text, Edited = true };
        return updated;
    }

    /// <summary>
    /// Speakers a line can be moved to: "me" first, then every speaker of the meeting in
    /// order of appearance, with the labels the transcript shows.
    /// </summary>
    public static IReadOnlyList<(string Id, string Label)> SpeakerChoices(IEnumerable<TranscriptEntryViewModel> entries)
    {
        var choices = new List<(string Id, string Label)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries.Where(entry => entry.IsSegment && entry.SpeakerId.Length > 0))
        {
            if (seen.Add(entry.SpeakerId))
            {
                choices.Add((entry.SpeakerId, entry.SpeakerLabel));
            }
        }

        var me = choices.FindIndex(choice => string.Equals(choice.Id, SpeakerIds.Me, StringComparison.OrdinalIgnoreCase));
        if (me > 0)
        {
            var choice = choices[me];
            choices.RemoveAt(me);
            choices.Insert(0, choice);
        }
        else if (me < 0)
        {
            choices.Insert(0, (SpeakerIds.Me, SpeakerLabels.DefaultLabel(SpeakerIds.Me)));
        }

        return choices;
    }
}
