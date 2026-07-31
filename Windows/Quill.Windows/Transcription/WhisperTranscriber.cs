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
        CancellationToken cancellationToken)
    {
        var normalizedPath = Path.Combine(
            Path.GetDirectoryName(audioPath)!,
            $".{Path.GetFileNameWithoutExtension(audioPath)}.{Guid.NewGuid():N}.whisper.wav");
        try
        {
            NormalizeToWhisperWav(audioPath, normalizedPath);
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
            }

            return segments;
        }
        finally
        {
            File.Delete(normalizedPath);
        }
    }

    public void Dispose() => _factory.Dispose();

    private static void NormalizeToWhisperWav(string inputPath, string outputPath)
    {
        using var reader = new AudioFileReader(inputPath);
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
    }
}
