using Quill.Core.Export;
using Quill.Core.Pipeline;
using Quill.Core.Summaries;
using Quill.Windows.Speakers;

namespace Quill.Windows.Composition;

/// <summary>
/// The post-transcription steps over the real modules. Each module already submits its model work
/// to the scheduler at Background priority, so nothing here is wrapped in a scheduler job.
/// </summary>
internal sealed class AppPostTranscriptionSteps(SessionAiServices services) : IPostTranscriptionSteps
{
    private readonly SessionAiServices _services = services ?? throw new ArgumentNullException(nameof(services));

    public async Task<PipelineStepResult> ResolveSpeakersAsync(string sessionId, CancellationToken cancellationToken)
    {
        const PostTranscriptionStep step = PostTranscriptionStep.Speakers;
        if (!_services.AreSpeakersAvailable())
        {
            return PipelineStepResult.Skipped(step, "diarization models not installed");
        }

        var result = await _services.ResolveSpeakers(sessionId, cancellationToken).ConfigureAwait(false);
        return result.State switch
        {
            SpeakersResolveState.Resolved => PipelineStepResult.Completed(step),
            SpeakersResolveState.Unavailable => PipelineStepResult.Skipped(step, result.Message),
            SpeakersResolveState.NoTranscript => PipelineStepResult.Skipped(step, "no final transcript"),
            _ => PipelineStepResult.Failed(step, "session not found"),
        };
    }

    public async Task<PipelineStepResult> SummarizeAsync(string sessionId, CancellationToken cancellationToken)
    {
        const PostTranscriptionStep step = PostTranscriptionStep.Summary;
        if (!_services.IsLanguageModelInstalled())
        {
            return PipelineStepResult.Skipped(step, "language model not installed");
        }

        var options = AppSessionActions.FinalSummaryOptions(_services, template: null);
        var outcome = await _services.BuildSummary(sessionId, options, cancellationToken).ConfigureAwait(false);
        return outcome.Status switch
        {
            SummaryStatus.Completed => PipelineStepResult.Completed(step),
            SummaryStatus.AiPackMissing => PipelineStepResult.Skipped(step, outcome.Error ?? "language model unavailable"),
            SummaryStatus.NoTranscript => PipelineStepResult.Skipped(step, "nothing to summarize"),
            _ => PipelineStepResult.Failed(step, outcome.Error ?? "summary failed"),
        };
    }

    public async Task<PipelineStepResult> ExportAsync(string sessionId, CancellationToken cancellationToken)
    {
        const PostTranscriptionStep step = PostTranscriptionStep.Export;
        var result = await _services.AutoExport(sessionId, cancellationToken).ConfigureAwait(false);
        return result switch
        {
            null => PipelineStepResult.Skipped(step, "auto-export off"),
            { Status: MarkdownExportStatus.Failed } => PipelineStepResult.Failed(step, result.Error),
            _ => PipelineStepResult.Completed(step, result.Path),
        };
    }
}
