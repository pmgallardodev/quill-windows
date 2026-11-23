namespace Quill.Core.Search;

/// <summary>Where a <see cref="Passage"/> comes from; also the <see cref="Ports.EmbeddingRecord.Kind"/> it is stored with.</summary>
public static class PassageKinds
{
    public const string Transcript = "transcript";
    public const string Summary = "summary";
    public const string Note = "note";
}

/// <summary>
/// A searchable piece of one meeting: a 40–90 s window of the final transcript, one summary
/// item or one note. <see cref="Text"/> keeps the "[hh:mm:ss] speakerId: text" line format of
/// <see cref="Transcripts.TranscriptChunker.FormatLine"/> so Ask can cite the exact timestamp.
/// </summary>
/// <param name="Id">Stable within a session, e.g. "{sessionId}:transcript:3".</param>
/// <param name="Start">First line offset; null for uncited text (executive summary, free notes).</param>
/// <param name="End">End of the last line; null when <paramref name="Start"/> is null.</param>
/// <param name="Speakers">Speaker ids that talk in the passage, in order of appearance.</param>
public sealed record Passage(
    string Id,
    string SessionId,
    string Kind,
    TimeSpan? Start,
    TimeSpan? End,
    IReadOnlyList<string> Speakers,
    string Text);

/// <summary>Thrown by <see cref="Ports.IEmbeddingModel"/> adapters when the AI pack has no usable text embedding model.</summary>
public sealed class EmbeddingModelUnavailableException : Exception
{
    public EmbeddingModelUnavailableException(string message)
        : base(message)
    {
    }

    public EmbeddingModelUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
