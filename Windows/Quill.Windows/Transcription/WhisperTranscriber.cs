using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Quill.Windows.Sessions;
using Whisper.net;

namespace Quill.Windows.Transcription;

internal sealed class WhisperTranscriber : IDisposable
{
    private readonly WhisperFactory _factory;

    public WhisperTranscriber(string modelPath)
    {
        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException("no se encontro el modelo Whisper incluido", modelPath);
        }

        _factory = WhisperFactory.FromPath(modelPath);
    }

    public async Task<IReadOnlyList<TranscriptSegment>> TranscribeAsync(
        string audioPath,
        string speaker,
        long offsetMilliseconds,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var normalizedPath = Path.Combine(
            Path.GetDirectoryName(audioPath)!,
            $".{Path.GetFileNameWithoutExtension(audioPath)}.{Guid.NewGuid():N}.whisper.wav");
        try
        {
            var total = NormalizeToWhisperWav(audioPath, normalizedPath);
            using var processor = _factory.CreateBuilder().WithLanguage("es").Build();
            await using var audio = File.OpenRead(normalizedPath);
            var segments = new List<TranscriptSegment>();
            await foreach (var result in processor.ProcessAsync(audio).WithCancellation(cancellationToken))
            {
                segments.Add(new TranscriptSegment(
                    speaker,
                    (long)result.Start.TotalMilliseconds + offsetMilliseconds,
                    (long)result.End.TotalMilliseconds + offsetMilliseconds,
                    result.Text.Trim()));

                if (progress is not null && total > TimeSpan.Zero)
                {
                    progress.Report(Math.Clamp(result.End / total, 0d, 1d));
                }
            }

            progress?.Report(1d);
            return segments;
        }
        finally
        {
            File.Delete(normalizedPath);
        }
    }

    public void Dispose() => _factory.Dispose();

    /// <summary>
    /// Convierte a 16 kHz mono y devuelve la duracion del original. AudioFileReader
    /// ya la conoce, y es mas fiable que meta.json porque las pistas tienen offsets
    /// distintos y pueden no durar lo mismo.
    /// </summary>
    internal static TimeSpan NormalizeToWhisperWav(string inputPath, string outputPath)
    {
        using var reader = new AudioFileReader(inputPath);
        var duration = reader.TotalTime;
        ISampleProvider source = reader;
        if (source.WaveFormat.Channels == 2)
        {
            source = new StereoToMonoSampleProvider(source);
        }
        else if (source.WaveFormat.Channels != 1)
        {
            throw new InvalidDataException($"canales no compatibles: {source.WaveFormat.Channels}");
        }

        var resampled = new WdlResamplingSampleProvider(source, 16_000);
        WaveFileWriter.CreateWaveFile16(outputPath, resampled);
        return duration;
    }
}
