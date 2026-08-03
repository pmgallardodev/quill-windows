namespace Quill.Windows.Sessions;

internal enum SessionState
{
    /// <summary>La grabacion que este proceso tiene abierta ahora mismo.</summary>
    Recording,

    /// <summary>Carpeta sin meta.json que no es la grabacion en curso: se interrumpio sin cerrar.</summary>
    Abandoned,

    Queued,
    Running,
    Done,
    Failed,
}

internal sealed record SessionSnapshot(
    string Directory,
    string Name,
    // Nombre puesto por el usuario en meta.json. Null si nunca la renombro.
    string? Title,
    DateTimeOffset Started,
    TimeSpan Duration,
    SessionState State,
    string? Model,
    string? Error,
    TimeSpan? TranscriptionElapsed);

/// <summary>
/// Deriva el estado de cada sesion a partir del disco. Sin estado propio: cada
/// llamada vuelve a mirar los archivos, de modo que borrar status.json a mano
/// degrada el detalle pero nunca rompe el resultado.
/// </summary>
internal static class SessionCatalog
{
    public static IReadOnlyList<SessionSnapshot> Scan(string recordingsRoot, string? activeSessionDirectory)
    {
        if (!Directory.Exists(recordingsRoot))
        {
            return Array.Empty<SessionSnapshot>();
        }

        return Directory.EnumerateDirectories(recordingsRoot)
            .Select(directory => Describe(directory, activeSessionDirectory))
            .OrderByDescending(snapshot => snapshot.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Borra la carpeta entera. Devuelve el motivo del fallo, o null si salio bien:
    /// el llamante tiene que poder decir por que no se pudo borrar, y una excepcion
    /// aqui solo significa que el WAV sigue abierto o que falta permiso.
    /// </summary>
    public static string? TryDelete(string sessionDirectory)
    {
        try
        {
            if (Directory.Exists(sessionDirectory))
            {
                Directory.Delete(sessionDirectory, recursive: true);
            }

            return null;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return exception.Message;
        }
    }

    private static SessionSnapshot Describe(string directory, string? activeSessionDirectory)
    {
        var metadata = SessionMetadata.TryRead(directory);
        var status = SessionStatus.TryRead(directory);
        var hasTranscript = File.Exists(Path.Combine(directory, "transcript.json"));
        var state = DetermineState(directory, activeSessionDirectory, metadata is not null, hasTranscript, status);

        return new SessionSnapshot(
            directory,
            Path.GetFileName(directory),
            string.IsNullOrWhiteSpace(metadata?.Title) ? null : metadata.Title.Trim(),
            metadata?.Started ?? Directory.GetCreationTimeUtc(directory),
            TimeSpan.FromSeconds(metadata?.DurationSeconds ?? 0),
            state,
            status?.Model,
            state == SessionState.Failed ? status?.Error : null,
            status?.ElapsedSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null);
    }

    private static SessionState DetermineState(
        string directory,
        string? activeSessionDirectory,
        bool hasMetadata,
        bool hasTranscript,
        SessionStatus? status)
    {
        if (IsSameDirectory(directory, activeSessionDirectory))
        {
            return SessionState.Recording;
        }

        if (!hasMetadata)
        {
            return SessionState.Abandoned;
        }

        if (hasTranscript)
        {
            return SessionState.Done;
        }

        // Un "done" sin transcript.json significa que el transcript se borro: hay que rehacerlo.
        return status?.State switch
        {
            SessionStatus.Running => SessionState.Running,
            SessionStatus.Failed => SessionState.Failed,
            _ => SessionState.Queued,
        };
    }

    private static bool IsSameDirectory(string directory, string? other) =>
        other is not null &&
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(other)),
            StringComparison.OrdinalIgnoreCase);
}
