using System.Globalization;
using Quill.Core.Domain;
using Quill.Core.Summaries;
using Quill.Core.Transcripts;

namespace Quill.Core.Search;

/// <summary>Size of transcript passages: grow to <see cref="Target"/>, never past <see cref="Max"/>.</summary>
/// <param name="Overlap">Lines that start within this much of a window's end open the next window too.</param>
public sealed record PassageWindow(TimeSpan Target, TimeSpan Max, TimeSpan Overlap)
{
    public static PassageWindow Default { get; } =
        new(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(15));
}

/// <summary>Splits a meeting into <see cref="Passage"/>s for search and Ask.</summary>
public static class PassageBuilder
{
    /// <summary>Transcript windows, then summary items, then notes.</summary>
    public static IReadOnlyList<Passage> ForSession(
        string sessionId,
        IReadOnlyList<TranscriptSegment> transcript,
        MeetingSummary? summary,
        IReadOnlyList<Note> notes,
        PassageWindow? window = null)
    {
        var passages = new List<Passage>(FromTranscript(sessionId, transcript, window));
        if (summary is not null)
        {
            passages.AddRange(FromSummary(sessionId, summary));
        }

        passages.AddRange(FromNotes(sessionId, notes));
        return passages;
    }

    /// <summary>
    /// Overlapping windows of whole lines, in time order. A window starts at a line and takes
    /// following lines while it is shorter than the target and the next line still ends within
    /// the maximum. The next window starts at the first line inside the overlap, so a sentence
    /// at a boundary is found in both. Blank lines are skipped; a line longer than the maximum
    /// is a window on its own.
    /// </summary>
    public static IReadOnlyList<Passage> FromTranscript(
        string sessionId,
        IReadOnlyList<TranscriptSegment> segments,
        PassageWindow? window = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(segments);
        var size = window ?? PassageWindow.Default;

        var lines = segments
            .Where(segment => segment is not null && !string.IsNullOrWhiteSpace(segment.Text))
            .OrderBy(segment => segment.Start)
            .ToList();
        var passages = new List<Passage>();
        var first = 0;
        while (first < lines.Count)
        {
            var last = WindowEnd(lines, first, size);
            passages.Add(Transcript(sessionId, passages.Count, lines, first, last));
            if (last + 1 >= lines.Count)
            {
                break;
            }

            first = NextStart(lines, first, last, size);
        }

        return passages;
    }

    /// <summary>One passage per cited summary item (with its <c>at</c>) and one for the executive summary.</summary>
    public static IReadOnlyList<Passage> FromSummary(string sessionId, MeetingSummary summary)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(summary);

        var items = new List<(string Label, string Text, TimeSpan? At)>();
        if (!string.IsNullOrWhiteSpace(summary.ExecutiveSummary))
        {
            items.Add(("resumen", summary.ExecutiveSummary, null));
        }

        items.AddRange(summary.Decisions.Select(item => ("decisión", item.Text, (TimeSpan?)item.At)));
        items.AddRange(summary.ActionItems.Select(item => ("tarea", item.Text, (TimeSpan?)item.At)));
        items.AddRange(summary.OpenQuestions.Select(item => ("pregunta abierta", item.Text, (TimeSpan?)item.At)));
        items.AddRange(summary.Topics.Select(item => ("tema", item.Title, (TimeSpan?)item.Start)));
        items.AddRange(summary.Risks.Select(item => ("riesgo", item.Text, (TimeSpan?)item.At)));
        items.AddRange(summary.KeyFacts.Select(item => ("dato clave", item.Text, (TimeSpan?)item.At)));

        return items
            .Where(item => !string.IsNullOrWhiteSpace(item.Text))
            .Select((item, index) => Single(sessionId, PassageKinds.Summary, index, item.Label, item.Text, item.At))
            .ToList();
    }

    public static IReadOnlyList<Passage> FromNotes(string sessionId, IReadOnlyList<Note> notes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(notes);
        return notes
            .Where(note => note is not null && !string.IsNullOrWhiteSpace(note.Text))
            .Select((note, index) => Single(sessionId, PassageKinds.Note, index, "nota", note.Text, note.At))
            .ToList();
    }

    private static int WindowEnd(List<TranscriptSegment> lines, int first, PassageWindow size)
    {
        var start = lines[first].Start;
        var last = first;
        while (last + 1 < lines.Count &&
            lines[last].End - start < size.Target &&
            lines[last + 1].End - start <= size.Max)
        {
            last++;
        }

        return last;
    }

    private static int NextStart(List<TranscriptSegment> lines, int first, int last, PassageWindow size)
    {
        var end = lines.Skip(first).Take(last - first + 1).Max(line => line.End);
        for (var candidate = first + 1; candidate <= last; candidate++)
        {
            // Overlap only when the window starting there reaches new lines; otherwise it
            // would repeat a subset of the window just emitted.
            if (lines[candidate].Start >= end - size.Overlap && WindowEnd(lines, candidate, size) > last)
            {
                return candidate;
            }
        }

        return last + 1;
    }

    private static Passage Transcript(string sessionId, int number, List<TranscriptSegment> lines, int first, int last)
    {
        var window = lines.GetRange(first, last - first + 1);
        return new Passage(
            Id(sessionId, PassageKinds.Transcript, number),
            sessionId,
            PassageKinds.Transcript,
            window[0].Start,
            window.Max(line => line.End),
            window.Select(line => line.SpeakerId).Distinct(StringComparer.Ordinal).ToList(),
            string.Join('\n', window.Select(TranscriptChunker.FormatLine)));
    }

    private static Passage Single(string sessionId, string kind, int number, string label, string text, TimeSpan? at)
    {
        var flat = string.Join(' ', text.Split(['\r', '\n', '\t', ' '], StringSplitOptions.RemoveEmptyEntries));
        var line = at is { } time ? $"[{TimestampJsonConverter.Format(time)}] {label}: {flat}" : $"{label}: {flat}";
        return new Passage(Id(sessionId, kind, number), sessionId, kind, at, at, [], line);
    }

    private static string Id(string sessionId, string kind, int number) =>
        string.Create(CultureInfo.InvariantCulture, $"{sessionId}:{kind}:{number}");
}
