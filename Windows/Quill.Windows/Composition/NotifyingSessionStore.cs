using Quill.Core.Domain;
using Quill.Core.Ports;

namespace Quill.Windows.Composition;

/// <summary>
/// The main window's <see cref="ISessionStore"/>: forwards everything and reports, after a
/// successful save, that a meeting's searchable content changed (notes, transcript edits, ticked
/// action items), so the search index follows edits made in the UI. The callback must not throw.
/// </summary>
internal sealed class NotifyingSessionStore(ISessionStore inner, Action<string> contentChanged) : ISessionStore
{
    private readonly ISessionStore _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly Action<string> _contentChanged = contentChanged ?? throw new ArgumentNullException(nameof(contentChanged));

    public Task<IReadOnlyList<Session>> ListSessionsAsync(CancellationToken cancellationToken) =>
        _inner.ListSessionsAsync(cancellationToken);

    public Task<Session?> LoadSessionAsync(string sessionId, CancellationToken cancellationToken) =>
        _inner.LoadSessionAsync(sessionId, cancellationToken);

    public Task<IReadOnlyList<TranscriptSegment>> LoadTranscriptAsync(string sessionId, TranscriptPass pass, CancellationToken cancellationToken) =>
        _inner.LoadTranscriptAsync(sessionId, pass, cancellationToken);

    public async Task SaveTranscriptAsync(
        string sessionId,
        TranscriptPass pass,
        TranscriptInfo info,
        IReadOnlyList<TranscriptSegment> segments,
        CancellationToken cancellationToken)
    {
        await _inner.SaveTranscriptAsync(sessionId, pass, info, segments, cancellationToken).ConfigureAwait(false);
        if (pass == TranscriptPass.Final)
        {
            _contentChanged(sessionId);
        }
    }

    public Task<MeetingSummary?> LoadSummaryAsync(string sessionId, SummaryKind kind, CancellationToken cancellationToken) =>
        _inner.LoadSummaryAsync(sessionId, kind, cancellationToken);

    public async Task SaveSummaryAsync(string sessionId, MeetingSummary summary, CancellationToken cancellationToken)
    {
        await _inner.SaveSummaryAsync(sessionId, summary, cancellationToken).ConfigureAwait(false);
        if (summary.Meta.Kind == SummaryKind.Final)
        {
            _contentChanged(sessionId);
        }
    }

    public Task<IReadOnlyList<Speaker>> LoadSpeakersAsync(string sessionId, CancellationToken cancellationToken) =>
        _inner.LoadSpeakersAsync(sessionId, cancellationToken);

    public async Task SaveSpeakersAsync(string sessionId, IReadOnlyList<Speaker> speakers, CancellationToken cancellationToken)
    {
        await _inner.SaveSpeakersAsync(sessionId, speakers, cancellationToken).ConfigureAwait(false);
        _contentChanged(sessionId);
    }

    public Task<string> LoadNotesAsync(string sessionId, CancellationToken cancellationToken) =>
        _inner.LoadNotesAsync(sessionId, cancellationToken);

    public async Task SaveNotesAsync(string sessionId, string markdown, CancellationToken cancellationToken)
    {
        await _inner.SaveNotesAsync(sessionId, markdown, cancellationToken).ConfigureAwait(false);
        _contentChanged(sessionId);
    }
}
