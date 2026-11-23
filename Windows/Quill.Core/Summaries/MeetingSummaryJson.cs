using System.Diagnostics.CodeAnalysis;
using System.Text.Encodings.Web;
using System.Text.Json;
using Quill.Core.Domain;

namespace Quill.Core.Summaries;

/// <summary>
/// Reads and writes <see cref="MeetingSummary"/>: summary.json on disk and the raw
/// JSON a language model returns. Reading is tolerant (missing or null sections
/// become empty, case-insensitive names, trailing commas) but timestamps must be
/// valid "hh:mm:ss"; anything else is a <see cref="JsonException"/>.
/// </summary>
public static class MeetingSummaryJson
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        // summary.json is meant to be readable: keep accents and punctuation as-is.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    public static string Serialize(MeetingSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        return JsonSerializer.Serialize(Normalize(summary), WriteOptions);
    }

    /// <exception cref="JsonException">Malformed JSON, a non-object root, or an invalid timestamp.</exception>
    public static MeetingSummary Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        MeetingSummary? summary;
        try
        {
            summary = JsonSerializer.Deserialize<MeetingSummary>(json, ReadOptions);
        }
        catch (Exception exception) when (exception is NotSupportedException or InvalidOperationException or FormatException)
        {
            throw new JsonException(exception.Message, exception);
        }

        return summary is null
            ? throw new JsonException("summary JSON must be an object")
            : Normalize(summary);
    }

    public static bool TryDeserialize(string json, [NotNullWhen(true)] out MeetingSummary? summary)
    {
        try
        {
            summary = Deserialize(json);
            return true;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            summary = null;
            return false;
        }
    }

    /// <summary>Replaces nulls the serializer may leave behind (JSON null, missing fields) with empty values.</summary>
    internal static MeetingSummary Normalize(MeetingSummary summary) => summary with
    {
        ExecutiveSummary = summary.ExecutiveSummary?.Trim() ?? string.Empty,
        Decisions = Clean(summary.Decisions, d => d with { Text = d.Text ?? string.Empty }),
        ActionItems = Clean(summary.ActionItems, a => a with { Text = a.Text ?? string.Empty }),
        OpenQuestions = Clean(summary.OpenQuestions, q => q with { Text = q.Text ?? string.Empty }),
        Topics = Clean(summary.Topics, t => t with { Title = t.Title ?? string.Empty }),
        Risks = Clean(summary.Risks, r => r with { Text = r.Text ?? string.Empty }),
        KeyFacts = Clean(summary.KeyFacts, f => f with { Text = f.Text ?? string.Empty }),
        NextSteps = summary.NextSteps?.Where(step => step is not null).ToList() ?? [],
        Meta = summary.Meta is null
            ? new SummaryMeta()
            : summary.Meta with
            {
                Template = summary.Meta.Template ?? string.Empty,
                Language = summary.Meta.Language ?? string.Empty,
                Model = summary.Meta.Model ?? string.Empty,
            },
    };

    private static IReadOnlyList<T> Clean<T>(IReadOnlyList<T>? items, Func<T, T> fix)
        where T : class =>
        items?.Where(item => item is not null).Select(fix).ToList() ?? [];
}
