using Quill.Core.Domain;
using Quill.Core.Summaries;
using Quill.Core.Templates;
using Xunit;

namespace Quill.Core.Tests.Templates;

public sealed class DefaultTemplatesTests
{
    [Fact]
    public void ShipsSixUniquelyNamedTemplatesIncludingTheDefault()
    {
        var names = DefaultTemplates.All.Select(template => template.Name).ToList();

        Assert.Equal(6, names.Count);
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains(SummaryOptions.DefaultTemplateName, names);
        Assert.Equal(SummaryOptions.DefaultTemplateName, DefaultTemplates.Default.Name);
    }

    [Fact]
    public void EveryTemplateHasADescriptionABodyAndOnlyKnownSections()
    {
        Assert.All(DefaultTemplates.All, template =>
        {
            Assert.False(string.IsNullOrWhiteSpace(template.Description), template.Name);
            Assert.False(string.IsNullOrWhiteSpace(template.Body), template.Name);
            Assert.All(template.Sections, section => Assert.Contains(section, SummarySections.All));
        });
    }

    [Fact]
    public void GeneralFillsEverySectionAndLeavesLanguageToTheUser()
    {
        var general = DefaultTemplates.Find("GENERAL");

        Assert.NotNull(general);
        Assert.Empty(general.Sections);
        Assert.Null(general.Language);
    }

    [Fact]
    public void EveryTemplateProducesAGrammarForItsSections()
    {
        Assert.All(DefaultTemplates.All, template =>
            Assert.Contains("root ::= ", JsonSchemaGbnf.Convert(MeetingSummarySchema.For(template.Sections)), StringComparison.Ordinal));
    }

    [Fact]
    public void FindReturnsNullForUnknownNames()
    {
        Assert.Null(DefaultTemplates.Find("does-not-exist"));
    }

    [Fact]
    public void RawTextRoundTripsThroughTheParser()
    {
        foreach (var (fileName, text) in DefaultTemplates.Files)
        {
            var parsed = TemplateParser.Parse(text);
            Assert.Equal(Path.GetFileNameWithoutExtension(fileName), parsed.Name);
        }
    }
}
