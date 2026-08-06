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
    private TimeSpan? _microphoneDuration;
    private TimeSpan? _systemDuration;
    private TimeSpan _elapsed;
    private string? _error;
    private string? _model;
    private TimeSpan? _transcriptionElapsed;
    private string? _title;
    private string _titleDraft = string.Empty;
    private bool _isEditingTitle;
    private bool _isPaused;

    public SessionRowViewModel(SessionSnapshot snapshot)
    {
        Directory = snapshot.Directory;
        Name = snapshot.Name;
        Duration = snapshot.Duration;
        _model = snapshot.Model;
        _title = snapshot.Title;
        _state = snapshot.State;
        _error = snapshot.Error;
        _transcriptionElapsed = snapshot.TranscriptionElapsed;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Directory { get; }

    /// <summary>Nombre de la carpeta. Nunca cambia: es la identidad de la sesion en el disco.</summary>
    public string Name { get; }

    public TimeSpan Duration { get; }

    public string? Model => _model;

    /// <summary>Lo que el usuario ve: su titulo si lo puso, y si no la carpeta.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(_title) ? Name : _title;

    /// <summary>La carpeta, como subtitulo, solo cuando el titulo la ha desplazado.</summary>
    public string FolderText => string.IsNullOrWhiteSpace(_title) ? string.Empty : Name;

    public bool HasTitle => !string.IsNullOrWhiteSpace(_title);

    public bool IsEditingTitle
    {
        get => _isEditingTitle;
        private set
        {
            if (_isEditingTitle == value)
            {
                return;
            }

            _isEditingTitle = value;
            Raise();
            Raise(nameof(IsNotEditingTitle));
        }
    }

    public bool IsNotEditingTitle => !_isEditingTitle;

    /// <summary>Texto en curso del cuadro de renombrar. Solo se guarda al confirmar.</summary>
    public string TitleDraft
    {
        get => _titleDraft;
        set
        {
            if (_titleDraft == value)
            {
                return;
            }

            _titleDraft = value;
            Raise();
        }
    }

    public string DurationText => Duration > TimeSpan.Zero ? Format(Duration) : "--:--";

    public bool IsRecording => _state == SessionState.Recording;

    public bool IsPaused => IsRecording && _isPaused;

    public bool IsRunning => _state == SessionState.Running;

    public bool IsFailed => _state == SessionState.Failed;

    public bool IsDone => _state == SessionState.Done;

    public bool IsDisabled => _state == SessionState.Disabled;

    public bool CanRetry => IsFailed || IsDisabled;

    /// <summary>Grabando o transcribiendo: hay archivos abiertos, borrar destruiria trabajo en curso.</summary>
    public bool IsBusy => IsRecording || _state == SessionState.Queued || IsRunning;

    public bool CanDelete => !IsBusy;

    public bool CanRename => !IsRecording && _state != SessionState.Abandoned;

    public bool HasError => IsFailed && !string.IsNullOrWhiteSpace(_error);

    public string? ErrorText => _error;

    public double MicProgress => _micProgress * 100d;

    public double SystemProgress => _systemProgress * 100d;

    public string StateGlyph => IsPaused ? "Ⅱ" : _state switch
    {
        SessionState.Recording => "●",
        SessionState.Abandoned => "⚠",
        SessionState.Queued => "⏳",
        SessionState.Running => "⚙",
        SessionState.Done => "✓",
        SessionState.Failed => "✗",
        SessionState.Disabled => "○",
        _ => "?",
    };

    public string StateText => IsPaused ? "PAUSADO" : _state switch
    {
        SessionState.Recording => "GRABANDO",
        SessionState.Abandoned => "incompleta",
        SessionState.Queued => "en cola",
        SessionState.Running => "transcribiendo",
        SessionState.Done => "listo",
        SessionState.Failed => "error",
        SessionState.Disabled => "transcripcion desactivada",
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
            var microphoneWeight = _microphoneDuration?.TotalSeconds > 0
                ? _microphoneDuration.Value.TotalSeconds
                : 1d;
            var systemWeight = _systemDuration?.TotalSeconds > 0
                ? _systemDuration.Value.TotalSeconds
                : 1d;
            var fraction = (_micProgress * microphoneWeight + _systemProgress * systemWeight) /
                (microphoneWeight + systemWeight);
            if (!IsRunning || fraction < 0.05d || _elapsed <= TimeSpan.Zero)
            {
                return string.Empty;
            }

            var remaining = TimeSpan.FromSeconds(_elapsed.TotalSeconds / fraction * (1d - fraction));
            return $"~{Format(remaining)} restantes";
        }
    }

    public void BeginRename()
    {
        TitleDraft = DisplayName;
        IsEditingTitle = true;
    }

    public void CancelRename() => IsEditingTitle = false;

    /// <summary>
    /// Guarda el titulo. Un cuadro vacio borra el titulo y devuelve la sesion a su
    /// nombre de carpeta, que es la unica forma de deshacer un renombrado.
    /// </summary>
    public bool CommitRename()
    {
        if (!IsEditingTitle)
        {
            return true;
        }

        var candidate = string.IsNullOrWhiteSpace(TitleDraft) || TitleDraft.Trim() == Name
            ? null
            : TitleDraft.Trim();

        if (candidate == _title)
        {
            IsEditingTitle = false;
            return true;
        }

        if (!SessionMetadata.TryUpdateTitle(Directory, candidate))
        {
            return false;
        }

        IsEditingTitle = false;
        _title = candidate;
        Raise(nameof(DisplayName));
        Raise(nameof(FolderText));
        Raise(nameof(HasTitle));
        return true;
    }

    public void Apply(TranscriptionUpdate update)
    {
        _state = update.State;
        if (!string.IsNullOrWhiteSpace(update.Model))
        {
            _model = update.Model;
        }
        if (_state != SessionState.Recording)
        {
            _isPaused = false;
        }
        _error = update.Error;
        _elapsed = update.Elapsed;

        if ((update.State is SessionState.Queued or SessionState.Running) && update.Tracks.Count == 0)
        {
            // A retry starts from zero; do not let the previous failed run
            // contaminate the ETA or the first progress frame.
            _micProgress = 0d;
            _systemProgress = 0d;
            _microphoneDuration = null;
            _systemDuration = null;
            _transcriptionElapsed = null;
        }

        foreach (var track in update.Tracks)
        {
            if (track.Track == "mic")
            {
                _micProgress = track.Fraction;
                _microphoneDuration = track.Duration ?? _microphoneDuration;
            }
            else if (track.Track == "system")
            {
                _systemProgress = track.Fraction;
                _systemDuration = track.Duration ?? _systemDuration;
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
                     nameof(StateText), nameof(StateGlyph), nameof(IsRecording), nameof(IsRunning),
                       nameof(IsPaused), nameof(IsFailed), nameof(IsDone), nameof(IsDisabled), nameof(CanRetry),
                      nameof(IsBusy), nameof(CanDelete), nameof(CanRename),
                      nameof(HasError), nameof(ErrorText), nameof(MicProgress),
                      nameof(SystemProgress), nameof(EtaText), nameof(Model), nameof(ElapsedText),
                 })
        {
            Raise(property);
        }
    }

    public void SetPaused(bool paused)
    {
        if (_isPaused == paused)
        {
            return;
        }

        _isPaused = paused;
        Raise(nameof(IsPaused));
        Raise(nameof(StateGlyph));
        Raise(nameof(StateText));
    }

    private void Raise([CallerMemberName] string? property = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));

    private static string Format(TimeSpan value) => value.TotalHours >= 1
        ? $"{(int)value.TotalHours}:{value.Minutes:D2}:{value.Seconds:D2}"
        : $"{value.Minutes}:{value.Seconds:D2}";
}

