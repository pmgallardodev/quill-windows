using System.Diagnostics;
using Quill.Core.Live;
using Quill.Core.Pipeline;
using Quill.Core.Ports;
using Quill.Core.Scheduling;
using Quill.Core.Settings;
using Quill.Core.Storage;
using Quill.Core.Summaries;
using Quill.Core.Templates;
using Quill.Windows.Infrastructure;
using Quill.Windows.Speakers;
using Quill.Windows.Summaries;
using Quill.Windows.Transcription;
using Quill.Windows.UI;
using Quill.Windows.UI.Companion;
using Quill.Windows.UI.Settings;
using Dispatcher = System.Windows.Threading.Dispatcher;

namespace Quill.Windows.Composition;

/// <summary>
/// Composition root of the tray app, built once at startup from <see cref="ConfigStore.LoadSettings"/>.
/// Owns the one app-wide <see cref="InferenceScheduler"/> (live Whisper, live/final summaries,
/// diarization all go through it) and every feature module. Settings saved in the Settings window
/// apply to the next recording or summary; the language model keeps its backend until restart.
/// </summary>
internal sealed class QuillServices : IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Lazy<bool> _gpuProbe = new(GpuProbe.HasVulkanLoader);
    private volatile QuillSettings _settings;
    private int _disposed;

    public QuillServices(string recordingsRoot, QuillSettings settings, Dispatcher dispatcher)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordingsRoot);
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        Scheduler = new InferenceScheduler();
        AiPack = new SettingsAiPackLocator(() => _settings);
        Store = new FileSessionStore(recordingsRoot);
        Summaries = new SummariesModule(AiPack, Scheduler, Store, LlamaOptions(settings.Ai));
        Speakers = new SpeakersModule(Store, AiPack, Scheduler);
        Companion = new CompanionPanelModule(dispatcher);

        AiServices = new SessionAiServices
        {
            Settings = () => _settings,
            Templates = () => SettingsModule.CreateTemplateCatalog(_settings).Load(),
            IsLanguageModelInstalled = () => Summaries.IsLanguageModelInstalled,
            AreSpeakersAvailable = () => Speakers.IsAvailable,
            BuildSummary = (sessionId, options, token) => Summaries.FinalSummaries.BuildAsync(sessionId, options, cancellationToken: token),
            ResolveSpeakers = (sessionId, token) => Speakers.ResolveSpeakersAsync(sessionId, cancellationToken: token),
            LoadFinalTranscript = (sessionId, token) => Store.LoadTranscriptAsync(sessionId, Core.Domain.TranscriptPass.Final, token),
            ReassignSegment = Speakers.ReassignAsync,
            AutoExport = (sessionId, token) => SettingsModule.AutoExportAsync(Store, sessionId, token),
        };
        SessionActions = new AppSessionActions(AiServices);
        PostTranscription = new PostTranscriptionPipeline(new AppPostTranscriptionSteps(AiServices));
        SettingsModule.Saved += OnSettingsSaved;
    }

    public QuillSettings Settings => _settings;

    public InferenceScheduler Scheduler { get; }

    /// <summary>AI pack from "ai.pack_path" with the "ai.llm_model" / "ai.live_whisper_model" overrides.</summary>
    public IAiPackLocator AiPack { get; }

    public FileSessionStore Store { get; }

    public SummariesModule Summaries { get; }

    public SpeakersModule Speakers { get; }

    public CompanionPanelModule Companion { get; }

    public SessionAiServices AiServices { get; }

    /// <summary>Real AI actions for the main window (wrapped there by PlaybackSessionActions).</summary>
    public ISessionActions SessionActions { get; }

    public PostTranscriptionPipeline PostTranscription { get; }

    /// <summary>Cancelled on shutdown: background pipelines stop with the app.</summary>
    public CancellationToken Lifetime => _lifetime.Token;

    /// <summary>A usable GPU for the live summary gate (cheap probe, cached; "cpu" setting means none).</summary>
    public bool GpuAvailable => LiveSummaryPolicy.GpuUsable(_settings.Ai, () => _gpuProbe.Value);

    public TemplateCatalog CreateTemplateCatalog() => SettingsModule.CreateTemplateCatalog(_settings);

    /// <summary>
    /// The live parts of a new recording. Live text needs the live Whisper model; the live summary
    /// additionally needs the language model and the setting/GPU gate (design §7.2). Never throws.
    /// </summary>
    public LiveRecording CreateLiveRecording()
    {
        var transcription = CreateLiveTranscription();
        LiveSummaryLoop? loop = null;
        try
        {
            var summaries = _settings.Summaries;
            if (transcription is not null &&
                LiveSummaryPolicy.ShouldRun(summaries, GpuAvailable, Summaries.IsLanguageModelInstalled))
            {
                loop = Summaries.CreateLiveLoop(summaries.LiveCadence);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Trace.TraceWarning($"quill: live summary unavailable: {exception.Message}");
        }

        return new LiveRecording(transcription, loop);
    }

    /// <summary>Options of the live summary and "What did I miss?": the summary language from the settings.</summary>
    public SummaryOptions LiveSummaryOptions() => new() { Language = _settings.Summaries.Language };

    /// <summary>"¿Qué me perdí?" over the live transcript, or null when no language model is installed.</summary>
    public CatchUpHandler? CreateCatchUp(LiveTranscriptBuffer? transcript)
    {
        if (!Summaries.IsLanguageModelInstalled)
        {
            return null;
        }

        return (window, token) => transcript is null
            ? Task.FromResult(SummaryOutcome.NoTranscript)
            : Summaries.CatchUp.SummarizeAsync(transcript.Snapshot(), LiveSummaryOptions(), window, cancellationToken: token);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        SettingsModule.Saved -= OnSettingsSaved;
        _lifetime.Cancel();
        try
        {
            _ = Companion.CloseAsync();
        }
        catch (InvalidOperationException)
        {
            // The dispatcher is already gone; the panel went with it.
        }

        Summaries.Dispose();
        Scheduler.Dispose();
        _lifetime.Dispose();
    }

    private LiveTranscriptionModule? CreateLiveTranscription()
    {
        try
        {
            var config = ConfigStore.Load().Transcription;
            var language = string.IsNullOrWhiteSpace(config.Language) ? "auto" : config.Language.Trim();
            var threads = WhisperStreamingTranscriber.LiveThreads(Environment.ProcessorCount);
            return LiveTranscriptionModule.TryCreate(
                AiPack.Locate(),
                modelPath => WhisperStreamingTranscriber.Create(modelPath, language, threads, Scheduler));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Trace.TraceWarning($"quill: live transcription unavailable: {exception.Message}");
            return null;
        }
    }

    private void OnSettingsSaved(object? sender, QuillSettings saved) => _settings = saved;

    private static LlamaSharpOptions LlamaOptions(AiSettings ai) => new()
    {
        Gpu = ai.Gpu,
        GpuDevice = ai.GpuDevice,
        // Same rule as SettingsAiPackLocator: an override whose file is missing falls back to the
        // pack instead of making the model unavailable.
        ModelPath = ExistingFile(ai.LanguageModelPath),
    };

    private static string? ExistingFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var expanded = Environment.ExpandEnvironmentVariables(path.Trim());
        try
        {
            return File.Exists(expanded) ? expanded : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Re-reads the settings on every call, so a changed pack path needs no restart.</summary>
    private sealed class SettingsAiPackLocator(Func<QuillSettings> settings) : IAiPackLocator
    {
        public AiPackStatus Locate()
        {
            try
            {
                var current = settings();
                return AiPackOverrides.Apply(
                    ConfigStore.CreateAiPackLocator(current).Locate(),
                    Expand(current.Ai.LanguageModelPath),
                    Expand(current.Ai.LiveWhisperModelPath),
                    File.Exists);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // The locator contract: cheap and never throws.
                Trace.TraceWarning($"quill: AI pack detection failed: {exception.Message}");
                return AiPackStatus.NotInstalled;
            }
        }

        private static string? Expand(string? path) =>
            string.IsNullOrWhiteSpace(path) ? null : Environment.ExpandEnvironmentVariables(path);
    }
}

/// <summary>Cheap, side-effect free check for a GPU the inference backends can use.</summary>
internal static class GpuProbe
{
    /// <summary>
    /// True when the Vulkan loader is installed (any Vulkan-capable driver ships it). It does not
    /// prove the GPU is fast, only that the Vulkan backend can load; false on any failure.
    /// </summary>
    public static bool HasVulkanLoader()
    {
        try
        {
            return File.Exists(Path.Combine(Environment.SystemDirectory, "vulkan-1.dll"));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return false;
        }
    }
}
