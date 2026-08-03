using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Quill.Windows.Sessions;
using Whisper.net;

namespace Quill.Windows.Transcription;

internal sealed class WhisperTranscriber : IDisposable
{
    private readonly TranscriptionOptions _options;
    private readonly WhisperFactory _factory;
    private readonly WhisperVadFactory? _vadFactory;

    public WhisperTranscriber(TranscriptionOptions options)
    {
        if (!File.Exists(options.ModelPath))
        {
            throw new FileNotFoundException("no se encontro el modelo Whisper incluido", options.ModelPath);
        }

        if (options.VadModelPath is { } vadModelPath && !File.Exists(vadModelPath))
        {
            throw new FileNotFoundException("no se encontro el modelo VAD", vadModelPath);
        }

        _options = options;
        _factory = WhisperFactory.FromPath(options.ModelPath);
        _vadFactory = options.VadModelPath is null ? null : WhisperVadFactory.FromPath(options.VadModelPath);
    }

    public async Task<IReadOnlyList<TranscriptSegment>> TranscribeAsync(
        string audioPath,
        string speaker,
        long offsetMilliseconds,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var normalizedPath = TemporaryPathFor(audioPath);
        try
        {
            var total = NormalizeToWhisperWav(audioPath, normalizedPath);
            if (total <= TimeSpan.Zero)
            {
                progress?.Report(1d);
                return Array.Empty<TranscriptSegment>();
            }

            var segments = new List<TranscriptSegment>();
            foreach (var block in DetectSpeechBlocks(normalizedPath, total))
            {
                cancellationToken.ThrowIfCancellationRequested();
                segments.AddRange(await TranscribeBlockAsync(
                    normalizedPath, block, speaker, offsetMilliseconds, total, progress, cancellationToken));
            }

            progress?.Report(1d);
            return segments;
        }
        finally
        {
            Delete(normalizedPath);
        }
    }

    public void Dispose()
    {
        _vadFactory?.Dispose();
        _factory.Dispose();
    }

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

    /// <summary>Corta un rango a su propio WAV para poder transcribirlo por separado.</summary>
    internal static void ExtractRange(string inputPath, string outputPath, TimeSpan start, TimeSpan end)
    {
        using var reader = new WaveFileReader(inputPath);
        var source = new OffsetSampleProvider(reader.ToSampleProvider())
        {
            SkipOver = start,
            Take = end - start,
        };
        WaveFileWriter.CreateWaveFile16(outputPath, source);
    }

    /// <summary>
    /// Sin VAD hay un unico bloque que cubre todo el audio, de modo que el camino
    /// normal y el optimizado comparten codigo y solo cambia como se trocea.
    /// </summary>
    private IReadOnlyList<SpeechBlock> DetectSpeechBlocks(string normalizedPath, TimeSpan total)
    {
        if (_vadFactory is null)
        {
            return new[] { new SpeechBlock(TimeSpan.Zero, total) };
        }

        using var processor = _vadFactory.CreateBuilder()
            .WithThreads(_options.Threads)
            .WithMinSpeechDuration(TimeSpan.FromMilliseconds(250))
            .WithMinSilenceDuration(TimeSpan.FromSeconds(1))
            .WithSpeechPadding(TimeSpan.FromMilliseconds(300))
            .Build();

        using var stream = File.OpenRead(normalizedPath);
        return processor.DetectSpeech(stream)
            .Select(segment => new SpeechBlock(
                segment.Start < TimeSpan.Zero ? TimeSpan.Zero : segment.Start,
                segment.End > total ? total : segment.End))
            .Where(block => block.End > block.Start)
            .ToList();
    }

    private async Task<IReadOnlyList<TranscriptSegment>> TranscribeBlockAsync(
        string normalizedPath,
        SpeechBlock block,
        string speaker,
        long offsetMilliseconds,
        TimeSpan total,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        // Los tiempos que devuelve Whisper son relativos al bloque, asi que el inicio
        // del bloque entra en el offset. Sin eso, las pistas me/them se desalinean.
        var blockOffsetMilliseconds = (long)block.Start.TotalMilliseconds + offsetMilliseconds;
        var wholeFile = block.Start == TimeSpan.Zero && block.End >= total;
        var sourcePath = normalizedPath;
        if (!wholeFile)
        {
            sourcePath = TemporaryPathFor(normalizedPath);
            ExtractRange(normalizedPath, sourcePath, block.Start, block.End);
        }

        try
        {
            using var processor = _factory.CreateBuilder()
                .WithLanguage(_options.Language)
                .WithThreads(_options.Threads)
                .Build();
            await using var audio = File.OpenRead(sourcePath);
            var segments = new List<TranscriptSegment>();
            await foreach (var result in processor.ProcessAsync(audio).WithCancellation(cancellationToken))
            {
                segments.Add(new TranscriptSegment(
                    speaker,
                    (long)result.Start.TotalMilliseconds + blockOffsetMilliseconds,
                    (long)result.End.TotalMilliseconds + blockOffsetMilliseconds,
                    result.Text.Trim()));

                progress?.Report(Math.Clamp((block.Start + result.End) / total, 0d, 1d));
            }

            return segments;
        }
        finally
        {
            if (!wholeFile)
            {
                Delete(sourcePath);
            }
        }
    }

    private static string TemporaryPathFor(string audioPath) => Path.Combine(
        Path.GetDirectoryName(audioPath)!,
        $".{Path.GetFileNameWithoutExtension(audioPath)}.{Guid.NewGuid():N}.whisper.wav");

    private static void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Un temporal que sobrevive no justifica perder la transcripcion.
        }
    }

    private readonly record struct SpeechBlock(TimeSpan Start, TimeSpan End);
}
