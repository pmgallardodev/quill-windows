namespace Quill.Core.Live;

/// <summary>
/// Fixed-capacity FIFO of float samples. Writing past capacity overwrites the oldest samples and
/// counts them in <see cref="OverwrittenSamples"/>, so memory never grows during a long meeting.
/// Not thread-safe: owned by a single producer (one <see cref="SpeechChunker"/>).
/// </summary>
public sealed class AudioRingBuffer
{
    private readonly float[] _samples;
    private int _head;

    public AudioRingBuffer(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _samples = new float[capacity];
    }

    public int Capacity => _samples.Length;

    public int Count { get; private set; }

    /// <summary>Samples lost because a write exceeded the capacity.</summary>
    public long OverwrittenSamples { get; private set; }

    public void Write(ReadOnlySpan<float> samples)
    {
        if (samples.Length > Capacity)
        {
            OverwrittenSamples += samples.Length - Capacity;
            samples = samples[^Capacity..];
        }

        var overflow = Count + samples.Length - Capacity;
        if (overflow > 0)
        {
            DiscardOldest(overflow);
            OverwrittenSamples += overflow;
        }

        var tail = (_head + Count) % Capacity;
        var first = Math.Min(samples.Length, Capacity - tail);
        samples[..first].CopyTo(_samples.AsSpan(tail));
        samples[first..].CopyTo(_samples);
        Count += samples.Length;
    }

    public void DiscardOldest(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        count = Math.Min(count, Count);
        _head = (_head + count) % Capacity;
        Count -= count;
    }

    public void Clear()
    {
        _head = 0;
        Count = 0;
    }

    /// <summary>Copies the buffered samples, oldest first.</summary>
    public float[] ToArray()
    {
        var result = new float[Count];
        var first = Math.Min(Count, Capacity - _head);
        _samples.AsSpan(_head, first).CopyTo(result);
        _samples.AsSpan(0, Count - first).CopyTo(result.AsSpan(first));
        return result;
    }
}
