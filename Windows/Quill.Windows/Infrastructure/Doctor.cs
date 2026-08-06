using NAudio.CoreAudioApi;
using Quill.Windows.Transcription;

namespace Quill.Windows.Infrastructure;

internal static class Doctor
{
    /// <summary>
    /// No se puede imponer el tamano de un modelo concreto porque config.json puede
    /// seleccionar cualquier modelo whisper.cpp, incluidos los modelos tiny.
    /// </summary>
    private const long MinimumModelBytes = 1L * 1024 * 1024;

    /// <summary>
    /// Silero es mucho mas pequeno que Whisper, pero un archivo vacio o truncado
    /// tampoco puede inicializar el detector.
    /// </summary>
    private const long MinimumVadModelBytes = 500L * 1024;

    /// <summary>
    /// Una hora de reunion a formato nativo pasa del gigabyte por pista. Dos GB es el
    /// minimo con el que una reunion normal cabe entera sin cortarse a media frase.
    /// </summary>
    private const long MinimumFreeBytes = 2L * 1024 * 1024 * 1024;

    public static int Run(string recordingsRoot)
    {
        var checks = new List<(string Name, bool Ok, string Detail)>();
        checks.Add(("Windows 11", OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000), Environment.OSVersion.VersionString));

        try
        {
            using var devices = new MMDeviceEnumerator();
            using var microphone = devices.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
            checks.Add(("microfono", true, microphone.FriendlyName));
        }
        catch (Exception exception)
        {
            checks.Add(("microfono", false, exception.Message));
        }

        try
        {
            using var devices = new MMDeviceEnumerator();
            using var speakers = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            checks.Add(("audio del sistema", true, speakers.FriendlyName));
        }
        catch (Exception exception)
        {
            checks.Add(("audio del sistema", false, exception.Message));
        }

        checks.Add(CheckRecordingsFolder(recordingsRoot));

        checks.Add(CheckFreeSpace(recordingsRoot));

        var transcription = ConfigStore.Load().Transcription;
        if (!transcription.Enabled)
        {
            checks.Add(("transcripcion", true, "desactivada"));
        }
        else
        {
            var modelPath = ConfigStore.ResolveModelPath(transcription.Model);
            var model = new FileInfo(modelPath);
            checks.Add(($"modelo {Path.GetFileNameWithoutExtension(transcription.Model)}",
                model.Exists && model.Length >= MinimumModelBytes,
                model.Exists
                    ? $"{model.Length / 1024 / 1024} MB (minimo {MinimumModelBytes / 1024 / 1024} MB)"
                    : modelPath));

            if (transcription.Vad)
            {
                var vadPath = ConfigStore.ResolveModelPath(transcription.VadModel);
                var vad = new FileInfo(vadPath);
                checks.Add(("modelo VAD", vad.Exists && vad.Length >= MinimumVadModelBytes,
                    vad.Exists
                        ? $"{vad.Length / 1024} KB (minimo {MinimumVadModelBytes / 1024} KB)"
                        : vadPath));
            }

            checks.Add(("hilos de inferencia", true,
                $"{TranscriptionOptions.ResolveThreads(transcription.Threads)} de {Environment.ProcessorCount} logicos"));
        }

        foreach (var check in checks)
        {
            Console.WriteLine($"{(check.Ok ? "ok" : "fallo")}  {check.Name}: {check.Detail}");
        }

        return checks.All(check => check.Ok) ? 0 : 1;
    }

    /// <summary>
    /// Crear el directorio no demuestra que se puedan guardar los WAV y metadatos.
    /// La prueba deja la carpeta como estaba antes de devolver el resultado.
    /// </summary>
    private static (string Name, bool Ok, string Detail) CheckRecordingsFolder(string recordingsRoot)
    {
        string? probe = null;
        try
        {
            Directory.CreateDirectory(recordingsRoot);
            probe = Path.Combine(recordingsRoot, $".quill-doctor-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, "quill doctor");
            File.Delete(probe);
            return ("carpeta de grabaciones", true, recordingsRoot);
        }
        catch (Exception exception)
        {
            if (probe is not null)
            {
                try
                {
                    File.Delete(probe);
                }
                catch (Exception cleanupException) when (
                    cleanupException is IOException or UnauthorizedAccessException)
                {
                    // Conserva el error original: es el que explica el fallo del check.
                }
            }

            return ("carpeta de grabaciones", false, exception.Message);
        }
    }

    /// <summary>
    /// DriveInfo lanza en rutas UNC y en unidades que no estan listas. Ahi el fallo es
    /// justo el resultado del check: quedarse sin espacio a mitad de reunion pierde el
    /// audio, y hacer reventar "doctor" ocultaria el resto de comprobaciones.
    /// </summary>
    private static (string Name, bool Ok, string Detail) CheckFreeSpace(string recordingsRoot)
    {
        try
        {
            var drive = new DriveInfo(Path.GetFullPath(recordingsRoot));
            var free = drive.AvailableFreeSpace;
            return ("espacio libre", free >= MinimumFreeBytes,
                $"{free / 1024d / 1024d / 1024d:0.0} GB en {drive.Name} (minimo {MinimumFreeBytes / 1024 / 1024 / 1024} GB)");
        }
        catch (Exception exception)
        {
            return ("espacio libre", false, exception.Message);
        }
    }
}
