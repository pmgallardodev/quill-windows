using System.ComponentModel;
using System.Runtime.CompilerServices;
using Quill.Windows.Sessions;
using Quill.Windows.Transcription;

namespace Quill.Windows.UI;

internal sealed class SessionRowViewModel : INotifyPropertyChanged
{
    private SessionState _state;
    private double _micProgress;
    private double _systemProgress;
    private TimeSpan _elapsed;
    private string? _error;
    private TimeSpan? _transcriptionElapsed;

    public SessionRowViewModel(SessionSnapshot snapshot)
    {
        Directory = snapshot.Directory;
        Name = snapshot.Name;
        Duration = snapshot.Duration;
        Model = snapshot.Model;
        _state = snapshot.State;
        _error = snapshot.Error;
        _transcriptionElapsed = snapshot.TranscriptionElapsed;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Directory { get; }

    public string Name { get; }

    public TimeSpan Duration { get; }

    public string? Model { get; }

    public string DurationText => Duration > TimeSpan.Zero ? Format(Duration) : "--:--";

    public bool IsRunning => _state == SessionState.Running;

    public bool IsFailed => _state == SessionState.Failed;

    public bool IsDone => _state == SessionState.Done;

    public bool HasError => IsFailed && !string.IsNullOrWhiteSpace(_error);

    public string? ErrorText => _error;

    public double MicProgress => _micProgress * 100d;

    public double SystemProgress => _systemProgress * 100d;

    public string StateGlyph => _state switch
    {
        SessionState.Recording => "●",
        SessionState.Abandoned => "⚠",
        SessionState.Queued => "⏳",
        SessionState.Running => "⚙",
        SessionState.Done => "✓",
        SessionState.Failed => "✗",
        _ => "?",
    };

    public string StateText => _state switch
    {
        SessionState.Recording => "GRABANDO",
        SessionState.Abandoned => "incompleta",
        SessionState.Queued => "en cola",
        SessionState.Running => "transcribiendo",
        SessionState.Done => "listo",
        SessionState.Failed => "error",
        _ => string.Empty,
    };

    public string ElapsedText => _transcriptionElapsed is { } elapsed
        ? $"transcrito en {Format(elapsed)}{(Model is null ? string.Empty : $" - {Model}")}"
        : string.Empty;

    /// <summary>
    /// Estimacion a partir del avance observado: se autocorrige y no necesita saber
    /// nada del modelo ni del hardware. Oculta por debajo del 5 % para no mostrar
    /// numeros absurdos en los primeros segundos.
    /// </summary>
    public string EtaText
    {
        get
        {
            var fraction = (_micProgress + _systemProgress) / 2d;
            if (!IsRunning || fraction < 0.05d || _elapsed <= TimeSpan.Zero)
            {
                return string.Empty;
            }

            var remaining = TimeSpan.FromSeconds(_elapsed.TotalSeconds / fraction * (1d - fraction));
            return $"~{Format(remaining)} restantes";
        }
    }

    public void Apply(TranscriptionUpdate update)
    {
        _state = update.State;
        _error = update.Error;
        _elapsed = update.Elapsed;

        foreach (var track in update.Tracks)
        {
            if (track.Track == "mic")
            {
                _micProgress = track.Fraction;
            }
            else if (track.Track == "system")
            {
                _systemProgress = track.Fraction;
            }
        }

        if (update.State == SessionState.Done)
        {
            _micProgress = 1d;
            _systemProgress = 1d;
            _transcriptionElapsed = update.Elapsed;
        }

        RaiseAll();
    }

    private void RaiseAll()
    {
        foreach (var property in new[]
                 {
                     nameof(StateText), nameof(StateGlyph), nameof(IsRunning), nameof(IsFailed),
                     nameof(IsDone), nameof(HasError), nameof(ErrorText), nameof(MicProgress),
                     nameof(SystemProgress), nameof(EtaText), nameof(ElapsedText),
                 })
        {
            Raise(property);
        }
    }

    private void Raise([CallerMemberName] string? property = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));

    private static string Format(TimeSpan value) => value.TotalHours >= 1
        ? $"{(int)value.TotalHours}:{value.Minutes:D2}:{value.Seconds:D2}"
        : $"{value.Minutes}:{value.Seconds:D2}";
}
