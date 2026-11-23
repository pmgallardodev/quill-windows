using System.Globalization;
using System.Text;
using Quill.Core.Domain;

namespace Quill.Core.Export;

/// <summary>Everything one exported meeting contains. <see cref="Summary"/> is null when none was generated.</summary>
public sealed record MeetingExport(
    Session Session,
    MeetingSummary? Summary,
    IReadOnlyList<Speaker> Speakers,
    IReadOnlyList<Note> Notes,
    IReadOnlyList<TranscriptSegment> Transcript);

/// <summary>
/// Document text of the export. The file is content, not UI, and Core has no resources, so the
/// labels live here with a Spanish default; a future language setting can pass another set.
/// </summary>
public sealed record MarkdownExportLabels
{
    public static MarkdownExportLabels Spanish { get; } = new();

    public string ExecutiveSummary { get; init; } = "Resumen ejecutivo";
    public string Decisions { get; init; } = "Decisiones";
    public string ActionItems { get; init; } = "Tareas";
    public string OpenQuestions { get; init; } = "Preguntas abiertas";
    public string Topics { get; init; } = "Temas";
    public string Risks { get; init; } = "Riesgos y bloqueos";
    public string KeyFacts { get; init; } = "Datos clave";
    public string NextSteps { get; init; } = "Próximos pasos";
    public string Notes { get; init; } = "Notas";
    public string Transcript { get; init; } = "Transcripción";
    public string None { get; init; } = "Ninguno";
    public string NoSummary { get; init; } = "_Sin resumen todavía._";

    /// <summary>{0} = participant.</summary>
    public string DecidedBy { get; init; } = "decidido por {0}";

    /// <summary>{0} = participant.</summary>
    public string Owner { get; init; } = "responsable: {0}";

    /// <summary>{0} = due date as said in the meeting.</summary>
    public string Due { get; init; } = "fecha: {0}";

    public string Me { get; init; } = "Yo";

    /// <summary>v1 sessions: everyone on the system track.</summary>
    public string Them { get; init; } = "Ellos";

    /// <summary>{0} = speaker number.</summary>
    public string SpeakerNumbered { get; init; } = "Interlocutor {0}";

    /// <summary>File name part for meetings without a usable title (ASCII, slug-safe).</summary>
    public string UntitledSlug { get; init; } = "reunion";
}

/// <summary>
/// Renders one meeting as an Obsidian-friendly Markdown note: YAML frontmatter (title, date,
/// time, duration, participants, tags, template, session id), the summary sections in the
/// §7.1 contract order ("Ninguno" when empty), notes, and the transcript with speaker names.
/// Timestamps are "[mm:ss]" ("[h:mm:ss]" past the hour). Pure and culture-independent; line
/// endings are always "\n". Writing (and edit protection) is <see cref="MarkdownExportWriter"/>.
/// </summary>
public static class MarkdownMeetingExporter
{
    public const int MaxSlugLength = 60;

    public static string Render(MeetingExport export, MarkdownExportLabels? labels = null)
    {
        ArgumentNullException.ThrowIfNull(export);
        labels ??= MarkdownExportLabels.Spanish;
        var names = new SpeakerNames(export.Speakers, labels);
        var output = new StringBuilder();
        var session = export.Session;

        output.Append("---\n");
        output.Append("title: ").Append(Yaml(session.DisplayTitle)).Append('\n');
        output.Append("date: ").Append(session.Started.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append('\n');
        output.Append("time: ").Append(Yaml(session.Started.ToString("HH:mm", CultureInfo.InvariantCulture))).Append('\n');
        output.Append("duration: ").Append(Yaml(Duration(export))).Append('\n');
        var participants = Participants(export).Select(names.Label).Distinct(StringComparer.Ordinal).ToList();
        if (participants.Count == 0)
        {
            output.Append("participants: []\n");
        }
        else
        {
            output.Append("participants:\n");
            foreach (var participant in participants)
            {
                output.Append("  - ").Append(Yaml(participant)).Append('\n');
            }
        }

        output.Append("tags: [quill, meeting]\n");
        if (export.Summary is { Meta.Template: { Length: > 0 } template })
        {
            output.Append("template: ").Append(Yaml(template)).Append('\n');
        }

        output.Append("quill_session: ").Append(Yaml(session.Id)).Append('\n');
        output.Append("---\n\n");
        output.Append("# ").Append(Flatten(session.DisplayTitle)).Append('\n');

        if (export.Summary is { } summary)
        {
            AppendSummary(output, summary, names, labels);
        }
        else
        {
            output.Append('\n').Append(labels.NoSummary).Append('\n');
        }

        Section(output, labels.Notes, export.Notes
            .Where(note => !string.IsNullOrWhiteSpace(note.Text))
            .Select(note => "- " + (note.At is { } at ? Stamp(at) + " " : string.Empty) + Flatten(note.Text))
            .ToList(), labels);

        output.Append("\n## ").Append(labels.Transcript).Append("\n\n");
        var lines = export.Transcript
            .Where(segment => !string.IsNullOrWhiteSpace(segment.Text))
            .Select(segment => $"**{Stamp(segment.Start)} {names.Label(segment.SpeakerId)}:** {Flatten(segment.Text)}")
            .ToList();
        output.Append(lines.Count == 0 ? labels.None : string.Join("\n\n", lines)).Append('\n');
        return output.ToString();
    }

    /// <summary>"yyyy-MM-dd-title-slug.md"; untitled meetings get "yyyy-MM-dd-HHmm-reunion.md".</summary>
    public static string FileName(Session session, MarkdownExportLabels? labels = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        labels ??= MarkdownExportLabels.Spanish;
        var date = session.Started.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var slug = Slug(session.Title);
        if (slug.Length == 0)
        {
            slug = session.Started.ToString("HHmm", CultureInfo.InvariantCulture) + "-" + labels.UntitledSlug;
        }

        return $"{date}-{slug}.md";
    }

    /// <summary>Lowercase ASCII letters and digits separated by single dashes, at most <see cref="MaxSlugLength"/> characters.</summary>
    public static string Slug(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var output = new StringBuilder();
        foreach (var character in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            var lower = char.ToLowerInvariant(character);
            if (lower is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                output.Append(lower);
            }
            else if (output.Length > 0 && output[^1] != '-')
            {
                output.Append('-');
            }
        }

        var slug = output.ToString().Trim('-');
        if (slug.Length > MaxSlugLength)
        {
            var cut = slug.LastIndexOf('-', MaxSlugLength);
            slug = (cut > 0 ? slug[..cut] : slug[..MaxSlugLength]).Trim('-');
        }

        return slug;
    }

    /// <summary>"[mm:ss]", or "[h:mm:ss]" from one hour on.</summary>
    public static string Stamp(TimeSpan at)
    {
        if (at < TimeSpan.Zero)
        {
            at = TimeSpan.Zero;
        }

        return at.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"[{(int)at.TotalHours}:{at.Minutes:D2}:{at.Seconds:D2}]")
            : string.Create(CultureInfo.InvariantCulture, $"[{at.Minutes:D2}:{at.Seconds:D2}]");
    }

