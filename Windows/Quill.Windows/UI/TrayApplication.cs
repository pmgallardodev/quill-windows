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
    private readonly DispatcherTimer _ticker;
    private readonly TranscriptionCoordinator _transcription;
    private readonly SessionsViewModel _sessionsViewModel;
    private SessionsWindow? _sessionsWindow;
    private RecordingSession? _session;
    private DateTimeOffset? _recordingStartedAt;
    private bool _disposed;

    public TrayController(WpfApplication application, string recordingsRoot)
    {
        _application = application;
        _recordingsRoot = recordingsRoot;
        Directory.CreateDirectory(recordingsRoot);

        var menu = new Forms.ContextMenuStrip();
        _toggle = new Forms.ToolStripMenuItem("Iniciar grabacion", null, async (_, _) => await ToggleAsync());
        menu.Items.Add(_toggle);
        menu.Items.Add(new Forms.ToolStripMenuItem("Ver sesiones", null, (_, _) => ShowSessions()));
        menu.Items.Add(new Forms.ToolStripMenuItem("Abrir grabaciones", null, (_, _) => OpenRecordings()));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(new Forms.ToolStripMenuItem("Salir", null, (_, _) => _application.Shutdown()));

        _icon = new Forms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "quill - listo",
            ContextMenuStrip = menu,
            Visible = true,
        };
        // Abrir la ventana en lugar de grabar: un doble clic accidental ya no puede
        // cortar una reunion en curso.
        _icon.DoubleClick += (_, _) => ShowSessions();

        _ticker = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _ticker.Tick += (_, _) => UpdateElapsedTime();

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
        if (_session is not null)
        {
            _session.Stop();
            _session.Dispose();
            _session = null;
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
            _sessionsViewModel.SetActiveSession(null);
            session.Dispose();
            _toggle.Text = "Iniciar grabacion";
        }
    }

    private void UpdateElapsedTime()
    {
        if (_recordingStartedAt is not { } startedAt)
        {
            return;
        }

        var elapsed = DateTimeOffset.UtcNow - startedAt;
        _icon.Text = $"quill - GRABANDO {FormatDuration(elapsed)}";
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

    private static System.Drawing.Icon LoadIcon()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "quill.ico");
        return File.Exists(path) ? new System.Drawing.Icon(path) : System.Drawing.SystemIcons.Application;
    }

    private static string FormatDuration(TimeSpan duration) => duration.TotalHours >= 1
        ? $"{(int)duration.TotalHours}:{duration.Minutes:D2}:{duration.Seconds:D2}"
        : $"{duration.Minutes}:{duration.Seconds:D2}";
}
