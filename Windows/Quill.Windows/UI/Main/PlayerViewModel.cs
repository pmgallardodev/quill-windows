using System.Windows.Input;
using System.Windows.Threading;
using Quill.Windows.Playback;
using Quill.Windows.Resources;

namespace Quill.Windows.UI;

/// <summary>One choice of the speed selector.</summary>
internal sealed record PlaybackSpeedOption(double Speed, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// The player bar of the Transcript tab. One per main window: it plays the open meeting
/// and every timestamp link (transcript and summary) seeks it through
/// <see cref="PlayAtAsync"/>. Audio is opened lazily, on the first play.
/// </summary>
internal sealed class PlayerViewModel : ObservableObject, IDisposable
{
    private readonly ISessionAudioSource _source;
    private readonly DispatcherTimer? _timer;
    private ISessionAudio? _audio;
    private string _sessionId = string.Empty;
    private string? _loadedSessionId;
    private bool _isPlaying;
    private double _positionSeconds;
    private double _durationSeconds;
    private PlaybackSpeedOption _selectedSpeed;
    private string _status = string.Empty;
    private int _openGeneration;

    /// <param name="dispatcher">Drives the position updates while playing; null in tests (call <see cref="Tick"/>).</param>
    public PlayerViewModel(ISessionAudioSource source, Dispatcher? dispatcher = null)
    {
        _source = source;
        Speeds = new[] { 1d, 1.25d, 1.5d, 2d }
            .Select(speed => new PlaybackSpeedOption(speed, $"{speed.ToString("0.##", Loc.Culture ?? System.Globalization.CultureInfo.CurrentUICulture)}x"))
            .ToList();
        _selectedSpeed = Speeds[0];
        PlayPauseCommand = new RelayCommand(_ => _ = PlayPauseAsync(), _ => _sessionId.Length > 0);
        if (dispatcher is not null)
        {
            _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(200) };
            _timer.Tick += (_, _) => Tick();
        }
    }

    /// <summary>The session clock moved (playing, seek). The transcript follows it.</summary>
    public event EventHandler? PositionChanged;

    public IReadOnlyList<PlaybackSpeedOption> Speeds { get; }

    public ICommand PlayPauseCommand { get; }

    public string SessionId => _sessionId;

    public bool IsPlaying
    {
        get => _isPlaying;
        private set => Set(ref _isPlaying, value);
    }

    public TimeSpan Position => TimeSpan.FromSeconds(_positionSeconds);

    /// <summary>Slider value. Setting it (dragging) seeks.</summary>
    public double PositionSeconds
    {
        get => _positionSeconds;
        set
        {
            var clamped = Math.Clamp(value, 0d, Math.Max(_durationSeconds, 0d));
            if (Math.Abs(clamped - _positionSeconds) < 0.25)
            {
                return;
            }

            _audio?.Seek(TimeSpan.FromSeconds(clamped));
            UpdatePosition(clamped);
        }
    }

    public double DurationSeconds
    {
        get => _durationSeconds;
        private set
        {
            if (Set(ref _durationSeconds, value))
            {
                Raise(nameof(TimeText));
            }
        }
    }

    /// <summary>"1:02 / 12:04".</summary>
    public string TimeText => $"{Clock(_positionSeconds)} / {Clock(_durationSeconds)}";

    public PlaybackSpeedOption SelectedSpeed
    {
        get => _selectedSpeed;
        set
        {
            if (value is not null && Set(ref _selectedSpeed, value) && _audio is not null)
            {
                _audio.Speed = value.Speed;
            }
        }
    }

    /// <summary>"El audio de esta reunión no está disponible", or an error. Empty when fine.</summary>
    public string Status
    {
        get => _status;
        private set
        {
            if (Set(ref _status, value))
            {
                Raise(nameof(HasStatus));
            }
        }
    }

    public bool HasStatus => _status.Length > 0;

    /// <summary>The meeting open in the window. Another meeting stops and releases the audio.</summary>
    public void SetSession(string sessionId)
    {
        if (string.Equals(sessionId, _sessionId, StringComparison.Ordinal))
        {
            return;
        }

        Unload();
        _sessionId = sessionId ?? string.Empty;
        Status = string.Empty;
        DurationSeconds = 0d;
        UpdatePosition(0d);
        Raise(nameof(SessionId));
    }

    /// <summary>Timestamp links: seek to <paramref name="time"/> and play.</summary>
    public async Task PlayAtAsync(string sessionId, TimeSpan time)
    {
        SetSession(sessionId);
        if (await EnsureOpenAsync().ConfigureAwait(true) is not { } audio)
        {
            return;
        }

        var seconds = Math.Clamp(time.TotalSeconds, 0d, _durationSeconds);
        audio.Seek(TimeSpan.FromSeconds(seconds));
        UpdatePosition(seconds);
        Play(audio);
    }

    public async Task PlayPauseAsync()
    {
        if (_isPlaying && _audio is not null)
        {
            _audio.Pause();
            IsPlaying = false;
            _timer?.Stop();
            Tick();
            return;
        }

        if (await EnsureOpenAsync().ConfigureAwait(true) is { } audio)
        {
            Play(audio);
        }
    }

    /// <summary>Reads the position from the device. The timer calls it while playing.</summary>
    public void Tick()
    {
        if (_audio is null)
        {
            return;
        }

        UpdatePosition(_audio.Position.TotalSeconds);
        var playing = _audio.IsPlaying;
        if (!playing && _isPlaying)
        {
            IsPlaying = false;
            _timer?.Stop();
        }
    }

    public void Dispose() => Unload();

    private async Task<ISessionAudio?> EnsureOpenAsync()
    {
        if (_audio is not null && string.Equals(_loadedSessionId, _sessionId, StringComparison.Ordinal))
        {
            return _audio;
        }

        if (_sessionId.Length == 0)
        {
            return null;
        }

        var generation = ++_openGeneration;
        var sessionId = _sessionId;
        ISessionAudio? audio;
        try
        {
            audio = await _source.OpenAsync(sessionId, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (generation == _openGeneration)
            {
                Status = Loc.Format("Player_Failed", exception.Message);
            }

            return null;
        }

        if (generation != _openGeneration || !string.Equals(sessionId, _sessionId, StringComparison.Ordinal))
        {
            audio?.Dispose();
            return null;
        }

        if (audio is null)
        {
            Status = Loc.Get("Player_NoAudio");
            return null;
        }

        _audio = audio;
        _loadedSessionId = sessionId;
        audio.Speed = _selectedSpeed.Speed;
        audio.Stopped += OnStopped;
        Status = string.Empty;
        DurationSeconds = audio.Duration.TotalSeconds;
        return audio;
    }

    private void Play(ISessionAudio audio)
    {
        try
        {
            audio.Play();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // No output device, or it was taken away.
            Status = Loc.Format("Player_Failed", exception.Message);
            return;
        }

        IsPlaying = true;
        _timer?.Start();
    }

    private void OnStopped(object? sender, EventArgs e)
    {
        if (ReferenceEquals(sender, _audio))
        {
            Tick();
            IsPlaying = false;
            _timer?.Stop();
        }
    }

    private void Unload()
    {
        _openGeneration++;
        _timer?.Stop();
        if (_audio is not null)
        {
            _audio.Stopped -= OnStopped;
            _audio.Dispose();
            _audio = null;
        }

        _loadedSessionId = null;
        IsPlaying = false;
    }

    private void UpdatePosition(double seconds)
    {
        if (Set(ref _positionSeconds, seconds, nameof(PositionSeconds)))
        {
            Raise(nameof(Position), nameof(TimeText));
            PositionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private static string Clock(double seconds) =>
        seconds <= 0 ? "0:00" : UiFormat.Duration(TimeSpan.FromSeconds(Math.Floor(seconds)));
}

/// <summary>
/// Routes <see cref="ISessionActions.PlayAt"/> (every timestamp link) to the window's
/// player and forwards everything else to the AI actions it wraps.
/// </summary>
internal sealed class PlaybackSessionActions(ISessionActions inner, PlayerViewModel player) : ISessionActions
{
    public bool IsAiAvailable => inner.IsAiAvailable;

    public IReadOnlyList<string> SummaryTemplates => inner.SummaryTemplates;

    public bool CanReassignSpeakers => inner.CanReassignSpeakers;

    public Task GenerateSummaryAsync(string sessionId, string template, Core.Domain.SummaryLevel level, CancellationToken cancellationToken) =>
        inner.GenerateSummaryAsync(sessionId, template, level, cancellationToken);

    public Task IdentifySpeakersAsync(string sessionId, CancellationToken cancellationToken) =>
        inner.IdentifySpeakersAsync(sessionId, cancellationToken);

    public Task ReassignSpeakerAsync(string sessionId, TimeSpan segmentStart, string speakerId, CancellationToken cancellationToken) =>
        inner.ReassignSpeakerAsync(sessionId, segmentStart, speakerId, cancellationToken);

    /// <summary>The inner PlayAt is not called: the window's player is the only audio output.</summary>
    public void PlayAt(string sessionId, TimeSpan time) => _ = player.PlayAtAsync(sessionId, time);
}
