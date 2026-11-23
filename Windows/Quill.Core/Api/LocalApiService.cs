using System.Text.Json.Nodes;
using Quill.Core.Domain;
using Quill.Core.Ports;
using Quill.Core.Summaries;

namespace Quill.Core.Api;

/// <summary>
/// Read-only use cases behind the local API and the MCP tools. Never writes to the store. An
/// unknown, unsafe or unreadable session id reads as "not found" (null) so callers cannot tell an
/// invalid id from a missing one and no file-system detail leaks out.
/// </summary>
public sealed class LocalApiService
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;
    public const int MaxQueryLength = 500;

    private readonly ISessionStore _store;
    private readonly IApiSearch? _search;

    public LocalApiService(ISessionStore store, IApiSearch? search = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _search = search;
    }

    public bool SupportsSearch => _search is not null;

    public async Task<IReadOnlyList<ApiSessionItem>> ListSessionsAsync(int limit, CancellationToken cancellationToken)
    {
        var sessions = await _store.ListSessionsAsync(cancellationToken).ConfigureAwait(false);
        var items = new List<ApiSessionItem>();
        foreach (var session in sessions.Take(Math.Clamp(limit, 1, MaxLimit)))
        {
            var summary = await TryRead(() => _store.LoadSummaryAsync(session.Id, SummaryKind.Final, cancellationToken)).ConfigureAwait(false);
            items.Add(new ApiSessionItem(session.Id, session.DisplayTitle, session.Started, session.Duration.TotalSeconds, summary is not null));
        }

        return items;
    }

    public async Task<ApiSessionDetail?> GetSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (await FindAsync(sessionId, cancellationToken).ConfigureAwait(false) is not { } session)
        {
            return null;
        }

        var summary = await TryRead(() => _store.LoadSummaryAsync(session.Id, SummaryKind.Final, cancellationToken)).ConfigureAwait(false);
        var speakers = await TryRead(() => _store.LoadSpeakersAsync(session.Id, cancellationToken)).ConfigureAwait(false) ?? [];
        return new ApiSessionDetail(
            session.Id,
            session.DisplayTitle,
            session.Started,
            session.Ended,
            session.Duration.TotalSeconds,
            speakers.Select(speaker => new ApiSpeaker(speaker.Id, speaker.Label, TrackNames.ToName(speaker.Track))).ToList(),
            summary is null ? null : JsonNode.Parse(MeetingSummaryJson.Serialize(summary)));
    }

    public async Task<ApiTranscript?> GetTranscriptAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (await FindAsync(sessionId, cancellationToken).ConfigureAwait(false) is not { } session)
        {
            return null;
        }

        var segments = await TryRead(() => _store.LoadTranscriptAsync(session.Id, TranscriptPass.Final, cancellationToken)).ConfigureAwait(false) ?? [];
        var speakers = await TryRead(() => _store.LoadSpeakersAsync(session.Id, cancellationToken)).ConfigureAwait(false) ?? [];
        var names = speakers.GroupBy(speaker => speaker.Id, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First().Label, StringComparer.Ordinal);
        return new ApiTranscript(
            session.Id,
            segments.Select(segment => new ApiSegment(
                segment.Start.TotalSeconds,
                segment.End.TotalSeconds,
                segment.SpeakerId,
                names.GetValueOrDefault(segment.SpeakerId, segment.SpeakerId),
                TrackNames.ToName(segment.Track),
                segment.Text)).ToList());
    }

    public async Task<ApiNotes?> GetNotesAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (await FindAsync(sessionId, cancellationToken).ConfigureAwait(false) is not { } session)
        {
            return null;
        }

        var notes = await TryRead(() => _store.LoadNotesAsync(session.Id, cancellationToken)).ConfigureAwait(false);
        return new ApiNotes(session.Id, notes ?? string.Empty);
    }

    /// <summary>Null when no search provider is configured.</summary>
    /// <exception cref="ArgumentException">Empty or overlong query.</exception>
    public async Task<IReadOnlyList<ApiSearchHit>?> SearchAsync(string? query, int limit, CancellationToken cancellationToken)
    {
        if (_search is null)
        {
            return null;
        }

        var trimmed = query?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > MaxQueryLength)
        {
            throw new ArgumentException($"query must have 1-{MaxQueryLength} characters", nameof(query));
        }

        return await _search.SearchAsync(trimmed, Math.Clamp(limit, 1, MaxLimit), cancellationToken).ConfigureAwait(false);
    }

    private async Task<Session?> FindAsync(string sessionId, CancellationToken cancellationToken) =>
        string.IsNullOrWhiteSpace(sessionId)
            ? null
            : await TryRead(() => _store.LoadSessionAsync(sessionId, cancellationToken)).ConfigureAwait(false);

    /// <summary>Unsafe ids (ArgumentException) and unreadable folders read as missing.</summary>
    private static async Task<T?> TryRead<T>(Func<Task<T>> read)
        where T : class?
    {
        try
        {
            return await read().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
