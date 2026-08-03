using System.Diagnostics;
using System.Windows.Threading;
using Quill.Windows.Audio;
using Quill.Windows.Sessions;
using Quill.Windows.Transcription;
using Forms = System.Windows.Forms;
using WpfApplication = System.Windows.Application;

namespace Quill.Windows.UI;

internal static class TrayApplication
{
    public static int Run(string recordingsRoot)
    {
        var application = new WpfApplication
        {
            ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown,
        };
        using var controller = new TrayController(application, recordingsRoot);
        return application.Run();
    }
}

internal sealed class TrayController : IDisposable
{
    private readonly WpfApplication _application;
    private readonly string _recordingsRoot;
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _toggle;
    private readonly Forms.ToolStripMenuItem _pause;
    private readonly Forms.ToolStripMenuItem _marker;
    private readonly System.Drawing.Icon _idleIcon;
    private readonly System.Drawing.Icon? _recordingIcon;
    private readonly DispatcherTimer _ticker;
    private readonly TranscriptionCoordinator _transcription;
    private readonly SessionsViewModel _sessionsViewModel;
    private SessionsWindow? _sessionsWindow;
    private RecordingSession? _session;
    private DateTimeOffset? _recordingStartedAt;
    private int _deviceCheckTicks;
    private bool _deviceChangeReported;
    private bool _disposed;

    public TrayController(WpfApplication application, string recordingsRoot)
    {
        _application = application;
        _recordingsRoot = recordingsRoot;
        Directory.CreateDirectory(recordingsRoot);

        var menu = new Forms.ContextMenuStrip();
        _toggle = new Forms.ToolStripMenuItem("Iniciar grabacion", null, async (_, _) => await ToggleAsync());
        menu.Items.Add(_toggle);
        _pause = new Forms.ToolStripMenuItem("Pausar grabacion", null, (_, _) => TogglePause()) { Visible = false };
        menu.Items.Add(_pause);
        _marker = new Forms.ToolStripMenuItem("Marcar momento", null, (_, _) => AddMarker()) { Visible = false };
        menu.Items.Add(_marker);
        menu.Items.Add(new Forms.ToolStripMenuItem("Ver sesiones", null, (_, _) => ShowSessions()));
        menu.Items.Add(new Forms.ToolStripMenuItem("Abrir grabaciones", null, (_, _) => OpenRecordings()));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(new Forms.ToolStripMenuItem("Salir", null, (_, _) => _application.Shutdown()));

        _idleIcon = LoadIcon();
        _recordingIcon = TryCreateRecordingIcon(_idleIcon);

        _icon = new Forms.NotifyIcon
        {
            Icon = _idleIcon,
            Text = "quill - listo",
            ContextMenuStrip = menu,
            Visible = true,
        };
        // Abrir la ventana en lugar de grabar: un doble clic accidental ya no puede
        // cortar una reunion en curso.
        _icon.DoubleClick += (_, _) => ShowSessions();

        _ticker = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _ticker.Tick += (_, _) => OnTick();

        _transcription = new TranscriptionCoordinator(recordingsRoot);
        _transcription.StateChanged += update => _application.Dispatcher.BeginInvoke(() => ShowTranscriptionStatus(update));
        _sessionsViewModel = new SessionsViewModel(recordingsRoot, _transcription, _application.Dispatcher);
        _transcription.ResumePending();
        _application.Exit += (_, _) => Dispose();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _ticker.Stop();
        var activeSession = _session;
        _session = null;
        if (activeSession is not null)
        {
            try
            {
                activeSession.Dispose();
            }
            catch
            {
                // Shutdown must continue even if an audio device already failed.
            }
        }

        if (_sessionsWindow is not null)
        {
            _sessionsWindow.AllowClose = true;
            _sessionsWindow.Close();
            _sessionsWindow = null;
        }

        _sessionsViewModel.Dispose();
        _transcription.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
        _recordingIcon?.Dispose();
        _idleIcon.Dispose();
    }

    private async Task ToggleAsync()
    {
        if (_session is null)
        {
            StartRecording();
        }
        else
        {
            StopRecording();
        }

        await Task.CompletedTask;
    }

