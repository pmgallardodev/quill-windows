using Quill.Core.Domain;
using Quill.Core.Templates;
using Xunit;

namespace Quill.Core.Tests.Templates;

public sealed class TemplateCatalogTests
{
    private const string Valid = """
        ---
        name: ventas
        description: Llamada de ventas
        sections: [executiveSummary, actionItems]
        ---
        Destaca el presupuesto y los próximos pasos.
        """;

    [Fact]
    public void WithoutAUserFolderListsTheBuiltInsInOrder()
    {
        using var temp = new TempDirectory();
        var snapshot = new TemplateCatalog(Path.Combine(temp.Path, "missing")).Load();

        Assert.Equal(DefaultTemplates.All.Select(t => t.Name), snapshot.Entries.Select(e => e.Template.Name));
        Assert.All(snapshot.Entries, entry => Assert.Equal(TemplateSource.BuiltIn, entry.Source));
        Assert.Empty(snapshot.Problems);
    }

    [Fact]
    public void UserTemplatesAreAddedAfterTheBuiltIns()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(Path.Combine(temp.Path, "ventas.md"), Valid);

        var snapshot = new TemplateCatalog(temp.Path).Load();
        var last = snapshot.Entries[^1];

        Assert.Equal("ventas", last.Template.Name);
        Assert.Equal(TemplateSource.User, last.Source);
        Assert.Equal(Path.Combine(temp.Path, "ventas.md"), last.FilePath);
        Assert.False(last.OverridesBuiltIn);
        Assert.Same(last.Template, snapshot.Find("VENTAS"));
    }

    [Fact]
    public void AUserTemplateOverridesABuiltInByNameInPlace()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(Path.Combine(temp.Path, "mi-daily.md"), "---\nname: Daily\n---\nVersión propia.");

        var snapshot = new TemplateCatalog(temp.Path).Load();
        var daily = snapshot.Entries.Single(e => e.Template.Name.Equals("daily", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(DefaultTemplates.All.Count, snapshot.Entries.Count);
        Assert.Equal(TemplateSource.User, daily.Source);
        Assert.True(daily.OverridesBuiltIn);
        Assert.Equal("Versión propia.", snapshot.Find("daily")!.Body);
        Assert.Equal(
            DefaultTemplates.All.Select(t => t.Name.ToLowerInvariant()),
            snapshot.Entries.Select(e => e.Template.Name.ToLowerInvariant()));
    }

    [Fact]
    public void NameDefaultsToTheFileName()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(Path.Combine(temp.Path, "comite.md"), "Resume el comité.");

        Assert.NotNull(new TemplateCatalog(temp.Path).Load().Find("comite"));
    }

    [Fact]
    public void InvalidFilesAreSkippedAndReportedWithTheirIssues()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(Path.Combine(temp.Path, "a.md"), "---\nname: roto\nsections: [decisions]\nsin cerrar");
        File.WriteAllText(Path.Combine(temp.Path, "b.md"), "---\nname: b\nsections: [decisions, agenda, bogus]\n---\n");
        File.WriteAllText(Path.Combine(temp.Path, "c.md"), Valid);
        File.WriteAllText(Path.Combine(temp.Path, "d.md"), Valid);
        File.WriteAllText(Path.Combine(temp.Path, "notes.txt"), "ignored");

        var snapshot = new TemplateCatalog(temp.Path).Load();
        var byFile = snapshot.Problems.ToDictionary(p => Path.GetFileName(p.FilePath));

        Assert.Single(snapshot.Entries, e => e.Source == TemplateSource.User);
        Assert.Equal(3, snapshot.Problems.Count);
        Assert.Equal([TemplateIssueKind.UnclosedFrontmatter], byFile["a.md"].Issues.Select(i => i.Kind));
        Assert.Equal(
            [new TemplateIssue(TemplateIssueKind.UnknownSection, "agenda"),
             new TemplateIssue(TemplateIssueKind.UnknownSection, "bogus"),
             new TemplateIssue(TemplateIssueKind.EmptyBody)],
            byFile["b.md"].Issues);
        Assert.Equal([new TemplateIssue(TemplateIssueKind.DuplicateName, "ventas")], byFile["d.md"].Issues);
    }

    [Fact]
    public void ValidateReturnsTheTemplateWhenThereAreNoIssues()
    {
        var result = TemplateCatalog.Validate(Valid, "x");

        Assert.True(result.IsValid);
        Assert.Equal("ventas", result.Template!.Name);
        Assert.Equal(["executiveSummary", "actionItems"], result.Template.Sections);
    }

    [Fact]
    public void ValidateReportsAMissingName()
    {
        var result = TemplateCatalog.Validate("---\ndescription: x\n---\nCuerpo", fallbackName: " ");

        Assert.False(result.IsValid);
        Assert.Null(result.Template);
        Assert.Equal([new TemplateIssue(TemplateIssueKind.MissingName)], result.Issues);
    }

    [Fact]
    public void CreateFromBuiltInCopiesTheFileOnceAndThenReusesIt()
    {
        using var temp = new TempDirectory();
        var folder = Path.Combine(temp.Path, "templates");
        var catalog = new TemplateCatalog(folder);

        var first = catalog.CreateFromBuiltIn("Daily");
        File.WriteAllText(first.Path, "---\nname: daily\n---\nEditada.");
        var second = catalog.CreateFromBuiltIn("daily");

        Assert.True(first.Created);
        Assert.Equal(Path.Combine(folder, "daily.md"), first.Path);
        Assert.False(second.Created);
        Assert.Equal(first.Path, second.Path);
        Assert.Equal("Editada.", catalog.Load().Find("daily")!.Body);
        Assert.Throws<ArgumentException>(() => catalog.CreateFromBuiltIn("nope"));
    }

    [Fact]
    public void ResolveFallsBackToTheDefaultTemplate()
    {
        using var temp = new TempDirectory();
        var snapshot = new TemplateCatalog(temp.Path).Load();

        Assert.Equal("planning", snapshot.Resolve("planning").Name);
        Assert.Equal(DefaultTemplates.Default.Name, snapshot.Resolve("deleted").Name);
        Assert.Equal(DefaultTemplates.Default.Name, snapshot.Resolve(null).Name);
    }
}
