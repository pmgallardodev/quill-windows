using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Quill.Windows.Audio;
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
        WhisperFactory? factory = null;
        WhisperVadFactory? vadFactory = null;
        try
        {
            factory = WhisperFactory.FromPath(options.ModelPath);
            vadFactory = options.VadModelPath is null ? null : WhisperVadFactory.FromPath(options.VadModelPath);
        }
        catch
        {
            vadFactory?.Dispose();
            factory?.Dispose();
            throw;
        }

        _factory = factory;
        _vadFactory = vadFactory;
    }

    public async Task<IReadOnlyList<TranscriptSegment>> TranscribeAsync(
        string audioPath,
        string speaker,
        long offsetMilliseconds,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CleanupTemporaries(Path.GetDirectoryName(audioPath)!);
        var normalizedPath = TemporaryPathFor(audioPath);
        try
        {
            var total = await NormalizeToWhisperWavAsync(audioPath, normalizedPath, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (total <= TimeSpan.Zero)
            {
                progress?.Report(1d);
                return Array.Empty<TranscriptSegment>();
            }

            var segments = new List<TranscriptSegment>();
            foreach (var block in await DetectSpeechBlocksAsync(normalizedPath, total, cancellationToken))
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
    /// distintos y pueden no durar lo mismo. Cualquier numero de canales se promedia:
    /// un dispositivo 5.1 o 7.1 no puede dejar la sesion sin transcribir.
    /// </summary>
    internal static TimeSpan NormalizeToWhisperWav(string inputPath, string outputPath)
    {
        using var reader = new AudioFileReader(inputPath);
        var duration = reader.TotalTime;
        ISampleProvider source = reader;
        if (source.WaveFormat.Channels > 1)
        {
            source = new MonoDownmixSampleProvider(source);
        }

        var resampled = new WdlResamplingSampleProvider(source, 16_000);
        WaveFileWriter.CreateWaveFile16(outputPath, resampled);
        return duration;
    }

    private static async Task<TimeSpan> NormalizeToWhisperWavAsync(
        string inputPath,
        string outputPath,
        CancellationToken cancellationToken)
    {
        using var reader = new AudioFileReader(inputPath);
        var duration = reader.TotalTime;
        ISampleProvider source = reader;
        if (source.WaveFormat.Channels > 1)
        {
            source = new MonoDownmixSampleProvider(source);
        }

        var resampled = new WdlResamplingSampleProvider(source, 16_000);
        using var writer = new WaveFileWriter(outputPath, new WaveFormat(16_000, 16, 1));
        var buffer = new float[8_192];
        int read;
        while ((read = resampled.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            writer.WriteSamples(buffer, 0, read);
            await Task.Yield();
        }

        return duration;
    }

    /// <summary>Corta un rango a su propio WAV para poder transcribirlo por separado.</summary>
    internal static void ExtractRange(string inputPath, string outputPath, TimeSpan start, TimeSpan end)
        => ExtractRange(inputPath, outputPath, start, end, CancellationToken.None);

    private static void ExtractRange(
        string inputPath,
        string outputPath,
        TimeSpan start,
        TimeSpan end,
        CancellationToken cancellationToken)
    {
        using var reader = new WaveFileReader(inputPath);
        var source = new OffsetSampleProvider(reader.ToSampleProvider())
        {
            SkipOver = start,
            Take = end - start,
        };
        using var writer = new WaveFileWriter(outputPath, new WaveFormat(reader.WaveFormat.SampleRate, 16, reader.WaveFormat.Channels));
        var buffer = new float[8_192];
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            writer.WriteSamples(buffer, 0, read);
        }
    }

    /// <summary>
    /// Sin VAD hay un unico bloque que cubre todo el audio, de modo que el camino
    /// normal y el optimizado comparten codigo y solo cambia como se trocea.
    /// </summary>
    private async Task<IReadOnlyList<SpeechBlock>> DetectSpeechBlocksAsync(
        string normalizedPath,
        TimeSpan total,
        CancellationToken cancellationToken)
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
        var blocks = new List<SpeechBlock>();
        var detected = await processor.DetectSpeechAsync(stream, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var segment in detected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var block = new SpeechBlock(
                segment.Start < TimeSpan.Zero ? TimeSpan.Zero : segment.Start,
                segment.End > total ? total : segment.End);
            if (block.End > block.Start)
            {
                blocks.Add(block);
            }
        }

        return blocks;
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
        var blockOffsetMilliseconds = checked((long)block.Start.TotalMilliseconds + offsetMilliseconds);
        var wholeFile = block.Start == TimeSpan.Zero && block.End >= total;
        var sourcePath = normalizedPath;
        if (!wholeFile)
        {
            sourcePath = TemporaryPathFor(normalizedPath);
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!wholeFile)
            {
                ExtractRange(normalizedPath, sourcePath, block.Start, block.End, cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();

            var builder = _factory.CreateBuilder()
                .WithLanguage(_options.Language)
                .WithThreads(_options.Threads);
            var prompt = _options.Prompt;
            if (!string.IsNullOrWhiteSpace(prompt))
            {
                // Contexto inicial por bloque, no acumulado: sin esto Whisper inventa
                // una grafia fonetica para los nombres propios y la jerga del equipo.
                builder = builder.WithPrompt(prompt);
            }

            await using var processor = builder.Build();
            await using var audio = File.OpenRead(sourcePath);
            var segments = new List<TranscriptSegment>();
            await foreach (var result in processor.ProcessAsync(audio).WithCancellation(cancellationToken))
            {
                var text = result.Text?.Trim() ?? string.Empty;
                if (text.Length > 0)
                {
                    var startMilliseconds = checked((long)result.Start.TotalMilliseconds + blockOffsetMilliseconds);
                    var endMilliseconds = checked((long)result.End.TotalMilliseconds + blockOffsetMilliseconds);
                    segments.Add(new TranscriptSegment(
                        speaker,
                        startMilliseconds,
                        endMilliseconds,
                        text));
                }

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

    internal static void CleanupTemporaries(string directory)
    {
        try
        {
            var cutoff = DateTime.UtcNow - TimeSpan.FromHours(1);
            foreach (var path in Directory.EnumerateFiles(directory, ".*.whisper.wav"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(path) < cutoff)
                    {
                        Delete(path);
                    }
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    // A locked stale temporary can be retried on the next session.
                }
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
        }
    }

    private readonly record struct SpeechBlock(TimeSpan Start, TimeSpan End);
}
