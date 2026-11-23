using NAudio.Wave;
using Quill.Core.Domain;
using Quill.Windows.Audio;
using Xunit;

namespace Quill.Windows.Tests;

public class LiveCaptureTapTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly DateTimeOffset T0 = new(2026, 11, 23, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ConvertsStereo48kFloatTo16kMono()
    {
        var sink = new CollectingSink();
        await using var tap = new LiveCaptureTap();
        var input = tap.AddTrack(Track.System, WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2));
        tap.Start(sink.Receive);

        // One second in 10 ms callbacks, left 0.6 and right 0.2: the mono mix is 0.4.
        for (var callback = 0; callback < 100; callback++)
        {
            var bytes = FloatBytes(Enumerable.Range(0, 480).SelectMany(_ => new[] { 0.6f, 0.2f }).ToArray());
            input.Write(T0, bytes, bytes.Length);
        }

        await tap.CompleteAsync(CancellationToken.None).WaitAsync(Timeout);

        var samples = sink.Samples(Track.System);
        Assert.InRange(samples.Length, 16_000 - 200, 16_000);
        Assert.All(samples.Skip(200).Take(15_000), sample => Assert.InRange(sample, 0.38f, 0.42f));
        Assert.Equal(0, tap.DroppedBuffers);
    }

    [Fact]
    public async Task PassesThrough16kMonoPcm()
    {
        var sink = new CollectingSink();
        await using var tap = new LiveCaptureTap();
        var input = tap.AddTrack(Track.Microphone, new WaveFormat(16_000, 16, 1));
        tap.Start(sink.Receive);
        var pcm = new short[] { 0, 16_384, -16_384, short.MaxValue };
        var bytes = new byte[pcm.Length * sizeof(short)];
        Buffer.BlockCopy(pcm, 0, bytes, 0, bytes.Length);

        input.Write(T0, bytes, bytes.Length);
        await tap.CompleteAsync(CancellationToken.None).WaitAsync(Timeout);

        var samples = sink.Samples(Track.Microphone);
        Assert.Equal(4, samples.Length);
        Assert.Equal(0.5f, samples[1], 3);
        Assert.Equal(-0.5f, samples[2], 3);
    }

    [Fact]
    public async Task QueuesUntilStartedAndKeepsArrivalOrderAndTrackStart()
    {
        var sink = new CollectingSink();
        await using var tap = new LiveCaptureTap();
        var microphone = tap.AddTrack(Track.Microphone, new WaveFormat(16_000, 16, 1));
        var system = tap.AddTrack(Track.System, new WaveFormat(16_000, 16, 1));
        var bytes = new byte[320];

        system.Write(T0, bytes, bytes.Length);
        microphone.Write(T0.AddMilliseconds(40), bytes, bytes.Length);
        system.Write(T0, bytes, bytes.Length);
        tap.Start(sink.Receive);
        await tap.CompleteAsync(CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal(
            [(Track.System, T0), (Track.Microphone, T0.AddMilliseconds(40)), (Track.System, T0)],
            sink.Calls);
    }

    [Fact]
    public async Task DropsNewBuffersInsteadOfBlockingCaptureWhenFull()
    {
        var sink = new CollectingSink();
        await using var tap = new LiveCaptureTap(capacity: 2);
        var input = tap.AddTrack(Track.System, new WaveFormat(16_000, 16, 1));
        var bytes = new byte[320];

        for (var index = 0; index < 5; index++)
        {
            input.Write(T0, bytes, bytes.Length);
        }

        tap.Start(sink.Receive);
        await tap.CompleteAsync(CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal(3, tap.DroppedBuffers);
        Assert.Equal(2, sink.Calls.Count);
    }

    [Fact]
    public async Task SinkFailureIsRecordedAndNeverReachesCapture()
    {
        await using var tap = new LiveCaptureTap();
        var input = tap.AddTrack(Track.System, new WaveFormat(16_000, 16, 1));
        tap.Start((_, _, _, _) => throw new InvalidOperationException("pipeline broken"));
        var bytes = new byte[320];

        input.Write(T0, bytes, bytes.Length);
        input.Write(T0, bytes, bytes.Length);
        await tap.CompleteAsync(CancellationToken.None).WaitAsync(Timeout);

        Assert.IsType<InvalidOperationException>(tap.Fault);
    }

    [Fact]
    public async Task WritesAfterCompletionAreDropped()
    {
        var sink = new CollectingSink();
        await using var tap = new LiveCaptureTap();
        var input = tap.AddTrack(Track.System, new WaveFormat(16_000, 16, 1));
        tap.Start(sink.Receive);
        await tap.CompleteAsync(CancellationToken.None).WaitAsync(Timeout);

        input.Write(T0, new byte[320], 320);

        Assert.Equal(1, tap.DroppedBuffers);
        Assert.Empty(sink.Calls);
    }

    [Fact]
    public void RejectsTheSameTrackTwice()
    {
        var tap = new LiveCaptureTap();
        tap.AddTrack(Track.System, new WaveFormat(16_000, 16, 1));

        Assert.Throws<InvalidOperationException>(() => tap.AddTrack(Track.System, new WaveFormat(48_000, 16, 2)));
    }

    private static byte[] FloatBytes(float[] samples)
    {
        var bytes = new byte[samples.Length * sizeof(float)];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private sealed class CollectingSink
    {
        private readonly List<(Track Track, DateTimeOffset StartedAt)> _calls = [];
        private readonly Dictionary<Track, List<float>> _samples = new();

        public List<(Track Track, DateTimeOffset StartedAt)> Calls
        {
            get
            {
                lock (_calls)
                {
                    return _calls.ToList();
                }
            }
        }

        public float[] Samples(Track track)
        {
            lock (_calls)
            {
                return _samples.TryGetValue(track, out var samples) ? samples.ToArray() : [];
            }
        }

        public ValueTask Receive(Track track, DateTimeOffset startedAt, ReadOnlyMemory<float> samples, CancellationToken cancellationToken)
        {
            lock (_calls)
            {
                _calls.Add((track, startedAt));
                if (!_samples.TryGetValue(track, out var list))
                {
                    list = [];
                    _samples[track] = list;
                }

                list.AddRange(samples.ToArray());
            }

            return ValueTask.CompletedTask;
        }
    }
}
