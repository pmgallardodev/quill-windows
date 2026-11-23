using System.Globalization;
using System.Text;
using System.Text.Json;
using Quill.Core.Domain;
using Quill.Core.Ports;
using Quill.Core.Transcripts;

namespace Quill.Core.Summaries;

/// <summary>Shared mechanics of the summary use cases: one schema-constrained call, token budgets, dedupe.</summary>
internal static class SummaryGeneration
{
    /// <summary>
    /// Asks for schema-constrained JSON and parses it. Grammar-constrained output should always
    /// parse; one retry covers the rare truncated or malformed answer.
    /// </summary>
    public static async Task<MeetingSummary> GenerateAsync(
        ILanguageModel model,
        LanguageModelRequest request,
        string schema,
        CancellationToken cancellationToken)
    {
        JsonException? last = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var json = await model.GenerateJsonAsync(request, schema, cancellationToken).ConfigureAwait(false);
            try
            {
                return MeetingSummaryJson.Deserialize(json);
            }
            catch (JsonException exception)
            {
                last = exception;
            }
        }

        throw new SummaryGenerationException($"model output is not a valid summary: {last!.Message}", last);
    }

    /// <summary>
    /// Tokens left for transcript (or partial summaries) once the system prompt, the answer and a
    /// safety margin are reserved. The chars/4 estimate is rough, hence the 10 % margin.
    /// </summary>
    public static int ContentBudget(ILanguageModel model, string systemPrompt, int maxOutputTokens)
    {
        var margin = Math.Max(128, model.ContextTokens / 10);
        var budget = model.ContextTokens - TranscriptChunker.EstimateTokens(systemPrompt) - maxOutputTokens - margin;
        return Math.Max(256, budget);
    }

    public static SummaryMeta Meta(ILanguageModel model, SummaryOptions options, IClock clock, SummaryKind kind, string? template = null) => new()
    {
        Template = template ?? options.TemplateName,
        Language = options.ResolvedLanguage,
        Level = options.ResolvedLevel,
        Model = model.ModelName,
        GeneratedAt = clock.Now,
        Kind = kind,
    };

    /// <summary>
    /// Removes items repeated within a section (same text ignoring case, accents, spacing and
    /// trailing punctuation), keeping the earliest citation. Order is otherwise preserved.
    /// </summary>
    public static MeetingSummary Deduplicate(MeetingSummary summary) => summary with
    {
        Decisions = Distinct(summary.Decisions, d => d.Text, d => d.At),
        ActionItems = Distinct(summary.ActionItems, a => a.Text, a => a.At),
        OpenQuestions = Distinct(summary.OpenQuestions, q => q.Text, q => q.At),
        Topics = Distinct(summary.Topics, t => t.Title, t => t.Start),
        Risks = Distinct(summary.Risks, r => r.Text, r => r.At),
        KeyFacts = Distinct(summary.KeyFacts, f => f.Text, f => f.At),
        NextSteps = Distinct(summary.NextSteps, s => s, _ => TimeSpan.Zero),
    };

    /// <summary>Concatenation of partial summaries without a model (last-resort reduce step).</summary>
    public static MeetingSummary Concatenate(IReadOnlyList<MeetingSummary> partials) => Deduplicate(new MeetingSummary
    {
        ExecutiveSummary = string.Join(' ', partials.Select(p => p.ExecutiveSummary).Where(text => !string.IsNullOrWhiteSpace(text))),
        Decisions = partials.SelectMany(p => p.Decisions).ToList(),
        ActionItems = partials.SelectMany(p => p.ActionItems).ToList(),
        OpenQuestions = partials.SelectMany(p => p.OpenQuestions).ToList(),
        Topics = partials.SelectMany(p => p.Topics).ToList(),
        Risks = partials.SelectMany(p => p.Risks).ToList(),
        KeyFacts = partials.SelectMany(p => p.KeyFacts).ToList(),
        NextSteps = partials.SelectMany(p => p.NextSteps).ToList(),
    });

    public static string Lines(IEnumerable<TranscriptSegment> segments) =>
        string.Concat(segments
            .Where(segment => segment is not null && !string.IsNullOrWhiteSpace(segment.Text))
            .Select(segment => TranscriptChunker.FormatLine(segment) + "\n"));

    private static IReadOnlyList<T> Distinct<T>(IReadOnlyList<T> items, Func<T, string> text, Func<T, TimeSpan> at)
    {
        var earliest = new Dictionary<string, int>(StringComparer.Ordinal);
        var kept = new List<T>();
        foreach (var item in items)
        {
            var key = Normalize(text(item));
            if (earliest.TryGetValue(key, out var index))
            {
                if (at(item) < at(kept[index]))
                {
                    kept[index] = item;
                }

                continue;
            }

            earliest[key] = kept.Count;
            kept.Add(item);
        }

        return kept;
    }

    private static string Normalize(string? text)
    {
        var decomposed = (text ?? string.Empty).Normalize(NormalizationForm.FormD);
        var output = new StringBuilder(decomposed.Length);
        var space = false;
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                space = output.Length > 0;
                continue;
            }

            if (space)
            {
                output.Append(' ');
                space = false;
            }

            output.Append(char.ToLowerInvariant(c));
        }

        return output.ToString().TrimEnd('.', ',', ';', ':', '!', '?', ' ');
    }
}
