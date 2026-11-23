using Quill.Core.Ask;
using Quill.Core.Ports;
using Quill.Core.Search;

namespace Quill.Windows.Ask;

/// <summary>What the Ask and search views need; <see cref="AskModule"/> in the app, fakes in tests.</summary>
internal interface IAskService
{
    /// <summary>False when the AI pack has no language model: the Ask box shows "install the AI pack".</summary>
    bool CanAsk { get; }

    Task<AskAnswer> AskMeetingAsync(string sessionId, string question, IProgress<string>? tokens, CancellationToken cancellationToken);

    Task<AskAnswer> AskLibraryAsync(string question, IProgress<string>? tokens, CancellationToken cancellationToken);

    Task<SearchResponse> SearchAsync(SearchQuery query, CancellationToken cancellationToken);

    /// <summary>Shows the meeting and plays it from <paramref name="at"/>. Fire and forget.</summary>
    void OpenAt(string sessionId, TimeSpan at);
}

/// <summary>
/// F10 wiring: semantic search index + Ask, behind <see cref="IAskService"/>.
/// <para>
/// The index lives in "{recordingsRoot}/.quill-index/" (catalog.json + manifest.json + vectors.bin).
/// <see cref="StartAsync"/> loads it and indexes new or changed meetings in the background;
/// <see cref="IndexSessionAsync"/> after a final transcript, summary or notes change, and
/// <see cref="RemoveSessionAsync"/> on delete keep it current. Indexing passes are serialized.
/// Without an embedding model search is lexical-only; without a language model
/// <see cref="CanAsk"/> is false but search still works.
/// </para>
/// The XAML views find the module through <see cref="Current"/>, set once by the composition root.
/// </summary>
internal sealed class AskModule : IAskService, IAsyncDisposable
{
    private readonly ISessionStore _store;
    private readonly ILanguageModel? _languageModel;
    private readonly IInferenceScheduler _scheduler;
    private readonly IEmbeddingModel? _embeddingModel;
    private readonly IEmbeddingIndex? _embeddingIndex;
    private readonly string _indexDirectory;
    private readonly Func<string> _language;
    private readonly Func<bool> _canAsk;
    private readonly SemaphoreSlim _indexGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Lazy<Task<(SearchCatalog Catalog, HybridSearch Search, MeetingIndexer Indexer)>> _state;
    private Task _background = Task.CompletedTask;

    /// <param name="languageModel">The shared LLM; null when the AI pack has none.</param>
    /// <param name="embeddingModel">Usually a <c>LlamaSharpEmbeddingModel</c> on the AI pack's TextEmbedding GGUF; null for lexical-only search.</param>
    /// <param name="language">Answer language code, read on every question (settings may change).</param>
    /// <param name="canAsk">Live "is the LLM installed" check; defaults to <c>languageModel != null</c>.</param>
    public AskModule(
        ISessionStore store,
        ILanguageModel? languageModel,
        IInferenceScheduler scheduler,
        string recordingsRoot,
        IEmbeddingModel? embeddingModel,
        Func<string> language,
        Func<bool>? canAsk = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        ArgumentException.ThrowIfNullOrWhiteSpace(recordingsRoot);
        _languageModel = languageModel;
        _embeddingModel = embeddingModel;
        _language = language ?? throw new ArgumentNullException(nameof(language));
        _canAsk = canAsk ?? (() => languageModel is not null);
        _indexDirectory = Path.Combine(recordingsRoot, FileEmbeddingIndex.DirectoryName);
        _embeddingIndex = embeddingModel is null ? null : new FileEmbeddingIndex(_indexDirectory);
        _state = new(LoadAsync);
    }

    /// <summary>The module the Ask and search views use; null shows them as unavailable.</summary>
    public static AskModule? Current { get; set; }

    /// <summary>
    /// Opens a meeting at a moment (citation chips, search results). Set by the composition root,
    /// typically: select the meeting in the main window, then <c>ISessionActions.PlayAt</c>.
    /// </summary>
    public Action<string, TimeSpan>? OpenMoment { get; set; }

