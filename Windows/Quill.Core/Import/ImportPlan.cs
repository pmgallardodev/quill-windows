using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Quill.Core.Domain;

namespace Quill.Core.Import;

/// <summary>Who speaks in a single imported file.</summary>
public enum ImportRole
{
    /// <summary>Remote people (a call recorded from the speakers): system track, "them".</summary>
    Others,

    /// <summary>Only the owner (a dictation): microphone track, "me".</summary>
    Me,

    /// <summary>
    /// Everybody on one file (a room recorder). Kept on the system track so it is never
    /// labelled "me"; speaker separation can split it later.
    /// </summary>
    Mixed,
}

/// <summary>How the files of an import map to tracks (recorded in meta.json "import.mode").</summary>
public enum ImportMode
{
    Others,
    Me,
    Mixed,

    /// <summary>Two files: microphone + system, like a Quill recording.</summary>
    Pair,
}

/// <summary>A file chosen for import.</summary>
/// <param name="Path">Absolute path of the source file.</param>
/// <param name="Created">When the recording started, as well as the file system can tell.</param>
public sealed record ImportSource(string Path, DateTimeOffset Created);

/// <summary>One track of the new session: which source becomes which WAV.</summary>
public sealed record ImportTrack(Track Track, string SourcePath, string FileName);

/// <summary>
/// Pure plan for turning one or two existing recordings into a standard session folder:
/// which file feeds which track, title, start time, folder name and meta.json. The
/// Windows importer converts the audio (16 kHz mono WAV) and follows this plan.
/// </summary>
public sealed class ImportPlan
{
    /// <summary>Sample rate of the converted WAVs, what the transcriber consumes.</summary>
    public const int SampleRate = 16_000;

    private const string FolderFormat = "yyyy.MM.dd-HHmm";
    private const int MinimumCommonTitle = 3;

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        // Titles come from file names: keep accents readable in meta.json.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly string[] MicrophoneHints = { "mic", "micro", "microphone", "microfono", "me", "yo", "local", "input" };
    private static readonly string[] SystemHints = { "system", "sys", "sistema", "loopback", "them", "otros", "remote", "output" };

    private ImportPlan(ImportMode mode, string? title, DateTimeOffset started, IReadOnlyList<ImportTrack> tracks)
    {
        Mode = mode;
        Title = title;
        Started = started;
        Tracks = tracks;
    }

    /// <summary>Extensions offered by the file picker (lower case, with dot).</summary>
    public static IReadOnlyList<string> SupportedExtensions { get; } = new[] { ".wav", ".mp3", ".m4a", ".mp4" };

    public ImportMode Mode { get; }

    /// <summary>From the file name(s); null when nothing usable is left.</summary>
    public string? Title { get; }

    public DateTimeOffset Started { get; }

    /// <summary>Microphone first, then system.</summary>
    public IReadOnlyList<ImportTrack> Tracks { get; }

