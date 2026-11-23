using LLama;
using LLama.Common;
using LLama.Native;
using Quill.Core.Ports;
using Quill.Core.Search;
using Quill.Windows.Summaries;

namespace Quill.Windows.Adapters.Embeddings;

/// <summary>Tuning for <see cref="LlamaSharpEmbeddingModel"/>.</summary>
public sealed record LlamaSharpEmbeddingOptions
{
    /// <summary>
    /// Context (and batch) size in tokens. Encoder models embed one text per batch, so this caps
    /// the passage length; longer texts are cut. 1024 covers a 90 s passage comfortably.
    /// </summary>
    public int ContextTokens { get; init; } = 1024;

    /// <summary>
    /// Layers on the GPU. 0 (default) keeps the small embedding model on the CPU so it never
    /// competes with Whisper or the LLM for the integrated GPU's memory.
    /// </summary>
    public int GpuLayers { get; init; }

    /// <summary>Prepended to every text, e.g. "passage: " for E5 models. Empty for BGE-M3 (recommended).</summary>
    public string TextPrefix { get; init; } = string.Empty;
}

/// <summary>
/// <see cref="IEmbeddingModel"/> over LLamaSharp's <see cref="LLamaEmbedder"/> on the AI pack's
/// <see cref="AiPackComponent.TextEmbedding"/> GGUF. Recommended model: BGE-M3 (multilingual,
/// Spanish included, 1024 dimensions, ~0.6 GB as Q8_0 GGUF, CLS pooling read from the GGUF).
/// The file is resolved and loaded on first use, so installing the pack later works without a
/// restart; a missing or unloadable file throws <see cref="EmbeddingModelUnavailableException"/>,
/// which search treats as "lexical only". Vectors are L2-normalized. Calls run one at a time.
/// </summary>
public sealed class LlamaSharpEmbeddingModel : IEmbeddingModel, IDisposable
{
    private readonly Func<string?> _resolveModelPath;
    private readonly LlamaSharpEmbeddingOptions _options;
    private readonly LlamaSharpOptions _backend;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private LLamaWeights? _weights;
    private LLamaEmbedder? _embedder;
    private string? _loadedPath;
    private bool _disposed;

    /// <param name="resolveModelPath">Returns the GGUF path, or null when the AI pack has no text embedding model.</param>
    /// <param name="backend">
    /// The LLM's options. llama.cpp's native library is chosen once per process; whichever model
    /// loads first picks it, so the embedder applies the same Vulkan/CPU choice as the LLM.
    /// </param>
    public LlamaSharpEmbeddingModel(
        Func<string?> resolveModelPath,
        LlamaSharpEmbeddingOptions? options = null,
        LlamaSharpOptions? backend = null)
    {
        _resolveModelPath = resolveModelPath ?? throw new ArgumentNullException(nameof(resolveModelPath));
        _options = options ?? new LlamaSharpEmbeddingOptions();
        _backend = backend ?? new LlamaSharpOptions();
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.ContextTokens, 128, nameof(options));
    }

    public string ModelName => LlamaSharpLanguageModel.NameFromPath(_loadedPath ?? SafeResolve()) ?? "unavailable";

    /// <summary>0 until the model has loaded (first <see cref="EmbedAsync"/>).</summary>
    public int Dimensions => _embedder?.EmbeddingSize ?? 0;

    public async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(texts);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var embedder = await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            var vectors = new List<float[]>(texts.Count);
            foreach (var text in texts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var input = Truncate(_options.TextPrefix + (text ?? string.Empty));
                var output = await embedder.GetEmbeddings(input, cancellationToken).ConfigureAwait(false);
                vectors.Add(VectorMath.Normalize(Pool(output, embedder.EmbeddingSize)));
            }

            return vectors;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _gate.Wait();
        try
        {
            _disposed = true;
            _embedder?.Dispose();
            _weights?.Dispose();
            _embedder = null;
            _weights = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// One vector per text. Models whose GGUF pools (BGE, E5) return one vector; a model without
    /// pooling returns one per token, which is mean-pooled here.
    /// </summary>
    internal static float[] Pool(IReadOnlyList<float[]> output, int dimensions)
    {
        if (output.Count == 1)
        {
            return output[0];
        }

        var mean = new float[dimensions];
        foreach (var vector in output)
        {
            for (var index = 0; index < Math.Min(dimensions, vector.Length); index++)
            {
                mean[index] += vector[index] / output.Count;
            }
        }

        return mean;
    }

    /// <summary>Character cut at roughly the context size (about 3 characters per token for Spanish).</summary>
    private string Truncate(string text)
    {
        var maxCharacters = (_options.ContextTokens - 8) * 3;
        return text.Length <= maxCharacters ? text : text[..maxCharacters];
    }

    private async Task<LLamaEmbedder> EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_embedder is not null)
        {
            return _embedder;
        }

        var path = SafeResolve();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw new EmbeddingModelUnavailableException(
                string.IsNullOrWhiteSpace(path) ? "the AI pack has no text embedding model" : $"text embedding model not found: {path}");
        }

        ConfigureNativeLibrary(LlamaBackendPlan.For(_backend));
        var parameters = new ModelParams(path)
        {
            ContextSize = (uint)_options.ContextTokens,
            BatchSize = (uint)_options.ContextTokens,
            UBatchSize = (uint)_options.ContextTokens,
            Embeddings = true,
            GpuLayerCount = Math.Max(0, _options.GpuLayers),
        };

        try
        {
            _weights = await LLamaWeights.LoadFromFileAsync(parameters, cancellationToken).ConfigureAwait(false);
            _embedder = new LLamaEmbedder(_weights, parameters);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _weights?.Dispose();
            _weights = null;
            throw new EmbeddingModelUnavailableException($"text embedding model could not be loaded: {exception.Message}", exception);
        }

        _loadedPath = path;
        return _embedder;
    }

    /// <summary>Same process-wide backend choice as <see cref="LlamaSharpLanguageModel"/>; ignored once loaded.</summary>
    private static void ConfigureNativeLibrary(LlamaBackendPlan plan)
    {
        try
        {
            NativeLibraryConfig.All.WithVulkan(plan.UseVulkan).WithAutoFallback(plan.AutoFallback);
        }
        catch (InvalidOperationException)
        {
            // The native library is already loaded (the LLM got there first); keep its backend.
        }
    }

    private string? SafeResolve()
    {
        try
        {
            return _resolveModelPath();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
