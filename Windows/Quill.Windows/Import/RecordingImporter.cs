using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Quill.Core.Domain;
using Quill.Core.Import;
using Quill.Windows.Audio;
using Quill.Windows.Sessions;

namespace Quill.Windows.Import;

/// <summary>Turns existing recordings into Quill sessions. Behind an interface so the import dialog is testable.</summary>
internal interface IRecordingImporter
{
    /// <summary>The file with its best-known start time.</summary>
    ImportSource Describe(string path);

    /// <summary>
    /// Converts the planned files to 16 kHz mono WAV and creates the session folder.
    /// Returns the new session directory. Nothing is left behind on failure or cancellation.
    /// </summary>
    Task<string> ImportAsync(ImportPlan plan, IProgress<double>? progress, CancellationToken cancellationToken);
}

/// <summary>
/// Builds the session in a hidden staging folder (no ".quill-session" marker, no meta.json,
/// so neither the list nor the recovery scan sees it), writes meta.json last and then
/// renames the folder into place. The caller enqueues the result for transcription.
/// </summary>
internal sealed class RecordingImporter(string recordingsRoot) : IRecordingImporter
{
    private const string StagingPrefix = ".import-";
    private const string SessionMarker = ".quill-session";

    public ImportSource Describe(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new FileNotFoundException("file not found", path);
        }

        // A copied file gets a new creation time but keeps its write time: the earlier of the
        // two is the closest the file system gets to when the recording happened.
        var created = info.CreationTime < info.LastWriteTime ? info.CreationTime : info.LastWriteTime;
        return new ImportSource(info.FullName, new DateTimeOffset(created));
    }

    public async Task<string> ImportAsync(ImportPlan plan, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        Directory.CreateDirectory(recordingsRoot);
        var staging = Path.Combine(recordingsRoot, $"{StagingPrefix}{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            var durations = new Dictionary<Track, TimeSpan>();
            for (var index = 0; index < plan.Tracks.Count; index++)
            {
                var track = plan.Tracks[index];
                var first = index;
                var trackProgress = new Progress<double>(fraction =>
                    progress?.Report((first + Math.Clamp(fraction, 0d, 1d)) / plan.Tracks.Count));
                durations[track.Track] = await Task.Run(
                    () => Convert(track.SourcePath, Path.Combine(staging, track.FileName), trackProgress, cancellationToken),
                    cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            AtomicFile.Write(Path.Combine(staging, "meta.json"), plan.MetadataJson(durations));
            File.WriteAllBytes(Path.Combine(staging, SessionMarker), Array.Empty<byte>());

            var name = plan.FolderName(candidate =>
                Directory.Exists(Path.Combine(recordingsRoot, candidate)) ||
                File.Exists(Path.Combine(recordingsRoot, candidate)));
            var destination = Path.Combine(recordingsRoot, name);
            Directory.Move(staging, destination);
            progress?.Report(1d);
            return destination;
        }
        catch
        {
            TryDelete(staging);
            throw;
        }
    }

    /// <summary>
    /// Any format Media Foundation decodes (WAV, MP3, AAC in M4A/MP4, the audio track of
    /// a video) to 16 kHz mono 16-bit PCM. Returns the converted duration.
    /// </summary>
    private static TimeSpan Convert(string source, string destination, IProgress<double> progress, CancellationToken cancellationToken)
    {
        using var reader = new MediaFoundationReader(source);
        ISampleProvider samples = reader.ToSampleProvider();
        if (samples.WaveFormat.Channels > 1)
        {
            samples = new MonoDownmixSampleProvider(samples);
        }

        if (samples.WaveFormat.SampleRate != ImportPlan.SampleRate)
        {
            samples = new WdlResamplingSampleProvider(samples, ImportPlan.SampleRate);
        }

        using var writer = new WaveFileWriter(destination, new WaveFormat(ImportPlan.SampleRate, 16, 1));
        var buffer = new float[ImportPlan.SampleRate];
        long written = 0;
        var lastReport = -1;
        int read;
        while ((read = samples.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            writer.WriteSamples(buffer, 0, read);
            written += read;
            if (reader.Length > 0)
            {
                var percent = (int)(100L * reader.Position / reader.Length);
                if (percent != lastReport)
                {
                    lastReport = percent;
                    progress.Report(percent / 100d);
                }
            }
        }

        if (written == 0)
        {
            throw new InvalidDataException($"no audio in {Path.GetFileName(source)}");
        }

        return TimeSpan.FromSeconds((double)written / ImportPlan.SampleRate);
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A leftover hidden staging folder is harmless: it has no marker or meta.json.
        }
    }
}
