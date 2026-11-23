using System.Text.Json;
using System.Text.Json.Nodes;
using Quill.Core.Domain;

namespace Quill.Core.Summaries;

/// <summary>
/// JSON Schema (draft 2020-12 subset understood by llama.cpp's schema-to-grammar)
/// that constrains what the language model generates for a <see cref="MeetingSummary"/>.
/// It describes the model's output only: <c>meta</c> is excluded because the
/// application fills it. Every requested section is required (an empty array is
/// how the model says "None"), and no extra properties are allowed.
/// </summary>
public static class MeetingSummarySchema
{
    /// <summary>"hh:mm:ss" with at least two hour digits.</summary>
    public const string TimestampPattern = "^[0-9]{2,}:[0-5][0-9]:[0-5][0-9]$";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>Final: all sections. Live: executive summary, decisions, action items, open questions.</summary>
    public static string For(SummaryKind kind) =>
        For(kind == SummaryKind.Live ? SummarySections.Live : SummarySections.All);

    /// <summary>
    /// Schema restricted to <paramref name="sections"/> (keys from <see cref="SummarySections"/>,
    /// e.g. a template's sections). The executive summary is always included; unknown keys
    /// are ignored; an empty collection means all sections.
    /// </summary>
    public static string For(IReadOnlyCollection<string> sections)
    {
        ArgumentNullException.ThrowIfNull(sections);
        var wanted = SummarySections.All
            .Where(section => section == SummarySections.ExecutiveSummary ||
                sections.Count == 0 ||
                sections.Contains(section, StringComparer.OrdinalIgnoreCase))
            .ToList();

        var properties = new JsonObject();
        foreach (var section in wanted)
        {
            properties[section] = SectionSchema(section);
        }

        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = Array(wanted),
            ["additionalProperties"] = false,
        };

        return schema.ToJsonString(Indented);
    }

    private static JsonNode SectionSchema(string section) => section switch
    {
        SummarySections.ExecutiveSummary => new JsonObject
        {
            ["type"] = "string",
            ["description"] = "3-5 sentences.",
        },
        SummarySections.Decisions => ListOf(Item(
            ("text", Text()), ("decidedBy", NullableString("Speaker id, or null if unknown.")), ("at", Timestamp()))),
        SummarySections.ActionItems => ListOf(Item(
            ("text", Text()),
            ("owner", NullableString("Speaker id, or null if unknown.")),
            ("due", NullableString("Due date as said in the meeting, or null.")),
            ("isMine", new JsonObject { ["type"] = "boolean" }),
            ("at", Timestamp()),
            ("done", new JsonObject { ["type"] = "boolean" }))),
        SummarySections.OpenQuestions => ListOf(Item(("text", Text()), ("at", Timestamp()))),
        SummarySections.Topics => ListOf(Item(("title", Text()), ("start", Timestamp()))),
        SummarySections.Risks => ListOf(Item(("text", Text()), ("at", Timestamp()))),
        SummarySections.KeyFacts => ListOf(Item(("text", Text()), ("at", Timestamp()))),
        SummarySections.NextSteps => ListOf(Text()),
        _ => throw new ArgumentOutOfRangeException(nameof(section), section, "unknown summary section"),
    };

    private static JsonObject ListOf(JsonNode items) => new() { ["type"] = "array", ["items"] = items };

    private static JsonObject Item(params (string Name, JsonNode Schema)[] fields)
    {
        var properties = new JsonObject();
        foreach (var (name, schema) in fields)
        {
            properties[name] = schema;
        }

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = Array(fields.Select(field => field.Name)),
            ["additionalProperties"] = false,
        };
    }

    private static JsonObject Text() => new() { ["type"] = "string", ["minLength"] = 1 };

    private static JsonObject NullableString(string description) => new()
    {
        ["type"] = new JsonArray("string", "null"),
        ["description"] = description,
    };

    private static JsonObject Timestamp() => new()
    {
        ["type"] = "string",
        ["pattern"] = TimestampPattern,
        ["description"] = "hh:mm:ss offset of the transcript line this item comes from.",
    };

    private static JsonArray Array(IEnumerable<string> values) =>
        new(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
}
