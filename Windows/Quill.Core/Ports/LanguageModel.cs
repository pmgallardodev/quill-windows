using System.Text;

namespace Quill.Core.Ports;

/// <param name="SystemPrompt">Instructions (template body, language, level).</param>
/// <param name="UserPrompt">Content, e.g. transcript lines from <see cref="Transcripts.TranscriptChunker"/>.</param>
/// <param name="MaxOutputTokens">Upper bound on generated tokens.</param>
/// <param name="Temperature">Sampling temperature; low for summaries.</param>
public sealed record LanguageModelRequest(
    string SystemPrompt,
    string UserPrompt,
    int MaxOutputTokens = 1024,
    float Temperature = 0.2f);

/// <summary>
/// Local LLM (adapter: LLamaSharp + Vulkan). Implementations must be safe to call
/// from one background scheduler at a time; they need not support concurrent calls.
/// </summary>
public interface ILanguageModel
{
    /// <summary>Model identifier recorded in summary meta, e.g. "qwen3-4b-instruct-q4_k_m".</summary>
    string ModelName { get; }

    /// <summary>Context window in tokens; callers size chunks below it.</summary>
    int ContextTokens { get; }

    /// <summary>Free text, streamed piece by piece.</summary>
    IAsyncEnumerable<string> GenerateAsync(LanguageModelRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// JSON constrained to <paramref name="jsonSchema"/> (e.g.
    /// <see cref="Summaries.MeetingSummarySchema.For(Domain.SummaryKind)"/>). Returns the raw JSON text;
    /// callers still parse and validate it.
    /// </summary>
    Task<string> GenerateJsonAsync(LanguageModelRequest request, string jsonSchema, CancellationToken cancellationToken);
}

public static class LanguageModelExtensions
{
    /// <summary>Collects <see cref="ILanguageModel.GenerateAsync"/> into one string.</summary>
    public static async Task<string> GenerateTextAsync(
        this ILanguageModel model,
        LanguageModelRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);
        var output = new StringBuilder();
        await foreach (var piece in model.GenerateAsync(request, cancellationToken).ConfigureAwait(false))
        {
            output.Append(piece);
        }

        return output.ToString();
    }
}

/// <summary>Text embeddings for semantic search (F10). Vectors are L2-normalized.</summary>
public interface IEmbeddingModel
{
    string ModelName { get; }

    int Dimensions { get; }

    Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken);
}
