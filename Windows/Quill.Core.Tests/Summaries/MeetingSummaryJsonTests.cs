using System.Text.Json;
using System.Text.Json.Nodes;
using Quill.Core.Domain;
using Quill.Core.Summaries;
using Xunit;

namespace Quill.Core.Tests.Summaries;

public sealed class MeetingSummaryJsonTests
{
    internal static MeetingSummary Sample() => new()
    {
        ExecutiveSummary = "Se acordo lanzar la beta el lunes.",
        Decisions = [new Decision("Lanzar la beta el lunes", "speaker-1", TimeSpan.FromSeconds(83))],
        ActionItems =
        [
            new ActionItem("Preparar notas de version", SpeakerIds.Me, "viernes", IsMine: true, TimeSpan.FromSeconds(125)),
            new ActionItem("Revisar precios", null, null, IsMine: false, TimeSpan.FromHours(1) + TimeSpan.FromSeconds(5), Done: true),
        ],
        OpenQuestions = [new OpenQuestion("Quien avisa a soporte?", TimeSpan.FromSeconds(200))],
        Topics = [new Topic("Lanzamiento", TimeSpan.Zero)],
        Risks = [new Risk("El servidor de pruebas esta lento", TimeSpan.FromSeconds(300))],
        KeyFacts = [new KeyFact("Hay 40 usuarios en la beta", TimeSpan.FromSeconds(310))],
        NextSteps = ["Enviar invitaciones"],
        Meta = new SummaryMeta
        {
            Template = "default",
            Language = "es",
            Level = SummaryLevel.Detailed,
            Model = "qwen3-4b-q4_k_m",
            GeneratedAt = new DateTimeOffset(2026, 11, 23, 10, 0, 0, TimeSpan.Zero),
            Kind = SummaryKind.Final,
        },
    };

    [Fact]
    public void RoundTripPreservesEverySection()
    {
        var original = Sample();

        var json = MeetingSummaryJson.Serialize(original);
        var restored = MeetingSummaryJson.Deserialize(json);

        Assert.Equal(original.ExecutiveSummary, restored.ExecutiveSummary);
        Assert.Equal(original.Decisions, restored.Decisions);
        Assert.Equal(original.ActionItems, restored.ActionItems);
        Assert.Equal(original.OpenQuestions, restored.OpenQuestions);
        Assert.Equal(original.Topics, restored.Topics);
        Assert.Equal(original.Risks, restored.Risks);
        Assert.Equal(original.KeyFacts, restored.KeyFacts);
        Assert.Equal(original.NextSteps, restored.NextSteps);
        Assert.Equal(original.Meta, restored.Meta);
    }

    [Fact]
    public void WritesDesignContractShape()
    {
        var node = JsonNode.Parse(MeetingSummaryJson.Serialize(Sample()))!.AsObject();

        Assert.Equal("00:01:23", node["decisions"]![0]!["at"]!.GetValue<string>());
        Assert.Equal("01:00:05", node["actionItems"]![1]!["at"]!.GetValue<string>());
        Assert.Equal("00:00:00", node["topics"]![0]!["start"]!.GetValue<string>());
        Assert.True(node["actionItems"]![0]!["isMine"]!.GetValue<bool>());
        Assert.Equal("detailed", node["meta"]!["level"]!.GetValue<string>());
        Assert.Equal("final", node["meta"]!["kind"]!.GetValue<string>());
        foreach (var section in SummarySections.All)
        {
            Assert.True(node.ContainsKey(section), section);
        }
    }

    [Fact]
    public void MissingAndNullSectionsBecomeEmpty()
    {
        var summary = MeetingSummaryJson.Deserialize(
            """{"executiveSummary":"Nada que destacar.","decisions":null,"meta":{"kind":"live"}}""");

        Assert.Equal("Nada que destacar.", summary.ExecutiveSummary);
        Assert.Empty(summary.Decisions);
        Assert.Empty(summary.ActionItems);
        Assert.Empty(summary.OpenQuestions);
        Assert.Empty(summary.Topics);
        Assert.Empty(summary.Risks);
        Assert.Empty(summary.KeyFacts);
        Assert.Empty(summary.NextSteps);
        Assert.Equal(SummaryKind.Live, summary.Meta.Kind);
    }

    [Theory]
    [InlineData("1:02:03", 3723)]
    [InlineData("02:03", 123)]
    [InlineData("00:00:07.900", 7)]
    [InlineData("100:00:00", 360000)]
    public void ReadsLenientTimestamps(string at, int expectedSeconds)
    {
        var summary = MeetingSummaryJson.Deserialize(
            $$"""{"keyFacts":[{"text":"x","at":"{{at}}"}]}""");

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), summary.KeyFacts[0].At);
    }

    [Theory]
    [InlineData("\"soon\"")]
    [InlineData("\"00:61:00\"")]
    [InlineData("42")]
    public void RejectsInvalidTimestamps(string at)
    {
        Assert.Throws<JsonException>(() => MeetingSummaryJson.Deserialize(
            $$"""{"keyFacts":[{"text":"x","at":{{at}}}]}"""));
        Assert.False(MeetingSummaryJson.TryDeserialize(
            $$"""{"keyFacts":[{"text":"x","at":{{at}}}]}""", out _));
    }

    [Fact]
    public void MissingCitationTimestampIsRejected()
    {
        // A silent 00:00:00 default would let an uncited item pass the citation validator.
        Assert.Throws<JsonException>(() => MeetingSummaryJson.Deserialize(
            """{"decisions":[{"text":"x","decidedBy":null}]}"""));
    }

    [Fact]
    public void TryDeserializeRejectsGarbage()
    {
        Assert.False(MeetingSummaryJson.TryDeserialize("not json", out var summary));
        Assert.Null(summary);
    }
}
