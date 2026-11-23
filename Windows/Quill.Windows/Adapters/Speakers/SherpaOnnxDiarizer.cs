using Quill.Core.Ports;
using SherpaOnnx;

namespace Quill.Windows.Adapters.Speakers;

/// <summary>
/// <see cref="IDiarizer"/> on sherpa-onnx (org.k2fsa.sherpa.onnx 2.0.3): pyannote segmentation
/// + speaker-embedding clustering, then one centroid embedding per cluster computed from
/// that cluster's own audio. Works on any track; no assumption about who is on it.
/// </summary>
internal sealed class SherpaOnnxDiarizer(
    string segmentationModel,
    string embeddingModel,
    int threads = 2,
    float clusteringThreshold = 0.5f) : IDiarizer
{
    private const int SampleRate = 16_000;

    /// <summary>Audio per cluster used for its centroid embedding: longest turns first, up to this much.</summary>
    private static readonly TimeSpan EmbeddingBudget = TimeSpan.FromSeconds(30);

    public Task<DiarizationResult> DiarizeAsync(
        DiarizationRequest request,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Task.Run(() => Diarize(request, progress, cancellationToken), cancellationToken);
    }

    private DiarizationResult Diarize(DiarizationRequest request, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var audio = MonoAudio.Read(request.AudioPath, SampleRate, cancellationToken);
        if (audio.Length < SampleRate / 2)
        {
            return new DiarizationResult(request.Track, [], new Dictionary<int, float[]>());
        }

        var config = new OfflineSpeakerDiarizationConfig();
        config.Segmentation.Pyannote.Model = segmentationModel;
        config.Segmentation.NumThreads = threads;
        config.Embedding.Model = embeddingModel;
        config.Embedding.NumThreads = threads;
        config.Clustering.NumClusters = request.ExpectedSpeakers ?? -1;
        config.Clustering.Threshold = clusteringThreshold;
        config.MinDurationOn = 0.3f;
        config.MinDurationOff = 0.5f;

        OfflineSpeakerDiarizationSegment[] segments;
        using (var diarization = new OfflineSpeakerDiarization(config))
        {
            OfflineSpeakerDiarizationProgressCallback callback = (processed, total, _) =>
            {
                if (total > 0)
                {
                    progress?.Report(0.8 * processed / total);
                }

                // Non-zero is meant as a stop request; not verified on 2.0.3, so cancellation
                // is also checked right after processing.
                return cancellationToken.IsCancellationRequested ? 1 : 0;
            };
            segments = diarization.ProcessWithCallback(audio, callback, IntPtr.Zero);
            GC.KeepAlive(callback);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var turns = segments
            .Where(segment => segment.End > segment.Start)
            .OrderBy(segment => segment.Start)
            .Select(segment => new SpeakerTurn(
                request.StartOffset + TimeSpan.FromSeconds(segment.Start),
                request.StartOffset + TimeSpan.FromSeconds(segment.End),
                segment.Speaker))
            .ToList();

        var embeddings = ClusterEmbeddings(audio, segments, cancellationToken);
        progress?.Report(1);
        return new DiarizationResult(request.Track, turns, embeddings);
    }

    private Dictionary<int, float[]> ClusterEmbeddings(
        float[] audio,
        IReadOnlyList<OfflineSpeakerDiarizationSegment> segments,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<int, float[]>();
        using var extractor = new SpeakerEmbeddingExtractor(new SpeakerEmbeddingExtractorConfig
        {
            Model = embeddingModel,
            NumThreads = threads,
        });

        foreach (var cluster in segments.GroupBy(segment => segment.Speaker))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var budget = (int)(EmbeddingBudget.TotalSeconds * SampleRate);
            var samples = new List<float>(budget);
            foreach (var segment in cluster.OrderByDescending(segment => segment.End - segment.Start))
            {
                var start = Math.Clamp((int)(segment.Start * SampleRate), 0, audio.Length);
                var end = Math.Clamp((int)(segment.End * SampleRate), start, audio.Length);
                var take = Math.Min(end - start, budget - samples.Count);
                samples.AddRange(audio.AsSpan(start, take).ToArray());
                if (samples.Count >= budget)
                {
                    break;
                }
            }

            if (samples.Count < SampleRate / 2)
            {
                continue;
            }

            var stream = extractor.CreateStream();
            stream.AcceptWaveform(SampleRate, samples.ToArray());
            stream.InputFinished();
            if (extractor.IsReady(stream))
            {
                result[cluster.Key] = extractor.Compute(stream);
            }
        }

        return result;
    }
}
