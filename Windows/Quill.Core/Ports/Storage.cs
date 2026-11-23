using Quill.Core.Domain;

namespace Quill.Core.Ports;

/// <summary>Engine and model that produced a transcript (transcript.json header).</summary>
public sealed record TranscriptInfo(string Engine, string Model);

/// <summary>
/// Session folder persistence (format v2, additive over v1). Sessions are keyed by
/// <see cref="Session.Id"/> (folder name). Missing optional files read as empty /
/// null; corrupt files read as empty / null too (reading never throws for bad
/// content, only for an unknown or unsafe session id). Writes are atomic per file.
/// Default implementation: <see cref="Storage.FileSessionStore"/>.
/// </summary>
public interface ISessionStore
{
    /// <summary>All sessions with readable metadata, newest first.</summary>
    Task<IReadOnlyList<Session>> ListSessionsAsync(CancellationToken cancellationToken);

    /// <summary>Null when the folder does not exist or has no valid meta.json.</summary>
    Task<Session?> LoadSessionAsync(string sessionId, CancellationToken cancellationToken);

    /// <summary>Final: transcript.json. Live: live/transcript.json. Empty when missing or invalid.</summary>
    Task<IReadOnlyList<TranscriptSegment>> LoadTranscriptAsync(
        string sessionId,
        TranscriptPass pass,
        CancellationToken cancellationToken);

    /// <summary>
    /// Writes transcript.md then transcript.json (final), or live/transcript.json (live).
    /// transcript.json is the completion marker for the final pass.
    /// </summary>
    Task SaveTranscriptAsync(
        string sessionId,
        TranscriptPass pass,
        TranscriptInfo info,
        IReadOnlyList<TranscriptSegment> segments,
        CancellationToken cancellationToken);

    /// <summary>Final: summary.json. Live: live/summary.json. Null when missing or invalid.</summary>
    Task<MeetingSummary?> LoadSummaryAsync(string sessionId, SummaryKind kind, CancellationToken cancellationToken);

    /// <summary>Destination chosen by <c>summary.Meta.Kind</c>.</summary>
    Task SaveSummaryAsync(string sessionId, MeetingSummary summary, CancellationToken cancellationToken);

    /// <summary>speakers.json. Empty when missing (v1 sessions: callers fall back to "me"/"them").</summary>
    Task<IReadOnlyList<Speaker>> LoadSpeakersAsync(string sessionId, CancellationToken cancellationToken);

    Task SaveSpeakersAsync(string sessionId, IReadOnlyList<Speaker> speakers, CancellationToken cancellationToken);

    /// <summary>notes.md as raw markdown; empty string when missing. See <see cref="Storage.NotesDocument"/>.</summary>
    Task<string> LoadNotesAsync(string sessionId, CancellationToken cancellationToken);

    Task SaveNotesAsync(string sessionId, string markdown, CancellationToken cancellationToken);
}

/// <summary>A stored vector with the text it came from.</summary>
/// <param name="Id">Unique id, e.g. "{sessionId}:seg:{index}".</param>
/// <param name="Kind">"transcript", "summary", "note" or "voice" (speaker embeddings, F11).</param>
/// <param name="At">Offset in the session the text comes from, when applicable.</param>
public sealed record EmbeddingRecord(
    string Id,
    string SessionId,
    string Kind,
    string Text,
    TimeSpan? At,
    float[] Vector);

/// <param name="Score">Cosine similarity, higher is closer.</param>
public sealed record EmbeddingHit(EmbeddingRecord Record, double Score);

/// <summary>Optional restrictions for <see cref="IEmbeddingIndex.SearchAsync"/>.</summary>
public sealed record EmbeddingFilter(string? SessionId = null, string? Kind = null);

/// <summary>Local vector index (adapter: SQLite). Used for Ask, search and speaker-naming memory.</summary>
public interface IEmbeddingIndex
{
    /// <summary>Inserts or replaces by <see cref="EmbeddingRecord.Id"/>.</summary>
    Task UpsertAsync(IReadOnlyList<EmbeddingRecord> records, CancellationToken cancellationToken);

    Task<IReadOnlyList<EmbeddingHit>> SearchAsync(
        float[] query,
        int topK,
        EmbeddingFilter? filter,
        CancellationToken cancellationToken);

    /// <summary>Removes every record of a session (e.g. on delete or reprocess).</summary>
    Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken);
}

/// <summary>Wall clock, injectable for tests.</summary>
public interface IClock
{
    DateTimeOffset Now { get; }
}

public sealed class SystemClock : IClock
{
    public static SystemClock Instance { get; } = new();

    public DateTimeOffset Now => DateTimeOffset.Now;
}
