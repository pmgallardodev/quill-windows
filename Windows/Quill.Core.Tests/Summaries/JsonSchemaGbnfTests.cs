using Quill.Core.Domain;
using Quill.Core.Summaries;
using Xunit;

namespace Quill.Core.Tests.Summaries;

public sealed class JsonSchemaGbnfTests
{
    private const string LiveValid = """
        {
          "executiveSummary": "Se decidió lanzar el lunes. \"Citado\" y acentos: ñ.",
          "decisions": [ { "text": "Lanzar", "decidedBy": null, "at": "00:01:05" } ],
          "actionItems": [
            { "text": "Preparar demo", "owner": "speaker-1", "due": "viernes", "isMine": false, "at": "100:00:59", "done": false }
          ],
          "openQuestions": []
        }
        """;

    private static GbnfMatcher Live() => new(JsonSchemaGbnf.Convert(MeetingSummarySchema.For(SummaryKind.Live)));

    [Fact]
    public void LiveGrammarAcceptsAValidSummaryIncludingNullsAndEscapes()
    {
        Assert.True(Live().Matches(LiveValid));
    }

    [Fact]
    public void FinalGrammarAcceptsEmptySectionsInSchemaOrder()
    {
        var grammar = new GbnfMatcher(JsonSchemaGbnf.Convert(MeetingSummarySchema.For(SummaryKind.Final)));

        Assert.True(grammar.Matches("""
            {"executiveSummary":"","decisions":[],"actionItems":[],"openQuestions":[],"topics":[{"title":"Intro","start":"00:00:00"}],"risks":[],"keyFacts":[],"nextSteps":["Llamar"]}
            """));
    }

    [Theory]
    [InlineData("\"at\": \"00:01:05\"", "\"at\": \"0:01:05\"")] // pattern: two hour digits
    [InlineData("\"at\": \"00:01:05\"", "\"at\": \"00:61:05\"")] // pattern: minutes 00-59
    [InlineData("\"decidedBy\": null", "\"decidedBy\": 3")] // ["string","null"]
    [InlineData("\"text\": \"Lanzar\"", "\"text\": \"\"")] // minLength 1
    [InlineData("\"openQuestions\": []", "\"openQuestions\": [], \"extra\": 1")] // additionalProperties false
    [InlineData(",\n  \"openQuestions\": []", "")] // required section missing
    [InlineData("\"isMine\": false", "\"isMine\": \"no\"")] // boolean
    public void LiveGrammarRejectsSchemaViolations(string valid, string invalid)
    {
        Assert.Contains(valid, LiveValid, StringComparison.Ordinal);

        Assert.False(Live().Matches(LiveValid.Replace(valid, invalid, StringComparison.Ordinal)));
    }

    [Fact]
    public void GrammarHasARootRuleAndNoUnboundedWhitespace()
    {
        var gbnf = JsonSchemaGbnf.Convert(MeetingSummarySchema.For(SummaryKind.Final));

        Assert.Contains("root ::= ", gbnf, StringComparison.Ordinal);
        Assert.DoesNotContain("[ \\t\\n]*", gbnf, StringComparison.Ordinal);
        Assert.False(new GbnfMatcher(gbnf).Matches("{" + new string(' ', 200) + "}"));
    }

    [Theory]
    [InlineData("""{"$ref":"#/defs/x"}""")]
    [InlineData("""{"type":"string","pattern":"^(a|b)$"}""")]
    [InlineData("""{"type":"object","properties":{"a":{"type":"string"}},"required":[]}""")]
    public void UnsupportedSchemaFeaturesFailLoudly(string schema)
    {
        Assert.Throws<NotSupportedException>(() => JsonSchemaGbnf.Convert(schema));
    }
}
