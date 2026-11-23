using Quill.Core.Domain;
using Quill.Core.Templates;
using Xunit;

namespace Quill.Core.Tests.Templates;

public sealed class TemplateParserTests
{
    [Fact]
    public void ParsesFrontmatterAndBody()
    {
        const string text = """
            ---
            name: Daily
            description: "Stand-up corto"
            language: es
            level: detailed
            sections: [executiveSummary, actionItems, risks]
            unknown: ignored
            ---

            Enfocate en bloqueos.
            """;

        var template = TemplateParser.Parse(text);

        Assert.Equal("Daily", template.Name);
        Assert.Equal("Stand-up corto", template.Description);
        Assert.Equal("es", template.Language);
        Assert.Equal(SummaryLevel.Detailed, template.Level);
        Assert.Equal(["executiveSummary", "actionItems", "risks"], template.Sections);
        Assert.Equal("Enfocate en bloqueos.", template.Body);
    }

    [Fact]
    public void ParsesBlockListSectionsAndCrLf()
    {
        var text = "---\r\nname: Cliente\r\nsections:\r\n  - decisions\r\n  - nextSteps\r\n---\r\nCuerpo\r\n";

        var template = TemplateParser.Parse(text);

        Assert.Equal(["decisions", "nextSteps"], template.Sections);
        Assert.Equal(SummaryLevel.Brief, template.Level);
        Assert.Null(template.Language);
        Assert.Equal("Cuerpo", template.Body);
    }

    [Fact]
    public void WithoutFrontmatterUsesFallbackName()
    {
        var template = TemplateParser.Parse("Solo cuerpo", fallbackName: "mi-plantilla");

        Assert.Equal("mi-plantilla", template.Name);
        Assert.Empty(template.Sections);
        Assert.Equal("Solo cuerpo", template.Body);
    }

    [Fact]
    public void MissingNameIsAnError() =>
        Assert.Throws<FormatException>(() => TemplateParser.Parse("---\nlevel: brief\n---\nx"));

    [Fact]
    public void RenderRoundTrips()
    {
        var template = new Template("Plan", "Planificacion: sprint", "en", SummaryLevel.Detailed, ["topics", "nextSteps"], "Cuerpo\n\nmas");

        Assert.Equal(template with { Sections = template.Sections.ToList() }, TemplateParser.Parse(TemplateParser.Render(template)), new TemplateComparer());
    }

    private sealed class TemplateComparer : IEqualityComparer<Template>
    {
        public bool Equals(Template? x, Template? y) =>
            x is not null && y is not null &&
            x.Name == y.Name && x.Description == y.Description && x.Language == y.Language &&
            x.Level == y.Level && x.Sections.SequenceEqual(y.Sections) && x.Body == y.Body;

        public int GetHashCode(Template obj) => obj.Name.GetHashCode();
    }
}
