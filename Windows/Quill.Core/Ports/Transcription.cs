using Quill.Core.Domain;

namespace Quill.Core.Ports;

/// <summary>Request to transcribe one track's audio file (final pass or import).</summary>
/// <param name="AudioPath">Path to a WAV file of a single track.</param>
/// <param name="Track">Track the file belongs to; copied into every segment.</param>
/// <param name="SpeakerId">Initial speaker id for every segment (speaker resolution may change it later).</param>
/// <param name="StartOffset">Added to every timestamp so segments are relative to the session start.</param>
/// <param name="Language">ISO code ("es"), or null for auto-detection.</param>
public sealed record TranscriptionRequest(
    string AudioPath,
    Track Track,
    string SpeakerId,
    TimeSpan StartOffset = default,
    string? Language = null);

/// <summary>Batch transcription of a whole file. Adapter: Whisper.net large-v3-turbo.</summary>
public interface ITranscriber
{
    /// <param name="progress">Fraction in [0, 1]; may be null.</param>
    Task<IReadOnlyList<TranscriptSegment>> TranscribeAsync(
        TranscriptionRequest request,
        IProgress<double>? progress,
        CancellationToken cancellationToken);
}

/// <summary>
/// A chunk of PCM audio for live transcription: 16 kHz, mono, 32-bit float in
/// [-1, 1]. Chunks are normally VAD-delimited utterances produced by the capture tap.
/// </summary>
/// <param name="Track">Track the samples come from.</param>
/// <param name="Start">Offset of the first sample from the session start.</param>
/// <param name="Samples">The samples. The callee must copy them if it keeps them past the call.</param>
public sealed record AudioChunk(Track Track, TimeSpan Start, ReadOnlyMemory<float> Samples)
{
    public const int SampleRate = 16_000;

    public TimeSpan Duration => TimeSpan.FromSeconds(Samples.Length / (double)SampleRate);

    public TimeSpan End => Start + Duration;
}

/// <summary>
/// Live transcription (whisper small). One instance per recording. Producers call
/// <see cref="FeedAsync"/> from the capture side (it must not block for long: drop
/// or queue, never stall capture); a consumer enumerates <see cref="ReadSegmentsAsync"/>.
/// <see cref="CompleteAsync"/> flushes pending audio and ends the segment stream.
/// Emitted segments have <c>Editable = false</c> (provisional).
/// </summary>
public interface IStreamingTranscriber : IAsyncDisposable
{
    ValueTask FeedAsync(AudioChunk chunk, CancellationToken cancellationToken);

    ValueTask CompleteAsync(CancellationToken cancellationToken);

    /// <summary>Single consumer. Completes after <see cref="CompleteAsync"/> drains.</summary>
    IAsyncEnumerable<TranscriptSegment> ReadSegmentsAsync(CancellationToken cancellationToken);
}
