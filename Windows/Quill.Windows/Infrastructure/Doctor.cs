using NAudio.CoreAudioApi;
using Quill.Windows.Transcription;

namespace Quill.Windows.Infrastructure;

internal static class Doctor
{
    /// <summary>
    /// Umbral de cordura, no el tamano de un modelo concreto: large-v3 q5_0 ocupa
    /// 1,1 GB y large-v3-turbo q5_0 unos 547 MB, asi que solo se comprueba que el
    /// archivo no sea un resto de una descarga cortada.
    /// </summary>
    private const long MinimumModelBytes = 100_000_000;

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

        try
        {
            Directory.CreateDirectory(recordingsRoot);
            checks.Add(("carpeta de grabaciones", true, recordingsRoot));
        }
        catch (Exception exception)
        {
            checks.Add(("carpeta de grabaciones", false, exception.Message));
        }

        var transcription = ConfigStore.Load().Transcription;
        var modelPath = ConfigStore.ResolveModelPath(transcription.Model);
        var model = new FileInfo(modelPath);
        checks.Add(($"modelo {Path.GetFileNameWithoutExtension(transcription.Model)}",
            model.Exists && model.Length >= MinimumModelBytes,
            model.Exists ? $"{model.Length / 1024 / 1024} MB" : modelPath));

        if (transcription.Vad)
        {
            var vadPath = ConfigStore.ResolveModelPath(transcription.VadModel);
            var vad = new FileInfo(vadPath);
            checks.Add(("modelo VAD", vad.Exists, vad.Exists ? $"{vad.Length / 1024} KB" : vadPath));
        }

        checks.Add(("hilos de inferencia", true,
            $"{TranscriptionOptions.ResolveThreads(transcription.Threads)} de {Environment.ProcessorCount} logicos"));

        foreach (var check in checks)
        {
            Console.WriteLine($"{(check.Ok ? "ok" : "fallo")}  {check.Name}: {check.Detail}");
        }

        return checks.All(check => check.Ok) ? 0 : 1;
    }
}
