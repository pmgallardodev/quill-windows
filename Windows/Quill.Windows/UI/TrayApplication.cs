using System.Diagnostics;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Threading;
using Quill.Core.AiPack;
using Quill.Core.Ports;
using Quill.Core.Scheduling;
using Quill.Core.Storage;
using Quill.Windows.Audio;
using Quill.Windows.Infrastructure;
using Quill.Windows.Privacy;
using Quill.Windows.Sessions;
using Quill.Windows.Transcription;
using Quill.Windows.UI.Companion;
using Forms = System.Windows.Forms;
using WpfApplication = System.Windows.Application;

namespace Quill.Windows.UI;

internal static class TrayApplication
{
    public static int Run(string recordingsRoot)
    {
        var mutexName = $"Local\\Quill-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(recordingsRoot)))}";
        using var instanceMutex = new Mutex(initiallyOwned: true, mutexName, out var createdNew);
        if (!createdNew)
        {
            return 0;
        }

        var application = new WpfApplication
        {
            ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown,
        };
        Theme.ThemeBootstrapper.Initialize(application);
        DiscreetMode.Initialize(ConfigStore.LoadSettings().Privacy);
        using var controller = new TrayController(application, recordingsRoot);
        var exitCode = application.Run();
        controller.WaitForCleanup();
        return exitCode;
    }
}

internal sealed class TrayController : IDisposable
{
    private readonly WpfApplication _application;
    private readonly string _recordingsRoot;
    private readonly Forms.NotifyIcon _icon;
    private readonly QuillNotifier _notifier;
    private readonly Forms.ToolStripMenuItem _toggle;
    private readonly Forms.ToolStripMenuItem _pause;
    private readonly Forms.ToolStripMenuItem _marker;
    private readonly System.Drawing.Icon _idleIcon;
    private readonly System.Drawing.Icon? _recordingIcon;
    private readonly DispatcherTimer _ticker;
    private readonly TranscriptionCoordinator _transcription;
    private readonly Action<TranscriptionUpdate> _transcriptionStateHandler;
    private readonly MainViewModel _sessionsViewModel;
    private MainWindow? _sessionsWindow;
    private RecordingSession? _session;
    /// <summary>Serializes live Whisper (and later the LLM) so jobs never fight over the GPU.</summary>
    private readonly InferenceScheduler _inference = new();
    private readonly IAiPackLocator _aiPack = new ManifestAiPackLocator(AppContext.BaseDirectory);
    private LiveTranscriptionModule? _live;
    private DateTimeOffset? _recordingStartedAt;
    private readonly GlobalHotkeys? _hotkeys;
    private readonly bool _detectsMeetings;
    private readonly bool _startsMeetingsAutomatically;
    private int _deviceCheckTicks;
    private int _meetingCheckTicks;
    private bool _meetingCheckRunning;
    private string? _meetingOffered;
    private bool _deviceChangeReported;
    private bool _disposed;
    private bool _isStopping;
    private Task? _stopTask;
    private Task? _cleanupTask;
    private bool _coreDisposed;
    private int _recordingGeneration;
    private bool _shutdownRequested;
    private string? _stoppingSessionDirectory;

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
        menu.Items.Add(new Forms.ToolStripMenuItem("Salir", null, (_, _) => _ = RequestShutdownAsync()));

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
        _notifier = new QuillNotifier(ShowOsNotification, () => DiscreetMode.IsOn);
        // The tray menu is a WinForms popup, not a WPF window: protect it when it opens.
        menu.Opened += (_, _) => DiscreetMode.ProtectSurface(menu.Handle);
        DiscreetMode.Changed += OnDiscreetModeChanged;

        _ticker = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _ticker.Tick += (_, _) => OnTick();