    private void StartRecording()
    {
        try
        {
            _session = RecordingSession.Start(_recordingsRoot);
            _recordingStartedAt = DateTimeOffset.UtcNow;
            _toggle.Text = "Detener grabacion";
            _pause.Text = "Pausar grabacion";
            _pause.Visible = true;
            _marker.Visible = true;
            _deviceCheckTicks = 0;
            _deviceChangeReported = false;
            ApplyIcon(recording: true);
            _icon.Text = "quill - GRABANDO 0:00";
            _ticker.Start();
            _sessionsViewModel.SetActiveSession(_session.Directory);
            _icon.ShowBalloonTip(3_000, "quill", "Grabacion activa: microfono y audio del sistema.", Forms.ToolTipIcon.Info);
        }
        catch (Exception exception)
        {
            _icon.ShowBalloonTip(6_000, "quill - error", exception.Message, Forms.ToolTipIcon.Error);
        }
    }

    private void StopRecording()
    {
        var session = _session;
        if (session is null)
        {
            return;
        }

        _session = null;
        _ticker.Stop();
        _recordingStartedAt = null;
        try
        {
            session.Stop();
            _transcription.Enqueue(session.Directory);
            _icon.Text = "quill - transcripcion en cola";
            _icon.ShowBalloonTip(3_000, "quill", "Grabacion terminada. La transcripcion local inicio en segundo plano.", Forms.ToolTipIcon.Info);
        }
        catch (Exception exception)
        {
            _icon.Text = "quill - error al guardar";
            _icon.ShowBalloonTip(6_000, "quill - error", exception.Message, Forms.ToolTipIcon.Error);
        }
        finally
        {
            // Enqueue first so the freshly scanned row includes the model written
            // to status.json instead of capturing a null model from the active state.
            try
            {
                _sessionsViewModel.SetActiveSession(null);
            }
            catch
            {
                // A refresh failure must not leave the audio session undisposed.
            }

            try
            {
                session.Dispose();
            }
            catch (Exception exception)
            {
                _icon.Text = "quill - error al cerrar audio";
                _icon.ShowBalloonTip(6_000, "quill - error", exception.Message, Forms.ToolTipIcon.Error);
            }

            _toggle.Text = "Iniciar grabacion";
            _pause.Visible = false;
            _marker.Visible = false;
            ApplyIcon(recording: false);
        }
    }

    private void UpdateElapsedTime()
    {
        if (_session is not { } session || _recordingStartedAt is null)
        {
            return;
        }

        var elapsed = FormatDuration(session.Elapsed);
        _icon.Text = session.IsPaused ? $"quill - PAUSADO {elapsed}" : $"quill - GRABANDO {elapsed}";
    }

    private void ShowTranscriptionStatus(TranscriptionUpdate update)
    {
        var name = Path.GetFileName(update.SessionDirectory);
        var isRecording = _session is not null;
        switch (update.State)
        {
            case SessionState.Queued:
            case SessionState.Running:
                if (!isRecording)
                {
                    _icon.Text = update.Queued > 0
                        ? $"quill - transcribiendo ({update.Queued} en cola)"
                        : "quill - transcribiendo";
                }
                break;

            case SessionState.Done:
                if (!isRecording)
                {
                    _icon.Text = "quill - listo";
                }
                _icon.ShowBalloonTip(4_000, "quill", $"Transcripcion lista: {name}", Forms.ToolTipIcon.Info);
                break;

            case SessionState.Failed:
                if (!isRecording)
                {
                    _icon.Text = "quill - error de transcripcion";
                }
                _icon.ShowBalloonTip(6_000, "quill - error", $"{name}: {update.Error}", Forms.ToolTipIcon.Error);
                break;
        }
    }

    private void TogglePause()
    {
        if (_session is not { } session)
        {
            return;
        }

        if (session.IsPaused)
        {
            session.Resume();
            _pause.Text = "Pausar grabacion";
        }
        else
        {
            session.Pause();
            _pause.Text = "Reanudar grabacion";
        }

        ApplyIcon(recording: !session.IsPaused);
        UpdateElapsedTime();
    }

