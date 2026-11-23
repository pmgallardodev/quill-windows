namespace Quill.Core.Pipeline;

/// <summary>Work done for a session once its final transcript exists, in this order.</summary>
public enum PostTranscriptionStep
{
    /// <summary>Echo classification + diarization (speakers.json, relabelled transcript).</summary>
    Speakers,

    /// <summary>Final summary (summary.json).</summary>
    Summary,

    /// <summary>Markdown auto-export, when enabled in the settings.</summary>
    Export,
}

public enum PipelineStepStatus
{
    Completed,

    /// <summary>Not applicable (AI pack missing, export off...). Not an error.</summary>
    Skipped,

    Failed,
}

/// <param name="Detail">Why a step was skipped or failed; free text for logs and the status line.</param>
public sealed record PipelineStepResult(PostTranscriptionStep Step, PipelineStepStatus Status, string? Detail = null)
{
    public static PipelineStepResult Completed(PostTranscriptionStep step, string? detail = null) => new(step, PipelineStepStatus.Completed, detail);

    public static PipelineStepResult Skipped(PostTranscriptionStep step, string? detail = null) => new(step, PipelineStepStatus.Skipped, detail);

    public static PipelineStepResult Failed(PostTranscriptionStep step, string? detail) => new(step, PipelineStepStatus.Failed, detail);
}

public sealed record PostTranscriptionReport(string SessionId, IReadOnlyList<PipelineStepResult> Steps)
{
    public bool HasFailures => Steps.Any(step => step.Status == PipelineStepStatus.Failed);

    public PipelineStepResult? Of(PostTranscriptionStep step) => Steps.FirstOrDefault(result => result.Step == step);
}

/// <summary>
/// The steps the pipeline runs. Implementations do their own inference scheduling (the Core use
/// cases already submit their model work at <c>InferencePriority.Background</c>); the pipeline must
/// never wrap a step in a scheduler job, since a job waiting for another job on the same
/// serialized scheduler would deadlock.
/// </summary>
public interface IPostTranscriptionSteps
{
    Task<PipelineStepResult> ResolveSpeakersAsync(string sessionId, CancellationToken cancellationToken);

    Task<PipelineStepResult> SummarizeAsync(string sessionId, CancellationToken cancellationToken);

    Task<PipelineStepResult> ExportAsync(string sessionId, CancellationToken cancellationToken);
}

/// <summary>
/// After the final transcript: speakers → final summary → auto-export. Speakers run first so the
/// summary cites the resolved speaker ids. A failing step never stops the next one, except that a
/// failed summary skips the export (it would export a meeting without its summary); a skipped
/// summary (no language model) still exports the transcript. Sessions run one at a time, and a
/// second request for a session already queued or running joins the first.
/// Step exceptions become <see cref="PipelineStepStatus.Failed"/>; only cancellation propagates.
/// </summary>
public sealed class PostTranscriptionPipeline
{
    private readonly IPostTranscriptionSteps _steps;
    private readonly SemaphoreSlim _turn = new(1, 1);
    private readonly Dictionary<string, Task<PostTranscriptionReport>> _active = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public PostTranscriptionPipeline(IPostTranscriptionSteps steps) =>
        _steps = steps ?? throw new ArgumentNullException(nameof(steps));

    public Task<PostTranscriptionReport> RunAsync(string sessionId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        lock (_gate)
        {
            if (_active.TryGetValue(sessionId, out var running))
            {
                return running;
            }

            var run = RunExclusiveAsync(sessionId, cancellationToken);
            if (!run.IsCompleted)
            {
                _active[sessionId] = run;
            }

            return run;
        }
    }

    private async Task<PostTranscriptionReport> RunExclusiveAsync(string sessionId, CancellationToken cancellationToken)
    {
        try
        {
            await _turn.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await RunStepsAsync(sessionId, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _turn.Release();
            }
        }
        finally
        {
            lock (_gate)
            {
                _active.Remove(sessionId);
            }
        }
    }

    private async Task<PostTranscriptionReport> RunStepsAsync(string sessionId, CancellationToken cancellationToken)
    {
        var speakers = await RunStepAsync(PostTranscriptionStep.Speakers, _steps.ResolveSpeakersAsync, sessionId, cancellationToken).ConfigureAwait(false);
        var summary = await RunStepAsync(PostTranscriptionStep.Summary, _steps.SummarizeAsync, sessionId, cancellationToken).ConfigureAwait(false);
        var export = summary.Status == PipelineStepStatus.Failed
            ? PipelineStepResult.Skipped(PostTranscriptionStep.Export, "summary failed")
            : await RunStepAsync(PostTranscriptionStep.Export, _steps.ExportAsync, sessionId, cancellationToken).ConfigureAwait(false);
        return new PostTranscriptionReport(sessionId, [speakers, summary, export]);
    }

    private static async Task<PipelineStepResult> RunStepAsync(
        PostTranscriptionStep step,
        Func<string, CancellationToken, Task<PipelineStepResult>> run,
        string sessionId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return await run(sessionId, cancellationToken).ConfigureAwait(false)
                ?? PipelineStepResult.Failed(step, "step returned no result");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return PipelineStepResult.Failed(step, exception.Message);
        }
    }
}
