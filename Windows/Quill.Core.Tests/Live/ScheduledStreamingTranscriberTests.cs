using Quill.Core.Domain;
using Quill.Core.Live;
using Quill.Core.Ports;
using Xunit;
using static Quill.Core.Tests.Live.SignalFactory;

namespace Quill.Core.Tests.Live;

public sealed class ScheduledStreamingTranscriberTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task EmitsProvisionalSessionRelativeSegmentsThroughTheLivePriority()
    {
        var recognizer = new FakeRecognizer(_ => [new RecognizedSpan(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), "  hola  ")]);
        var scheduler = new RecordingScheduler();
        await using var transcriber = new ScheduledStreamingTranscriber(recognizer, scheduler);

        await transcriber.FeedAsync(Chunk(Track.System, 10, 6), CancellationToken.None);
        await transcriber.FeedAsync(Chunk(Track.Microphone, 20, 6), CancellationToken.None);
        await transcriber.CompleteAsync(CancellationToken.None).AsTask().WaitAsync(Timeout);
        var segments = await ReadAllAsync(transcriber);

        Assert.Equal(
            [
                new TranscriptSegment(TimeSpan.FromSeconds(11), TimeSpan.FromSeconds(13), "hola", "speaker-1", Track.System, Editable: false),
                new TranscriptSegment(TimeSpan.FromSeconds(21), TimeSpan.FromSeconds(23), "hola", "me", Track.Microphone, Editable: false),
            ],
            segments);
        Assert.Equal([InferencePriority.LiveTranscription, InferencePriority.LiveTranscription], scheduler.Priorities);
    }

    [Fact]
    public async Task ClampsSpansToTheChunkAndDropsNonSpeechText()
    {
        var recognizer = new FakeRecognizer(_ =>
        [
            new RecognizedSpan(TimeSpan.FromSeconds(-1), TimeSpan.FromSeconds(2), "uno"),
            new RecognizedSpan(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2.5), "   "),
            new RecognizedSpan(TimeSpan.FromSeconds(2.5), TimeSpan.FromSeconds(3), "[Música]"),
            new RecognizedSpan(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(4), "(risas)"),
            new RecognizedSpan(TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(4.5), "♪ ♪"),
            new RecognizedSpan(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), "dos"),
        ]);
        await using var transcriber = new ScheduledStreamingTranscriber(recognizer, new RecordingScheduler());

        await transcriber.FeedAsync(Chunk(Track.Microphone, 0, 6), CancellationToken.None);
        await transcriber.CompleteAsync(CancellationToken.None).AsTask().WaitAsync(Timeout);
        var segments = await ReadAllAsync(transcriber);

        Assert.Equal(["uno", "dos"], segments.Select(segment => segment.Text));
        Assert.Equal(TimeSpan.Zero, segments[0].Start);
        Assert.Equal(TimeSpan.FromSeconds(6), segments[1].End);
    }

    [Fact]
    public async Task FeedNeverWaitsAndDropsTheOldestChunkWhenBehind()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var busy = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recognizer = new FakeRecognizer(async _ =>
        {
            busy.TrySetResult();
            await gate.Task;
            return [new RecognizedSpan(TimeSpan.Zero, TimeSpan.FromSeconds(1), "texto")];
        });
        await using var transcriber = new ScheduledStreamingTranscriber(recognizer, new RecordingScheduler(), maxQueuedChunks: 2);

        await transcriber.FeedAsync(Chunk(Track.System, 10, 1), CancellationToken.None);
        await busy.Task.WaitAsync(Timeout);
        for (var second = 2; second <= 5; second++)
        {
            var feed = transcriber.FeedAsync(Chunk(Track.System, second * 10, second), CancellationToken.None);
            Assert.True(feed.IsCompleted);
            await feed;
        }

        gate.SetResult();
        await transcriber.CompleteAsync(CancellationToken.None).AsTask().WaitAsync(Timeout);
        var segments = await ReadAllAsync(transcriber);

        // Chunk 1 was already being recognized; chunks 2 and 3 were dropped for 4 and 5.
        Assert.Equal([10d, 40d, 50d], segments.Select(segment => segment.Start.TotalSeconds));
        Assert.Equal(2, transcriber.DroppedChunks);
    }

    [Fact]
    public async Task SkipsAFailedChunkAndKeepsTranscribing()
    {
        var calls = 0;
        var recognizer = new FakeRecognizer(_ =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                throw new InvalidOperationException("gpu hiccup");
            }

            return [new RecognizedSpan(TimeSpan.Zero, TimeSpan.FromSeconds(1), "sigue")];
        });
        await using var transcriber = new ScheduledStreamingTranscriber(recognizer, new RecordingScheduler());

        await transcriber.FeedAsync(Chunk(Track.System, 0, 6), CancellationToken.None);
        await transcriber.FeedAsync(Chunk(Track.System, 6, 6), CancellationToken.None);
        await transcriber.CompleteAsync(CancellationToken.None).AsTask().WaitAsync(Timeout);
        var segments = await ReadAllAsync(transcriber);

        var segment = Assert.Single(segments);
        Assert.Equal(TimeSpan.FromSeconds(6), segment.Start);
        Assert.Equal(1, transcriber.FailedChunks);
        Assert.IsType<InvalidOperationException>(transcriber.LastError);
    }

    [Fact]
    public async Task RepeatedFailuresEndTheSegmentStreamWithTheError()
    {
        var recognizer = new FakeRecognizer(AlwaysFails);
        await using var transcriber = new ScheduledStreamingTranscriber(recognizer, new RecordingScheduler());

        for (var index = 0; index < ScheduledStreamingTranscriber.MaxConsecutiveFailures; index++)
        {
            await transcriber.FeedAsync(Chunk(Track.System, index * 6, 6), CancellationToken.None);
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => ReadAllAsync(transcriber)).WaitAsync(Timeout);
    }

    [Fact]
    public async Task FeedAfterCompleteIsRejected()
    {
        await using var transcriber = new ScheduledStreamingTranscriber(new FakeRecognizer(_ => []), new RecordingScheduler());
        await transcriber.CompleteAsync(CancellationToken.None).AsTask().WaitAsync(Timeout);

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await transcriber.FeedAsync(Chunk(Track.System, 0, 1), CancellationToken.None));
        Assert.Empty(await ReadAllAsync(transcriber));
    }

    [Fact]
    public async Task FedSamplesAreCopiedBeforeFeedReturns()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        float firstSample = -1;
        var recognizer = new FakeRecognizer(async samples =>
        {
            await gate.Task;
            firstSample = samples.Span[0];
            return [];
        });
        await using var transcriber = new ScheduledStreamingTranscriber(recognizer, new RecordingScheduler());
        var samples = Tone(1);
        var expected = samples[100];

        await transcriber.FeedAsync(new AudioChunk(Track.System, TimeSpan.Zero, samples.AsMemory(100)), CancellationToken.None);
        Array.Clear(samples);
        gate.SetResult();
        await transcriber.CompleteAsync(CancellationToken.None).AsTask().WaitAsync(Timeout);

        Assert.Equal(expected, firstSample);
    }

    [Fact]
    public async Task DisposeCancelsWorkAndDisposesTheRecognizer()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recognizer = new FakeRecognizer(async (_, cancellationToken) =>
        {
            started.SetResult();
            await Task.Delay(System.Threading.Timeout.Infinite, cancellationToken);
            return [];
        });
        var transcriber = new ScheduledStreamingTranscriber(recognizer, new RecordingScheduler());
        await transcriber.FeedAsync(Chunk(Track.System, 0, 6), CancellationToken.None);
        await started.Task.WaitAsync(Timeout);

        await transcriber.DisposeAsync().AsTask().WaitAsync(Timeout);

        Assert.True(recognizer.Disposed);
    }

    private static IReadOnlyList<RecognizedSpan> AlwaysFails(ReadOnlyMemory<float> samples) =>
        throw new InvalidOperationException("model broken");

    private static AudioChunk Chunk(Track track, double startSeconds, double seconds) =>
        new(track, TimeSpan.FromSeconds(startSeconds), Tone(seconds));

    private static async Task<List<TranscriptSegment>> ReadAllAsync(IStreamingTranscriber transcriber)
    {
        var segments = new List<TranscriptSegment>();
        await foreach (var segment in transcriber.ReadSegmentsAsync(CancellationToken.None))
        {
            segments.Add(segment);
        }

        return segments;
    }

    private sealed class FakeRecognizer : IChunkRecognizer
    {
        private readonly Func<ReadOnlyMemory<float>, CancellationToken, Task<IReadOnlyList<RecognizedSpan>>> _recognize;

        public FakeRecognizer(Func<ReadOnlyMemory<float>, IReadOnlyList<RecognizedSpan>> recognize)
            => _recognize = (samples, _) => Task.FromResult(recognize(samples));

        public FakeRecognizer(Func<ReadOnlyMemory<float>, Task<IReadOnlyList<RecognizedSpan>>> recognize)
            => _recognize = (samples, _) => recognize(samples);

        public FakeRecognizer(Func<ReadOnlyMemory<float>, CancellationToken, Task<IReadOnlyList<RecognizedSpan>>> recognize)
            => _recognize = recognize;

        public bool Disposed { get; private set; }

        public Task<IReadOnlyList<RecognizedSpan>> RecognizeAsync(ReadOnlyMemory<float> samples, CancellationToken cancellationToken)
            => _recognize(samples, cancellationToken);

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Runs jobs inline and records their priority.</summary>
    private sealed class RecordingScheduler : IInferenceScheduler
    {
        private readonly List<InferencePriority> _priorities = [];

        public IReadOnlyList<InferencePriority> Priorities
        {
            get
            {
                lock (_priorities)
                {
                    return _priorities.ToList();
                }
            }
        }

        public int PendingCount => 0;

        public Task<T> RunAsync<T>(InferencePriority priority, Func<CancellationToken, Task<T>> job, CancellationToken cancellationToken = default)
        {
            lock (_priorities)
            {
                _priorities.Add(priority);
            }

            return job(cancellationToken);
        }

        public async Task<(bool Ran, T? Result)> TryRunAsync<T>(InferencePriority priority, Func<CancellationToken, Task<T>> job, CancellationToken cancellationToken = default)
            => (true, await RunAsync(priority, job, cancellationToken));
    }
}