        _transcription = new TranscriptionCoordinator(recordingsRoot);
        _transcriptionStateHandler = update =>
        {
            if (_disposed || _coreDisposed)
            {
                return;
            }

            try
            {
                _ = _application.Dispatcher.BeginInvoke(() =>
                {
                    if (!_disposed && !_coreDisposed)
                    {
                        ShowTranscriptionStatus(update);
                    }
                });
            }
            catch (InvalidOperationException)
            {
                // The dispatcher may already be shutting down.
            }
        };
        _transcription.StateChanged += _transcriptionStateHandler;
        _sessionsViewModel = new MainViewModel(recordingsRoot, _transcription, _application.Dispatcher);
        var recording = ConfigStore.Load().Recording;
        _detectsMeetings = recording.DetectsMeetings;
        _startsMeetingsAutomatically = recording.StartsMeetingsAutomatically;
        if (recording.GlobalHotkeys)
        {
            _hotkeys = GlobalHotkeys.TryCreate(
                () => Post(() => _ = ToggleAsync()),
                () => Post(AddMarker),
                DiscreetMode.Settings.CompanionGesture);
            if (_hotkeys is not null)
            {
                _hotkeys.CompanionToggleRequested += () => Post(OnCompanionHotkey);
            }
        }

        // El ticker tiene que correr tambien sin grabacion: detectar una reunion
        // solo sirve cuando aun no le has dado a grabar.
        if (_detectsMeetings)
        {
            _ticker.Start();
        }

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
        if (_stopTask is { } stopTask)
        {
            _cleanupTask = FinishDisposeAfterAsync(stopTask, _stoppingSessionDirectory);
            return;
        }

        var activeSession = _session;
        _session = null;
        if (activeSession is not null)
        {
            _cleanupTask = FinishDisposeAfterAsync(Task.Run(activeSession.Dispose), activeSession.Directory);
            return;
        }

