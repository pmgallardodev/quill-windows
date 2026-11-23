using Quill.Core.Domain;
using Quill.Core.Ports;

namespace Quill.Core.Live;

/// <summary>Tuning for <see cref="SpeechChunker"/>. Defaults target "live text ≤ 10 s behind" (design §15).</summary>
public sealed record SpeechChunkerOptions
{
    /// <summary>A chunk is cut on a normal pause only once it is at least this long.</summary>
    public TimeSpan MinChunk { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Continuous speech is cut at this length, keeping <see cref="Overlap"/> for the next chunk.</summary>
    public TimeSpan MaxChunk { get; init; } = TimeSpan.FromSeconds(12);

    /// <summary>Silence that ends an utterance of at least <see cref="MinChunk"/>.</summary>
    public TimeSpan PauseSilence { get; init; } = TimeSpan.FromMilliseconds(600);

    /// <summary>Silence that ends any utterance, even a short "sí", so it never waits for the next one.</summary>
    public TimeSpan ShortUtteranceSilence { get; init; } = TimeSpan.FromSeconds(1.5);

    /// <summary>Audio repeated at the start of the next chunk after a forced cut, so words are not split.</summary>
    public TimeSpan Overlap { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Silence kept before the first speech frame of a chunk.</summary>
    public TimeSpan PreRoll { get; init; } = TimeSpan.FromMilliseconds(300);

    /// <summary>Chunks with less detected speech than this are dropped (clicks, coughs).</summary>
    public TimeSpan MinSpeech { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>VAD analysis frame.</summary>
    public TimeSpan Frame { get; init; } = TimeSpan.FromMilliseconds(30);

    /// <summary>Absolute RMS below which a frame is never speech (about -46 dBFS).</summary>
    public float MinSpeechRms { get; init; } = 0.005f;

    /// <summary>A frame is speech when its RMS exceeds the tracked noise floor times this factor.</summary>
    public float NoiseFloorFactor { get; init; } = 3f;

    /// <summary>
    /// Cap for the tracked noise floor (about -26 dBFS). Background noise is never this loud, so a
    /// recording that starts mid-sentence, or a talker with no pauses, still counts as speech.
    /// </summary>
    public float MaxNoiseFloorRms { get; init; } = 0.05f;
}

/// <summary>
/// Energy-based VAD chunker for one track of 16 kHz mono float audio. Push samples as they
/// arrive; complete utterances come back as <see cref="AudioChunk"/>s whose <c>Start</c> is
/// <c>trackOffset + samples seen so far</c>, i.e. relative to the session start. Silence is
/// never emitted, so the transcriber does not hallucinate on it. Not thread-safe.
/// </summary>
/// <remarks>
/// The noise floor is a fast-down, slow-up minimum tracker of frame RMS: the short dips between
/// syllables keep it at the background level during speech, while steady background noise (fans,
/// line hum on the loopback track) raises it within a few seconds and stops counting as speech.
/// </remarks>
public sealed class SpeechChunker
{
    /// <summary>Per-frame rise toward louder frames: a time constant of about 3 s at 30 ms frames.</summary>
    private const float FloorRise = 0.01f;

    private readonly Track _track;
    private readonly TimeSpan _trackOffset;
    private readonly SpeechChunkerOptions _options;
    private readonly AudioRingBuffer _pending;
    private readonly float[] _frame;
    private readonly int _minChunk;
    private readonly int _maxChunk;
    private readonly int _pauseSilence;
    private readonly int _shortSilence;
    private readonly int _overlap;
    private readonly int _preRoll;
    private readonly int _minSpeech;
    private int _frameFill;
    private long _totalSamples;
    private long _pendingStart;
    private int _speechSamples;
    private int _trailingSilence;
    private float _noiseFloor = -1f;

    public SpeechChunker(Track track, TimeSpan trackOffset, SpeechChunkerOptions? options = null)
    {
        _options = options ?? new SpeechChunkerOptions();
        _track = track;
        _trackOffset = trackOffset < TimeSpan.Zero ? TimeSpan.Zero : trackOffset;
        _minChunk = ToSamples(_options.MinChunk);
        _maxChunk = ToSamples(_options.MaxChunk);
        _pauseSilence = ToSamples(_options.PauseSilence);
        _shortSilence = ToSamples(_options.ShortUtteranceSilence);
        _overlap = ToSamples(_options.Overlap);
        _preRoll = ToSamples(_options.PreRoll);
        _minSpeech = Math.Max(1, ToSamples(_options.MinSpeech));
        var frameSamples = ToSamples(_options.Frame);
        if (frameSamples <= 0 || _maxChunk <= frameSamples || _minChunk > _maxChunk ||
            _overlap >= _maxChunk / 2 || _preRoll >= _maxChunk / 2)
        {
            throw new ArgumentException("inconsistent speech chunker options", nameof(options));
        }

        _frame = new float[frameSamples];
        _pending = new AudioRingBuffer(_maxChunk + frameSamples);
    }

    /// <summary>Session-relative position just after the last pushed sample.</summary>
    public TimeSpan Position => _trackOffset + ToTime(_totalSamples);

    public IReadOnlyList<AudioChunk> Push(ReadOnlySpan<float> samples)
    {
        List<AudioChunk>? chunks = null;
        while (!samples.IsEmpty)
        {
            var take = Math.Min(samples.Length, _frame.Length - _frameFill);
            samples[..take].CopyTo(_frame.AsSpan(_frameFill));
            samples = samples[take..];
            _frameFill += take;
            _totalSamples += take;
            if (_frameFill == _frame.Length)
            {
                _frameFill = 0;
                if (ProcessFrame() is { } chunk)
                {
                    (chunks ??= []).Add(chunk);
                }
            }
        }

        return chunks ?? (IReadOnlyList<AudioChunk>)Array.Empty<AudioChunk>();
    }

    /// <summary>Emits the pending utterance, if it holds enough speech, and resets. Call on stop.</summary>
    public IReadOnlyList<AudioChunk> Flush()
    {
        _pending.Write(_frame.AsSpan(0, _frameFill));
        _frameFill = 0;
        var result = _speechSamples >= _minSpeech
            ? new[] { Emit(keepOverlap: false) }
            : Array.Empty<AudioChunk>();
        ResetPending();
        return result;
    }

    private AudioChunk? ProcessFrame()
    {
        var rms = Rms(_frame);
        var isSpeech = IsSpeech(rms);
        _pending.Write(_frame);
        if (isSpeech)
        {
            _speechSamples += _frame.Length;
            _trailingSilence = 0;
        }
        else
        {
            _trailingSilence += _frame.Length;
        }

        if (_speechSamples == 0)
        {
            var excess = _pending.Count - _preRoll;
            if (excess > 0)
            {
                _pending.DiscardOldest(excess);
                _pendingStart += excess;
            }

            return null;
        }

        if (_speechSamples >= _minSpeech &&
            ((_pending.Count >= _minChunk && _trailingSilence >= _pauseSilence) ||
             _trailingSilence >= _shortSilence))
        {
            var chunk = Emit(keepOverlap: false);
            ResetPending();
            return chunk;
        }

        // Cut before the next frame would push the chunk past the maximum.
        if (_pending.Count + _frame.Length > _maxChunk)
        {
            if (_speechSamples >= _minSpeech)
            {
                return Emit(keepOverlap: true);
            }

            // Only specks of "speech" in a full buffer: noise. Start over from the pre-roll.
            var excess = _pending.Count - _preRoll;
            _pending.DiscardOldest(excess);
            _pendingStart += excess;
            _speechSamples = 0;
        }

        return null;
    }

    private bool IsSpeech(float rms)
    {
        if (_noiseFloor < 0f)
        {
            _noiseFloor = Math.Min(rms, _options.MaxNoiseFloorRms);
        }

        var isSpeech = rms >= Math.Max(_options.MinSpeechRms, _noiseFloor * _options.NoiseFloorFactor);
        _noiseFloor = rms < _noiseFloor
            ? rms
            : Math.Min(_options.MaxNoiseFloorRms, _noiseFloor + ((rms - _noiseFloor) * FloorRise));
        return isSpeech;
    }

    private AudioChunk Emit(bool keepOverlap)
    {
        var samples = _pending.ToArray();
        var chunk = new AudioChunk(_track, _trackOffset + ToTime(_pendingStart), samples);
        if (keepOverlap)
        {
            var drop = _pending.Count - _overlap;
            _pending.DiscardOldest(drop);
            _pendingStart += drop;
            _speechSamples = 0;
            _trailingSilence = 0;
        }

        return chunk;
    }

    private void ResetPending()
    {
        _pending.Clear();
        _pendingStart = _totalSamples - _frameFill;
        _speechSamples = 0;
        _trailingSilence = 0;
    }

    private static float Rms(ReadOnlySpan<float> frame)
    {
        double sum = 0;
        foreach (var sample in frame)
        {
            sum += sample * sample;
        }

        return (float)Math.Sqrt(sum / frame.Length);
    }

    private static int ToSamples(TimeSpan duration) =>
        (int)Math.Round(duration.TotalSeconds * AudioChunk.SampleRate);

    private static TimeSpan ToTime(long samples) =>
        TimeSpan.FromTicks(samples * TimeSpan.TicksPerSecond / AudioChunk.SampleRate);
}