    private static void AppendSummary(StringBuilder output, MeetingSummary summary, SpeakerNames names, MarkdownExportLabels labels)
    {
        output.Append("\n## ").Append(labels.ExecutiveSummary).Append("\n\n");
        var executive = summary.ExecutiveSummary.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        output.Append(executive.Length == 0 ? labels.None : executive).Append('\n');

        Section(output, labels.Decisions, summary.Decisions
            .Select(item => $"- {Stamp(item.At)} {Flatten(item.Text)}" + Details(Format(labels.DecidedBy, names.LabelOrNull(item.DecidedBy))))
            .ToList(), labels);
        Section(output, labels.ActionItems, summary.ActionItems
            .Select(item => $"- [{(item.Done ? 'x' : ' ')}] {Stamp(item.At)} {Flatten(item.Text)}" + Details(
                Format(labels.Owner, names.LabelOrNull(item.Owner)),
                Format(labels.Due, string.IsNullOrWhiteSpace(item.Due) ? null : Flatten(item.Due))))
            .ToList(), labels);
        Section(output, labels.OpenQuestions, summary.OpenQuestions.Select(item => $"- {Stamp(item.At)} {Flatten(item.Text)}").ToList(), labels);
        Section(output, labels.Topics, summary.Topics.Select(item => $"- {Stamp(item.Start)} {Flatten(item.Title)}").ToList(), labels);
        Section(output, labels.Risks, summary.Risks.Select(item => $"- {Stamp(item.At)} {Flatten(item.Text)}").ToList(), labels);
        Section(output, labels.KeyFacts, summary.KeyFacts.Select(item => $"- {Stamp(item.At)} {Flatten(item.Text)}").ToList(), labels);
        Section(output, labels.NextSteps, summary.NextSteps
            .Where(step => !string.IsNullOrWhiteSpace(step))
            .Select(step => "- " + Flatten(step))
            .ToList(), labels);
    }

    private static void Section(StringBuilder output, string heading, IReadOnlyList<string> lines, MarkdownExportLabels labels)
    {
        output.Append("\n## ").Append(heading).Append("\n\n");
        if (lines.Count == 0)
        {
            output.Append(labels.None).Append('\n');
            return;
        }

        foreach (var line in lines)
        {
            output.Append(line).Append('\n');
        }
    }

    private static string? Format(string pattern, string? value) =>
        value is null ? null : string.Format(CultureInfo.InvariantCulture, pattern, value);

    private static string Details(params string?[] parts)
    {
        var present = parts.Where(part => part is not null).ToList();
        return present.Count == 0 ? string.Empty : " (" + string.Join("; ", present) + ")";
    }

    /// <summary>Speaker ids in order of first appearance, then the remaining known speakers.</summary>
    private static IEnumerable<string> Participants(MeetingExport export) =>
        export.Transcript.Select(segment => segment.SpeakerId)
            .Concat(export.Speakers.Select(speaker => speaker.Id))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal);

    private static string Duration(MeetingExport export)
    {
        var duration = export.Session.Duration;
        if (duration <= TimeSpan.Zero && export.Transcript.Count > 0)
        {
            duration = export.Transcript.Max(segment => segment.End);
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{(int)duration.TotalHours:D2}:{duration.Minutes:D2}:{duration.Seconds:D2}");
    }

    private static string Flatten(string? text) =>
        string.Join(' ', (text ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static string Yaml(string value) =>
        "\"" + Flatten(value).Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private sealed class SpeakerNames(IReadOnlyList<Speaker> speakers, MarkdownExportLabels labels)
    {
        public string? LabelOrNull(string? id) => string.IsNullOrWhiteSpace(id) ? null : Label(id);

        public string Label(string id)
        {
            var known = speakers.FirstOrDefault(speaker => speaker.Id == id);
            if (!string.IsNullOrWhiteSpace(known?.DisplayName))
            {
                return Flatten(known.DisplayName);
            }

            if (id == SpeakerIds.Me)
            {
                return labels.Me;
            }

            if (id == SpeakerIds.LegacyThem)
            {
                return labels.Them;
            }

            return Speakers.SpeakerProfile.RemoteNumber(id) is { } number
                ? string.Format(CultureInfo.InvariantCulture, labels.SpeakerNumbered, number)
                : id;
        }
    }
}