    public static bool IsSupported(string path) =>
        SupportedExtensions.Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// One file goes to the track <paramref name="role"/> implies. Two files become
    /// microphone + system: the file whose name hints "mic"/"yo" is the microphone,
    /// otherwise the first one; <paramref name="swapPair"/> exchanges them.
    /// </summary>
    /// <exception cref="ArgumentException">Not one or two files, unsupported format, or the same file twice.</exception>
    public static ImportPlan Create(IReadOnlyList<ImportSource> sources, ImportRole role = ImportRole.Others, bool swapPair = false)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Count is < 1 or > 2)
        {
            throw new ArgumentException("import takes one or two files", nameof(sources));
        }

        foreach (var source in sources)
        {
            if (source is null || string.IsNullOrWhiteSpace(source.Path) || !IsSupported(source.Path))
            {
                throw new ArgumentException($"unsupported file: {source?.Path}", nameof(sources));
            }
        }

        var started = sources.Min(source => source.Created);
        if (sources.Count == 1)
        {
            var source = sources[0];
            var track = role == ImportRole.Me ? Track.Microphone : Track.System;
            var mode = role switch
            {
                ImportRole.Me => ImportMode.Me,
                ImportRole.Mixed => ImportMode.Mixed,
                _ => ImportMode.Others,
            };
            return new ImportPlan(mode, TitleOf(source.Path), started, new[] { TrackFor(track, source.Path) });
        }

        if (string.Equals(
                System.IO.Path.GetFullPath(sources[0].Path),
                System.IO.Path.GetFullPath(sources[1].Path),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("the same file was chosen twice", nameof(sources));
        }

        var (microphone, system) = MicrophoneHint(sources[1].Path) > MicrophoneHint(sources[0].Path)
            ? (sources[1], sources[0])
            : (sources[0], sources[1]);
        if (swapPair)
        {
            (microphone, system) = (system, microphone);
        }

        return new ImportPlan(
            ImportMode.Pair,
            CommonTitle(microphone.Path, system.Path) ?? TitleOf(microphone.Path),
            started,
            new[] { TrackFor(Track.Microphone, microphone.Path), TrackFor(Track.System, system.Path) });
    }

    /// <summary>Same format as the recorder ("2026.11.20-0930"), with "-2", "-3"... when taken.</summary>
    public string FolderName(Func<string, bool> isTaken)
    {
        ArgumentNullException.ThrowIfNull(isTaken);
        var baseName = Started.ToString(FolderFormat, CultureInfo.InvariantCulture);
        for (var suffix = 1; suffix < 10_000; suffix++)
        {
            var candidate = suffix == 1 ? baseName : $"{baseName}-{suffix}";
            if (!isTaken(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("no free session folder name");
    }

    /// <summary>
    /// meta.json for the new session (v1 keys plus an additive "import" object that older
    /// readers ignore). Every track starts at offset 0; the session lasts as long as the
    /// longest track. Source paths are reduced to file names.
    /// </summary>
    /// <param name="durations">Duration of each converted track; one per planned track.</param>
    public string MetadataJson(IReadOnlyDictionary<Track, TimeSpan> durations)
    {
        ArgumentNullException.ThrowIfNull(durations);
        var missing = Tracks.Where(track => !durations.ContainsKey(track.Track)).ToList();
        if (missing.Count > 0 || durations.Values.Any(duration => duration < TimeSpan.Zero))
        {
            throw new ArgumentException("every planned track needs a non-negative duration", nameof(durations));
        }

        var length = Tracks.Max(track => durations[track.Track]);
        var files = new JsonObject();
        var offsets = new JsonObject();
        foreach (var track in Tracks)
        {
            files[TrackNames.ToName(track.Track)] = track.FileName;
            offsets[TrackNames.ToName(track.Track)] = 0L;
        }

        var sources = new JsonArray();
        foreach (var track in Tracks)
        {
            sources.Add(System.IO.Path.GetFileName(track.SourcePath));
        }

        var document = new JsonObject
        {
            ["started"] = Iso(Started),
            ["ended"] = Iso(Started + length),
            ["duration_seconds"] = (long)Math.Round(length.TotalSeconds, MidpointRounding.AwayFromZero),
            ["files"] = files,
            ["start_offset_ms"] = offsets,
            ["title"] = Title,
            ["markers"] = new JsonArray(),
            ["import"] = new JsonObject
            {
                ["mode"] = Mode.ToString().ToLowerInvariant(),
                ["sources"] = sources,
            },
        };
        return JsonSerializer.Serialize(document, WriteOptions);
    }

    /// <summary>Round-trip text, what System.Text.Json writes for a DateTimeOffset.</summary>
    private static string Iso(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);

    private static ImportTrack TrackFor(Track track, string sourcePath) =>
        new(track, sourcePath, $"{TrackNames.ToName(track)}.wav");

    private static string? TitleOf(string path)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(path).Trim();
        return name.Length == 0 ? null : name;
    }

    /// <summary>1 when the name says microphone, -1 when it says system, 0 when it says nothing.</summary>
    private static int MicrophoneHint(string path)
    {
        var words = Regex.Split(System.IO.Path.GetFileNameWithoutExtension(path).ToLowerInvariant(), @"[^\p{L}\p{N}]+");
        if (words.Any(word => MicrophoneHints.Contains(word)))
        {
            return 1;
        }

        return words.Any(word => SystemHints.Contains(word)) ? -1 : 0;
    }

    private static string? CommonTitle(string first, string second)
    {
        var left = System.IO.Path.GetFileNameWithoutExtension(first);
        var right = System.IO.Path.GetFileNameWithoutExtension(second);
        var length = 0;
        while (length < left.Length && length < right.Length &&
               char.ToLowerInvariant(left[length]) == char.ToLowerInvariant(right[length]))
        {
            length++;
        }

        var common = left[..length].TrimEnd(' ', '-', '_', '.', '(', '[').Trim();
        return common.Length >= MinimumCommonTitle ? common : null;
    }
}
