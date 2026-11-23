using System.Text.Json.Nodes;
using Quill.Core.Domain;
using Quill.Core.Summaries;
using Xunit;

namespace Quill.Core.Tests.Summaries;

public sealed class MeetingSummarySchemaTests
{
    [Fact]
    public void FinalSchemaRequiresEverySection()
    {
        var schema = JsonNode.Parse(MeetingSummarySchema.For(SummaryKind.Final))!.AsObject();

        var properties = schema["properties"]!.AsObject();
        var required = schema["required"]!.AsArray().Select(node => node!.GetValue<string>()).ToList();
        foreach (var section in SummarySections.All)
        {
            Assert.True(properties.ContainsKey(section), section);
            Assert.Contains(section, required);
        }

        Assert.False(properties.ContainsKey("meta"));
        Assert.False(schema["additionalProperties"]!.GetValue<bool>());
    }

    [Fact]
    public void LiveSchemaHasOnlyLiveSections()
    {
        var properties = JsonNode.Parse(MeetingSummarySchema.For(SummaryKind.Live))!["properties"]!.AsObject();

        Assert.Equal(SummarySections.Live.OrderBy(x => x), properties.Select(p => p.Key).OrderBy(x => x));
    }

    [Fact]
    public void CitedItemsRequireTimestampPattern()
    {
        var properties = JsonNode.Parse(MeetingSummarySchema.For(SummaryKind.Final))!["properties"]!;

        foreach (var (section, field) in new[]
        {
            ("decisions", "at"), ("actionItems", "at"), ("openQuestions", "at"),
            ("risks", "at"), ("keyFacts", "at"), ("topics", "start"),
        })
        {
            var item = properties[section]!["items"]!;
            Assert.Contains(field, item["required"]!.AsArray().Select(n => n!.GetValue<string>()));
            Assert.Equal(MeetingSummarySchema.TimestampPattern, item["properties"]![field]!["pattern"]!.GetValue<string>());
        }
    }

    [Fact]
    public void SubsetSchemaKeepsRequestedSectionsAndExecutiveSummary()
    {
        var properties = JsonNode.Parse(MeetingSummarySchema.For([SummarySections.ActionItems]))!["properties"]!.AsObject();

        Assert.Equal(
            new[] { SummarySections.ActionItems, SummarySections.ExecutiveSummary }.OrderBy(x => x),
            properties.Select(p => p.Key).OrderBy(x => x));
    }

    [Fact]
    public void ModelOutputMatchingSchemaDeserializes()
    {
        const string modelOutput = """
            {"executiveSummary":"Resumen.","decisions":[],"actionItems":[{"text":"t","owner":null,"due":null,"isMine":false,"at":"00:00:05","done":false}],
             "openQuestions":[],"topics":[],"risks":[],"keyFacts":[],"nextSteps":[]}
            """;

        var summary = MeetingSummaryJson.Deserialize(modelOutput);

        Assert.Single(summary.ActionItems);
        Assert.Equal(TimeSpan.FromSeconds(5), summary.ActionItems[0].At);
    }
}