/// <summary>
/// Una linea del panel de lectura: o un segmento hablado, o una marca puesta
/// durante la grabacion. Van en la misma lista porque el orden temporal es lo
/// unico que importa al leer.
/// </summary>
internal sealed class TranscriptLineViewModel
{
    private TranscriptLineViewModel(long startMs, bool isMarker)
    {
        StartMs = startMs;
        IsMarker = isMarker;
        TimeText = $"[{TranscriptExport.Clock(startMs)}]";
    }

    public static TranscriptLineViewModel Segment(TranscriptSegment segment)
    {
        var speaker = string.IsNullOrWhiteSpace(segment.Speaker) ? "?" : segment.Speaker.Trim();
        return new TranscriptLineViewModel(segment.StartMs, isMarker: false)
        {
            SpeakerText = speaker,
            Text = segment.Text?.Trim() ?? string.Empty,
            IsMe = string.Equals(speaker, "me", StringComparison.OrdinalIgnoreCase),
        };
    }

    public static TranscriptLineViewModel Marker(long startMs, int number) =>
        new(startMs, isMarker: true)
        {
            SpeakerText = string.Empty,
            Text = $"marca {number}",
        };

    public long StartMs { get; }

    public string TimeText { get; }

    public bool IsMarker { get; }

    public bool IsSegment => !IsMarker;

    public bool IsMe { get; private init; }

    public string SpeakerText { get; private init; } = string.Empty;

    public string Text { get; private init; } = string.Empty;
}

/// <summary>Un fragmento encontrado por la busqueda, con la sesion de la que sale.</summary>
internal sealed class SearchHitViewModel
{
    public SearchHitViewModel(TranscriptHit hit)
    {
        Directory = hit.Directory;
        SessionLabel = hit.SessionLabel;
        TimeText = $"[{TranscriptExport.Clock(hit.StartMs)}]";
        SpeakerText = hit.Speaker;
        Text = hit.Text;
    }

    public string Directory { get; }

    public string SessionLabel { get; }

    public string TimeText { get; }

    public string SpeakerText { get; }

    public string Text { get; }
}

internal sealed class TimelineSlotViewModel
{
    public TimelineSlotViewModel(TimelineSlot slot)
    {
        Microphone = slot.Microphone;
        System = slot.System;
        TimeText = TranscriptExport.Clock((long)slot.At.TotalMilliseconds);
    }

    public bool Microphone { get; }

    public bool System { get; }

    public string TimeText { get; }

    public string Tooltip => Microphone && System
        ? $"{TimeText}: ambos"
        : Microphone
            ? $"{TimeText}: me"
            : System
                ? $"{TimeText}: them"
                : $"{TimeText}: silencio";
}
