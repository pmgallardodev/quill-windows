using Quill.Windows.Sessions;

namespace Quill.Windows.Transcription;

internal sealed record TrackProgress(
    string Track,
    double Fraction,
    bool Completed,
    TimeSpan? Duration = null);

/// <summary>
/// Se publica por dos eventos distintos con contratos de coste opuestos:
/// StateChanged unas cinco veces por sesion, ProgressChanged cientos por minuto.
/// Un consumidor que hace trabajo caro por evento se suscribe solo al primero.
/// </summary>
internal sealed record TranscriptionUpdate(
    string SessionDirectory,
    SessionState State,
    int Queued,
    IReadOnlyList<TrackProgress> Tracks,
    string? Error,
    TimeSpan Elapsed,
    string? Model = null);
