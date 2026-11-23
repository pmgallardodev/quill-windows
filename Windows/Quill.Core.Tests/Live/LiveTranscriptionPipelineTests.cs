using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Quill.Core.Domain;
using Quill.Core.Live;
using Quill.Core.Ports;
using Quill.Core.Storage;
using Xunit;
using static Quill.Core.Tests.Live.SignalFactory;

namespace Quill.Core.Tests.Live;

public sealed class LiveTranscriptionPipelineTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly DateTimeOffset T0 = new(2026, 11, 23, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ChunksEachTrackAndCollectsSessionRelativeSegments()
    {
        var transcriber = new FakeStreamingTranscriber();
        var buffer = new LiveTranscriptBuffer();
        await using var pipeline = new LiveTranscriptionPipeline(transcriber, buffer);
        pipeline.Start();

        await pipeline.PushAsync(Track.Microphone, T0, Concat(Speech(4), Silence(1)), CancellationToken.None);
        await pipeline.PushAsync(Track.System, T0.AddSeconds(2), Concat(Silence(1), Speech(4), Silence(1)), CancellationToken.None);
        await pipeline.CompleteAsync(CancellationToken.None).WaitAsync(Timeout);

        var segments = buffer.Snapshot();
        Assert.Equal(2, segments.Count);
        Assert.Equal(Track.Microphone, segments[0].Track);
        Assert.Equal(SpeakerIds.Me, segments[0].SpeakerId);
        Assert.Equal(Track.System, segments[1].Track);
        Assert.InRange(segments[1].Start, TimeSpan.FromSeconds(2.6), TimeSpan.FromSeconds(3.1));
        Assert.True(transcriber.Completed);
        Assert.Equal(2, pipeline.ChunksFed);
    }

    [Fact]
    public async Task CompleteFlushesPendingSpeech()
    {
        var transcriber = new FakeStreamingTranscriber();
        var buffer = new LiveTranscriptBuffer();
        await using var pipeline = new LiveTranscriptionPipeline(transcriber, buffer);
        pipeline.Start();

        await pipeline.PushAsync(Track.Microphone, T0, Speech(2), CancellationToken.None);
        Assert.Equal(0, pipeline.ChunksFed);
        await pipeline.CompleteAsync(CancellationToken.None).WaitAsync(Timeout);

        Assert.Single(buffer.Snapshot());
    }

    [Fact]
    public async Task PersistsLiveTranscriptWhenConfigured()
    {
        using var temp = new TempDirectory();
        temp.Session("s1");
        var store = new FileSessionStore(temp.Path);
        var buffer = new LiveTranscriptBuffer();
        var persistence = new LiveTranscriptPersistence(store, "s1", new TranscriptInfo("whisper.net", "small"));
        await using var pipeline = new LiveTranscriptionPipeline(new FakeStreamingTranscriber(), buffer, persistence);
        pipeline.Start();

        await pipeline.PushAsync(Track.System, T0, Concat(Speech(4), Silence(1)), CancellationToken.None);
        await pipeline.CompleteAsync(CancellationToken.None).WaitAsync(Timeout);

        Assert.Single(await store.LoadTranscriptAsync("s1", TranscriptPass.Live, CancellationToken.None));
    }

    [Fact]
    public async Task TranscriberFaultIsRecordedNotThrown()
    {
        var transcriber = new FakeStreamingTranscriber { FailReading = true };
        await using var pipeline = new LiveTranscriptionPipeline(transcriber, new LiveTranscriptBuffer());
        pipeline.Start();

        await pipeline.PushAsync(Track.System, T0, Concat(Speech(4), Silence(1)), CancellationToken.None);
        await pipeline.CompleteAsync(CancellationToken.None).WaitAsync(Timeout);

        Assert.IsType<InvalidOperationException>(pipeline.Fault);
    }

    [Fact]
    public async Task PersistenceFailureIsRecordedNotThrown()
    {
        using var temp = new TempDirectory();
        var store = new FileSessionStore(temp.Path);
        var persistence = new LiveTranscriptPersistence(store, "missing", new TranscriptInfo("whisper.net", "small"));
        var buffer = new LiveTranscriptBuffer();
        await using var pipeline = new LiveTranscriptionPipeline(new FakeStreamingTranscriber(), buffer, persistence);
        pipeline.Start();

        await pipeline.PushAsync(Track.System, T0, Concat(Speech(4), Silence(1)), CancellationToken.None);
        await pipeline.CompleteAsync(CancellationToken.None).WaitAsync(Timeout);

        Assert.Single(buffer.Snapshot());
        Assert.NotNull(pipeline.LastPersistenceError);
    }

    [Fact]
    public async Task PushAfterCompleteIsIgnored()
    {
        var transcriber = new FakeStreamingTranscriber();
        await using var pipeline = new LiveTranscriptionPipeline(transcriber, new LiveTranscriptBuffer());
        pipeline.Start();
        await pipeline.CompleteAsync(CancellationToken.None).WaitAsync(Timeout);

        await pipeline.PushAsync(Track.System, T0, Concat(Speech(4), Silence(1)), CancellationToken.None);

        Assert.Equal(0, pipeline.ChunksFed);
    }

    /// <summary>Emits one segment per fed chunk covering the whole chunk.</summary>
    private sealed class FakeStreamingTranscriber : IStreamingTranscriber
    {
        private readonly Channel<TranscriptSegment> _segments = Channel.CreateUnbounded<TranscriptSegment>();
        private int _count;

        public bool Completed { get; private set; }

        public bool FailReading { get; init; }

        public ValueTask FeedAsync(AudioChunk chunk, CancellationToken cancellationToken)
        {
            _count++;
            _segments.Writer.TryWrite(new TranscriptSegment(
                chunk.Start,
                chunk.End,
                $"chunk {_count}",
                LiveSpeakers.ProvisionalFor(chunk.Track),
                chunk.Track,
                Editable: false));
            return ValueTask.CompletedTask;
        }

        public ValueTask CompleteAsync(CancellationToken cancellationToken)
        {
            Completed = true;
            _segments.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }

        public async IAsyncEnumerable<TranscriptSegment> ReadSegmentsAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var segment in _segments.Reader.ReadAllAsync(cancellationToken))
            {
                if (FailReading)
                {
                    throw new InvalidOperationException("model crashed");
                }

                yield return segment;
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
