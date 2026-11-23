using Quill.Core.Ports;
using Quill.Core.Search;

namespace Quill.Core.Ask;

/// <summary>
/// "Preguntar a todas las reuniones": hybrid search picks the top passages across every indexed
/// meeting (optionally within a date range), they are packed into the model context and the
/// model answers citing "[sessionId@hh:mm:ss]". When nothing matches, the answer is "not found"
/// without calling the model. Runs at <see cref="InferencePriority.Interactive"/>.
/// </summary>
public sealed class LibraryAsk
{
    private readonly HybridSearch _search;
    private readonly ILanguageModel _model;
    private readonly IInferenceScheduler _scheduler;
    private readonly AskOptions _options;

    public LibraryAsk(HybridSearch search, ILanguageModel model, IInferenceScheduler scheduler, AskOptions? options = null)
    {
        _search = search ?? throw new ArgumentNullException(nameof(search));
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _options = options ?? new AskOptions();
    }

    public async Task<AskAnswer> AskAsync(
        string question,
        IProgress<string>? tokens,
        CancellationToken cancellationToken,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);
        var response = await _search.SearchAsync(new SearchQuery(question, _options.TopK, From: from, To: to), cancellationToken)
            .ConfigureAwait(false);
        var ranked = response.Results.Select(result => new AskSource(result.Passage, result.SessionTitle, result.Started));
        var sources = AskRunner.Pack(ranked, AskRunner.PassageBudget(_model, _options, question, singleMeeting: false));

        // Passages of the same meeting next to each other, in time order, read better.
        var ordered = sources
            .GroupBy(source => source.Passage.SessionId, StringComparer.Ordinal)
            .SelectMany(group => group.OrderBy(source => source.Passage.Start ?? TimeSpan.MaxValue))
            .ToList();
        return await AskRunner.RunAsync(_model, _scheduler, _options, question, ordered, null, tokens, cancellationToken)
            .ConfigureAwait(false);
    }
}
