using Quill.Core.Domain;
using Quill.Core.Ports;
using Quill.Core.Transcripts;

namespace Quill.Core.Summaries;

/// <summary>
/// "¿Qué me perdí?": a short structured recap (executive summary, decisions, tasks, open
/// questions) of the last minutes of the live transcript. User-requested, so it runs at
/// <see cref="InferencePriority.Interactive"/>, ahead of live summary refreshes. Not saved.
/// </summary>
public sealed class CatchUpSummarizer
{
    public const string TemplateName = "catch-up";

    public static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(5);

    private readonly ILanguageModel _model;
    private readonly IInferenceScheduler _scheduler;
    private readonly IClock _clock;

    public CatchUpSummarizer(ILanguageModel model, IInferenceScheduler scheduler, IClock? clock = null)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _clock = clock ?? SystemClock.Instance;
    }

    /// <param name="transcript">Live transcript so far.</param>
    /// <param name="window">How far back to look; default <see cref="DefaultWindow"/>.</param>
    /// <param name="now">Current recording offset; default the end of the last segment.</param>
    public async Task<SummaryOutcome> SummarizeAsync(
        IReadOnlyList<TranscriptSegment> transcript,
        SummaryOptions options,
        TimeSpan? window = null,
        TimeSpan? now = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transcript);
        ArgumentNullException.ThrowIfNull(options);
        var span = window ?? DefaultWindow;
        var recent = TranscriptWindow.Last(transcript, span, now);

        var system = SummaryPrompts.System(SummaryPromptKind.CatchUp, options, SummarySections.Live, []);
        var maxOutput = SummaryPrompts.MaxOutputTokens(SummaryPromptKind.CatchUp, SummaryLevel.Brief);
        var chunks = TranscriptChunker.Chunk(recent, SummaryGeneration.ContentBudget(_model, system, maxOutput));
        if (chunks.Count == 0)
        {
            return SummaryOutcome.NoTranscript;
        }

        // Too much speech for one request: the most recent part matters most.
        var latest = chunks[^1];
        var request = new LanguageModelRequest(system, SummaryPrompts.CatchUp(latest.Text, span), maxOutput);
        try
        {
            var summary = await _scheduler.RunAsync(
                InferencePriority.Interactive,
                token => SummaryGeneration.GenerateAsync(_model, request, MeetingSummarySchema.For(SummaryKind.Live), token),
                cancellationToken).ConfigureAwait(false);

            var validated = CitationValidator.Validate(SummaryGeneration.Deduplicate(summary), latest.Segments);
            var recap = validated.Summary with
            {
                Meta = SummaryGeneration.Meta(_model, options, _clock, SummaryKind.Live, TemplateName) with { Level = SummaryLevel.Brief },
            };
            return new SummaryOutcome(SummaryStatus.Completed, recap, validated.Dropped);
        }
        catch (LanguageModelUnavailableException exception)
        {
            return SummaryOutcome.AiPackMissing(exception.Message);
        }
        catch (SummaryGenerationException exception)
        {
            return SummaryOutcome.Failed(exception.Message);
        }
    }
}
