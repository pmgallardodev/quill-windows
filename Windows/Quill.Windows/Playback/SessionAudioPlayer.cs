using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Quill.Core.Domain;
using Quill.Core.Ports;
using Quill.Windows.Audio;

namespace Quill.Windows.Playback;

/// <summary>Playback of one session's audio on the session clock. Implemented by <see cref="SessionAudioPlayer"/>.</summary>
internal interface ISessionAudio : IDisposable
{
    bool IsPlaying { get; }

    /// <summary>Session clock (offset from the session start).</summary>
    TimeSpan Position { get; }

    TimeSpan Duration { get; }

    /// <summary>1, 1.25, 1.5, 2... Pitch is preserved.</summary>
    double Speed { get; set; }

    void Play();

    void Pause();

    void Seek(TimeSpan position);

    /// <summary>Playback stopped by itself: end of audio or device error.</summary>
    event EventHandler? Stopped;
}

/// <summary>Opens a session's audio. Behind an interface so the player view model is testable.</summary>
internal interface ISessionAudioSource
{
    /// <summary>Null when the session has no audio files left (deleted after transcription, or never recorded).</summary>
    Task<ISessionAudio?> OpenAsync(string sessionId, CancellationToken cancellationToken);
}

/// <summary>Finds the session's WAVs through the store and opens a <see cref="SessionAudioPlayer"/>.</summary>
internal sealed class SessionAudioSource(ISessionStore store) : ISessionAudioSource
{
    public async Task<ISessionAudio?> OpenAsync(string sessionId, CancellationToken cancellationToken)
    {
        var session = await store.LoadSessionAsync(sessionId, cancellationToken).ConfigureAwait(true);
        if (session is null)
        {
            return null;
        }

        var tracks = new List<(string Path, TimeSpan Offset)>();
        foreach (var track in session.Tracks)
        {
            var path = Path.GetFullPath(Path.Combine(session.Folder, track.FileName));
            var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(session.Folder)) + Path.DirectorySeparatorChar;
            if (path.StartsWith(folder, StringComparison.OrdinalIgnoreCase) && File.Exists(path))
            {
                tracks.Add((path, track.StartOffset < TimeSpan.Zero ? TimeSpan.Zero : track.StartOffset));
            }
        }

        if (tracks.Count == 0)
        {
            return null;
        }

        // Opening parses the WAV headers: off the UI thread. The output device itself is
        // created on the calling (UI) thread so its Stopped event comes back there.
        var mix = await Task.Run(() => new SessionMixProvider(tracks), cancellationToken).ConfigureAwait(true);
        return new SessionAudioPlayer(mix);
    }
}

/// <summary>
/// NAudio player for a session: mic.wav and system.wav mixed on the session clock (each
/// delayed by its start offset), then time-stretched for speeds other than 1x.
/// </summary>
internal sealed class SessionAudioPlayer : ISessionAudio
{
    private readonly object _gate = new();
    private readonly SessionMixProvider _mix;
    private readonly TimeStretchSampleProvider _stretch;
    private readonly WaveOutEvent _output;
    private bool _disposed;

    public SessionAudioPlayer(SessionMixProvider mix)
    {
        _mix = mix;
        _stretch = new TimeStretchSampleProvider(mix);
        _output = new WaveOutEvent { DesiredLatency = 200 };
        _output.Init(new LockedSampleProvider(_stretch, _gate));
        _output.PlaybackStopped += OnPlaybackStopped;
    }

    public event EventHandler? Stopped;

    public bool IsPlaying => !_disposed && _output.PlaybackState == PlaybackState.Playing;

    public TimeSpan Position
    {
        get
        {
            lock (_gate)
            {
                return _mix.Position;
            }
        }
    }

    public TimeSpan Duration => _mix.Duration;

    public double Speed
    {
        get => _stretch.Speed;
        set
        {
            lock (_gate)
            {
                _stretch.Speed = value;
            }
        }
    }

    public void Play()
    {
        if (_disposed)
        {
            return;
        }

        if (Position >= Duration)
        {
            Seek(TimeSpan.Zero);
        }

        _output.Play();
    }

