using Quill.Core.Domain;
using Quill.Core.Ports;

namespace Quill.Core.Speakers;

/// <param name="Segments">Final transcript, both tracks, in transcript order.</param>
/// <param name="EchoVerdicts">Verdicts for the microphone segments of <paramref name="Segments"/>, indexed in their order.</param>
/// <param name="SystemDiarization">Diarization of the system track (turns relative to the session start).</param>
/// <param name="MicrophoneDiarization">Optional diarization of the mic track (in-person mode, F13).</param>
public sealed record SpeakerResolutionInput(
    IReadOnlyList<TranscriptSegment> Segments,
    IReadOnlyList<EchoVerdict> EchoVerdicts,
    DiarizationResult SystemDiarization,
    DiarizationResult? MicrophoneDiarization = null);

public sealed record SpeakerResolutionOptions
{
    /// <summary>Clusters with less total speech are merged into the most similar cluster.</summary>
    public TimeSpan MinClusterDuration { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>An echo segment whose text overlaps a system segment this much is a duplicate and is removed.</summary>
    public double DuplicateTextOverlap { get; init; } = 0.5;

    /// <summary>Slack when matching an echo segment with system segments in time.</summary>
    public TimeSpan TimeTolerance { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Also diarize the microphone track (in-person mode, F13). Off by default.</summary>
    public bool DiarizeMicrophone { get; init; }
}

/// <param name="Segments">Transcript with v2 speaker ids, original order, duplicates removed.</param>
/// <param name="Speakers">Every speaker used by <paramref name="Segments"/>, "me" first then by number.</param>
public sealed record ResolvedSpeakers(
    IReadOnlyList<TranscriptSegment> Segments,
    IReadOnlyList<SpeakerProfile> Speakers,
    int EchoSegmentsRemoved,
    int EchoSegmentsReattributed);

/// <summary>
/// Assigns v2 speaker ids. Genuine microphone speech is "me" (or, with mic diarization,
/// the dominant mic voice is "me" and other mic voices get remote numbers); echo is
/// removed when it duplicates a system segment, otherwise re-attributed to the remote
/// speaker talking at that moment; system turns become "speaker-1..N" numbered by first
/// appearance in the transcript. Pure: no I/O.
/// </summary>
public static class SpeakerResolver
{
    private const int NoCluster = -1;

    public static ResolvedSpeakers Resolve(SpeakerResolutionInput input, SpeakerResolutionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        options ??= new SpeakerResolutionOptions();

        var system = Clusters.From(input.SystemDiarization, options.MinClusterDuration);
        var microphone = input.MicrophoneDiarization is { } mic ? Clusters.From(mic, options.MinClusterDuration) : null;
        var ownerCluster = microphone?.Dominant();
        var verdicts = input.EchoVerdicts.ToDictionary(verdict => verdict.MicrophoneSegmentIndex);

        var segments = input.Segments;
        var ids = new string?[segments.Count];
        var keys = new Dictionary<string, (Track Track, int Cluster)>(StringComparer.Ordinal);
        var numbers = new Dictionary<(Track Track, int Cluster), int>();
        var echoes = new List<int>();

        string Number(Track track, int cluster)
        {
            if (!numbers.TryGetValue((track, cluster), out var number))
            {
                number = numbers.Count + 1;
                numbers[(track, cluster)] = number;
            }

            var id = SpeakerIds.Remote(number);
            keys[id] = (track, cluster);
            return id;
        }

        // Pass 1, in time order so numbering follows first appearance.
        var micIndex = new Dictionary<int, int>();
        for (int i = 0, m = 0; i < segments.Count; i++)
        {
            if (segments[i].Track == Track.Microphone)
            {
                micIndex[i] = m++;
            }
        }

        foreach (var i in Enumerable.Range(0, segments.Count).OrderBy(i => segments[i].Start).ThenBy(i => i))
        {
            var segment = segments[i];
            if (segment.Track == Track.System)
            {
                ids[i] = Number(Track.System, system.Best(segment) ?? NoCluster);
            }
            else if (verdicts.TryGetValue(micIndex[i], out var verdict) && verdict.IsEcho)
            {
                echoes.Add(i);
            }
            else if (microphone?.Best(segment) is { } cluster && cluster != ownerCluster)
            {
                ids[i] = Number(Track.Microphone, cluster);
            }
            else
            {
                ids[i] = SpeakerIds.Me;
            }
        }

        // Pass 2: echo, now that every system segment has an id.
        var removed = new HashSet<int>();
        var reattributed = 0;
        foreach (var i in echoes)
        {
            var echo = segments[i];
            var duplicate = !echo.Edited && Enumerable.Range(0, segments.Count).Any(j =>
                segments[j].Track == Track.System
                && Overlaps(echo, segments[j], options.TimeTolerance)
                && TextOverlap.Score(echo.Text, segments[j].Text) >= options.DuplicateTextOverlap);
            if (duplicate)
            {
                removed.Add(i);
                continue;
            }

            reattributed++;
            ids[i] = system.Best(echo, nearestFallback: false) is { } cluster
                ? Number(Track.System, cluster)
                : NearestSystemId(segments, ids, echo) ?? Number(Track.System, NoCluster);
        }

        var result = new List<TranscriptSegment>(segments.Count - removed.Count);
        for (var i = 0; i < segments.Count; i++)
        {
            if (!removed.Contains(i))
            {
                result.Add(segments[i] with { SpeakerId = ids[i]! });
            }
        }

        var used = result.Select(segment => segment.SpeakerId).Distinct(StringComparer.Ordinal);
        var profiles = used.Select(id =>
        {
            if (id == SpeakerIds.Me)
            {
                var ownerEmbedding = ownerCluster is { } owner ? microphone!.Embedding(owner) : null;
                return new SpeakerProfile(SpeakerIds.Me, Track.Microphone, null, 0, ownerEmbedding);
            }

            var (track, cluster) = keys[id];
            var source = track == Track.System ? system : microphone!;
            return new SpeakerProfile(id, track, null, numbers[(track, cluster)], source.Embedding(cluster));
        });

        return new ResolvedSpeakers(result, SpeakerEdits.Order(profiles), removed.Count, reattributed);
    }

    private static bool Overlaps(TranscriptSegment a, TranscriptSegment b, TimeSpan tolerance) =>
        b.Start - tolerance < a.End && b.End + tolerance > a.Start;

    private static string? NearestSystemId(IReadOnlyList<TranscriptSegment> segments, string?[] ids, TranscriptSegment target)
    {
        string? best = null;
        var bestGap = TimeSpan.MaxValue;
        for (var j = 0; j < segments.Count; j++)
        {
            if (segments[j].Track != Track.System || ids[j] is null)
            {
                continue;
            }

            var gap = Gap(segments[j].Start, segments[j].End, target.Start, target.End);
            if (gap < bestGap)
            {
                bestGap = gap;
                best = ids[j];
            }
        }

        return best;
    }

    private static TimeSpan Gap(TimeSpan aStart, TimeSpan aEnd, TimeSpan bStart, TimeSpan bEnd) =>
        aEnd < bStart ? bStart - aEnd : bEnd < aStart ? aStart - bEnd : TimeSpan.Zero;

    /// <summary>Diarization turns after merging very short clusters.</summary>
    private sealed class Clusters
    {
        private readonly IReadOnlyList<SpeakerTurn> _turns;
        private readonly Dictionary<int, float[]> _embeddings;

        private Clusters(IReadOnlyList<SpeakerTurn> turns, Dictionary<int, float[]> embeddings)
        {
            _turns = turns;
            _embeddings = embeddings;
        }

        public static Clusters From(DiarizationResult result, TimeSpan minDuration)
        {
            var durations = result.Turns
                .GroupBy(turn => turn.ClusterId)
                .ToDictionary(group => group.Key, group => group.Sum(turn => (turn.End - turn.Start).TotalSeconds));
            var embeddings = result.ClusterEmbeddings.ToDictionary(pair => pair.Key, pair => pair.Value);
            var longClusters = durations.Where(pair => pair.Value >= minDuration.TotalSeconds).Select(pair => pair.Key).ToList();
            var target = new Dictionary<int, int>();
            if (longClusters.Count > 0)
            {
                foreach (var (cluster, _) in durations.Where(pair => pair.Value < minDuration.TotalSeconds))
                {
                    var into = longClusters
                        .OrderByDescending(candidate => Vectors.Cosine(embeddings.GetValueOrDefault(cluster), embeddings.GetValueOrDefault(candidate)))
                        .ThenByDescending(candidate => durations[candidate])
                        .ThenBy(candidate => candidate)
                        .First();
                    target[cluster] = into;
                    embeddings[into] = Vectors.Average(
                        embeddings.GetValueOrDefault(into),
                        embeddings.GetValueOrDefault(cluster),
                        durations[into],
                        durations[cluster])!;
                    embeddings.Remove(cluster);
                }
            }

            var turns = result.Turns
                .Select(turn => target.TryGetValue(turn.ClusterId, out var into) ? turn with { ClusterId = into } : turn)
                .OrderBy(turn => turn.Start)
                .ToList();
            foreach (var key in embeddings.Where(pair => pair.Value is null).Select(pair => pair.Key).ToList())
            {
                embeddings.Remove(key);
            }

            return new Clusters(turns, embeddings);
        }

        public float[]? Embedding(int cluster) => _embeddings.GetValueOrDefault(cluster);

        /// <summary>Cluster with the most speech.</summary>
        public int? Dominant() =>
            _turns.Count == 0
                ? null
                : _turns.GroupBy(turn => turn.ClusterId)
                    .OrderByDescending(group => group.Sum(turn => (turn.End - turn.Start).Ticks))
                    .ThenBy(group => group.Key)
                    .First().Key;

        /// <summary>Cluster overlapping the segment the most; else the nearest turn (when allowed); null without turns.</summary>
        public int? Best(TranscriptSegment segment, bool nearestFallback = true)
        {
            int? best = null;
            var bestOverlap = TimeSpan.Zero;
            foreach (var turn in _turns)
            {
                var overlap = Min(turn.End, segment.End) - Max(turn.Start, segment.Start);
                if (overlap > bestOverlap)
                {
                    bestOverlap = overlap;
                    best = turn.ClusterId;
                }
            }

            if (best is not null || !nearestFallback)
            {
                return best;
            }

            var bestGap = TimeSpan.MaxValue;
            foreach (var turn in _turns)
            {
                var gap = Gap(turn.Start, turn.End, segment.Start, segment.End);
                if (gap < bestGap)
                {
                    bestGap = gap;
                    best = turn.ClusterId;
                }
            }

            return best;
        }

        private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

        private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
    }
}
