namespace Quill.Core.Domain;

/// <summary>
/// A summary template: a markdown body (prompt guidance) with a YAML-ish
/// frontmatter. Parse and render with <see cref="Templates.TemplateParser"/>.
/// </summary>
/// <param name="Name">Unique template name (frontmatter <c>name</c>).</param>
/// <param name="Description">One line shown in pickers.</param>
/// <param name="Language">Output language code (e.g. "es"); null means "same as the meeting".</param>
/// <param name="Level">Brief or detailed.</param>
/// <param name="Sections">
/// Summary section keys to fill, using the JSON names of <see cref="MeetingSummary"/>
/// (see <see cref="SummarySections"/>). Empty means all sections.
/// </param>
/// <param name="Body">Markdown body after the frontmatter, trimmed.</param>
public sealed record Template(
    string Name,
    string Description,
    string? Language,
    SummaryLevel Level,
    IReadOnlyList<string> Sections,
    string Body);