    public void Pause()
    {
        if (!_disposed && _output.PlaybackState == PlaybackState.Playing)
        {
            _output.Pause();
        }
    }

    public void Seek(TimeSpan position)
    {
        lock (_gate)
        {
            _mix.Seek(position);
            _stretch.Reset();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _output.PlaybackStopped -= OnPlaybackStopped;
        _output.Dispose();
        _mix.Dispose();
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e) => Stopped?.Invoke(this, EventArgs.Empty);

    /// <summary>Serializes the device thread's reads with seeks and speed changes from the UI.</summary>
    private sealed class LockedSampleProvider(ISampleProvider source, object gate) : ISampleProvider
    {
        public WaveFormat WaveFormat => source.WaveFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            lock (gate)
            {
                return source.Read(buffer, offset, count);
            }
        }
    }
}

/// <summary>
/// Mixes the session tracks to mono at the highest track sample rate, positioned on the
/// session clock. Seeking rebuilds each track's chain (reader position + leading
/// silence for a track that starts later). Not thread-safe: <see cref="SessionAudioPlayer"/> locks.
/// </summary>
internal sealed class SessionMixProvider : ISampleProvider, IDisposable
{
    private readonly List<TrackSource> _tracks;
    private readonly int _rate;
    private readonly long _length;
    private float[] _scratch = Array.Empty<float>();
    private long _position;

    public SessionMixProvider(IReadOnlyList<(string Path, TimeSpan Offset)> tracks)
    {
        _tracks = new List<TrackSource>(tracks.Count);
        try
        {
            foreach (var (path, offset) in tracks)
            {
                _tracks.Add(new TrackSource(new AudioFileReader(path), offset));
            }
        }
        catch
        {
            Dispose();
            throw;
        }

        _rate = _tracks.Max(track => track.Reader.WaveFormat.SampleRate);
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(_rate, 1);
        _length = _tracks.Max(track => Samples(track.Offset + track.Reader.TotalTime));
        Seek(TimeSpan.Zero);
    }

    public WaveFormat WaveFormat { get; }

    public TimeSpan Position => TimeSpan.FromSeconds((double)_position / _rate);

    public TimeSpan Duration => TimeSpan.FromSeconds((double)_length / _rate);

    public void Seek(TimeSpan position)
    {
        _position = Math.Clamp(Samples(position), 0L, _length);
        foreach (var track in _tracks)
        {
            var local = position - track.Offset;
            if (local < TimeSpan.Zero)
            {
                track.Reader.CurrentTime = TimeSpan.Zero;
                track.Delay = Samples(-local);
            }
            else
            {
                track.Reader.CurrentTime = local < track.Reader.TotalTime ? local : track.Reader.TotalTime;
                track.Delay = 0;
            }

            ISampleProvider chain = track.Reader;
            if (chain.WaveFormat.Channels > 1)
            {
                chain = new MonoDownmixSampleProvider(chain);
            }

            if (chain.WaveFormat.SampleRate != _rate)
            {
                chain = new WdlResamplingSampleProvider(chain, _rate);
            }

            track.Chain = chain;
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        var wanted = (int)Math.Min(count, _length - _position);
        if (wanted <= 0)
        {
            return 0;
        }

        Array.Clear(buffer, offset, wanted);
        if (_scratch.Length < wanted)
        {
            _scratch = new float[wanted];
        }

        foreach (var track in _tracks)
        {
            var silent = (int)Math.Min(track.Delay, wanted);
            track.Delay -= silent;
            var read = track.Chain!.Read(_scratch, 0, wanted - silent);
            for (var index = 0; index < read; index++)
            {
                buffer[offset + silent + index] += _scratch[index];
            }
        }

        _position += wanted;
        return wanted;
    }

    public void Dispose()
    {
        foreach (var track in _tracks)
        {
            track.Reader.Dispose();
        }
    }

    private long Samples(TimeSpan time) => (long)(time.TotalSeconds * _rate);

    private sealed class TrackSource(AudioFileReader reader, TimeSpan offset)
    {
        public AudioFileReader Reader { get; } = reader;

        public TimeSpan Offset { get; } = offset;

        public ISampleProvider? Chain { get; set; }

        public long Delay { get; set; }
    }
}
