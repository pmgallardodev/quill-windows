using System.Security.Cryptography;
using System.Text;
using Quill.Core.Domain;
using Quill.Core.Ports;
using Quill.Core.Storage;

namespace Quill.Core.Search;

/// <summary>Outcome of one indexing pass.</summary>
/// <param name="VectorsAvailable">False when the embedding model was missing: new passages are lexical only.</param>
public sealed record IndexReport(int Indexed, int Unchanged, int Removed, bool VectorsAvailable);

/// <summary>
/// Keeps <see cref="SearchCatalog"/> and the vector index in step with the recordings. Only
/// meetings with a final transcript are indexed. A meeting is (re)indexed when its content hash
/// changed, or when it was indexed without vectors (or with another model) and an embedding
/// model is now available. Embedding runs at <see cref="InferencePriority.Background"/>. When
/// the embedding model is missing the passages still go to the catalog (lexical search works).
/// Not safe for concurrent passes; callers serialize them.
/// </summary>
public sealed class MeetingIndexer
{
    /// <summary>Passages per embedding call.</summary>
    public const int BatchSize = 16;

    private readonly ISessionStore _store;
    private readonly SearchCatalog _catalog;
    private readonly IEmbeddingIndex? _index;
    private readonly IEmbeddingModel? _model;
    private readonly IInferenceScheduler? _scheduler;
    private readonly PassageWindow _window;

    public MeetingIndexer(
        ISessionStore store,
        SearchCatalog catalog,
        IEmbeddingIndex? index,
        IEmbeddingModel? model,
        IInferenceScheduler? scheduler = null,
        PassageWindow? window = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _index = index;
        _model = index is null ? null : model;
        _scheduler = scheduler;
        _window = window ?? PassageWindow.Default;
    }

    /// <summary>Indexes every session of the store and drops the ones that disappeared.</summary>
    public async Task<IndexReport> IndexAllAsync(CancellationToken cancellationToken)
    {
        var sessions = await _store.ListSessionsAsync(cancellationToken).ConfigureAwait(false);
        return await IndexAsync(sessions, cancellationToken, removeMissing: true).ConfigureAwait(false);
    }

    /// <param name="removeMissing">Also remove indexed sessions that are not in <paramref name="sessions"/>.</param>
    public async Task<IndexReport> IndexAsync(
        IReadOnlyList<Session> sessions,
        CancellationToken cancellationToken,
        bool removeMissing = false)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        int indexed = 0, unchanged = 0, removed = 0;
        var vectors = _model is not null;

        if (removeMissing)
        {
            var present = sessions.Select(session => session.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var stale in _catalog.Sessions.Where(entry => !present.Contains(entry.SessionId)).ToList())
            {
                await RemoveAsync(stale.SessionId, cancellationToken).ConfigureAwait(false);
                removed++;
            }
        }

        foreach (var session in sessions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var passages = await PassagesAsync(session.Id, cancellationToken).ConfigureAwait(false);
            if (passages.Count == 0)
            {
                if (await RemoveAsync(session.Id, cancellationToken).ConfigureAwait(false))
                {
                    removed++;
                }

                continue;
            }

            var hash = Hash(session.DisplayTitle, passages);
            var existing = _catalog.Get(session.Id);
            if (existing is not null && existing.ContentHash == hash &&
                (!vectors || existing.EmbeddingModel == _model!.ModelName))
            {
                unchanged++;
                continue;
            }

            string? embeddedWith = null;
            if (vectors)
            {
                embeddedWith = await TryEmbedAsync(session.Id, passages, cancellationToken).ConfigureAwait(false);
                vectors = embeddedWith is not null;
            }

            _catalog.Set(new IndexedSession(session.Id, session.DisplayTitle, session.Started, hash, embeddedWith, passages));
            indexed++;
        }

        if (indexed + removed > 0)
        {
            await _catalog.SaveAsync(cancellationToken).ConfigureAwait(false);
        }

        return new IndexReport(indexed, unchanged, removed, vectors);
    }

    /// <summary>Drops a meeting from the catalog and the vector index (delete or reprocess).</summary>
    public async Task<bool> RemoveSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        var removed = await RemoveAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (removed)
        {
            await _catalog.SaveAsync(cancellationToken).ConfigureAwait(false);
        }

        return removed;
    }

    /// <summary>Text given to the embedding model: the passage without "[hh:mm:ss]" markers.</summary>
    public static string EmbeddingText(Passage passage) => TextFolding.StripTimestamps(passage.Text).Trim();

    private async Task<IReadOnlyList<Passage>> PassagesAsync(string sessionId, CancellationToken cancellationToken)
    {
        var transcript = await _store.LoadTranscriptAsync(sessionId, TranscriptPass.Final, cancellationToken).ConfigureAwait(false);
        if (!transcript.Any(segment => !string.IsNullOrWhiteSpace(segment?.Text)))
        {
            return [];
        }

        var summary = await _store.LoadSummaryAsync(sessionId, SummaryKind.Final, cancellationToken).ConfigureAwait(false);
        var notes = NotesDocument.Parse(await _store.LoadNotesAsync(sessionId, cancellationToken).ConfigureAwait(false));
        return PassageBuilder.ForSession(sessionId, transcript, summary, notes, _window);
    }

    /// <returns>The model name, or null when the model is unavailable (nothing written).</returns>
    private async Task<string?> TryEmbedAsync(string sessionId, IReadOnlyList<Passage> passages, CancellationToken cancellationToken)
    {
        var model = _model!;
        var records = new List<EmbeddingRecord>(passages.Count);
        try
        {
            foreach (var batch in passages.Chunk(BatchSize))
            {
                var texts = batch.Select(EmbeddingText).ToList();
                var vectors = await RunAsync(token => model.EmbedAsync(texts, token), cancellationToken).ConfigureAwait(false);
                if (vectors.Count != batch.Length)
                {
                    throw new InvalidOperationException($"embedding model returned {vectors.Count} vectors for {batch.Length} texts");
                }

                records.AddRange(batch.Select((passage, index) => new EmbeddingRecord(
                    passage.Id, passage.SessionId, passage.Kind, passage.Text, passage.Start, vectors[index])));
            }
        }
        catch (EmbeddingModelUnavailableException)
        {
            // Vectors of an older version of this meeting would no longer match its passages.
            await _index!.DeleteSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
            return null;
        }

        await _index!.DeleteSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        await _index.UpsertAsync(records, cancellationToken).ConfigureAwait(false);
        return model.ModelName;
    }

    private Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> job, CancellationToken cancellationToken) =>
        _scheduler is null ? job(cancellationToken) : _scheduler.RunAsync(InferencePriority.Background, job, cancellationToken);

    private async Task<bool> RemoveAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (_index is not null)
        {
            await _index.DeleteSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        }

        return _catalog.Remove(sessionId);
    }

    private static string Hash(string title, IReadOnlyList<Passage> passages)
    {
        var content = new StringBuilder(title).Append('\n');
        foreach (var passage in passages)
        {
            content.Append(passage.Id).Append('\u001f').Append(passage.Text).Append('\u001e');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content.ToString())));
    }
}
