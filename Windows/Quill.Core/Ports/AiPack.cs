namespace Quill.Core.Ports;

/// <summary>Optional model files shipped in the separate AI pack ZIP (design Q1).</summary>
public enum AiPackComponent
{
    /// <summary>Summary/Ask LLM (GGUF).</summary>
    LanguageModel,

    /// <summary>Whisper small for live transcription (ggml .bin).</summary>
    LiveWhisper,

    /// <summary>Speaker segmentation model for diarization (ONNX).</summary>
    DiarizationSegmentation,

    /// <summary>Speaker embedding model for diarization and voice memory (ONNX).</summary>
    SpeakerEmbedding,

    /// <summary>Text embedding model for semantic search (GGUF or ONNX).</summary>
    TextEmbedding,
}

/// <summary>
/// Result of AI pack detection. A component path is set only when its file exists.
/// <see cref="IsInstalled"/> is false when the pack folder or manifest is missing;
/// features check their own component and show "Install AI pack" when it is null.
/// </summary>
public sealed record AiPackStatus(
    bool IsInstalled,
    string? Root,
    string? Version,
    IReadOnlyDictionary<AiPackComponent, string> Paths,
    IReadOnlyList<AiPackComponent> Missing)
{
    public static AiPackStatus NotInstalled { get; } = new(
        false,
        null,
        null,
        new Dictionary<AiPackComponent, string>(),
        Enum.GetValues<AiPackComponent>());

    /// <summary>
    /// Components shipped split in parts that Quill is joining (or failed to join). Each one is
    /// also listed in <see cref="Missing"/> until its joined file is verified and in place.
    /// </summary>
    public IReadOnlyList<ModelPreparation> Preparing { get; init; } = [];

    public string? PathOf(AiPackComponent component) =>
        Paths.TryGetValue(component, out var path) ? path : null;

    public bool Has(AiPackComponent component) => Paths.ContainsKey(component);

    /// <summary>The join state of a split component, or null when it is ready or not split.</summary>
    public ModelPreparation? PreparationOf(AiPackComponent component) =>
        Preparing.FirstOrDefault(preparation => preparation.Component == component);
}

public enum ModelPreparationState
{
    /// <summary>Parts are being joined and verified in the background ("Preparando el modelo de IA…").</summary>
    Preparing,

    /// <summary>The joined file is verified and in place.</summary>
    Ready,

    /// <summary>Joining failed; <see cref="ModelPreparation.Error"/> says why.</summary>
    Failed,
}

/// <summary>Progress of joining one split AI pack component.</summary>
/// <param name="Progress">Fraction of bytes joined, 0..1.</param>
/// <param name="Error">Why joining failed (English, for logs and diagnostics); null otherwise.</param>
public sealed record ModelPreparation(AiPackComponent Component, ModelPreparationState State, double Progress, string? Error);

/// <summary>
/// Finds the AI pack: an explicit path from Settings first, otherwise next to the
/// app. Must be cheap and never throw. Default implementation:
/// <see cref="AiPack.ManifestAiPackLocator"/>.
/// </summary>
public interface IAiPackLocator
{
    AiPackStatus Locate();
}
