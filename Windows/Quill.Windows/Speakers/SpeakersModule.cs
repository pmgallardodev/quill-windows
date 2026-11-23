using Quill.Core.Ports;
using Quill.Core.Speakers;
using Quill.Windows.Adapters.Speakers;

namespace Quill.Windows.Speakers;

public enum SpeakersResolveState
{
    /// <summary>transcript.json, speakers.json and speakers.resolution.json were rewritten.</summary>
    Resolved,

    /// <summary>AI pack (or its diarization models / native runtime) missing. Nothing was changed: legacy me/them labels stay.</summary>
    Unavailable,

    SessionNotFound,

    /// <summary>No final transcript yet; run transcription first.</summary>
    NoTranscript,
}

/// <param name="Outcome">Counts when <see cref="State"/> is <see cref="SpeakersResolveState.Resolved"/>.</param>
/// <param name="Message">Diagnostic for logs when unavailable.</param>
public sealed record SpeakersResolveResult(SpeakersResolveState State, SpeakerResolutionOutcome? Outcome = null, string? Message = null);

/// <summary>
/// Composition entry point of speaker separation (F5) for the tray app and UI:
/// "Identify speakers" for a session plus the manual corrections. The AI pack is
/// located on every resolve, so installing it later needs no restart. Manual
/// operations work without the pack (they never diarize).
/// </summary>
public sealed class SpeakersModule
{
    private readonly IAiPackLocator _aiPack;
    private readonly SpeakerResolutionService _service;

    public SpeakersModule(
        ISessionStore sessions,
        IAiPackLocator aiPack,
        IInferenceScheduler scheduler,
        SpeakerResolutionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        _aiPack = aiPack ?? throw new ArgumentNullException(nameof(aiPack));
        _service = new SpeakerResolutionService(
            sessions,
            new FileSpeakerResolutionStore(sessions),
            new CrossTrackEchoClassifier(),
            new AiPackDiarizer(aiPack),
            scheduler ?? throw new ArgumentNullException(nameof(scheduler)),
            new NAudioEnergyEnvelopeReader(),
            options);
    }

    /// <summary>True when the AI pack provides both diarization models.</summary>
    public bool IsAvailable => AiPackDiarizer.Models(_aiPack.Locate()) is not null;

    /// <summary>Runs echo classification, diarization and resolution at background priority.</summary>
    public async Task<SpeakersResolveResult> ResolveSpeakersAsync(
        string sessionId,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
        {
            return new SpeakersResolveResult(SpeakersResolveState.Unavailable, Message: "AI pack diarization models not installed");
        }

        try
        {
            var outcome = await _service.ResolveAsync(sessionId, progress, cancellationToken).ConfigureAwait(false);
            return outcome.Status switch
            {
                SpeakerResolutionStatus.Resolved => new SpeakersResolveResult(SpeakersResolveState.Resolved, outcome),
                SpeakerResolutionStatus.SessionNotFound => new SpeakersResolveResult(SpeakersResolveState.SessionNotFound),
                _ => new SpeakersResolveResult(SpeakersResolveState.NoTranscript),
            };
        }
        catch (Exception exception) when (exception is DllNotFoundException or TypeInitializationException or BadImageFormatException or EntryPointNotFoundException or AiPackMissingException)
        {
            // Raised before anything is written: the transcript keeps its labels.
            return new SpeakersResolveResult(SpeakersResolveState.Unavailable, Message: exception.Message);
        }
    }

    /// <summary>Speakers with colour slot and embedding; v1 sessions list "me" and "them".</summary>
    public Task<IReadOnlyList<SpeakerProfile>> LoadSpeakersAsync(string sessionId, CancellationToken cancellationToken = default) =>
        _service.LoadSpeakersAsync(sessionId, cancellationToken);

    /// <summary>Sets or clears (null/blank) a display name.</summary>
    public Task RenameAsync(string sessionId, string speakerId, string? displayName, CancellationToken cancellationToken = default) =>
        _service.RenameAsync(sessionId, speakerId, displayName, cancellationToken);

    /// <summary>Folds <paramref name="mergedId"/> into <paramref name="keptId"/>.</summary>
    public Task MergeAsync(string sessionId, string keptId, string mergedId, CancellationToken cancellationToken = default) =>
        _service.MergeAsync(sessionId, keptId, mergedId, cancellationToken);

    /// <summary>Gives final-transcript segment <paramref name="segmentIndex"/> to "me" or "speaker-N".</summary>
    public Task ReassignAsync(string sessionId, int segmentIndex, string speakerId, CancellationToken cancellationToken = default) =>
        _service.ReassignAsync(sessionId, segmentIndex, speakerId, cancellationToken);

    private sealed class AiPackMissingException(string message) : Exception(message);

    /// <summary>Resolves the model paths at call time and delegates to <see cref="SherpaOnnxDiarizer"/>.</summary>
    private sealed class AiPackDiarizer(IAiPackLocator aiPack) : IDiarizer
    {
        public static (string Segmentation, string Embedding)? Models(AiPackStatus status) =>
            status.PathOf(AiPackComponent.DiarizationSegmentation) is { } segmentation &&
            status.PathOf(AiPackComponent.SpeakerEmbedding) is { } embedding
                ? (segmentation, embedding)
                : null;

        public Task<DiarizationResult> DiarizeAsync(DiarizationRequest request, IProgress<double>? progress, CancellationToken cancellationToken)
        {
            var models = Models(aiPack.Locate())
                ?? throw new AiPackMissingException("AI pack diarization models not installed");
            var threads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
            return new SherpaOnnxDiarizer(models.Segmentation, models.Embedding, threads)
                .DiarizeAsync(request, progress, cancellationToken);
        }
    }
}