    private void AddMarker()
    {
        if (_session is not { } session)
        {
            return;
        }

        session.AddMarker();
        _icon.ShowBalloonTip(1_500, "quill", $"Momento marcado en {FormatDuration(session.Elapsed)}.", Forms.ToolTipIcon.Info);
    }

    private void OnTick()
    {
        UpdateElapsedTime();
        WatchAudioDevices();
    }

    /// <summary>
    /// Cambiar de auriculares a media reunion deja el loopback apuntando al
    /// dispositivo viejo y system.wav se queda mudo sin que nada lo indique. Se
    /// avisa una sola vez y no se detiene nada: cortar la grabacion seria peor.
    /// </summary>
    private void WatchAudioDevices()
    {
        if (_session is not { } session || _deviceChangeReported || ++_deviceCheckTicks < 5)
        {
            return;
        }

        _deviceCheckTicks = 0;
        if (!session.DefaultDevicesChanged())
        {
            return;
        }

        _deviceChangeReported = true;
        _icon.ShowBalloonTip(
            8_000,
            "quill - aviso",
            "El dispositivo de audio predeterminado cambio. La grabacion sigue con el anterior, "
                + "asi que puede que el audio del sistema ya no se este capturando.",
            Forms.ToolTipIcon.Warning);
    }

    private void ShowSessions()
    {
        _sessionsWindow ??= new SessionsWindow(_sessionsViewModel);
        _sessionsWindow.ShowRefreshed();
    }

    private void OpenRecordings()
    {
        Directory.CreateDirectory(_recordingsRoot);
        Process.Start(new ProcessStartInfo(_recordingsRoot) { UseShellExecute = true });
    }

    /// <summary>
    /// El README promete que la grabacion se indica de forma visible, y el tooltip
    /// solo se ve al pasar el raton. El punto rojo se ve sin interactuar.
    /// </summary>
    private void ApplyIcon(bool recording)
    {
        var icon = recording ? _recordingIcon ?? _idleIcon : _idleIcon;
        if (!ReferenceEquals(_icon.Icon, icon))
        {
            _icon.Icon = icon;
        }
    }

    private static System.Drawing.Icon LoadIcon()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "quill.ico");
        return File.Exists(path) ? new System.Drawing.Icon(path) : System.Drawing.SystemIcons.Application;
    }

    /// <summary>
    /// Se dibuja sobre el icono base en vez de empaquetar un segundo .ico para que
    /// siga funcionando aunque el asset cambie. Un fallo aqui solo cuesta el punto.
    /// </summary>
    private static System.Drawing.Icon? TryCreateRecordingIcon(System.Drawing.Icon source)
    {
        try
        {
            using var bitmap = source.ToBitmap();
            using var graphics = System.Drawing.Graphics.FromImage(bitmap);
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            var diameter = Math.Max(6f, bitmap.Width * 0.45f);
            var left = bitmap.Width - diameter;
            var top = bitmap.Height - diameter;
            using var halo = new System.Drawing.SolidBrush(System.Drawing.Color.White);
            graphics.FillEllipse(halo, left - 1f, top - 1f, diameter + 2f, diameter + 2f);
            using var dot = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(0xD3, 0x2F, 0x2F));
            graphics.FillEllipse(dot, left, top, diameter, diameter);

            var handle = bitmap.GetHicon();
            try
            {
                // Clonar suelta el handle nativo: el icono resultante es autonomo.
                using var borrowed = System.Drawing.Icon.FromHandle(handle);
                return (System.Drawing.Icon)borrowed.Clone();
            }
            finally
            {
                DestroyIcon(handle);
            }
        }
        catch (Exception exception) when (
            exception is ArgumentException or System.Runtime.InteropServices.ExternalException)
        {
            return null;
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    private static string FormatDuration(TimeSpan duration) => duration.TotalHours >= 1
        ? $"{(int)duration.TotalHours}:{duration.Minutes:D2}:{duration.Seconds:D2}"
        : $"{duration.Minutes}:{duration.Seconds:D2}";
}