        DisposeCore();
    }

    public void WaitForCleanup()
    {
        try
        {
            _cleanupTask?.Wait(TimeSpan.FromSeconds(10));
        }
        catch
        {
            // Cleanup errors were already handled before the process exits.
        }

        DisposeCore();
    }

    private void DisposeCore()
    {
        if (_coreDisposed)
        {
            return;
        }

        _coreDisposed = true;
        if (_sessionsWindow is not null)
        {
            _sessionsWindow.AllowClose = true;
            _sessionsWindow.Close();
            _sessionsWindow = null;
        }

        DiscreetMode.Changed -= OnDiscreetModeChanged;
        _hotkeys?.Dispose();
        _sessionsViewModel.Dispose();
        _transcription.StateChanged -= _transcriptionStateHandler;
        _transcription.Dispose();
        if (_live is { } live)
        {
            _live = null;
            _ = live.DisposeAsync().AsTask();
        }

        _inference.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
        _recordingIcon?.Dispose();
        _idleIcon.Dispose();
    }

    private async Task FinishDisposeAfterAsync(Task cleanup, string? sessionDirectory = null)
    {
        try
        {
            await cleanup.ConfigureAwait(false);
        }
        catch
        {
            // Shutdown must continue even if an audio device already failed.
        }

        if (sessionDirectory is not null && File.Exists(Path.Combine(sessionDirectory, "meta.json")))
        {
            try
            {
                _transcription.Enqueue(sessionDirectory);
            }
            catch
            {
                // The next launch can recover a session with metadata even when
                // shutdown happened while the coordinator was closing.
            }
        }

        try
        {
            _ = _application.Dispatcher.BeginInvoke(DisposeCore);
        }
        catch (InvalidOperationException)
        {
            // The dispatcher is already stopped. WaitForCleanup performs the
            // final cleanup on the application thread after Run returns.
        }
    }

    private async Task ToggleAsync()
    {
        if (_isStopping)
        {
            return;
        }

        if (_session is null)
        {
            StartRecording();
        }
        else
        {
            await StopAndTrackAsync();
        }
    }

    private async Task StopAndTrackAsync()
    {
        var stopTask = StopRecordingAsync();
        _stopTask = stopTask;
        try
        {
            await stopTask;
        }
        finally
        {
            if (ReferenceEquals(_stopTask, stopTask))
            {
                _stopTask = null;
            }
        }
    }

    private async Task RequestShutdownAsync()
    {
        if (_disposed || _shutdownRequested)
        {
            return;
        }

        _shutdownRequested = true;
        try
        {
            if (_stopTask is { } stopTask)
            {
                await stopTask;
            }
            else if (_session is not null)
            {
                await StopAndTrackAsync();
            }
        }
        catch
        {
            // StopRecordingAsync already surfaced the detailed error. Shutdown must continue.
        }

        if (!_disposed)
        {
            _application.Shutdown();
        }
    }

    private void StartRecording()
    {
        if (_isStopping || _disposed)
        {
            return;
        }

        _recordingGeneration++;
        var live = CreateLiveTranscription();
        try
        {
            _session = RecordingSession.Start(_recordingsRoot, live?.Tap);
            _session.CaptureFailed += OnCaptureFailed;
            if (live is not null)
            {
                live.Start(new FileSessionStore(_recordingsRoot), Path.GetFileName(_session.Directory));
                _live = live;
                live = null;
            }

            _recordingStartedAt = DateTimeOffset.UtcNow;
            DiscreetMode.SetRecording(true);
            _toggle.Text = "Detener grabacion";
            _pause.Text = "Pausar grabacion";
            _pause.Visible = true;
            _marker.Visible = true;
            _deviceCheckTicks = 0;
            _deviceChangeReported = false;
            ApplyIcon(recording: true);
            SetTooltip("quill - GRABANDO 0:00");
            _ticker.Start();
            _sessionsViewModel.SetActiveSession(_session.Directory, isPaused: false);
            _notifier.Notify(3_000, "quill", "Grabacion activa: microfono y audio del sistema.", NotificationKind.Info);
        }
        catch (Exception exception)
        {
            var failedSession = _session;
            _session = null;
            _recordingStartedAt = null;
            DiscreetMode.SetRecording(false);
            if (!_detectsMeetings)
            {
                _ticker.Stop();
            }
            _toggle.Text = "Iniciar grabacion";
            _pause.Visible = false;
            _marker.Visible = false;
            ApplyIcon(recording: false);
            try
            {
                _sessionsViewModel.SetActiveSession(null);
            }
            catch
            {
                // The failed start is already being reported below.
            }

            try
            {
                failedSession?.Dispose();
            }
            catch
            {
                // Preserve the original startup error for the user.
            }

            if (live is not null)
            {
                _ = live.DisposeAsync().AsTask();
            }

            _notifier.Notify(6_000, "quill - error", exception.Message, NotificationKind.Error);
        }
    }

    /// <summary>Null when the AI pack has no live model: recording then works without live text.</summary>
    private LiveTranscriptionModule? CreateLiveTranscription()
    {
        try
        {
            var config = ConfigStore.Load().Transcription;
            var language = string.IsNullOrWhiteSpace(config.Language) ? "auto" : config.Language.Trim();
            var threads = WhisperStreamingTranscriber.LiveThreads(Environment.ProcessorCount);
            return LiveTranscriptionModule.TryCreate(
                _aiPack.Locate(),
                modelPath => WhisperStreamingTranscriber.Create(modelPath, language, threads, _inference));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Trace.TraceWarning($"quill: live transcription unavailable: {exception.Message}");
            return null;
        }
    }

    private async Task StopRecordingAsync()
    {
        var session = _session;
        if (session is null || _isStopping)
        {
            return;
        }

        _isStopping = true;
        _recordingGeneration++;
        _session = null;
        DiscreetMode.SetRecording(false);
        _stoppingSessionDirectory = session.Directory;
        if (!_detectsMeetings)
        {
            _ticker.Stop();
        }

        _recordingStartedAt = null;
        Exception? stopError = null;
        var queued = false;
        try
        {
            // WASAPI may dispatch RecordingStopped through the WPF context. Waiting
            // on the UI thread would deadlock that callback, so stop off-dispatcher.
            await Task.Run(session.Stop);
        }
        catch (Exception exception)
        {
            stopError = exception;
        }

        try
        {
            await Task.Run(session.Dispose);
        }
        catch (Exception exception)
        {
            stopError ??= exception;
        }
        finally
        {
            session.CaptureFailed -= OnCaptureFailed;
        }

        // Capture has stopped, so the tap holds everything. Finish live text before the final
        // pass starts so both never compete for the GPU; StopAsync never throws.
        if (_live is { } live)
        {
            _live = null;
            await live.StopAsync(TimeSpan.FromSeconds(15));
        }

        // A capture error can be reported after metadata was written. Do not lose
        // the valid session just because Stop surfaced that warning to the caller.
        if (File.Exists(Path.Combine(session.Directory, "meta.json")))
        {
            try
            {
                queued = _transcription.Enqueue(session.Directory);
            }
            catch (Exception exception)
            {
                stopError ??= exception;
            }
        }

        try
        {
            _sessionsViewModel.SetActiveSession(null);
        }
        catch
        {
            // A refresh failure must not leave the audio session undisposed.
        }

        if (stopError is not null)
        {
            SetTooltip(queued ? "quill - transcripcion en cola" : "quill - error al guardar");
            _notifier.Notify(6_000, "quill - aviso", stopError.Message, NotificationKind.Warning);
        }
        else
        {
            SetTooltip(queued ? "quill - transcripcion en cola" : "quill - grabacion guardada");
            _notifier.Notify(
                3_000,
                "quill",
                queued
                    ? "Grabacion terminada. La transcripcion local inicio en segundo plano."
                    : "Grabacion terminada. La transcripcion esta desactivada o no pudo encolarse.",
                NotificationKind.Info);
        }

        _toggle.Text = "Iniciar grabacion";
        _pause.Visible = false;
        _marker.Visible = false;
        ApplyIcon(recording: false);
        _isStopping = false;
        _stoppingSessionDirectory = null;
    }

    private void OnCaptureFailed(Exception exception)
    {
        Post(() =>
        {
            if (_session is not null)
            {
                _notifier.Notify(
                    6_000,
                    "quill - aviso",
                    $"La captura de audio encontro un problema: {exception.Message}",
                    NotificationKind.Warning);
            }
        });
    }

    private void UpdateElapsedTime()
    {
        if (_session is not { } session || _recordingStartedAt is null)
        {
            return;
        }

        var elapsed = FormatDuration(session.Elapsed);
        SetTooltip(session.IsPaused ? $"quill - PAUSADO {elapsed}" : $"quill - GRABANDO {elapsed}");
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
                    SetTooltip(update.Queued > 0
                        ? $"quill - transcribiendo ({update.Queued} en cola)"
                        : "quill - transcribiendo");
                }
                break;

            case SessionState.Done:
                if (!isRecording)
                {
                    SetTooltip("quill - listo");
                }
                _notifier.Notify(4_000, "quill", $"Transcripcion lista: {name}", NotificationKind.Info);
                break;

            case SessionState.Failed:
                if (!isRecording)
                {
                    SetTooltip("quill - error de transcripcion");
                }
                _notifier.Notify(6_000, "quill - error", $"{name}: {update.Error}", NotificationKind.Error);
                break;

            case SessionState.Disabled:
                if (!isRecording)
                {
                    SetTooltip("quill - transcripcion desactivada");
                }

                _notifier.Notify(
                    4_000,
                    "quill",
                    $"Grabacion guardada sin transcribir: {name}",
                    NotificationKind.Info);
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

        _sessionsViewModel.SetActiveSession(session.Directory, session.IsPaused);
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
        _notifier.Notify(1_500, "quill", $"Momento marcado en {FormatDuration(session.Elapsed)}.", NotificationKind.Info);
    }

    private void OnTick()
    {
        UpdateElapsedTime();
        WatchAudioDevices();
        WatchForMeetings();
    }

    /// <summary>
    /// El fallo mas caro de un grabador es olvidarse de darle a grabar. Se comprueba
    /// cada cinco segundos y solo se ofrece una vez por aplicacion, para no convertir
    /// una llamada larga en una fila de globos.
    /// </summary>
    private async void WatchForMeetings()
    {
        if (!_detectsMeetings || _isStopping || _meetingCheckRunning || ++_meetingCheckTicks < 5)
        {
            return;
        }

        _meetingCheckTicks = 0;
        _meetingCheckRunning = true;
        var observedGeneration = _recordingGeneration;
        string? application;
        try
        {
            application = await Task.Run(MeetingDetector.DetectActiveMeeting);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return;
        }
        finally
        {
            _meetingCheckRunning = false;
        }

        if (_disposed || observedGeneration != _recordingGeneration || _session is not null || _isStopping)
        {
            return;
        }

        if (application is null)
        {
            // Al colgar se olvida, de modo que la siguiente reunion vuelve a avisar.
            _meetingOffered = null;
            return;
        }

        if (string.Equals(application, _meetingOffered, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (_startsMeetingsAutomatically)
        {
            StartRecording();
            if (_session is not null)
            {
                _meetingOffered = application;
                _notifier.Notify(4_000, "quill", $"Grabacion iniciada: {application} esta usando el microfono.", NotificationKind.Info);
            }

            return;
        }

        _meetingOffered = application;
        var shortcut = _hotkeys?.ToggleRegistered == true
            ? "Ctrl+Shift+R"
            : "el menu de la bandeja";
        _notifier.Notify(
            6_000,
            "quill",
            $"{application} esta usando el microfono. Usa {shortcut} para grabar.",
            NotificationKind.Info,
            NotificationTopic.MeetingDetected,
            application);
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
        var changed = session.DefaultDevicesChanged();
        if (!changed.Microphone && !changed.System)
        {
            return;
        }

        _deviceChangeReported = true;
        var changedDevices = changed.Microphone && changed.System
            ? "el microfono y el dispositivo de audio"
            : changed.Microphone
                ? "el microfono"
                : "el dispositivo de audio";
        _notifier.Notify(
            8_000,
            "quill - aviso",
            $"Cambio {changedDevices} predeterminado. La grabacion sigue con el anterior, "
                + "asi que puede que una pista ya no se este capturando.",
            NotificationKind.Warning);
    }

    private void ShowSessions()
    {
        _sessionsWindow ??= new MainWindow(_sessionsViewModel);
        _sessionsWindow.ShowRefreshed();
    }

    private void OpenRecordings()
    {
        try
        {
            Directory.CreateDirectory(_recordingsRoot);
            Process.Start(new ProcessStartInfo(_recordingsRoot) { UseShellExecute = true });
        }
        catch (Exception exception) when (
            exception is Win32Exception or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        {
            _notifier.Notify(6_000, "quill - error", $"No se pudo abrir grabaciones: {exception.Message}", NotificationKind.Error);
        }
    }

    private void Post(Action action)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _application.Dispatcher.BeginInvoke(action);
        }
        catch (InvalidOperationException)
        {
            // A hotkey can arrive while WPF is shutting down.
        }
    }

    /// <summary>
    /// El README promete que la grabacion se indica de forma visible, y el tooltip
    /// solo se ve al pasar el raton. El punto rojo se ve sin interactuar.
    /// </summary>
    private void ApplyIcon(bool recording)
    {
        var icon = TrayAppearance.ShowsRecordingIcon(recording, DiscreetMode.Settings.UsesNeutralTrayIcon)
            ? _recordingIcon ?? _idleIcon
            : _idleIcon;
        if (!ReferenceEquals(_icon.Icon, icon))
        {
            _icon.Icon = icon;
        }
    }

    /// <summary>All user notifications go through here; see <see cref="QuillNotifier"/>.</summary>
    public QuillNotifier Notifier => _notifier;

    /// <summary>
    /// Set by the composition root once the companion panel module exists; the companion
    /// hotkey then shows or hides it directly. While null the hotkey raises
    /// <see cref="CompanionToggleRequested"/> instead.
    /// </summary>
    public CompanionPanelModule? CompanionPanel { get; set; }

    /// <summary>The companion hotkey was pressed and no <see cref="CompanionPanel"/> is set (UI thread).</summary>
    public event Action? CompanionToggleRequested;

    private void OnCompanionHotkey()
    {
        if (CompanionPanel is { } panel)
        {
            panel.ToggleVisibility();
        }
        else
        {
            CompanionToggleRequested?.Invoke();
        }
    }

    private void ShowOsNotification(QuillNotification notification) =>
        _icon.ShowBalloonTip(
            (int)notification.Duration.TotalMilliseconds,
            notification.Title,
            notification.Text,
            notification.Kind switch
            {
                NotificationKind.Warning => Forms.ToolTipIcon.Warning,
                NotificationKind.Error => Forms.ToolTipIcon.Error,
                _ => Forms.ToolTipIcon.Info,
            });

    /// <summary>In discreet mode with a neutral icon the tooltip never says "GRABANDO".</summary>
    private void SetTooltip(string text) =>
        _icon.Text = TrayAppearance.Tooltip(text, _session is not null, DiscreetMode.Settings.UsesNeutralTrayIcon);

    private void OnDiscreetModeChanged(object? sender, EventArgs e)
    {
        if (_coreDisposed)
        {
            return;
        }

        if (_session is { } session)
        {
            ApplyIcon(recording: !session.IsPaused);
            UpdateElapsedTime();
        }

        var gesture = DiscreetMode.Settings.CompanionGesture;
        if (_hotkeys is not null && _hotkeys.CompanionGesture != gesture)
        {
            _hotkeys.SetCompanionGesture(gesture);
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
