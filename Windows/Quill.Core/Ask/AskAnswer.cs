using Quill.Core.Search;

namespace Quill.Core.Ask;

/// <summary>A moment the answer relies on.</summary>
/// <param name="At">Line timestamp as shown to the model (whole seconds), for click-to-play.</param>
/// <param name="Quote">The cited line without its timestamp, e.g. "speaker-1: Aprobamos el presupuesto".</param>
public sealed record AskCitation(string SessionId, TimeSpan At, string Quote);

/// <summary>A passage given to the model, with the meeting it belongs to.</summary>
public sealed record AskSource(Passage Passage, string SessionTitle, DateTimeOffset? Started);

/// <summary>
/// Result of Ask. <see cref="Text"/> keeps the verified "[sessionId@hh:mm:ss]" markers (invented
/// ones are removed); <see cref="PlainText"/> has none, for display next to citation chips.
/// </summary>
/// <param name="NotFound">The model (or retrieval) found nothing to answer with.</param>
/// <param name="Sources">Passages the model was given, best first.</param>
public sealed record AskAnswer(
    string Text,
    string PlainText,
    IReadOnlyList<AskCitation> Citations,
    bool NotFound,
    IReadOnlyList<AskSource> Sources)
{
    /// <summary>
    /// True when the answer cites at least one real line. An answer that is neither "not found"
    /// nor grounded is shown with a warning: nothing backs it (design §4, no invented answers).
    /// </summary>
    public bool IsGrounded => Citations.Count > 0;

    public static AskAnswer NothingFound(string language, IReadOnlyList<AskSource>? sources = null)
    {
        var text = AskPrompts.NotFoundText(language);
        return new AskAnswer(text, text, [], NotFound: true, sources ?? []);
    }
}

/// <summary>Tuning for <see cref="MeetingAsk"/> and <see cref="LibraryAsk"/>.</summary>
public sealed record AskOptions
{
    /// <summary>Answer language code ("es", "en"...), from settings.</summary>
    public string Language { get; init; } = Summaries.SummaryOptions.DefaultLanguage;

    /// <summary>Passages retrieved before packing them into the context.</summary>
    public int TopK { get; init; } = 8;

    public int MaxOutputTokens { get; init; } = 768;

    public float Temperature { get; init; } = 0.2f;

    /// <summary>Tokens kept free besides prompt and answer (chat template, estimate error).</summary>
    public int SafetyMarginTokens { get; init; } = 256;
}
