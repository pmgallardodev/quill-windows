using System.Diagnostics;
using System.Windows.Threading;
using Quill.Windows.Audio;
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
        _icon.DoubleClick += async (_, _) => await ToggleAsync();

        _ticker = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _ticker.Tick += (_, _) => UpdateElapsedTime();

        _transcription = new TranscriptionCoordinator(recordingsRoot);
        _transcription.StatusChanged += status => _application.Dispatcher.BeginInvoke(() => ShowTranscriptionStatus(status));
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

    private void ShowTranscriptionStatus(TranscriptionStatus status)
    {
        var name = Path.GetFileName(status.SessionDirectory);
        if (!status.Finished)
        {
            _icon.Text = status.Queued > 0
                ? $"quill - transcribiendo ({status.Queued} en cola)"
                : "quill - transcribiendo";
            return;
        }

        if (status.Error is null)
        {
            _icon.Text = "quill - listo";
            _icon.ShowBalloonTip(4_000, "quill", $"Transcripcion lista: {name}", Forms.ToolTipIcon.Info);
        }
        else
        {
            _icon.Text = "quill - error de transcripcion";
            _icon.ShowBalloonTip(6_000, "quill - error", $"{name}: {status.Error}", Forms.ToolTipIcon.Error);
        }
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
