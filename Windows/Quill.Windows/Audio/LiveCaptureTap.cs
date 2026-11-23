using System.Buffers;
using System.Threading.Channels;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Quill.Core.Domain;

namespace Quill.Windows.Audio;

/// <summary>Receives converted live audio: 16 kHz mono float samples of one track.</summary>
/// <param name="trackStartedAt">Wall time of the track's first captured buffer.</param>
internal delegate ValueTask LiveAudioSink(
    Track track,
    DateTimeOffset trackStartedAt,
    ReadOnlyMemory<float> samples,
    CancellationToken cancellationToken);

/// <summary>
/// Copies captured buffers off the WASAPI callback for live transcription. The capture side only
/// copies bytes into a bounded queue (never waits, never throws); one consumer converts them to
/// 16 kHz mono float, whatever the device format or the voice-quality setting, and hands them to
/// the sink in arrival order. When the consumer falls behind, new buffers are dropped and counted
/// instead of stalling capture.
/// </summary>
internal sealed class LiveCaptureTap : IAsyncDisposable
{
    public const int SampleRate = 16_000;

    private readonly Channel<Pending> _queue;
    private readonly Dictionary<Track, Converter> _converters = new();
    private readonly CancellationTokenSource _stopping = new();
    private Task _consumer = Task.CompletedTask;
    private long _droppedBuffers;
    private int _started;

    /// <param name="capacity">Buffers queued before new ones are dropped (WASAPI sends about 100 per second per track).</param>
    public LiveCaptureTap(int capacity = 1_000)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _queue = Channel.CreateBounded<Pending>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });
    }

    /// <summary>Buffers dropped because the consumer was behind or the tap was completed.</summary>
    public long DroppedBuffers => Interlocked.Read(ref _droppedBuffers);

    /// <summary>Sink or conversion failure that stopped live audio, if any. Capture is unaffected.</summary>
    public Exception? Fault { get; private set; }

    /// <summary>Registers a track and returns the input its capture callback writes to.</summary>
    public LiveTrackInput AddTrack(Track track, WaveFormat captureFormat)
    {
        ArgumentNullException.ThrowIfNull(captureFormat);
        lock (_converters)
        {
            if (_converters.ContainsKey(track))
            {
                throw new InvalidOperationException($"track {track} is already registered");
            }

            _converters[track] = new Converter(captureFormat);
        }

        return new LiveTrackInput(this, track);
    }

    /// <summary>Starts delivering audio to <paramref name="sink"/>. Buffers written before this wait in the queue.</summary>
    public void Start(LiveAudioSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException("the live tap is already started");
        }

        _consumer = Task.Run(() => ConsumeAsync(sink));
    }

    /// <summary>Stops accepting audio and waits until every queued buffer reached the sink.</summary>
    public async Task CompleteAsync(CancellationToken cancellationToken)
    {
        _queue.Writer.TryComplete();
        if (Volatile.Read(ref _started) == 0)
        {
            DiscardQueued();
            return;
        }

        await _consumer.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        _stopping.Cancel();
        try
        {
            await _consumer.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        DiscardQueued();
        _stopping.Dispose();
    }

    /// <summary>Called on the capture thread. Never blocks and never throws.</summary>
    internal void Write(Track track, DateTimeOffset trackStartedAt, byte[] buffer, int count)
    {
        if (count <= 0)
        {
            return;
        }

        byte[]? copy = null;
        try
        {
            copy = ArrayPool<byte>.Shared.Rent(count);
            Buffer.BlockCopy(buffer, 0, copy, 0, count);
            if (_queue.Writer.TryWrite(new Pending(track, trackStartedAt, copy, count)))
            {
                return;
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Live text is best effort; the WAV writer must keep going.
        }

        if (copy is not null)
        {
            ArrayPool<byte>.Shared.Return(copy);
        }

        Interlocked.Increment(ref _droppedBuffers);
    }

    private async Task ConsumeAsync(LiveAudioSink sink)
    {
        try
        {
            await foreach (var pending in _queue.Reader.ReadAllAsync(_stopping.Token).ConfigureAwait(false))
            {
                try
                {
                    if (Fault is not null)
                    {
                        continue;
                    }

                    Converter converter;
                    lock (_converters)
                    {
                        converter = _converters[pending.Track];
                    }

                    var samples = converter.Convert(pending.Bytes, pending.Count);
                    if (samples.Length > 0)
                    {
                        await sink(pending.Track, pending.TrackStartedAt, samples, _stopping.Token).ConfigureAwait(false);
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    Fault ??= exception;
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(pending.Bytes);
                }
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
        }
    }

    private void DiscardQueued()
    {
        while (_queue.Reader.TryRead(out var pending))
        {
            ArrayPool<byte>.Shared.Return(pending.Bytes);
        }
    }

    private readonly record struct Pending(Track Track, DateTimeOffset TrackStartedAt, byte[] Bytes, int Count);

    /// <summary>Capture format to 16 kHz mono float, same chain as the voice-quality WAV writer.</summary>
    private sealed class Converter
    {
        private readonly BufferedWaveProvider _pending;
        private readonly ISampleProvider _converted;
        private float[] _output = new float[4_096];

        public Converter(WaveFormat captureFormat)
        {
            _pending = new BufferedWaveProvider(captureFormat)
            {
                BufferDuration = TimeSpan.FromSeconds(5),
                DiscardOnBufferOverflow = true,
                ReadFully = false,
            };

            ISampleProvider source = _pending.ToSampleProvider();
            if (source.WaveFormat.Channels > 1)
            {
                source = new MonoDownmixSampleProvider(source);
            }

            _converted = source.WaveFormat.SampleRate == SampleRate
                ? source
                : new WdlResamplingSampleProvider(source, SampleRate);
        }

        public float[] Convert(byte[] bytes, int count)
        {
            _pending.AddSamples(bytes, 0, count);
            var produced = 0;
            while (true)
            {
                if (produced == _output.Length)
                {
                    Array.Resize(ref _output, _output.Length * 2);
                }

                var read = _converted.Read(_output, produced, _output.Length - produced);
                if (read <= 0)
                {
                    break;
                }

                produced += read;
            }

            return produced == 0 ? Array.Empty<float>() : _output.AsSpan(0, produced).ToArray();
        }
    }
}

/// <summary>One track's entry point into a <see cref="LiveCaptureTap"/>, used by the capture callback.</summary>
internal sealed class LiveTrackInput
{
    private readonly LiveCaptureTap _tap;

    internal LiveTrackInput(LiveCaptureTap tap, Track track)
    {
        _tap = tap;
        Track = track;
    }

    public Track Track { get; }

    /// <summary>Queues raw capture bytes. Never blocks and never throws.</summary>
    /// <param name="trackStartedAt">Wall time of the track's first captured buffer (same as the WAV writer's).</param>
    public void Write(DateTimeOffset trackStartedAt, byte[] buffer, int count) =>
        _tap.Write(Track, trackStartedAt, buffer, count);
}
