using Quill.Core.Domain;
using Quill.Core.Ports;

namespace Quill.Core.Speakers.VoiceMemory;

/// <summary>A microphone cluster considered for "me" in in-person mode.</summary>
/// <param name="Speech">Total speech of the cluster.</param>
/// <param name="MeanEnergy">Mean RMS over its turns, or null without an energy envelope.</param>
public sealed record MicrophoneCluster(int Id, TimeSpan Speech, float[]? Embedding, double? MeanEnergy);

public enum OwnerSelectionReason
{
    /// <summary>Clearly matched the owner's saved voice.</summary>
    VoicePrint,

    /// <summary>No usable voice print: the loudest substantial cluster (closest to the mic).</summary>
    Loudest,

    /// <summary>No voice print and no energy: the cluster that spoke most.</summary>
    MostSpeech,
}

public sealed record OwnerSelection(int ClusterId, OwnerSelectionReason Reason, double? Similarity = null);

/// <summary>
/// Picks the owner ("me") among the microphone clusters of an in-person meeting: the cluster
/// clearly matching the owner's voice print, else the loudest cluster with a meaningful share
/// of speech, else the one with most speech. Pure.
/// </summary>
public static class OwnerSelector
{
    /// <summary>Minimum cosine similarity with the owner's voice print.</summary>
    public const double MinSimilarity = 0.6;

    /// <summary>The match must beat the next cluster by this much; two owner-like clusters are ambiguous.</summary>
    public const double MinMargin = 0.05;

    /// <summary>Clusters with less speech than this share of the largest one never win on loudness.</summary>
    public const double LoudestMinSpeechShare = 0.25;

    public static OwnerSelection? Select(IReadOnlyList<MicrophoneCluster> clusters, float[]? ownerVoice)
    {
        ArgumentNullException.ThrowIfNull(clusters);
        if (clusters.Count == 0)
        {
            return null;
        }

        if (ownerVoice is { Length: > 0 })
        {
            var scored = clusters
                .Select(cluster => (Cluster: cluster, Score: Vectors.Cosine(cluster.Embedding, ownerVoice)))
                .OrderByDescending(pair => pair.Score)
                .ThenBy(pair => pair.Cluster.Id)
                .ToList();
            var runnerUp = scored.Count > 1 ? scored[1].Score : 0;
            if (scored[0].Score >= MinSimilarity && scored[0].Score - runnerUp >= MinMargin)
            {
                return new OwnerSelection(scored[0].Cluster.Id, OwnerSelectionReason.VoicePrint, Math.Round(scored[0].Score, 3));
            }
        }

        var most = clusters.Max(cluster => cluster.Speech);
        var substantial = clusters.Where(cluster => cluster.Speech.Ticks >= most.Ticks * LoudestMinSpeechShare).ToList();
        if (substantial.All(cluster => cluster.MeanEnergy is not null))
        {
            var loudest = substantial
                .OrderByDescending(cluster => cluster.MeanEnergy)
                .ThenByDescending(cluster => cluster.Speech)
                .ThenBy(cluster => cluster.Id)
                .First();
            return new OwnerSelection(loudest.Id, OwnerSelectionReason.Loudest);
        }

        var dominant = clusters.OrderByDescending(cluster => cluster.Speech).ThenBy(cluster => cluster.Id).First();
        return new OwnerSelection(dominant.Id, OwnerSelectionReason.MostSpeech);
    }

    /// <summary>Mean RMS of <paramref name="envelope"/> over the given turns; null when nothing overlaps.</summary>
    public static double? MeanEnergy(EnergyEnvelope? envelope, IEnumerable<(TimeSpan Start, TimeSpan End)> turns)
    {
        if (envelope is null || envelope.Rms.Count == 0 || envelope.FrameDuration <= TimeSpan.Zero)
        {
            return null;
        }

        double sum = 0;
        var frames = 0;
        foreach (var (start, end) in turns)
        {
            var first = Math.Max(0, (int)(start.Ticks / envelope.FrameDuration.Ticks));
            var last = Math.Min(envelope.Rms.Count, (int)Math.Ceiling(end.Ticks / (double)envelope.FrameDuration.Ticks));
            for (var i = first; i < last; i++)
            {
                sum += envelope.Rms[i];
                frames++;
            }
        }

        return frames == 0 ? null : sum / frames;
    }
}

/// <summary>
/// Learns the owner's voice from an ordinary online meeting, where genuine (non-echo)
/// microphone speech is the owner: the mic cluster covering most of that speech, when it
/// covers a clear majority of enough speech. Pure.
/// </summary>
public static class OwnerVoiceLearner
{
    /// <summary>Genuine mic speech needed before a meeting teaches anything.</summary>
    public static readonly TimeSpan MinSpeech = TimeSpan.FromSeconds(20);

    /// <summary>Share of genuine mic speech the chosen cluster must cover (lower means a shared mic).</summary>
    public const double MinShare = 0.7;

    /// <param name="microphoneSegments">Microphone segments in order; <paramref name="verdicts"/> index into them.</param>
    public static float[]? FromOnlineMeeting(
        DiarizationResult microphone,
        IReadOnlyList<TranscriptSegment> microphoneSegments,
        IReadOnlyList<EchoVerdict> verdicts)
    {
        ArgumentNullException.ThrowIfNull(microphone);
        var echo = verdicts.Where(verdict => verdict.IsEcho).Select(verdict => verdict.MicrophoneSegmentIndex).ToHashSet();
        var genuine = microphoneSegments.Where((_, index) => !echo.Contains(index)).ToList();
        var total = genuine.Sum(segment => Math.Max(0, (segment.End - segment.Start).TotalSeconds));
        if (total < MinSpeech.TotalSeconds)
        {
            return null;
        }

        var covered = new Dictionary<int, double>();
        foreach (var segment in genuine)
        {
            foreach (var turn in microphone.Turns)
            {
                var overlap = (Min(turn.End, segment.End) - Max(turn.Start, segment.Start)).TotalSeconds;
                if (overlap > 0)
                {
                    covered[turn.ClusterId] = covered.GetValueOrDefault(turn.ClusterId) + overlap;
                }
            }
        }

        if (covered.Count == 0)
        {
            return null;
        }

        var (cluster, seconds) = covered.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key).First();
        return seconds / total >= MinShare ? microphone.ClusterEmbeddings.GetValueOrDefault(cluster) : null;
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}
