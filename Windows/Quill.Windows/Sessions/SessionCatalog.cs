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
    Disabled,
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
    public static IReadOnlyList<SessionSnapshot> Scan(
        string recordingsRoot,
        string? activeSessionDirectory,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(recordingsRoot))
        {
            return Array.Empty<SessionSnapshot>();
        }

        var snapshots = new List<SessionSnapshot>();
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(recordingsRoot))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (ContainsReparsePoint(directory) ||
                        (!IsKnownSessionDirectory(directory) && !IsSameDirectory(directory, activeSessionDirectory)))
                    {
                        continue;
                    }

                    snapshots.Add(Describe(directory, activeSessionDirectory));
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException or
                        ArgumentException or NotSupportedException or OverflowException)
                {
                    // A session deleted or locked while scanning should not take
                    // down the sessions window.
                }
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Return the sessions already read when the root changes mid-enumeration.
        }

        return snapshots
            .OrderByDescending(snapshot => snapshot.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Borra la carpeta entera. Devuelve el motivo del fallo, o null si salio bien:
    /// el llamante tiene que poder decir por que no se pudo borrar, y una excepcion
    /// aqui solo significa que el WAV sigue abierto o que falta permiso.
    /// </summary>
    public static string? TryDelete(string recordingsRoot, string sessionDirectory)
    {
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(recordingsRoot));
            var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sessionDirectory));
            var parent = Path.GetDirectoryName(directory);
            if (!string.Equals(parent, root, StringComparison.OrdinalIgnoreCase))
            {
                return "la carpeta no pertenece al directorio de grabaciones";
            }

            if (!Directory.Exists(directory))
            {
                if (File.Exists(directory))
                {
                    return "la ruta de sesion no es una carpeta";
                }

                return null;
            }

            if (!IsSafeSessionDirectory(root, directory))
            {
                return "la carpeta no parece una sesion de quill";
            }

            if (ContainsReparsePoint(directory))
            {
                return "la carpeta contiene un enlace no seguro";
            }

            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }

            return null;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return exception.Message;
        }
    }

    private static SessionSnapshot Describe(string directory, string? activeSessionDirectory)
    {
        var metadata = SessionMetadata.TryRead(directory);
        var status = SessionStatus.TryRead(directory);
        var transcript = TranscriptExport.Read(directory);
        var hasFailureMarker = SessionStatus.HasFailureMarker(directory);
        var state = DetermineState(
            directory,
            activeSessionDirectory,
            metadata is not null,
            transcript.Status is TranscriptReadStatus.Valid or TranscriptReadStatus.Empty,
            transcript.Status != TranscriptReadStatus.Missing,
            status,
            hasFailureMarker);

        return new SessionSnapshot(
            directory,
            Path.GetFileName(directory),
            string.IsNullOrWhiteSpace(metadata?.Title) ? null : metadata.Title.Trim(),
            metadata?.Started ?? Directory.GetCreationTimeUtc(directory),
            TimeSpan.FromSeconds(metadata?.DurationSeconds ?? 0),
            state,
            status?.Model,
            state == SessionState.Failed
                ? status?.Error ?? SessionStatus.ReadFailureMarker(directory) ??
                    (transcript.Status == TranscriptReadStatus.Invalid
                        ? "transcript.json invalido"
                        : null)
                : null,
            status?.ElapsedSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null);
    }

    private static SessionState DetermineState(
        string directory,
        string? activeSessionDirectory,
        bool hasMetadata,
        bool transcriptValid,
        bool hasTranscript,
        SessionStatus? status,
        bool hasFailureMarker)
    {
        if (IsSameDirectory(directory, activeSessionDirectory))
        {
            return SessionState.Recording;
        }

        if (!hasMetadata)
        {
            return SessionState.Abandoned;
        }

        if (hasTranscript && transcriptValid)
        {
            return SessionState.Done;
        }

        if (hasTranscript)
        {
            if (hasFailureMarker)
            {
                return SessionState.Failed;
            }

            return status?.State switch
            {
                SessionStatus.Queued => SessionState.Queued,
                SessionStatus.Running => SessionState.Running,
                SessionStatus.Disabled => SessionState.Disabled,
                SessionStatus.Failed => SessionState.Failed,
                _ => SessionState.Failed,
            };
        }

        // Un "done" sin transcript.json significa que el transcript se borro: hay que rehacerlo.
        if (hasFailureMarker)
        {
            return SessionState.Failed;
        }

        return status?.State switch
        {
            SessionStatus.Running => SessionState.Running,
            SessionStatus.Failed => SessionState.Failed,
            SessionStatus.Disabled => SessionState.Disabled,
            _ => SessionState.Queued,
        };
    }

    private static bool IsKnownSessionDirectory(string directory)
    {
        try
        {
            // Discovery also keeps an interrupted legacy capture visible. Durable
            // state files and human-readable exports alone are not session identity.
            return HasSessionMarker(directory) ||
                IsRegularFile(Path.Combine(directory, "meta.json")) ||
                HasValidMetadata(directory) ||
                HasExpectedAudioArtifacts(directory);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or
                InvalidDataException or NotSupportedException or OverflowException)
        {
            return false;
        }
    }

    internal static bool IsSafeSessionDirectory(string recordingsRoot, string sessionDirectory)
    {
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(recordingsRoot));
            var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sessionDirectory));
            return !PathContainsReparsePoint(root) &&
                string.Equals(Path.GetDirectoryName(directory), root, StringComparison.OrdinalIgnoreCase) &&
                Directory.Exists(directory) &&
                HasSessionIdentity(directory) &&
                !ContainsReparsePoint(directory);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool HasSessionIdentity(string directory) =>
        HasSessionMarker(directory) || HasValidMetadata(directory);

    private static bool HasSessionMarker(string directory) =>
        IsRegularFile(Path.Combine(directory, ".quill-session"));

    private static bool HasValidMetadata(string directory) =>
        SessionMetadata.TryRead(directory) is not null;

    private static bool HasExpectedAudioArtifacts(string directory) =>
        new[] { "mic.wav", "system.wav" }
            .Any(file => IsRegularFile(Path.Combine(directory, file)));

    private static bool IsRegularFile(string path) =>
        File.Exists(path) && !HasReparsePoint(path);

    private static bool HasReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return true;
        }
    }

    private static bool PathContainsReparsePoint(string path)
    {
        try
        {
            for (var current = new DirectoryInfo(Path.GetFullPath(path)); current is not null; current = current.Parent)
            {
                if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return true;
                }
            }

            return false;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return true;
        }
    }

    private static bool ContainsReparsePoint(string directory)
    {
        if (HasReparsePoint(directory))
        {
            return true;
        }

        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if (HasReparsePoint(entry))
                {
                    return true;
                }

                if (Directory.Exists(entry) && ContainsReparsePoint(entry))
                {
                    return true;
                }
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return true;
        }

        return false;
    }

    private static bool IsSameDirectory(string directory, string? other) =>
        other is not null &&
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(other)),
            StringComparison.OrdinalIgnoreCase);
}
