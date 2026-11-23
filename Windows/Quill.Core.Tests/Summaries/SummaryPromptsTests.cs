using Quill.Core.Domain;
using Quill.Core.Summaries;
using Xunit;
using static Quill.Core.Tests.Summaries.Segments;

namespace Quill.Core.Tests.Summaries;

public sealed class SummaryPromptsTests
{
    private static readonly Template Client = new(
        "cliente",
        "Llamada con cliente",
        null,
        SummaryLevel.Detailed,
        [SummarySections.Decisions, SummarySections.ActionItems],
        "Destaca compromisos con el cliente y plazos.");

    [Fact]
    public void DefaultOutputLanguageIsSpanishAndIndependentOfTheTranscript()
    {
        var options = new SummaryOptions();

        var prompt = SummaryPrompts.System(SummaryPromptKind.Final, options, SummarySections.All, []);

        Assert.Equal("es", options.ResolvedLanguage);
        Assert.Contains("español", prompt, StringComparison.Ordinal);
        Assert.Contains("aunque la transcripción esté en otro idioma", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void LanguageAndLevelPrecedenceIsCallThenTemplateThenDefault()
    {
        var english = Client with { Language = "en", Level = SummaryLevel.Brief };

        Assert.Equal("en", new SummaryOptions { Template = english }.ResolvedLanguage);
        Assert.Equal("fr", new SummaryOptions { Template = english, Language = "fr" }.ResolvedLanguage);
        Assert.Equal(SummaryLevel.Brief, new SummaryOptions { Template = english }.ResolvedLevel);
        Assert.Equal(SummaryLevel.Detailed, new SummaryOptions { Template = english, Level = SummaryLevel.Detailed }.ResolvedLevel);
        Assert.Equal("general", new SummaryOptions().TemplateName);
        Assert.Equal("cliente", new SummaryOptions { Template = Client }.TemplateName);
        Assert.Contains("inglés", SummaryPrompts.System(SummaryPromptKind.Final, new SummaryOptions { Template = english }, [], []), StringComparison.Ordinal);
    }

    [Fact]
    public void SystemPromptCarriesStrictRulesTemplateLevelAndSections()
    {
        var options = new SummaryOptions { Template = Client };

        var prompt = SummaryPrompts.System(SummaryPromptKind.Final, options, Client.Sections, []);

        Assert.Contains("No inventes", prompt, StringComparison.Ordinal);
        Assert.Contains("[]", prompt, StringComparison.Ordinal);
        Assert.Contains("hh:mm:ss", prompt, StringComparison.Ordinal);
        Assert.Contains(Client.Body, prompt, StringComparison.Ordinal);
        Assert.Contains("detallado", prompt, StringComparison.Ordinal);
        Assert.Contains(SummarySections.ActionItems, prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(SummarySections.Risks, prompt, StringComparison.Ordinal);

        var brief = SummaryPrompts.System(SummaryPromptKind.Final, new SummaryOptions { Level = SummaryLevel.Brief }, [], []);
        Assert.Contains("breve", brief, StringComparison.Ordinal);
    }

    [Fact]
    public void OwnerNotesAreHighPriorityHintsWithTheirTimestamps()
    {
        var notes = new List<Note> { new("Ojo con el presupuesto", S(125)), new("Preguntar por el contrato") };

        var prompt = SummaryPrompts.System(SummaryPromptKind.Final, new SummaryOptions(), [], notes);

        Assert.Contains("prioridad alta", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("- [00:02:05] Ojo con el presupuesto", prompt, StringComparison.Ordinal);
        Assert.Contains("- Preguntar por el contrato", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void TranscriptPromptUsesChunkerLinesSoTheModelCitesExactTimestamps()
    {
        var prompt = SummaryPrompts.Transcript("[00:01:05] me: Hola\n", part: 2, parts: 3);

        Assert.Contains("[00:01:05] me: Hola", prompt, StringComparison.Ordinal);
        Assert.Contains("2 de 3", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void ModelJsonOmitsMetaAndUnrequestedSections()
    {
        var summary = new MeetingSummary
        {
            ExecutiveSummary = "Resumen",
            Decisions = [new Decision("Sí", null, S(1))],
            Risks = [new Risk("riesgo", S(2))],
            Meta = new SummaryMeta { Model = "secret-model" },
        };

        var json = SummaryPrompts.ModelJson(summary, SummarySections.Live);

        Assert.Contains("\"decisions\"", json, StringComparison.Ordinal);
        Assert.Contains("\"00:00:01\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("meta", json, StringComparison.Ordinal);
        Assert.DoesNotContain("risks", json, StringComparison.Ordinal);
    }

    [Fact]
    public void LivePromptCarriesPreviousSummaryAndOnlyNewLines()
    {
        var previous = new MeetingSummary { ExecutiveSummary = "Antes hablamos del plan." };

        var prompt = SummaryPrompts.Live(previous, "[00:03:00] me: Nueva línea\n");

        Assert.Contains("Antes hablamos del plan.", prompt, StringComparison.Ordinal);
        Assert.Contains("[00:03:00] me: Nueva línea", prompt, StringComparison.Ordinal);
    }
}
