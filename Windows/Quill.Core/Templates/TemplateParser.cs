using System.Text;
using Quill.Core.Domain;

namespace Quill.Core.Templates;

/// <summary>
/// Reads and writes template files: an optional frontmatter block delimited by
/// "---" lines, then a markdown body. The frontmatter is a small YAML subset:
/// "key: value" scalars (optionally quoted) and, for <c>sections</c>, either an
/// inline list "[a, b]" or a block list of "- item" lines. Unknown keys are ignored.
/// </summary>
public static class TemplateParser
{
    private const string Fence = "---";

    /// <exception cref="FormatException">No name in the frontmatter and no <paramref name="fallbackName"/>.</exception>
    public static Template Parse(string text, string? fallbackName = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var sections = new List<string>();
        var bodyStart = 0;

        if (lines.Length > 0 && lines[0].Trim() == Fence)
        {
            var end = Array.FindIndex(lines, 1, line => line.Trim() == Fence);
            if (end < 0)
            {
                throw new FormatException("template frontmatter is not closed with ---");
            }

            string? currentList = null;
            for (var index = 1; index < end; index++)
            {
                var line = lines[index];
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                {
                    continue;
                }

                if (trimmed.StartsWith("- ", StringComparison.Ordinal) && currentList is not null)
                {
                    if (currentList.Equals("sections", StringComparison.OrdinalIgnoreCase))
                    {
                        sections.Add(Unquote(trimmed[2..]));
                    }

                    continue;
                }

                var colon = trimmed.IndexOf(':');
                if (colon <= 0)
                {
                    continue;
                }

                var key = trimmed[..colon].Trim();
                var value = trimmed[(colon + 1)..].Trim();
                currentList = value.Length == 0 ? key : null;
                if (key.Equals("sections", StringComparison.OrdinalIgnoreCase))
                {
                    if (value.StartsWith('[') && value.EndsWith(']'))
                    {
                        sections.AddRange(value[1..^1]
                            .Split(',')
                            .Select(Unquote)
                            .Where(item => item.Length > 0));
                    }

                    continue;
                }

                values[key] = Unquote(value);
            }

            bodyStart = end + 1;
        }

        var name = values.GetValueOrDefault("name");
        if (string.IsNullOrWhiteSpace(name))
        {
            name = fallbackName;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new FormatException("template has no name");
        }

        var level = values.GetValueOrDefault("level")?.ToLowerInvariant() switch
        {
            "detailed" => SummaryLevel.Detailed,
            _ => SummaryLevel.Brief,
        };
        var language = values.GetValueOrDefault("language");

        return new Template(
            name.Trim(),
            values.GetValueOrDefault("description") ?? string.Empty,
            string.IsNullOrWhiteSpace(language) ? null : language,
            level,
            sections,
            string.Join('\n', lines.Skip(bodyStart)).Trim());
    }

    public static string Render(Template template)
    {
        ArgumentNullException.ThrowIfNull(template);
        var output = new StringBuilder();
        output.Append(Fence).Append('\n');
        output.Append("name: ").Append(Quote(template.Name)).Append('\n');
        output.Append("description: ").Append(Quote(template.Description)).Append('\n');
        if (!string.IsNullOrWhiteSpace(template.Language))
        {
            output.Append("language: ").Append(template.Language).Append('\n');
        }

        output.Append("level: ").Append(template.Level == SummaryLevel.Detailed ? "detailed" : "brief").Append('\n');
        if (template.Sections.Count > 0)
        {
            output.Append("sections: [").Append(string.Join(", ", template.Sections)).Append("]\n");
        }

        output.Append(Fence).Append("\n\n");
        output.Append(template.Body.Trim()).Append('\n');
        return output.ToString();
    }

    private static string Unquote(string value)
    {
        value = value.Trim();
        return value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0]
            ? value[1..^1]
            : value;
    }

    private static string Quote(string value) => $"\"{value.Replace("\"", "'", StringComparison.Ordinal)}\"";
}
