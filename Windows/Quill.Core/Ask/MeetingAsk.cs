using Quill.Core.Domain;
using Quill.Core.Ports;
using Quill.Core.Search;
using Quill.Core.Storage;
using Quill.Core.Transcripts;

namespace Quill.Core.Ask;

/// <summary>
/// "Preguntar" on one meeting. When the whole transcript (plus summary and notes) fits the model
/// context, the model gets all of it; otherwise the passages most related to the question
/// (hybrid search when the meeting is indexed, BM25 over the meeting otherwise; in time order
/// when no word matches, e.g. "¿de qué se habló?"). Uses the final transcript, or the live one
/// while the final pass is not ready. Generation runs at <see cref="InferencePriority.Interactive"/>
/// and streams through <c>tokens</c>. A missing model surfaces as
/// <see cref="Summaries.LanguageModelUnavailableException"/> from the adapter.
/// </summary>
public sealed class MeetingAsk
{
    private readonly ISessionStore _store;
    private readonly ILanguageModel _model;
    private readonly IInferenceScheduler _scheduler;
    private readonly AskOptions _options;
    private readonly HybridSearch? _search;

    public MeetingAsk(
        ISessionStore store,
        ILanguageModel model,
        IInferenceScheduler scheduler,
        AskOptions? options = null,
        HybridSearch? search = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _options = options ?? new AskOptions();
        _search = search;
    }

    public async Task<AskAnswer> AskAsync(
        string sessionId,
        string question,
        IProgress<string>? tokens,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(question);

        var transcript = await _store.LoadTranscriptAsync(sessionId, TranscriptPass.Final, cancellationToken).ConfigureAwait(false);
        if (!HasSpeech(transcript))
        {
            transcript = await _store.LoadTranscriptAsync(sessionId, TranscriptPass.Live, cancellationToken).ConfigureAwait(false);
        }

        if (!HasSpeech(transcript))
        {
            return AskAnswer.NothingFound(_options.Language);
        }

        var session = await _store.LoadSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var title = session?.DisplayTitle ?? sessionId;
        var summary = await _store.LoadSummaryAsync(sessionId, SummaryKind.Final, cancellationToken).ConfigureAwait(false);
        var notes = NotesDocument.Parse(await _store.LoadNotesAsync(sessionId, cancellationToken).ConfigureAwait(false));
        var extras = new List<Passage>();
        if (summary is not null)
        {
            extras.AddRange(PassageBuilder.FromSummary(sessionId, summary));
        }

        extras.AddRange(PassageBuilder.FromNotes(sessionId, notes));

        AskSource Source(Passage passage) => new(passage, title, session?.Started);
        var budget = AskRunner.PassageBudget(_model, _options, question, singleMeeting: true);
        var whole = WholeTranscript(sessionId, transcript);
        var everything = new[] { whole }.Concat(extras).Select(Source).ToList();
        var sources = AskRunner.Fits(everything, budget)
            ? everything
            : AskRunner.Pack(
                (await RankAsync(sessionId, question, transcript, extras, cancellationToken).ConfigureAwait(false)).Select(Source),
                budget);

        return await AskRunner.RunAsync(_model, _scheduler, _options, question, sources, sessionId, tokens, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<Passage>> RankAsync(
        string sessionId,
        string question,
        IReadOnlyList<TranscriptSegment> transcript,
        IReadOnlyList<Passage> extras,
        CancellationToken cancellationToken)
    {
        var passages = PassageBuilder.FromTranscript(sessionId, transcript).Concat(extras).ToList();

        if (_search?.Catalog.Get(sessionId) is not null)
        {
            var response = await _search.SearchAsync(
                new SearchQuery(question, _options.TopK * 2, SessionId: sessionId),
                cancellationToken).ConfigureAwait(false);
            if (response.Results.Count > 0)
            {
                return response.Results.Select(result => result.Passage).ToList();
            }
        }

        var byId = passages.ToDictionary(passage => passage.Id, StringComparer.Ordinal);
        var hits = new Bm25Index(passages.Select(passage => (passage.Id, passage.Text)))
            .Search(question, _options.TopK * 2);
        if (hits.Count > 0)
        {
            return hits.Select(hit => byId[hit.Id]).ToList();
        }

        // Nothing matches the words (a general question): summary and notes first, then the
        // transcript from the beginning, as far as the context allows.
        return extras.Concat(passages.Where(passage => passage.Kind == PassageKinds.Transcript)).ToList();
    }

    private static Passage WholeTranscript(string sessionId, IReadOnlyList<TranscriptSegment> transcript)
    {
        var lines = transcript
            .Where(segment => segment is not null && !string.IsNullOrWhiteSpace(segment.Text))
            .OrderBy(segment => segment.Start)
            .ToList();
        return new Passage(
            $"{sessionId}:{PassageKinds.Transcript}:all",
            sessionId,
            PassageKinds.Transcript,
            lines[0].Start,
            lines.Max(segment => segment.End),
            lines.Select(segment => segment.SpeakerId).Distinct(StringComparer.Ordinal).ToList(),
            string.Join('\n', lines.Select(TranscriptChunker.FormatLine)));
    }

    private static bool HasSpeech(IReadOnlyList<TranscriptSegment> transcript) =>
        transcript.Any(segment => segment is not null && !string.IsNullOrWhiteSpace(segment.Text));
}
