using Quill.Core.Domain;

namespace Quill.Core.Summaries;

/// <summary>
/// What a summary call should produce. Language and level resolve as
/// explicit value, then the template's, then the default (Spanish, brief).
/// The output language is independent of the language spoken in the meeting.
/// </summary>
public sealed record SummaryOptions
{
    public const string DefaultLanguage = "es";
    public const string DefaultTemplateName = "general";

    /// <summary>Output language code ("es", "en"...). Null: template language, else <see cref="DefaultLanguage"/>.</summary>
    public string? Language { get; init; }

    /// <summary>Null: template level, else brief.</summary>
    public SummaryLevel? Level { get; init; }

    /// <summary>Template whose body guides the model and whose sections are filled (final summaries only).</summary>
    public Template? Template { get; init; }

    public string ResolvedLanguage =>
        FirstNonBlank(Language, Template?.Language) ?? DefaultLanguage;

    public SummaryLevel ResolvedLevel => Level ?? Template?.Level ?? SummaryLevel.Brief;

    public string TemplateName => FirstNonBlank(Template?.Name) ?? DefaultTemplateName;

    /// <summary>Sections a final summary fills: the template's, or all when it lists none.</summary>
    public IReadOnlyList<string> FinalSections =>
        Template is { Sections.Count: > 0 } template ? template.Sections : SummarySections.All;

    private static string? FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
}

public enum SummaryStatus
{
    Completed,

    /// <summary>Nothing to summarize (no transcript, or nothing said in the window).</summary>
    NoTranscript,

    /// <summary>The language model is not installed; the UI shows "Install AI pack".</summary>
    AiPackMissing,

    /// <summary>The model failed or returned unusable output; see <see cref="SummaryOutcome.Error"/>.</summary>
    Failed,
}

/// <param name="Summary">Validated summary when <see cref="Status"/> is <see cref="SummaryStatus.Completed"/>.</param>
/// <param name="Dropped">Items removed by <see cref="CitationValidator"/> (for logging).</param>
public sealed record SummaryOutcome(
    SummaryStatus Status,
    MeetingSummary? Summary,
    IReadOnlyList<DroppedCitation> Dropped,
    string? Error = null)
{
    public static SummaryOutcome NoTranscript { get; } = new(SummaryStatus.NoTranscript, null, []);

    public static SummaryOutcome AiPackMissing(string? reason = null) => new(SummaryStatus.AiPackMissing, null, [], reason);

    public static SummaryOutcome Failed(string error) => new(SummaryStatus.Failed, null, [], error);
}

/// <summary>
/// Thrown by <see cref="Ports.ILanguageModel"/> adapters when the model cannot be used at all
/// (AI pack or model file missing). Use cases turn it into <see cref="SummaryStatus.AiPackMissing"/>.
/// </summary>
public sealed class LanguageModelUnavailableException : Exception
{
    public LanguageModelUnavailableException(string message)
        : base(message)
    {
    }

    public LanguageModelUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The model output could not be parsed as a summary, even after a retry.</summary>
public sealed class SummaryGenerationException : Exception
{
    public SummaryGenerationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
