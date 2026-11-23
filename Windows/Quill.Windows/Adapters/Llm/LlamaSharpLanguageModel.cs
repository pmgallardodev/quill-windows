using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using LLama;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;
using Quill.Core.Ports;
using Quill.Core.Settings;
using Quill.Core.Summaries;

namespace Quill.Windows.Summaries;

/// <summary>Tuning for <see cref="LlamaSharpLanguageModel"/>; defaults fit a 4B Q4 model on an iGPU.</summary>
public sealed record LlamaSharpOptions
{
    /// <summary>Context window in tokens. Summary use cases size their chunks below it.</summary>
    public int ContextTokens { get; init; } = 8192;

    /// <summary>
    /// Backend preference (settings <c>ai.gpu</c>). <see cref="GpuPreference.Auto"/> and
    /// <see cref="GpuPreference.Vulkan"/> load the Vulkan build of llama.cpp and fall back to the
    /// CPU builds when it cannot load; <see cref="GpuPreference.Cpu"/> never touches the GPU.
    /// </summary>
    public GpuPreference Gpu { get; init; } = GpuPreference.Auto;

    /// <summary>Vulkan device index used as the main GPU (settings <c>ai.gpu_device</c>); null lets llama.cpp choose.</summary>
    public int? GpuDevice { get; init; }

    /// <summary>Layers offloaded to the GPU unless <see cref="Gpu"/> is CPU; a large value means all.</summary>
    public int GpuLayers { get; init; } = 999;

    /// <summary>
    /// GGUF file that replaces the AI pack's model (settings <c>ai.llm_model</c>). Null or blank
    /// uses the AI pack; a path that does not exist makes the model unavailable, as a missing pack does.
    /// </summary>
    public string? ModelPath { get; init; }

    /// <summary>Fixed sampling seed so the same transcript gives the same summary.</summary>
    public uint Seed { get; init; } = 42;
}

/// <summary>How the native llama.cpp library and the model are set up for one <see cref="LlamaSharpOptions"/>.</summary>
/// <param name="UseVulkan">Ask LLamaSharp for its Vulkan build.</param>
/// <param name="AutoFallback">Let LLamaSharp fall back to the CPU builds when the requested one cannot load.</param>
/// <param name="GpuLayers">Layers to offload; 0 keeps everything on the CPU.</param>
/// <param name="MainGpu">Main GPU device index, or null for the llama.cpp default.</param>
internal readonly record struct LlamaBackendPlan(bool UseVulkan, bool AutoFallback, int GpuLayers, int? MainGpu)
{
    public static LlamaBackendPlan For(LlamaSharpOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.Gpu == GpuPreference.Cpu
            ? new LlamaBackendPlan(UseVulkan: false, AutoFallback: true, GpuLayers: 0, MainGpu: null)
            : new LlamaBackendPlan(UseVulkan: true, AutoFallback: true, Math.Max(0, options.GpuLayers), options.GpuDevice is >= 0 ? options.GpuDevice : null);
    }
}

/// <summary>
/// <see cref="ILanguageModel"/> over LLamaSharp (llama.cpp). The GGUF file comes from the AI pack
/// (or from <see cref="LlamaSharpOptions.ModelPath"/>) and is resolved and loaded on first use, so
/// installing the pack while the app runs works without a restart. A missing file, or weights or native libraries that fail to load, surface as
/// <see cref="LanguageModelUnavailableException"/>, which the use cases report as "AI pack missing".
/// Requests run one at a time; each one gets a fresh context (stateless).
/// </summary>
public sealed class LlamaSharpLanguageModel : ILanguageModel, IDisposable
{
    private static readonly object NativeGate = new();
    private static bool _nativeConfigured;

    private readonly Func<string?> _resolveModelPath;
    private readonly Func<string?>? _describeMissingModel;
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
        ArgumentNullException.ThrowIfNull(resolveModelPath);
        _options = options ?? new LlamaSharpOptions();
        var overridePath = _options.ModelPath;
        _resolveModelPath = string.IsNullOrWhiteSpace(overridePath) ? resolveModelPath : () => overridePath;
        if (_options.ContextTokens < 512)
        {
            throw new ArgumentOutOfRangeException(nameof(options), _options.ContextTokens, "context must be at least 512 tokens");
        }
    }

    /// <summary>
    /// Resolves the model through the AI pack on every load attempt. While a split model is being
    /// joined (or failed to join) the <see cref="LanguageModelUnavailableException"/> says so
    /// instead of reporting a plain missing model.
    /// </summary>
    public LlamaSharpLanguageModel(IAiPackLocator aiPack, LlamaSharpOptions? options = null)
        : this(() => aiPack.Locate().PathOf(AiPackComponent.LanguageModel), options)
    {
        ArgumentNullException.ThrowIfNull(aiPack);
        _describeMissingModel = () => MissingModelReason(aiPack.Locate());
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

    /// <summary>Why the AI pack has no usable language model, for <see cref="LanguageModelUnavailableException"/>.</summary>
    internal static string MissingModelReason(AiPackStatus status) =>
        status.PreparationOf(AiPackComponent.LanguageModel) switch
        {
            { State: ModelPreparationState.Preparing } preparing =>
                $"the AI model is being prepared ({(int)(preparing.Progress * 100)}%)",
            { State: ModelPreparationState.Failed } failed =>
                $"the AI model could not be prepared: {failed.Error}",
            _ => "the AI pack has no language model",
        };

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
                string.IsNullOrWhiteSpace(path)
                    ? SafeDescribeMissingModel() ?? "the AI pack has no language model"
                    : $"language model not found: {path}");
        }

        var plan = LlamaBackendPlan.For(_options);
        ConfigureNativeLibrary(plan);
        var parameters = new ModelParams(path)
        {
            ContextSize = (uint)_options.ContextTokens,
            GpuLayerCount = plan.GpuLayers,
        };
        if (plan.MainGpu is { } mainGpu)
        {
            parameters.MainGpu = mainGpu;
        }

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
    /// Picks the Vulkan build of llama.cpp when asked, falling back to the CPU builds (the best AVX
    /// level the processor supports is auto-detected) when Vulkan is unavailable. The choice is
    /// process-wide: it must run before the first native call, and later calls are ignored.
    /// </summary>
    private static void ConfigureNativeLibrary(LlamaBackendPlan plan)
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
                NativeLibraryConfig.All.WithVulkan(plan.UseVulkan).WithAutoFallback(plan.AutoFallback);
            }
            catch (InvalidOperationException)
            {
                // The native library is already loaded; keep whatever backend it chose.
            }
        }
    }

    private string? SafeDescribeMissingModel()
    {
        try
        {
            return _describeMissingModel?.Invoke();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
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
