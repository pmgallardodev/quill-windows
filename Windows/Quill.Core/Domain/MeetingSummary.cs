using System.Text.Json.Serialization;
using Quill.Core.Summaries;

namespace Quill.Core.Domain;

/// <summary>Live (rolling, during recording) or final (after the final transcript).</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<SummaryKind>))]
public enum SummaryKind
{
    Live,
    Final,
}

[JsonConverter(typeof(CamelCaseEnumConverter<SummaryLevel>))]
public enum SummaryLevel
{
    Brief,
    Detailed,
}

/// <summary>
/// Structured meeting summary, design §7.1. Serialize with
/// <see cref="MeetingSummaryJson"/>; the model is constrained with
/// <see cref="MeetingSummarySchema"/>. Every cited item carries the transcript
/// offset (<c>at</c>, "hh:mm:ss") it comes from; <see cref="CitationValidator"/>
/// drops items whose offset is not inside any transcript segment.
/// Empty lists are valid and mean "None" for that section.
/// </summary>
public sealed record MeetingSummary
{
    [JsonPropertyName("executiveSummary")]
    public string ExecutiveSummary { get; init; } = string.Empty;

    [JsonPropertyName("decisions")]
    public IReadOnlyList<Decision> Decisions { get; init; } = [];

    [JsonPropertyName("actionItems")]
    public IReadOnlyList<ActionItem> ActionItems { get; init; } = [];

    [JsonPropertyName("openQuestions")]
    public IReadOnlyList<OpenQuestion> OpenQuestions { get; init; } = [];

    [JsonPropertyName("topics")]
    public IReadOnlyList<Topic> Topics { get; init; } = [];

    [JsonPropertyName("risks")]
    public IReadOnlyList<Risk> Risks { get; init; } = [];

    [JsonPropertyName("keyFacts")]
    public IReadOnlyList<KeyFact> KeyFacts { get; init; } = [];

    [JsonPropertyName("nextSteps")]
    public IReadOnlyList<string> NextSteps { get; init; } = [];

    /// <summary>Filled by the application, never by the model.</summary>
    [JsonPropertyName("meta")]
    public SummaryMeta Meta { get; init; } = new();
}

/// <summary>Section keys (JSON property names), in display order.</summary>
public static class SummarySections
{
    public const string ExecutiveSummary = "executiveSummary";
    public const string Decisions = "decisions";
    public const string ActionItems = "actionItems";
    public const string OpenQuestions = "openQuestions";
    public const string Topics = "topics";
    public const string Risks = "risks";
    public const string KeyFacts = "keyFacts";
    public const string NextSteps = "nextSteps";

    public static IReadOnlyList<string> All { get; } =
        [ExecutiveSummary, Decisions, ActionItems, OpenQuestions, Topics, Risks, KeyFacts, NextSteps];

    /// <summary>Live summaries fill only these (design §7.1).</summary>
    public static IReadOnlyList<string> Live { get; } =
        [ExecutiveSummary, Decisions, ActionItems, OpenQuestions];
}

public sealed record Decision(
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("decidedBy")] string? DecidedBy,
    [property: JsonPropertyName("at"), JsonRequired, JsonConverter(typeof(TimestampJsonConverter))] TimeSpan At);

public sealed record ActionItem(
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("owner")] string? Owner,
    [property: JsonPropertyName("due")] string? Due,
    [property: JsonPropertyName("isMine")] bool IsMine,
    [property: JsonPropertyName("at"), JsonRequired, JsonConverter(typeof(TimestampJsonConverter))] TimeSpan At,
    [property: JsonPropertyName("done")] bool Done = false);

public sealed record OpenQuestion(
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("at"), JsonRequired, JsonConverter(typeof(TimestampJsonConverter))] TimeSpan At);

public sealed record Topic(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("start"), JsonRequired, JsonConverter(typeof(TimestampJsonConverter))] TimeSpan Start);

public sealed record Risk(
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("at"), JsonRequired, JsonConverter(typeof(TimestampJsonConverter))] TimeSpan At);

public sealed record KeyFact(
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("at"), JsonRequired, JsonConverter(typeof(TimestampJsonConverter))] TimeSpan At);

public sealed record SummaryMeta
{
    [JsonPropertyName("template")]
    public string Template { get; init; } = string.Empty;

    /// <summary>Output language code, e.g. "es".</summary>
    [JsonPropertyName("language")]
    public string Language { get; init; } = string.Empty;

    [JsonPropertyName("level")]
    public SummaryLevel Level { get; init; } = SummaryLevel.Brief;

    [JsonPropertyName("model")]
    public string Model { get; init; } = string.Empty;

    [JsonPropertyName("generatedAt")]
    public DateTimeOffset GeneratedAt { get; init; }

    [JsonPropertyName("kind")]
    public SummaryKind Kind { get; init; } = SummaryKind.Final;
}
