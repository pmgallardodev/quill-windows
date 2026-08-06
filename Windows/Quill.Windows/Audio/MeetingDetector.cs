using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using System.Runtime.InteropServices;

namespace Quill.Windows.Audio;

/// <summary>
/// Detecta que una aplicacion de reuniones esta usando el microfono. El fallo mas
/// caro de un grabador es olvidarse de pulsar grabar, y la sesion de captura de
/// WASAPI ya dice quien tiene el microfono abierto: no hace falta espiar ventanas
/// ni titulos, solo mirar que proceso esta capturando.
/// </summary>
internal static class MeetingDetector
{
    /// <summary>
    /// Nombres de proceso, sin extension y en minusculas. Una lista explicita evita
    /// que cualquier cosa que abra el microfono dispare una grabacion.
    /// </summary>
    private static readonly string[] KnownApplications =
    {
        "teams",
        "ms-teams",
        "zoom",
        "webex",
        "webexmta",
        "slack",
        "discord",
        "chrome",
        "msedge",
        "firefox",
    };

    /// <summary>
    /// Devuelve el nombre de la aplicacion que esta capturando, o null. Nunca lanza:
    /// esto se llama desde un temporizador y un fallo de COM no puede tumbar la app.
    /// </summary>
    public static string? DetectActiveMeeting()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var microphone = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
            var sessions = microphone.AudioSessionManager.Sessions;
            if (sessions is null)
            {
                return null;
            }

            for (var index = 0; index < sessions.Count; index++)
            {
                try
                {
                    using var session = sessions[index];
                    if (session.State != AudioSessionState.AudioSessionStateActive)
                    {
                        continue;
                    }

                    var name = ProcessName(session);
                    if (name is not null && KnownApplications.Contains(name, StringComparer.OrdinalIgnoreCase))
                    {
                        return name;
                    }
                }
                catch (Exception exception) when (
                    exception is InvalidOperationException or System.ComponentModel.Win32Exception or
                        UnauthorizedAccessException or COMException)
                {
                    // A session can disappear between enumeration and inspection.
                    continue;
                }
            }

            return null;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Sin dispositivo, sin permisos o con el enumerador ocupado: sin deteccion.
            return null;
        }
    }

    private static string? ProcessName(AudioSessionControl session)
    {
        try
        {
            var id = (int)session.GetProcessID;
            if (id <= 0)
            {
                return null;
            }

            using var process = System.Diagnostics.Process.GetProcessById(id);
            return process.ProcessName;
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or
                System.ComponentModel.Win32Exception)
        {
            // El proceso pudo morir entre listar la sesion y consultarlo.
            return null;
        }
    }
}
