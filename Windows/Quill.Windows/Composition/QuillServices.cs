using System.Diagnostics;
using Quill.Core.Api;
using Quill.Core.Live;
using Quill.Core.Search;
using Quill.Windows.Adapters.Embeddings;
using Quill.Windows.Ask;
using Quill.Windows.LocalApi;
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
        var llama = LlamaOptions(settings.Ai);
        Summaries = new SummariesModule(AiPack, Scheduler, Store, llama);
        Speakers = new SpeakersModule(Store, AiPack, Scheduler, recordingsRoot: recordingsRoot);
        Companion = new CompanionPanelModule(dispatcher);

        // Same backend options as the LLM: llama.cpp picks its native library once per process.
        Embeddings = new LlamaSharpEmbeddingModel(() => AiPack.Locate().PathOf(AiPackComponent.TextEmbedding), backend: llama);
        Ask = new AskModule(
            Store,
            Summaries.LanguageModel,
            Scheduler,
            recordingsRoot,
            Embeddings,
            () => _settings.Summaries.Language,
            () => Summaries.IsLanguageModelInstalled);
        Ask.IndexFailed += (_, exception) => Trace.TraceWarning($"quill: search indexing failed: {exception.Message}");
        AskModule.Current = Ask;
        // Off by default: StartAsync binds nothing until the user enables the API in Settings.
        Api = new LocalApiModule(Store, new HybridApiSearch(Ask.SearchAsync));
        Api.StateChanged += (_, _) =>
        {
            if (Api.State == LocalApiState.Failed)
            {
                Trace.TraceWarning($"quill: local API could not start: {Api.FailureReason}");
            }
        };
        UiStore = new NotifyingSessionStore(Store, OnSessionContentChanged);

        var summaryState = new FileSummaryStateStore(Store);
        AiServices = new SessionAiServices
        {
            Settings = () => _settings,
            Templates = () => SettingsModule.CreateTemplateCatalog(_settings).Load(),
            IsLanguageModelInstalled = () => Summaries.IsLanguageModelInstalled,
            AreSpeakersAvailable = () => Speakers.IsAvailable,
            BuildSummary = BuildFinalSummaryAsync,
            ResolveSpeakers = (sessionId, token) => Speakers.ResolveSpeakersAsync(sessionId, cancellationToken: token),
            LoadFinalTranscript = (sessionId, token) => Store.LoadTranscriptAsync(sessionId, Core.Domain.TranscriptPass.Final, token),
            ReassignSegment = Speakers.ReassignAsync,
            AutoExport = (sessionId, token) => SettingsModule.AutoExportAsync(Store, sessionId, token),
            IsSummaryOutdated = async (sessionId, token) =>
                (await summaryState.LoadStateAsync(sessionId, token).ConfigureAwait(false)).Outdated,
        };
        SessionActions = new AppSessionActions(AiServices) { Voices = new SpeakersVoiceActions(Speakers) };
        PostTranscription = new PostTranscriptionPipeline(new AppPostTranscriptionSteps(AiServices), summaryState);
        SettingsModule.Saved += OnSettingsSaved;
        ApiSettingsStore.Saved += OnApiSettingsSaved;
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

    /// <summary>Text embeddings for semantic search (AI pack TextEmbedding GGUF, loaded on first use).</summary>
    public LlamaSharpEmbeddingModel Embeddings { get; }

    /// <summary>Search index + Ask (F10); also <see cref="AskModule.Current"/> for the views.</summary>
    public AskModule Ask { get; }

    /// <summary>Local API, webhooks and MCP (F12). Off unless enabled in Settings.</summary>
    public LocalApiModule Api { get; }

    /// <summary>The store the main window uses: saves made there re-index the meeting.</summary>
    public ISessionStore UiStore { get; }

    /// <summary>
    /// Starts the local API (no-op when disabled) and the search index (loads it, then indexes new
    /// or changed meetings in the background). Call once at startup; never throws.
    /// </summary>
    public async Task StartBackgroundAsync()
    {
        try
        {
            await Api.StartAsync(Lifetime).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Trace.TraceWarning($"quill: local API start failed: {exception.Message}");
        }

        try
        {
            await Ask.StartAsync(Lifetime).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Trace.TraceWarning($"quill: search index unavailable: {exception.Message}");
        }
    }

    /// <summary>A recording stopped and its audio is on disk.</summary>
    public void OnSessionRecorded(string sessionId) => Publish(ApiEventTypes.SessionRecorded, sessionId);

    /// <summary>The final transcript (transcript.json) was written.</summary>
    public void OnTranscriptCompleted(string sessionId)
    {
        Publish(ApiEventTypes.TranscriptCompleted, sessionId);
        IndexInBackground(sessionId);
    }

    /// <summary>Notes, transcript edits or summary state changed: re-index (cheap when nothing changed).</summary>
    public void OnSessionContentChanged(string sessionId) => IndexInBackground(sessionId);

    public void OnSessionDeleted(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        _ = RunQuietlyAsync(() => Ask.RemoveSessionAsync(sessionId, Lifetime), $"removing {sessionId} from the search index");
    }

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
        ApiSettingsStore.Saved -= OnApiSettingsSaved;
        _lifetime.Cancel();
        try
        {
            _ = Companion.CloseAsync();
        }
        catch (InvalidOperationException)
        {
            // The dispatcher is already gone; the panel went with it.
        }

        // Bounded: the socket must be released, but a stuck background pass must not block exit.
        WaitQuietly(Api.DisposeAsync(), "local API");
        WaitQuietly(Ask.DisposeAsync(), "search index");
        Embeddings.Dispose();
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

    private async void OnApiSettingsSaved(object? sender, ApiSettings saved)
    {
        try
        {
            await Api.RestartAsync(Lifetime).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // OperationCanceledException (shutdown) and ObjectDisposedException land here too.
            Trace.TraceWarning($"quill: local API restart failed: {exception.Message}");
        }
    }

    /// <summary>Every final summary save (pipeline or "Regenerar") goes through here.</summary>
    private async Task<SummaryOutcome> BuildFinalSummaryAsync(string sessionId, SummaryOptions options, CancellationToken token)
    {
        var outcome = await Summaries.FinalSummaries.BuildAsync(sessionId, options, cancellationToken: token).ConfigureAwait(false);
        if (outcome.Status == SummaryStatus.Completed)
        {
            Publish(ApiEventTypes.SummaryCompleted, sessionId);
            IndexInBackground(sessionId);
        }

        return outcome;
    }

    private void Publish(string type, string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        try
        {
            Api.Publish(type, sessionId);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Trace.TraceWarning($"quill: webhook event {type} not queued: {exception.Message}");
        }
    }

    private void IndexInBackground(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || _disposed != 0)
        {
            return;
        }

        _ = RunQuietlyAsync(() => Ask.IndexSessionAsync(sessionId, Lifetime), $"indexing {sessionId}");
    }

    private static async Task RunQuietlyAsync(Func<Task> work, string what)
    {
        try
        {
            await Task.Run(work).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutting down; the next start catches up.
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Trace.TraceWarning($"quill: {what} failed: {exception.Message}");
        }
    }

    private static void WaitQuietly(ValueTask pending, string what)
    {
        try
        {
            if (!pending.AsTask().Wait(TimeSpan.FromSeconds(5)))
            {
                Trace.TraceWarning($"quill: {what} did not stop in time");
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Trace.TraceWarning($"quill: stopping the {what} failed: {exception.Message}");
        }
    }

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
