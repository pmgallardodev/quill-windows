using System.Reflection;
using Quill.Core.Domain;
using Quill.Core.Summaries;

namespace Quill.Core.Templates;

/// <summary>
/// Built-in templates shipped inside Quill.Core (Templates/Defaults/*.md, embedded resources).
/// The template editor (F9) copies <see cref="Files"/> to the user's templates folder; until a
/// user template overrides one by name, these are used as-is.
/// </summary>
public static class DefaultTemplates
{
    private const string ResourcePrefix = "Quill.Core.Templates.Defaults.";

    private static readonly Lazy<IReadOnlyList<(string FileName, string Text)>> LazyFiles = new(Load);

    private static readonly Lazy<IReadOnlyList<Template>> LazyAll = new(() =>
        LazyFiles.Value.Select(file => TemplateParser.Parse(file.Text, Path.GetFileNameWithoutExtension(file.FileName))).ToList());

    /// <summary>Raw template files ("general.md", text) in display order, general first.</summary>
    public static IReadOnlyList<(string FileName, string Text)> Files => LazyFiles.Value;

    public static IReadOnlyList<Template> All => LazyAll.Value;

    /// <summary>The template used when none is chosen (<see cref="SummaryOptions.DefaultTemplateName"/>).</summary>
    public static Template Default => Find(SummaryOptions.DefaultTemplateName)
        ?? throw new InvalidOperationException("the default template is not embedded");

    /// <summary>Case-insensitive lookup by name; null when there is no such built-in template.</summary>
    public static Template? Find(string name) =>
        All.FirstOrDefault(template => string.Equals(template.Name, name, StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<(string, string)> Load()
    {
        var assembly = typeof(DefaultTemplates).Assembly;
        return assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            .Select(name => (FileName: name[ResourcePrefix.Length..], Text: Read(assembly, name)))
            .OrderBy(file => file.FileName == SummaryOptions.DefaultTemplateName + ".md" ? 0 : 1)
            .ThenBy(file => file.FileName, StringComparer.Ordinal)
            .Select(file => (file.FileName, file.Text))
            .ToList();
    }

    private static string Read(Assembly assembly, string resource)
    {
        using var stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"missing embedded template {resource}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
