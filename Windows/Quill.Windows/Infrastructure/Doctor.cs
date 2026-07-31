using NAudio.CoreAudioApi;

namespace Quill.Windows.Infrastructure;

internal static class Doctor
{
    private const long MinimumModelBytes = 900_000_000;

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

        var model = new FileInfo(ConfigStore.ModelPath);
        checks.Add(("Whisper large-v3", model.Exists && model.Length >= MinimumModelBytes,
            model.Exists ? $"{model.Length / 1024 / 1024} MB" : ConfigStore.ModelPath));

        foreach (var check in checks)
        {
            Console.WriteLine($"{(check.Ok ? "ok" : "fallo")}  {check.Name}: {check.Detail}");
        }

        return checks.All(check => check.Ok) ? 0 : 1;
    }
}
