using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using LLama;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;
using Quill.Core.Ports;
using Quill.Core.Summaries;

namespace Quill.Windows.Summaries;

/// <summary>Tuning for <see cref="LlamaSharpLanguageModel"/>; defaults fit a 4B Q4 model on an iGPU.</summary>
public sealed record LlamaSharpOptions
{
    /// <summary>Context window in tokens. Summary use cases size their chunks below it.</summary>
    public int ContextTokens { get; init; } = 8192;

    /// <summary>Prefer the Vulkan backend. CPU is used when false or when Vulkan cannot load.</summary>
    public bool UseGpu { get; init; } = true;

    /// <summary>Layers offloaded to the GPU when <see cref="UseGpu"/>; a large value means all.</summary>
    public int GpuLayers { get; init; } = 999;

    /// <summary>Fixed sampling seed so the same transcript gives the same summary.</summary>
    public uint Seed { get; init; } = 42;
}

/// <summary>
/// <see cref="ILanguageModel"/> over LLamaSharp (llama.cpp). The GGUF file comes from the AI pack
/// and is resolved and loaded on first use, so installing the pack while the app runs works without
/// a restart. A missing file, or weights or native libraries that fail to load, surface as
/// <see cref="LanguageModelUnavailableException"/>, which the use cases report as "AI pack missing".
/// Requests run one at a time; each one gets a fresh context (stateless).
/// </summary>
public sealed class LlamaSharpLanguageModel : ILanguageModel, IDisposable
{
    private static readonly object NativeGate = new();
    private static bool _nativeConfigured;

    private readonly Func<string?> _resolveModelPath;
    private readonly LlamaSharpOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, string> _grammars = new(StringComparer.Ordinal);

    private LLamaWeights? _weights;
    private ModelParams? _parameters;
    private string? _loadedPath;
    private bool _disposed;

    /// <param name="resolveModelPath">Returns the GGUF path, or null when the AI pack has no model.</param>
    public LlamaSharpLanguageModel(Func<string?> resolveModelPath, LlamaSharpOptions? options = null)
    {
        _resolveModelPath = resolveModelPath ?? throw new ArgumentNullException(nameof(resolveModelPath));
        _options = options ?? new LlamaSharpOptions();
        if (_options.ContextTokens < 512)
        {
            throw new ArgumentOutOfRangeException(nameof(options), _options.ContextTokens, "context must be at least 512 tokens");
        }
    }

    public string ModelName => NameFromPath(_loadedPath ?? SafeResolve()) ?? "unavailable";

    public int ContextTokens => _options.ContextTokens;

    public IAsyncEnumerable<string> GenerateAsync(LanguageModelRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return StreamAsync(request, gbnf: null, cancellationToken);
    }

    public async Task<string> GenerateJsonAsync(LanguageModelRequest request, string jsonSchema, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var grammar = _grammars.GetOrAdd(jsonSchema, JsonSchemaGbnf.Convert);
        var output = new StringBuilder();
        await foreach (var piece in StreamAsync(request, grammar, cancellationToken).ConfigureAwait(false))
        {
            output.Append(piece);
        }

        return output.ToString().Trim();
    }

    public void Dispose()
    {
        _gate.Wait();
        try
        {
            _disposed = true;
            _weights?.Dispose();
            _weights = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>"Qwen3-4B-Instruct-2507-Q4_K_M.gguf" becomes "qwen3-4b-instruct-2507-q4_k_m".</summary>
    internal static string? NameFromPath(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : Path.GetFileNameWithoutExtension(path).ToLowerInvariant();

    private async IAsyncEnumerable<string> StreamAsync(
        LanguageModelRequest request,
        string? gbnf,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var weights = await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            var executor = new StatelessExecutor(weights, _parameters!);
            var inference = new InferenceParams
            {
                MaxTokens = request.MaxOutputTokens,
                SamplingPipeline = new DefaultSamplingPipeline
                {
                    Temperature = request.Temperature,
                    Seed = _options.Seed,
                    Grammar = gbnf is null ? null : new Grammar(gbnf, "root"),
                },
            };

            await foreach (var piece in executor.InferAsync(ChatPrompt(weights, request), inference, cancellationToken).ConfigureAwait(false))
            {
                yield return piece;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string ChatPrompt(LLamaWeights weights, LanguageModelRequest request)
    {
        var template = new LLamaTemplate(weights) { AddAssistant = true };
        template.Add("system", request.SystemPrompt);
        template.Add("user", request.UserPrompt);
        return Encoding.UTF8.GetString(template.Apply());
    }

    private async Task<LLamaWeights> EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_weights is not null)
        {
            return _weights;
        }

        var path = _resolveModelPath();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw new LanguageModelUnavailableException(
                string.IsNullOrWhiteSpace(path) ? "the AI pack has no language model" : $"language model not found: {path}");
        }

        ConfigureNativeLibrary(_options.UseGpu);
        var parameters = new ModelParams(path)
        {
            ContextSize = (uint)_options.ContextTokens,
            GpuLayerCount = _options.UseGpu ? _options.GpuLayers : 0,
        };

        try
        {
            _weights = await LLamaWeights.LoadFromFileAsync(parameters, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new LanguageModelUnavailableException($"language model could not be loaded: {exception.Message}", exception);
        }

        _parameters = parameters;
        _loadedPath = path;
        return _weights;
    }

    /// <summary>
    /// Picks the Vulkan build of llama.cpp when asked, falling back to the CPU builds (AVX2/AVX...)
    /// when Vulkan is unavailable. Must run before the first native call; later calls are ignored.
    /// </summary>
    private static void ConfigureNativeLibrary(bool useGpu)
    {
        lock (NativeGate)
        {
            if (_nativeConfigured)
            {
                return;
            }

            _nativeConfigured = true;
            try
            {
                NativeLibraryConfig.All.WithVulkan(useGpu).WithAutoFallback(true);
            }
            catch (InvalidOperationException)
            {
                // The native library is already loaded; keep whatever backend it chose.
            }
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
