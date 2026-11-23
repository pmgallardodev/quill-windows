using System.Text;
using Quill.Core.Domain;
using Quill.Core.Summaries;

namespace Quill.Core.Storage;

/// <summary>
/// notes.md convention: one note per bullet, "- [hh:mm:ss] text" for notes taken
/// during recording and "- text" for free notes. The file stays plain markdown the
/// user may edit by hand, so parsing is lenient: any non-empty line is a note.
/// </summary>
public static class NotesDocument
{
    public static string Render(IEnumerable<Note> notes)
    {
        ArgumentNullException.ThrowIfNull(notes);
        var output = new StringBuilder();
        foreach (var note in notes)
        {
            var text = Flatten(note.Text);
            if (text.Length == 0)
            {
                continue;
            }

            output.Append("- ");
            if (note.At is { } at)
            {
                output.Append('[').Append(TimestampJsonConverter.Format(at)).Append("] ");
            }

            output.Append(text).Append('\n');
        }

        return output.ToString();
    }

    public static IReadOnlyList<Note> Parse(string? markdown)
    {
        var notes = new List<Note>();
        foreach (var rawLine in (markdown ?? string.Empty).Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal) ||
                line is "-" or "*")
            {
                line = line[1..].Trim();
            }

            if (line.Length == 0)
            {
                continue;
            }

            TimeSpan? at = null;
            var close = line.IndexOf(']');
            if (line.StartsWith('[') && close > 0 &&
                TimestampJsonConverter.TryParse(line[1..close], out var parsed))
            {
                at = parsed;
                line = line[(close + 1)..].Trim();
            }

            if (line.Length > 0)
            {
                notes.Add(new Note(line, at));
            }
        }

        return notes;
    }

    private static string Flatten(string? text) =>
        string.Join(' ', (text ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Trim())
            .Where(part => part.Length > 0));
}
