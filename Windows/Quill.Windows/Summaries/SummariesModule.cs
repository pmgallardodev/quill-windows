using Quill.Core.Domain;
using Quill.Core.Ports;
using Quill.Core.Summaries;
using Quill.Core.Templates;

namespace Quill.Windows.Summaries;

/// <summary>
/// Composition root for summaries: one shared <see cref="LlamaSharpLanguageModel"/> (model path
/// from the AI pack) behind the Core use cases. The UI talks to these use cases only. The model
/// loads on first use; while the AI pack has no model, every use case returns
/// <see cref="SummaryStatus.AiPackMissing"/> and recording keeps working.
/// </summary>
public sealed class SummariesModule : IDisposable
{
    private readonly IAiPackLocator _aiPack;
    private readonly IInferenceScheduler _scheduler;
    private readonly ISessionStore _store;
    private readonly IClock _clock;
    private readonly LlamaSharpLanguageModel _model;

    public SummariesModule(
        IAiPackLocator aiPack,
        IInferenceScheduler scheduler,
        ISessionStore store,
        LlamaSharpOptions? options = null,
        IClock? clock = null)
    {
        _aiPack = aiPack ?? throw new ArgumentNullException(nameof(aiPack));
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _clock = clock ?? SystemClock.Instance;
        _model = new LlamaSharpLanguageModel(_aiPack, options);
        FinalSummaries = new FinalSummaryBuilder(_model, _scheduler, _store, _clock);
        CatchUp = new CatchUpSummarizer(_model, _scheduler, _clock);
    }

    public ILanguageModel LanguageModel => _model;

    /// <summary>Post-meeting summary (and re-summarize after transcript edits).</summary>
    public FinalSummaryBuilder FinalSummaries { get; }

    /// <summary>"What did I miss?" during a recording.</summary>
    public CatchUpSummarizer CatchUp { get; }

    /// <summary>Built-in templates; F9 layers user templates on top.</summary>
    public IReadOnlyList<Template> Templates => DefaultTemplates.All;

    /// <summary>Whether the AI pack currently provides a language model file (cheap, re-checked each call).</summary>
    public bool IsLanguageModelInstalled => _aiPack.Locate().Has(AiPackComponent.LanguageModel);

    /// <summary>A live summary loop for one recording. Start it with the session id; stop it when recording ends.</summary>
    public LiveSummaryLoop CreateLiveLoop(TimeSpan? interval = null) =>
        new(_model, _scheduler, _store, _clock, interval);

    public void Dispose() => _model.Dispose();
}
