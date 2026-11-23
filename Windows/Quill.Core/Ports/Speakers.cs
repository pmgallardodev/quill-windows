using Quill.Core.Domain;

namespace Quill.Core.Ports;

/// <param name="AudioPath">WAV of a single track. Any track: system by default, mic for in-person mode (F13).</param>
/// <param name="Track">Which track the file is.</param>
/// <param name="StartOffset">Added to every turn so turns are relative to the session start.</param>
/// <param name="ExpectedSpeakers">Known speaker count, or null to estimate.</param>
public sealed record DiarizationRequest(
    string AudioPath,
    Track Track,
    TimeSpan StartOffset = default,
    int? ExpectedSpeakers = null);

/// <summary>A stretch of audio attributed to one cluster.</summary>
/// <param name="ClusterId">0-based cluster within this result; not stable across calls.</param>
/// <param name="Embedding">Speaker embedding of this turn, when the adapter computes one.</param>
public sealed record SpeakerTurn(TimeSpan Start, TimeSpan End, int ClusterId, float[]? Embedding = null);

/// <param name="Turns">Turns ordered by start.</param>
/// <param name="ClusterEmbeddings">Centroid embedding per cluster id (for owner-voice and naming memory).</param>
public sealed record DiarizationResult(
    Track Track,
    IReadOnlyList<SpeakerTurn> Turns,
    IReadOnlyDictionary<int, float[]> ClusterEmbeddings);

/// <summary>
/// Who-spoke-when on one track (adapter: sherpa-onnx). Must not assume the
/// microphone holds a single person.
/// </summary>
public interface IDiarizer
{
    Task<DiarizationResult> DiarizeAsync(
        DiarizationRequest request,
        IProgress<double>? progress,
        CancellationToken cancellationToken);
}

/// <summary>RMS energy per fixed-size frame, starting at the session start.</summary>
public sealed record EnergyEnvelope(TimeSpan FrameDuration, IReadOnlyList<float> Rms);

/// <param name="MicrophoneSegments">Segments from the mic track to classify.</param>
/// <param name="SystemSegments">Segments from the system track (what may have leaked into the mic).</param>
/// <param name="MicrophoneEnergy">Optional mic energy envelope.</param>
/// <param name="SystemEnergy">Optional system energy envelope.</param>
public sealed record EchoClassificationRequest(
    IReadOnlyList<TranscriptSegment> MicrophoneSegments,
    IReadOnlyList<TranscriptSegment> SystemSegments,
    EnergyEnvelope? MicrophoneEnergy = null,
    EnergyEnvelope? SystemEnergy = null);

/// <param name="MicrophoneSegmentIndex">Index into <see cref="EchoClassificationRequest.MicrophoneSegments"/>.</param>
/// <param name="IsEcho">True when the segment is remote audio leaking through speakers, not the owner.</param>
/// <param name="Confidence">In [0, 1].</param>
/// <param name="Reason">Short diagnostic, e.g. "text overlap 0.92 with system segment".</param>
public sealed record EchoVerdict(int MicrophoneSegmentIndex, bool IsEcho, double Confidence, string? Reason = null);

/// <summary>
/// Flags microphone segments that are echo of the system track so they are never
/// labelled "me" (design goal 4). Returns one verdict per microphone segment, in order.
/// </summary>
public interface IEchoClassifier
{
    Task<IReadOnlyList<EchoVerdict>> ClassifyAsync(
        EchoClassificationRequest request,
        CancellationToken cancellationToken);
}
