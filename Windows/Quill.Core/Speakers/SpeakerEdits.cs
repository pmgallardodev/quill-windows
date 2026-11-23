using Quill.Core.Domain;

namespace Quill.Core.Speakers;

/// <summary>Applies user corrections (merge, reassign) to a resolved transcript. Pure and idempotent.</summary>
public static class SpeakerEdits
{
    private static readonly TimeSpan SameStart = TimeSpan.FromMilliseconds(1);

    /// <summary>
    /// Replays <paramref name="edits"/> in order. Edits that no longer apply (unknown
    /// speaker, missing segment) are skipped. Speakers left without segments are dropped.
    /// </summary>
    public static (IReadOnlyList<TranscriptSegment> Segments, IReadOnlyList<SpeakerProfile> Speakers) Apply(
        IReadOnlyList<TranscriptSegment> segments,
        IReadOnlyList<SpeakerProfile> speakers,
        IReadOnlyList<SpeakerEdit> edits)
    {
        var result = segments.ToList();
        var profiles = speakers.ToList();
        foreach (var edit in edits)
        {
            switch (edit)
            {
                case MergeSpeakers merge:
                    Merge(result, profiles, merge);
                    break;
                case ReassignSegment reassign:
                    Reassign(result, profiles, reassign);
                    break;
            }
        }

        var used = result.Select(segment => segment.SpeakerId).ToHashSet(StringComparer.Ordinal);
        return (result, Order(profiles.Where(profile => used.Contains(profile.Id))));
    }

    /// <summary>"me" first, then by colour slot, then id.</summary>
    public static IReadOnlyList<SpeakerProfile> Order(IEnumerable<SpeakerProfile> profiles) =>
        profiles
            .OrderBy(profile => profile.Id == SpeakerIds.Me ? 0 : 1)
            .ThenBy(profile => profile.ColorSlot)
            .ThenBy(profile => profile.Id, StringComparer.Ordinal)
            .ToList();

    /// <summary>Index of the uniquely matching segment an edit targets, or -1 when absent or ambiguous.</summary>
    public static int FindSegment(IReadOnlyList<TranscriptSegment> segments, Track track, TimeSpan start)
    {
        var found = -1;
        for (var i = 0; i < segments.Count; i++)
        {
            if (segments[i].Track == track && (segments[i].Start - start).Duration() < SameStart)
            {
                if (found >= 0)
                {
                    return -1;
                }

                found = i;
            }
        }

        return found;
    }

    private static void Merge(List<TranscriptSegment> segments, List<SpeakerProfile> profiles, MergeSpeakers merge)
    {
        var from = profiles.FindIndex(profile => profile.Id == merge.From);
        var into = profiles.FindIndex(profile => profile.Id == merge.Into);
        if (merge.From == merge.Into || from < 0 || into < 0)
        {
            return;
        }

        for (var i = 0; i < segments.Count; i++)
        {
            if (segments[i].SpeakerId == merge.From)
            {
                segments[i] = segments[i] with { SpeakerId = merge.Into };
            }
        }

        profiles[into] = profiles[into] with
        {
            Embedding = Vectors.Average(profiles[into].Embedding, profiles[from].Embedding),
        };
        profiles.RemoveAt(from);
    }

    private static void Reassign(List<TranscriptSegment> segments, List<SpeakerProfile> profiles, ReassignSegment reassign)
    {
        var index = FindSegment(segments, reassign.Track, reassign.Start);
        if (index < 0 || !SpeakerProfile.IsAssignable(reassign.SpeakerId))
        {
            return;
        }

        if (profiles.All(profile => profile.Id != reassign.SpeakerId))
        {
            var track = reassign.SpeakerId == SpeakerIds.Me ? Track.Microphone : segments[index].Track;
            profiles.Add(new SpeakerProfile(
                reassign.SpeakerId,
                track,
                null,
                SpeakerProfile.DefaultColorSlot(reassign.SpeakerId),
                null));
        }

        segments[index] = segments[index] with { SpeakerId = reassign.SpeakerId };
    }
}

/// <summary>Small helpers for speaker embeddings.</summary>
public static class Vectors
{
    /// <summary>Cosine similarity; 0 when either vector is null, empty, of different length or zero.</summary>
    public static double Cosine(float[]? a, float[]? b)
    {
        if (a is null || b is null || a.Length == 0 || a.Length != b.Length)
        {
            return 0;
        }

        double dot = 0, normA = 0, normB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        return normA <= 0 || normB <= 0 ? 0 : dot / Math.Sqrt(normA * normB);
    }

    /// <summary>Weighted mean of two vectors; returns the other one when one is missing or sizes differ.</summary>
    public static float[]? Average(float[]? a, float[]? b, double weightA = 1, double weightB = 1)
    {
        if (a is null || a.Length == 0)
        {
            return b;
        }

        if (b is null || b.Length != a.Length)
        {
            return a;
        }

        var total = weightA + weightB;
        var result = new float[a.Length];
        for (var i = 0; i < a.Length; i++)
        {
            result[i] = (float)((a[i] * weightA + b[i] * weightB) / total);
        }

        return result;
    }
}
