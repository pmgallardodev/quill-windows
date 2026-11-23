using NAudio.Wave;

namespace Quill.Windows.Playback;

/// <summary>
/// Changes playback speed without changing pitch (WSOLA: waveform-similarity overlap-add).
/// NAudio has no time-stretcher; resampling would make voices sound like chipmunks. Each
/// output step takes a Hann-windowed frame from the input near where the speed says it
/// should be, choosing the exact start that best continues the previous frame, and
/// overlap-adds it at 50 %. Good enough for speech at 1x-2x. Mono only.
/// Not thread-safe: the caller serializes <see cref="Read"/>, <see cref="Reset"/> and speed changes.
/// </summary>
internal sealed class TimeStretchSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _frame;
    private readonly int _hop;
    private readonly int _tolerance;
    private readonly float[] _window;
    private readonly float[] _readBuffer;
    private readonly List<float> _input = new();
    private readonly Queue<float> _output = new();
    private float[] _tail;
    private long _inputStart;
    private double _analysis; // absolute input index of the next ideal frame start
    private long _previous = -1;
    private bool _sourceEnded;
    private bool _flushed;
    private double _speed = 1d;

    public TimeStretchSampleProvider(ISampleProvider source)
    {
        if (source.WaveFormat.Channels != 1)
        {
            throw new ArgumentException("time stretch expects mono", nameof(source));
        }

        _source = source;
        var rate = source.WaveFormat.SampleRate;
        _hop = Math.Max(64, rate * 15 / 1000);          // 15 ms out per step
        _frame = _hop * 2;                               // 30 ms frames, 50 % overlap
        _tolerance = Math.Max(16, rate * 8 / 1000);      // search +-8 ms
        _window = new float[_frame];
        for (var index = 0; index < _frame; index++)
        {
            _window[index] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * index / _frame));
        }

        _tail = new float[_hop];
        _readBuffer = new float[_frame];
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    /// <summary>1 = untouched. Clamped to 0.5..2.5.</summary>
    public double Speed
    {
        get => _speed;
        set
        {
            var speed = Math.Clamp(value, 0.5d, 2.5d);
            if (Math.Abs(speed - _speed) > 0.001)
            {
                _speed = speed;
                Reset();
            }
        }
    }

    /// <summary>Forgets buffered audio (after a seek or a speed change).</summary>
    public void Reset()
    {
        _input.Clear();
        _output.Clear();
        _tail = new float[_hop];
        _inputStart = 0;
        _analysis = 0;
        _previous = -1;
        _sourceEnded = false;
        _flushed = false;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        if (Math.Abs(_speed - 1d) < 0.001 && _output.Count == 0)
        {
            return _source.Read(buffer, offset, count);
        }

        while (_output.Count < count && Step())
        {
        }

        var produced = Math.Min(count, _output.Count);
        for (var index = 0; index < produced; index++)
        {
            buffer[offset + index] = _output.Dequeue();
        }

        return produced;
    }

    /// <summary>One overlap-add step. False when the source is exhausted.</summary>
    private bool Step()
    {
        var ideal = (long)Math.Round(_analysis);
        var searchEnd = ideal + _tolerance + _frame;
        if (!Fill(searchEnd))
        {
            Flush();
            return false;
        }

        long chosen;
        if (_previous < 0)
        {
            chosen = ideal;
        }
        else
        {
            var natural = _previous + _hop;
            chosen = ideal;
            var best = double.NegativeInfinity;
            var from = Math.Max(_inputStart, ideal - _tolerance);
            for (var candidate = from; candidate <= ideal + _tolerance; candidate += 2)
            {
                var score = 0d;
                for (var index = 0; index < _hop; index += 2)
                {
                    score += At(candidate + index) * At(natural + index);
                }

                if (score > best)
                {
                    best = score;
                    chosen = candidate;
                }
            }
        }

        for (var index = 0; index < _hop; index++)
        {
            _output.Enqueue(_tail[index] + At(chosen + index) * _window[index]);
        }

        for (var index = 0; index < _hop; index++)
        {
            _tail[index] = At(chosen + _hop + index) * _window[_hop + index];
        }

        _previous = chosen;
        _analysis += _hop * _speed;
        Discard(Math.Min(chosen + _hop, (long)Math.Round(_analysis) - _tolerance));
        return true;
    }

    private float At(long absolute) => _input[(int)(absolute - _inputStart)];

    /// <summary>Reads from the source until input up to <paramref name="end"/> is buffered.</summary>
    private bool Fill(long end)
    {
        while (_inputStart + _input.Count < end)
        {
            if (_sourceEnded)
            {
                return false;
            }

            var read = _source.Read(_readBuffer, 0, _readBuffer.Length);
            if (read <= 0)
            {
                _sourceEnded = true;
                return false;
            }

            for (var index = 0; index < read; index++)
            {
                _input.Add(_readBuffer[index]);
            }
        }

        return true;
    }

    private void Discard(long before)
    {
        var count = (int)Math.Clamp(before - _inputStart, 0, _input.Count);
        if (count > 0)
        {
            _input.RemoveRange(0, count);
            _inputStart += count;
        }
    }

    private void Flush()
    {
        if (_flushed)
        {
            return;
        }

        _flushed = true;
        foreach (var sample in _tail)
        {
            _output.Enqueue(sample);
        }

        _tail = new float[_hop];
    }
}
