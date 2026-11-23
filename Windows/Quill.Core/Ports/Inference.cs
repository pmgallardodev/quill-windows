namespace Quill.Core.Ports;

/// <summary>
/// Priority of a background inference job (design §7.2). Capture threads never go through
/// the scheduler; they always have priority over every job listed here.
/// </summary>
public enum InferencePriority
{
    /// <summary>Live transcription chunks: small and latency sensitive.</summary>
    LiveTranscription = 0,

    /// <summary>User-requested work while recording, such as "What did I miss?".</summary>
    Interactive = 1,

    /// <summary>Periodic live summary refresh. May be skipped when the scheduler is behind.</summary>
    LiveSummary = 2,

    /// <summary>Post-meeting work: final transcript, diarization, final summary, embeddings.</summary>
    Background = 3,
}

/// <summary>
/// Serializes GPU/CPU-heavy inference (Whisper, LLM, diarization, embeddings) so jobs never
/// compete for the shared integrated GPU. Higher priority jobs run first; jobs of equal priority
/// run in submission order.
/// </summary>
public interface IInferenceScheduler
{
    /// <summary>Queues a job and returns its result when it has run.</summary>
    Task<T> RunAsync<T>(
        InferencePriority priority,
        Func<CancellationToken, Task<T>> job,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs the job only if nothing of the same or higher priority is queued or running;
    /// otherwise returns <c>(false, default)</c> immediately. Used for skippable work such as
    /// live summary refreshes, so they are dropped instead of queued without bound.
    /// </summary>
    Task<(bool Ran, T? Result)> TryRunAsync<T>(
        InferencePriority priority,
        Func<CancellationToken, Task<T>> job,
        CancellationToken cancellationToken = default);

    /// <summary>Number of jobs queued or running, for "summary delayed" UI state.</summary>
    int PendingCount { get; }
}
