using Quill.Core.Domain;
using Quill.Core.Ports;
using Quill.Core.Settings;

namespace Quill.Core.Pipeline;

/// <summary>Maps a transcript position shown in the UI back to the segment index the speaker API takes.</summary>
public static class TranscriptSegmentLocator
{
    /// <summary>Saved transcripts keep millisecond precision, so starts match within 1 ms.</summary>
    public static readonly TimeSpan Tolerance = TimeSpan.FromMilliseconds(1);

    /// <returns>Index of the first segment starting at <paramref name="start"/>, or -1.</returns>
    public static int IndexOf(IReadOnlyList<TranscriptSegment> segments, TimeSpan start)
    {
        ArgumentNullException.ThrowIfNull(segments);
        for (var index = 0; index < segments.Count; index++)
        {
            if ((segments[index].Start - start).Duration() <= Tolerance)
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>Returns the selected index only when its timestamp, speaker, and text still identify that segment.</summary>
    public static int IndexOf(
        IReadOnlyList<TranscriptSegment> segments,
        int selectedIndex,
        TimeSpan start,
        string speakerId,
        string text)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(speakerId);
        ArgumentNullException.ThrowIfNull(text);
        if ((uint)selectedIndex >= (uint)segments.Count)
        {
            return -1;
        }

        var segment = segments[selectedIndex];
        return (segment.Start - start).Duration() <= Tolerance &&
            string.Equals(segment.SpeakerId?.Trim(), speakerId.Trim(), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(segment.Text?.Trim(), text.Trim(), StringComparison.Ordinal)
                ? selectedIndex
                : -1;
    }
}

/// <summary>When the live summary runs during a recording (design §7.2).</summary>
public static class LiveSummaryPolicy
{
    /// <summary>The user setting wins; "automatic" means on only with a usable GPU. Always needs the model.</summary>
    public static bool ShouldRun(SummarySettings settings, bool gpuAvailable, bool languageModelInstalled)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return languageModelInstalled && settings.IsLiveSummaryEnabled(gpuAvailable);
    }

    /// <summary>"cpu" means no GPU whatever the hardware; a probe that throws means no GPU.</summary>
    public static bool GpuUsable(AiSettings settings, Func<bool> probe)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(probe);
        if (settings.Gpu == GpuPreference.Cpu)
        {
            return false;
        }

        try
        {
            return probe();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return false;
        }
    }
}

/// <summary>
/// Applies the "ai.llm_model" / "ai.live_whisper_model" settings on top of the detected AI pack:
/// an override whose file exists replaces the pack's component (and works without a pack).
/// </summary>
public static class AiPackOverrides
{
    public static AiPackStatus Apply(
        AiPackStatus pack,
        string? languageModelPath,
        string? liveWhisperPath,
        Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(fileExists);
        var overrides = new Dictionary<AiPackComponent, string>();
        Add(AiPackComponent.LanguageModel, languageModelPath);
        Add(AiPackComponent.LiveWhisper, liveWhisperPath);
        if (overrides.Count == 0)
        {
            return pack;
        }

        var paths = new Dictionary<AiPackComponent, string>(pack.Paths);
        foreach (var (component, path) in overrides)
        {
            paths[component] = path;
        }

        return pack with
        {
            IsInstalled = true,
            Paths = paths,
            Missing = pack.Missing.Where(component => !overrides.ContainsKey(component)).ToList(),
        };

        void Add(AiPackComponent component, string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            var trimmed = path.Trim();
            try
            {
                if (fileExists(trimmed))
                {
                    overrides[component] = trimmed;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // A bad override path behaves like no override.
            }
        }
    }
}
