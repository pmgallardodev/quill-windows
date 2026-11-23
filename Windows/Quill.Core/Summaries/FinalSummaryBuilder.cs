using Quill.Core.Domain;
using Quill.Core.Ports;
using Quill.Core.Storage;
using Quill.Core.Transcripts;

namespace Quill.Core.Summaries;

/// <summary>
/// Final summary of a recorded meeting (design §7.1): transcript + owner notes + template →
/// schema-constrained JSON → citation validation → summary.json. Long transcripts are
/// map-reduced: each part that fits the model context is summarized on its own, then the
/// partial summaries are merged (and deduplicated) until one remains. Every model call is a
/// separate <see cref="InferencePriority.Background"/> job, so live and interactive work can
/// run between steps.
/// </summary>
public sealed class FinalSummaryBuilder
{
    private readonly ILanguageModel _model;
    private readonly IInferenceScheduler _scheduler;
    private readonly ISessionStore _store;
    private readonly IClock _clock;
    private readonly ISummaryStateStore? _state;

    /// <param name="state">
    /// Backup + outdated sidecars. Defaults to <see cref="FileSummaryStateStore"/> when
    /// <paramref name="store"/> is a <see cref="FileSessionStore"/>; otherwise no sidecars are written.
    /// </param>
    public FinalSummaryBuilder(
        ILanguageModel model,
        IInferenceScheduler scheduler,
        ISessionStore store,
        IClock? clock = null,
        ISummaryStateStore? state = null)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _clock = clock ?? SystemClock.Instance;
        _state = state ?? (store is FileSessionStore files ? new FileSummaryStateStore(files, _clock) : null);
    }

    /// <summary>
    /// Summarizes the session's final transcript (transcript.json) with its notes.md and saves
    /// summary.json. Never throws for a missing model or bad model output: see <see cref="SummaryOutcome.Status"/>.
    /// Regenerating keeps the user's state: done flags are carried over by
    /// <see cref="SummaryUserStateMerger"/>, the replaced summary is kept as summary.prev.json and the
    /// "outdated" flag is cleared.
    /// </summary>
    /// <param name="progress">0..1, reported after each model call.</param>
    public async Task<SummaryOutcome> BuildAsync(
        string sessionId,
        SummaryOptions options,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var transcript = await _store.LoadTranscriptAsync(sessionId, TranscriptPass.Final, cancellationToken).ConfigureAwait(false);
        var notes = NotesDocument.Parse(await _store.LoadNotesAsync(sessionId, cancellationToken).ConfigureAwait(false));

        var outcome = await SummarizeAsync(transcript, notes, options, progress, cancellationToken).ConfigureAwait(false);
        if (outcome.Status != SummaryStatus.Completed)
        {
            return outcome;
        }

        var summary = outcome.Summary!;
        var previous = await _store.LoadSummaryAsync(sessionId, SummaryKind.Final, cancellationToken).ConfigureAwait(false);
        if (previous is not null)
        {
            summary = SummaryUserStateMerger.Merge(previous, summary).Summary;
            await TrySidecarAsync(() => _state!.BackupAsync(sessionId, previous, cancellationToken)).ConfigureAwait(false);
        }

        await _store.SaveSummaryAsync(sessionId, summary, cancellationToken).ConfigureAwait(false);
        await TrySidecarAsync(() => _state!.ClearOutdatedAsync(sessionId, cancellationToken)).ConfigureAwait(false);
        return outcome with { Summary = summary };
    }

    /// <summary>Sidecars are a safety net: an I/O failure there must not lose the new summary.</summary>
    private async Task TrySidecarAsync(Func<Task> write)
    {
        if (_state is null)
        {
            return;
        }

        try
        {
            await write().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning($"quill: summary sidecar write failed: {exception.Message}");
        }
    }

    /// <summary>Same pipeline without storage, e.g. for an imported transcript or a preview.</summary>
    public async Task<SummaryOutcome> SummarizeAsync(
        IReadOnlyList<TranscriptSegment> transcript,
        IReadOnlyList<Note> notes,
        SummaryOptions options,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transcript);
        ArgumentNullException.ThrowIfNull(notes);
        ArgumentNullException.ThrowIfNull(options);

        var sections = options.FinalSections;
        var schema = MeetingSummarySchema.For(sections);
        var maxOutput = SummaryPrompts.MaxOutputTokens(SummaryPromptKind.Final, options.ResolvedLevel);

        // Size chunks against the longer (partial) system prompt so every part fits.
        var partialSystem = SummaryPrompts.System(SummaryPromptKind.Partial, options, sections, notes);
        var chunks = TranscriptChunker.Chunk(transcript, SummaryGeneration.ContentBudget(_model, partialSystem, maxOutput));
        if (chunks.Count == 0)
        {
            return SummaryOutcome.NoTranscript;
        }

        try
        {
            MeetingSummary summary;
            if (chunks.Count == 1)
            {
                var system = SummaryPrompts.System(SummaryPromptKind.Final, options, sections, notes);
                summary = await RunAsync(system, SummaryPrompts.Transcript(chunks[0].Text), maxOutput, schema, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                var partials = new List<MeetingSummary>();
                for (var index = 0; index < chunks.Count; index++)
                {
                    var user = SummaryPrompts.Transcript(chunks[index].Text, index + 1, chunks.Count);
                    partials.Add(await RunAsync(partialSystem, user, maxOutput, schema, cancellationToken).ConfigureAwait(false));
                    progress?.Report(0.8 * (index + 1) / chunks.Count);
                }

                summary = await MergeAsync(partials, options, sections, maxOutput, schema, cancellationToken).ConfigureAwait(false);
            }

            var validated = CitationValidator.Validate(SummaryGeneration.Deduplicate(summary), transcript);
            var final = validated.Summary with
            {
                Meta = SummaryGeneration.Meta(_model, options, _clock, SummaryKind.Final),
            };
            progress?.Report(1);
            return new SummaryOutcome(SummaryStatus.Completed, final, validated.Dropped);
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

    /// <summary>Reduce step: merges groups of partials that fit the context until one summary remains.</summary>
    private async Task<MeetingSummary> MergeAsync(
        List<MeetingSummary> partials,
        SummaryOptions options,
        IReadOnlyList<string> sections,
        int maxOutput,
        string schema,
        CancellationToken cancellationToken)
    {
        var system = SummaryPrompts.System(SummaryPromptKind.Merge, options, sections, []);
        var budget = SummaryGeneration.ContentBudget(_model, system, maxOutput);

        while (partials.Count > 1)
        {
            var groups = Group(partials, sections, budget);
            if (groups.All(group => group.Count == 1))
            {
                // Not even two partials fit one request: combine without the model rather than loop.
                return SummaryGeneration.Concatenate(partials);
            }

            var merged = new List<MeetingSummary>();
            foreach (var group in groups)
            {
                merged.Add(group.Count == 1
                    ? group[0]
                    : await RunAsync(system, SummaryPrompts.Merge(group, sections), maxOutput, schema, cancellationToken).ConfigureAwait(false));
            }

            partials = merged;
        }

        return partials[0];
    }

    private static List<List<MeetingSummary>> Group(IReadOnlyList<MeetingSummary> partials, IReadOnlyList<string> sections, int budget)
    {
        var groups = new List<List<MeetingSummary>>();
        var current = new List<MeetingSummary>();
        var tokens = 0;
        foreach (var partial in partials)
        {
            var size = TranscriptChunker.EstimateTokens(SummaryPrompts.ModelJson(partial, sections)) + 8;
            if (current.Count > 0 && tokens + size > budget)
            {
                groups.Add(current);
                current = [];
                tokens = 0;
            }

            current.Add(partial);
            tokens += size;
        }

        groups.Add(current);
        return groups;
    }

    private Task<MeetingSummary> RunAsync(string system, string user, int maxOutput, string schema, CancellationToken cancellationToken) =>
        _scheduler.RunAsync(
            InferencePriority.Background,
            token => SummaryGeneration.GenerateAsync(_model, new LanguageModelRequest(system, user, maxOutput), schema, token),
            cancellationToken);
}
