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

    public string? PathOf(AiPackComponent component) =>
        Paths.TryGetValue(component, out var path) ? path : null;

    public bool Has(AiPackComponent component) => Paths.ContainsKey(component);
}

/// <summary>
/// Finds the AI pack: an explicit path from Settings first, otherwise next to the
/// app. Must be cheap and never throw. Default implementation:
/// <see cref="AiPack.ManifestAiPackLocator"/>.
/// </summary>
public interface IAiPackLocator
{
    AiPackStatus Locate();
}
