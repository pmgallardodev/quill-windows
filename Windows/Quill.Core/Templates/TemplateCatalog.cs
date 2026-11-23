using Quill.Core.Domain;

namespace Quill.Core.Templates;

public enum TemplateSource
{
    /// <summary>Shipped with Quill (<see cref="DefaultTemplates"/>).</summary>
    BuiltIn,

    /// <summary>A .md file in the user templates folder.</summary>
    User,
}

/// <summary>Why a template file was rejected. The UI maps each kind to a localized message.</summary>
public enum TemplateIssueKind
{
    /// <summary>The file could not be read. Detail: the system error.</summary>
    Unreadable,

    /// <summary>The file starts with "---" but the frontmatter is never closed.</summary>
    UnclosedFrontmatter,

    /// <summary>No <c>name</c> in the frontmatter and no usable file name.</summary>
    MissingName,

    /// <summary>A <c>sections</c> entry is not a summary section key. Detail: the entry.</summary>
    UnknownSection,

    /// <summary>The body after the frontmatter is empty, so the model gets no guidance.</summary>
    EmptyBody,

    /// <summary>Another user file already defines this name. Detail: the name.</summary>
    DuplicateName,
}

public sealed record TemplateIssue(TemplateIssueKind Kind, string? Detail = null);

public sealed record TemplateValidation(Template? Template, IReadOnlyList<TemplateIssue> Issues)
{
    public bool IsValid => Template is not null && Issues.Count == 0;
}

/// <param name="OverridesBuiltIn">A user template replacing a built-in of the same name.</param>
public sealed record TemplateEntry(Template Template, TemplateSource Source, string? FilePath, bool OverridesBuiltIn);

public sealed record TemplateFileProblem(string FilePath, IReadOnlyList<TemplateIssue> Issues);

/// <summary>Templates available at one moment: built-ins in their order (overridden in place), then user templates.</summary>
public sealed record TemplateCatalogSnapshot(IReadOnlyList<TemplateEntry> Entries, IReadOnlyList<TemplateFileProblem> Problems)
{
    /// <summary>Case-insensitive lookup; null when no template has that name.</summary>
    public Template? Find(string? name) =>
        Entries.FirstOrDefault(entry => string.Equals(entry.Template.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase))?.Template;

    /// <summary>The named template, else the default one (a deleted user template must not break summaries).</summary>
    public Template Resolve(string? name) =>
        Find(name) ?? Find(DefaultTemplates.Default.Name) ?? DefaultTemplates.Default;
}

/// <summary>
/// Built-in templates merged with the user's templates folder (default
/// "%APPDATA%/Quill/templates", see <see cref="Settings.TemplateSettings"/>). Every *.md file
/// at the top of the folder is one template; a user template whose name matches a built-in
/// overrides it. Invalid files are skipped and reported, never fatal. Reads the disk on every
/// <see cref="Load"/>, so edits made in an external editor show up on reload.
/// </summary>
public sealed class TemplateCatalog
{
    public TemplateCatalog(string userFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userFolder);
        UserFolder = userFolder;
    }

    public string UserFolder { get; }

    public TemplateCatalogSnapshot Load()
    {
        var entries = DefaultTemplates.All
            .Select(template => new TemplateEntry(template, TemplateSource.BuiltIn, null, false))
            .ToList();
        var problems = new List<TemplateFileProblem>();
        var userNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in UserFiles())
        {
            TemplateValidation validation;
            try
            {
                validation = Validate(File.ReadAllText(file), Path.GetFileNameWithoutExtension(file));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                problems.Add(new TemplateFileProblem(file, [new TemplateIssue(TemplateIssueKind.Unreadable, exception.Message)]));
                continue;
            }

            if (!validation.IsValid)
            {
                problems.Add(new TemplateFileProblem(file, validation.Issues));
                continue;
            }

            var template = validation.Template!;
            if (!userNames.Add(template.Name))
            {
                problems.Add(new TemplateFileProblem(file, [new TemplateIssue(TemplateIssueKind.DuplicateName, template.Name)]));
                continue;
            }

            var builtIn = entries.FindIndex(entry =>
                entry.Source == TemplateSource.BuiltIn &&
                string.Equals(entry.Template.Name, template.Name, StringComparison.OrdinalIgnoreCase));
            if (builtIn >= 0)
            {
                entries[builtIn] = new TemplateEntry(template, TemplateSource.User, file, true);
            }
            else
            {
                entries.Add(new TemplateEntry(template, TemplateSource.User, file, false));
            }
        }

        return new TemplateCatalogSnapshot(entries, problems);
    }

    /// <summary>Parses <paramref name="text"/> and checks it is usable as a template.</summary>
    /// <param name="fallbackName">Name used when the frontmatter has none (the file name).</param>
    public static TemplateValidation Validate(string text, string? fallbackName)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines[0].Trim() == "---" && !lines.Skip(1).Any(line => line.Trim() == "---"))
        {
            return new TemplateValidation(null, [new TemplateIssue(TemplateIssueKind.UnclosedFrontmatter)]);
        }

        Template template;
        try
        {
            template = TemplateParser.Parse(text, string.IsNullOrWhiteSpace(fallbackName) ? null : fallbackName.Trim());
        }
        catch (FormatException)
        {
            return new TemplateValidation(null, [new TemplateIssue(TemplateIssueKind.MissingName)]);
        }

        var issues = template.Sections
            .Where(section => !SummarySections.All.Contains(section, StringComparer.Ordinal))
            .Select(section => new TemplateIssue(TemplateIssueKind.UnknownSection, section))
            .ToList();
        if (string.IsNullOrWhiteSpace(template.Body))
        {
            issues.Add(new TemplateIssue(TemplateIssueKind.EmptyBody));
        }

        return new TemplateValidation(issues.Count == 0 ? template : null, issues);
    }

    /// <summary>
    /// Copies a built-in template into the user folder so the user can customize it (the copy
    /// keeps the name, so it overrides the built-in). An existing copy is never overwritten.
    /// </summary>
    /// <exception cref="ArgumentException">No built-in template has that name.</exception>
    public (string Path, bool Created) CreateFromBuiltIn(string builtInName)
    {
        var index = DefaultTemplates.All
            .Select((template, position) => (template, position))
            .FirstOrDefault(pair => string.Equals(pair.template.Name, builtInName?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (index.template is null)
        {
            throw new ArgumentException($"no built-in template named '{builtInName}'", nameof(builtInName));
        }

        var (fileName, text) = DefaultTemplates.Files[index.position];
        var path = Path.Combine(UserFolder, fileName);
        if (File.Exists(path))
        {
            return (path, false);
        }

        Directory.CreateDirectory(UserFolder);
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
        using (var writer = new StreamWriter(stream))
        {
            writer.Write(text);
        }

        return (path, true);
    }

    private IEnumerable<string> UserFiles()
    {
        try
        {
            return Directory.Exists(UserFolder)
                ? Directory.GetFiles(UserFolder, "*.md").OrderBy(file => file, StringComparer.OrdinalIgnoreCase).ToList()
                : [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