    /// <summary>Raised (on a pool thread) after a background indexing pass, e.g. to log the report.</summary>
    public event EventHandler<IndexReport>? Indexed;

    /// <summary>Raised (on a pool thread) when the background pass of <see cref="StartAsync"/> fails, for logging.</summary>
    public event EventHandler<Exception>? IndexFailed;

    public bool CanAsk => _languageModel is not null && _canAsk();

    /// <summary>Loads the index and starts indexing every meeting in the background. Returns once loaded.</summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _state.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        _background = Task.Run(
            async () =>
            {
                try
                {
                    await IndexAllAsync(_lifetime.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
                {
                }
                catch (Exception exception)
                {
                    // Search keeps working on what was indexed before; the next start retries.
                    IndexFailed?.Invoke(this, exception);
                }
            },
            CancellationToken.None);
    }

    /// <summary>(Re)indexes one meeting if its content changed. Safe to call often.</summary>
    public async Task IndexSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        var session = await _store.LoadSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var (_, _, indexer) = await _state.Value.ConfigureAwait(false);
        await _indexGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (session is null)
            {
                await indexer.RemoveSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await indexer.IndexAsync([session], cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _indexGate.Release();
        }
    }

    public async Task RemoveSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        var (_, _, indexer) = await _state.Value.ConfigureAwait(false);
        await _indexGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await indexer.RemoveSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _indexGate.Release();
        }
    }

    public async Task<IndexReport> IndexAllAsync(CancellationToken cancellationToken)
    {
        var (_, _, indexer) = await _state.Value.ConfigureAwait(false);
        await _indexGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var report = await indexer.IndexAllAsync(cancellationToken).ConfigureAwait(false);
            Indexed?.Invoke(this, report);
            return report;
        }
        finally
        {
            _indexGate.Release();
        }
    }

    public async Task<AskAnswer> AskMeetingAsync(string sessionId, string question, IProgress<string>? tokens, CancellationToken cancellationToken)
    {
        var (_, search, _) = await _state.Value.ConfigureAwait(false);
        return await new MeetingAsk(_store, RequireModel(), _scheduler, Options(), search)
            .AskAsync(sessionId, question, tokens, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AskAnswer> AskLibraryAsync(string question, IProgress<string>? tokens, CancellationToken cancellationToken)
    {
        var (_, search, _) = await _state.Value.ConfigureAwait(false);
        return await new LibraryAsk(search, RequireModel(), _scheduler, Options())
            .AskAsync(question, tokens, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SearchResponse> SearchAsync(SearchQuery query, CancellationToken cancellationToken)
    {
        var (_, search, _) = await _state.Value.ConfigureAwait(false);
        return await search.SearchAsync(query, cancellationToken).ConfigureAwait(false);
    }

    public void OpenAt(string sessionId, TimeSpan at) => OpenMoment?.Invoke(sessionId, at);

    public async ValueTask DisposeAsync()
    {
        if (ReferenceEquals(Current, this))
        {
            Current = null;
        }

        await _lifetime.CancelAsync().ConfigureAwait(false);
        try
        {
            await _background.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutting down: an interrupted pass is redone on the next start.
        }

        _lifetime.Dispose();
    }

    private async Task<(SearchCatalog, HybridSearch, MeetingIndexer)> LoadAsync()
    {
        var catalog = await SearchCatalog.LoadAsync(Path.Combine(_indexDirectory, SearchCatalog.FileName), CancellationToken.None)
            .ConfigureAwait(false);
        return (
            catalog,
            new HybridSearch(catalog, _embeddingIndex, _embeddingModel, _scheduler),
            new MeetingIndexer(_store, catalog, _embeddingIndex, _embeddingModel, _scheduler));
    }

    private ILanguageModel RequireModel() =>
        CanAsk ? _languageModel! : throw new Quill.Windows.UI.AiPackMissingException();

    private AskOptions Options() => new() { Language = _language() };
}
