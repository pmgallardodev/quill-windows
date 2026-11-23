using System.Runtime.CompilerServices;
using System.Threading.Channels;
using NAudio.Wave;
using Quill.Core.Domain;
using Quill.Core.Live;
using Quill.Core.Ports;
using Quill.Core.Storage;
using Quill.Windows.Transcription;
using Xunit;

namespace Quill.Windows.Tests;

public class LiveTranscriptionModuleTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 23, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void IsOffWithoutTheLiveModel()
    {
        Assert.Null(LiveTranscriptionModule.TryCreate(AiPackStatus.NotInstalled, _ => throw new InvalidOperationException()));
    }

    [Fact]
    public void IsOffWhenTheManifestPointsToAMissingFile()
    {
        using var temp = new TempDirectory();
        var pack = Pack(Path.Combine(temp.Path, "missing.bin"));

        Assert.Null(LiveTranscriptionModule.TryCreate(pack, _ => throw new InvalidOperationException()));
    }

    [Fact]
    public async Task TranscribesCapturedAudioAndWritesTheLiveTranscript()
    {
        using var temp = new TempDirectory();
        var model = Path.Combine(temp.Path, "ggml-small.bin");
        File.WriteAllText(model, "model");
        var session = temp.Session("2026.11.23-1000");
        string? loadedModel = null;
        var module = LiveTranscriptionModule.TryCreate(Pack(model), path =>
        {
            loadedModel = path;
            return new EchoTranscriber();
        });
        Assert.NotNull(module);
        var input = module.Tap.AddTrack(Track.Microphone, new WaveFormat(16_000, 16, 1));

        module.Start(new FileSessionStore(temp.Path), Path.GetFileName(session));
        foreach (var buffer in Pcm(Speech(7), Silence(2)))
        {
            input.Write(T0, buffer, buffer.Length);
        }

        await module.StopAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(model, loadedModel);
        var segment = Assert.Single(module.Buffer.Snapshot());
        Assert.Equal(Track.Microphone, segment.Track);
        Assert.Null(module.Fault);
        Assert.True(File.Exists(Path.Combine(session, FileSessionStore.LiveFolder, FileSessionStore.TranscriptJsonFile)));
    }

    [Fact]
    public async Task ModelLoadFailureSwitchesLiveTextOffWithoutThrowing()
    {
        using var temp = new TempDirectory();
        var model = Path.Combine(temp.Path, "ggml-small.bin");
        File.WriteAllText(model, "model");
        var session = temp.Session("2026.11.23-1000");
        var module = LiveTranscriptionModule.TryCreate(Pack(model), _ => throw new InvalidDataException("bad model"));
        Assert.NotNull(module);
        var input = module.Tap.AddTrack(Track.System, new WaveFormat(16_000, 16, 1));

        module.Start(new FileSessionStore(temp.Path), Path.GetFileName(session));
        input.Write(T0, new byte[320], 320);
        await module.StopAsync(TimeSpan.FromSeconds(10));

        Assert.IsType<InvalidDataException>(module.StartError);
        Assert.Empty(module.Buffer.Snapshot());
        Assert.False(Directory.Exists(Path.Combine(session, FileSessionStore.LiveFolder)));
    }

    [Fact]
    public void WhisperRecognizerRequiresTheModelFile()
    {
        using var temp = new TempDirectory();

        Assert.Throws<FileNotFoundException>(
            () => new WhisperChunkRecognizer(Path.Combine(temp.Path, "missing.bin"), "es", 2));
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(8, 2)]
    [InlineData(16, 4)]
    public void LiveInferenceUsesAQuarterOfTheCores(int processors, int expected)
    {
        Assert.Equal(expected, WhisperStreamingTranscriber.LiveThreads(processors));
    }

    private static AiPackStatus Pack(string liveModel) => new(
        true,
        Path.GetDirectoryName(liveModel),
        "1.0.0",
        new Dictionary<AiPackComponent, string> { [AiPackComponent.LiveWhisper] = liveModel },
        []);

    private static float[] Silence(double seconds) => new float[(int)(seconds * 16_000)];

    /// <summary>A 220 Hz tone with short dips, like syllables, so the chunker sees speech.</summary>
    private static float[] Speech(double seconds)
    {
        var samples = new float[(int)(seconds * 16_000)];
        for (var index = 0; index < samples.Length; index++)
        {
            samples[index] = index % 6_400 >= 5_120 ? 0f : 0.4f * MathF.Sin(2 * MathF.PI * 220 * index / 16_000);
        }

        return samples;
    }

    /// <summary>16-bit PCM in 10 ms capture-sized buffers.</summary>
    private static IEnumerable<byte[]> Pcm(params float[][] parts)
    {
        var all = parts.SelectMany(part => part).ToArray();
        for (var offset = 0; offset < all.Length; offset += 160)
        {
            var count = Math.Min(160, all.Length - offset);
            var bytes = new byte[count * sizeof(short)];
            for (var index = 0; index < count; index++)
            {
                BitConverter.TryWriteBytes(bytes.AsSpan(index * 2), (short)(all[offset + index] * short.MaxValue));
            }

            yield return bytes;
        }
    }

    /// <summary>One segment per chunk, covering the chunk.</summary>
    private sealed class EchoTranscriber : IStreamingTranscriber
    {
        private readonly Channel<TranscriptSegment> _segments = Channel.CreateUnbounded<TranscriptSegment>();

        public ValueTask FeedAsync(AudioChunk chunk, CancellationToken cancellationToken)
        {
            _segments.Writer.TryWrite(new TranscriptSegment(
                chunk.Start, chunk.End, "hola", LiveSpeakers.ProvisionalFor(chunk.Track), chunk.Track, Editable: false));
            return ValueTask.CompletedTask;
        }

        public ValueTask CompleteAsync(CancellationToken cancellationToken)
        {
            _segments.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }

        public async IAsyncEnumerable<TranscriptSegment> ReadSegmentsAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var segment in _segments.Reader.ReadAllAsync(cancellationToken))
            {
                yield return segment;
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
