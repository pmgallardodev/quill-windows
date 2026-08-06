using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Quill.Windows.Infrastructure;
using Quill.Windows.Sessions;

namespace Quill.Windows.Audio;

/// <summary>
/// Captures microphone and speaker loopback independently. The tracks make
/// speaker labels deterministic without sending audio to a diarization service.
/// </summary>
internal sealed class RecordingSession : IDisposable
{
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private readonly MMDeviceEnumerator _devices;
    private readonly MMDevice _microphoneDevice;
    private readonly MMDevice _speakerDevice;
    private readonly WasapiCapture _microphone;
    private readonly WasapiLoopbackCapture _systemAudio;
    private readonly TrackWriter _microphoneWriter;
    private readonly TrackWriter _systemAudioWriter;
    private readonly List<long> _markers = new();
    private DateTimeOffset? _pausedAt;
    private TimeSpan _pausedTotal;
    private bool _stopped;
    private bool _capturing;
    private Exception? _captureError;
    private readonly object _captureStateLock = new();
    private SessionMetadata? _pendingMetadata;

    private RecordingSession(
        string directory,
        MMDeviceEnumerator devices,
        MMDevice microphone,
        MMDevice speakers)
    {
        Directory = directory;
        _devices = devices;
        _microphoneDevice = microphone;
        _speakerDevice = speakers;
        MicrophoneDeviceId = microphone.ID;
        SystemDeviceId = speakers.ID;

        WasapiCapture? microphoneCapture = null;
        WasapiLoopbackCapture? systemAudioCapture = null;
        TrackWriter? microphoneWriter = null;
        TrackWriter? systemAudioWriter = null;
        try
        {
            var voiceQuality = ConfigStore.Load().Recording.RecordsVoiceQuality;
            microphoneCapture = new WasapiCapture(microphone);
            systemAudioCapture = new WasapiLoopbackCapture(speakers);
            microphoneWriter = new TrackWriter(
                microphoneCapture,
                Path.Combine(directory, "mic.wav"),
                voiceQuality);
            systemAudioWriter = new TrackWriter(
                systemAudioCapture,
                Path.Combine(directory, "system.wav"),
                voiceQuality);
            microphoneWriter.CaptureStopped += OnCaptureStopped;
            systemAudioWriter.CaptureStopped += OnCaptureStopped;
        }
        catch
        {
            DisposeQuietly(systemAudioWriter);
            DisposeQuietly(microphoneWriter);
            DisposeQuietly(systemAudioCapture);
            DisposeQuietly(microphoneCapture);
            throw;
        }

        _microphone = microphoneCapture;
        _systemAudio = systemAudioCapture;
        _microphoneWriter = microphoneWriter;
        _systemAudioWriter = systemAudioWriter;
    }

    public string Directory { get; }

    public string MicrophoneDeviceId { get; }

    public string SystemDeviceId { get; }

    public bool IsPaused => _pausedAt is not null;

    public event Action<Exception>? CaptureFailed;

    /// <summary>Tiempo grabado de verdad: lo que se pasa en pausa no cuenta.</summary>
    public TimeSpan Elapsed => (_pausedAt ?? DateTimeOffset.UtcNow) - _startedAt - _pausedTotal;

    public void Pause()
    {
        if (_stopped || _pausedAt is not null)
        {
            return;
        }

        _pausedAt = DateTimeOffset.UtcNow;
        _microphoneWriter.IsPaused = true;
        _systemAudioWriter.IsPaused = true;
    }

    public void Resume()
    {
        if (_stopped || _pausedAt is not { } pausedAt)
        {
            return;
        }

        _pausedTotal += DateTimeOffset.UtcNow - pausedAt;
        _pausedAt = null;
        _microphoneWriter.IsPaused = false;
        _systemAudioWriter.IsPaused = false;
    }

    /// <summary>Apunta el instante actual para destacarlo luego en el transcript.</summary>
    public void AddMarker()
    {
        if (_stopped)
        {
            return;
        }

        lock (_markers)
        {
            _markers.Add((long)Elapsed.TotalMilliseconds);
        }
    }

    /// <summary>
    /// WASAPI sigue apuntando al dispositivo que habia al empezar. Si el usuario
    /// cambia de auriculares, la pista se queda muda sin que nada lo indique.
    /// </summary>
    public AudioDeviceChange DefaultDevicesChanged()
    {
        try
        {
            using var microphone = _devices.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
            using var speakers = _devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return new AudioDeviceChange(
                microphone.ID != MicrophoneDeviceId,
                speakers.ID != SystemDeviceId);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Quedarse sin dispositivo por defecto tambien es un cambio.
            return new AudioDeviceChange(true, true);
        }
    }

    public static RecordingSession Start(string recordingsRoot)
    {
        System.IO.Directory.CreateDirectory(recordingsRoot);
        var directory = CreateSessionDirectory(recordingsRoot);
        MMDeviceEnumerator? devices = null;
        MMDevice? microphone = null;
        MMDevice? speakers = null;
        try
        {
            devices = new MMDeviceEnumerator();
            microphone = devices.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
            speakers = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var session = new RecordingSession(directory, devices, microphone, speakers);
            microphone = null;
            speakers = null;
            try
            {
                session.StartCapture();
                return session;
            }
            catch
            {
                session.Dispose();
                throw;
            }
        }
        catch
        {
            microphone?.Dispose();
            speakers?.Dispose();
            devices?.Dispose();
            try
            {
                System.IO.Directory.Delete(directory, recursive: true);
            }
            catch (Exception cleanupException) when (
                cleanupException is IOException or UnauthorizedAccessException)
            {
                // The startup error is more useful than a failed best-effort cleanup.
            }

            throw;
        }
    }

    public void Stop()
    {
        if (_stopped)
        {
            if (_pendingMetadata is not null)
            {
                WriteMetadata(_pendingMetadata);
            }

            return;
        }

        _stopped = true;
        var completedCapture = _capturing;
        Exception? stopError = null;
        if (completedCapture)
        {
            try
            {
                _microphone.StopRecording();
            }
            catch (Exception exception)
            {
                stopError = exception;
            }

            try
            {
                _systemAudio.StopRecording();
            }
            catch (Exception exception)
            {
                stopError ??= exception;
            }

            var microphoneStopped = _microphoneWriter.WaitForCaptureStopped(TimeSpan.FromSeconds(5));
            var systemStopped = _systemAudioWriter.WaitForCaptureStopped(TimeSpan.FromSeconds(5));
            if (!microphoneStopped || !systemStopped)
            {
                stopError ??= new TimeoutException("la captura de audio no termino a tiempo");
            }

            _capturing = false;
        }

        lock (_captureStateLock)
        {
            stopError ??= _captureError ?? _microphoneWriter.CaptureError ?? _systemAudioWriter.CaptureError;
        }

        try
        {
            _microphoneWriter.Dispose();
        }
        catch (Exception exception)
        {
            stopError ??= exception;
        }

        try
        {
            _systemAudioWriter.Dispose();
        }
        catch (Exception exception)
        {
            stopError ??= exception;
        }

        if (!completedCapture)
        {
            if (stopError is not null)
            {
                throw stopError;
            }

            return;
        }

        var endedAt = DateTimeOffset.UtcNow;
        var pausedTotal = _pausedTotal + (_pausedAt is { } pausedAt
            ? endedAt - pausedAt
            : TimeSpan.Zero);
        var microphoneStarted = _microphoneWriter.FirstBufferAt ?? _startedAt;
        var systemStarted = _systemAudioWriter.FirstBufferAt ?? _startedAt;
        var earliest = microphoneStarted <= systemStarted ? microphoneStarted : systemStarted;
        var metadata = new SessionMetadata
        {
            Started = _startedAt,
            Ended = endedAt,
            DurationSeconds = Math.Max(0L, (long)(endedAt - _startedAt - pausedTotal).TotalSeconds),
            Files = new Dictionary<string, string>
            {
                ["mic"] = "mic.wav",
                ["system"] = "system.wav",
            },
            StartOffsetMs = new Dictionary<string, long>
            {
                ["mic"] = (long)(microphoneStarted - earliest).TotalMilliseconds,
                ["system"] = (long)(systemStarted - earliest).TotalMilliseconds,
            },
            Markers = MarkersSnapshot(),
        };
        _pendingMetadata = metadata;
        try
        {
            WriteMetadata(metadata);
        }
        catch (Exception exception)
        {
            stopError ??= exception;
        }

        if (stopError is not null)
        {
            throw stopError;
        }
    }

    private void WriteMetadata(SessionMetadata metadata)
    {
        AtomicFile.Write(
            Path.Combine(Directory, "meta.json"),
            System.Text.Json.JsonSerializer.Serialize(
                metadata,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        _pendingMetadata = null;
    }

    public void Dispose()
    {
        Exception? disposeError = null;
        try
        {
            Stop();
        }
        catch (Exception exception)
        {
            disposeError = exception;
        }

        foreach (var disposable in new IDisposable[]
                 {
                     _microphone, _systemAudio, _microphoneDevice, _speakerDevice, _devices,
                 })
        {
            try
            {
                disposable.Dispose();
            }
            catch (Exception exception)
            {
                disposeError ??= exception;
            }
        }

        if (disposeError is not null)
        {
            throw disposeError;
        }
    }

    private void StartCapture()
    {
        var systemStarted = false;
        var microphoneStarted = false;
        try
        {
            _systemAudio.StartRecording();
            systemStarted = true;
            _microphone.StartRecording();
            microphoneStarted = true;
            _capturing = true;
        }
        catch
        {
            if (microphoneStarted)
            {
                try
                {
                    _microphone.StopRecording();
                }
                catch
                {
                }
            }

            if (systemStarted)
            {
                try
                {
                    _systemAudio.StopRecording();
                }
                catch
                {
                }
            }

            throw;
        }
    }

    private List<long> MarkersSnapshot()
    {
        lock (_markers)
        {
            return new List<long>(_markers);
        }
    }

    private static string CreateSessionDirectory(string root)
    {
        var baseName = DateTime.Now.ToString("yyyy.MM.dd-HHmm");
        for (var suffix = 1; suffix < 10_000; suffix++)
        {
            var candidate = Path.Combine(root, suffix == 1 ? baseName : $"{baseName}-{suffix}");
            if (System.IO.Directory.Exists(candidate))
            {
                continue;
            }

            try
            {
                System.IO.Directory.CreateDirectory(candidate);
                using (new FileStream(
                           Path.Combine(candidate, ".quill-session"),
                           FileMode.CreateNew,
                           FileAccess.Write,
                           FileShare.Read))
                {
                }

                return new System.IO.DirectoryInfo(candidate).FullName;
            }
            catch (IOException)
            {
                // Another process may have claimed the same minute. Try the next
                // suffix instead of sharing its WAV files.
            }
        }

        throw new IOException("no se pudo reservar una carpeta de sesion unica");
    }

    private static void DisposeQuietly(IDisposable? disposable)
    {
        try
        {
            disposable?.Dispose();
        }
        catch
        {
            // Preserve the startup error; cleanup is best effort here.
        }
    }

    /// <summary>
    /// Escribe una pista. Por defecto convierte a 16 kHz mono 16 bits, que es
    /// exactamente lo que consume Whisper: el formato nativo de WASAPI suele ser
    /// 48 kHz estereo en coma flotante y ocupa unas once veces mas para nada.
    /// </summary>
    private sealed class TrackWriter : IDisposable
    {
        /// <summary>Whisper trabaja a 16 kHz; remuestrear mas arriba solo gasta disco.</summary>
        private const int VoiceSampleRate = 16_000;

        private readonly IWaveIn _capture;
        private readonly WaveFileWriter _writer;
        private readonly object _writeLock = new();
        private readonly BufferedWaveProvider? _pending;
        private readonly ISampleProvider? _converted;
        private float[] _conversionBuffer = Array.Empty<float>();
        private long _firstBufferTicks;
        private bool _disposed;
        private readonly ManualResetEventSlim _captureStopped = new(false);
        private readonly object _captureSignalLock = new();
        private bool _disposeSignalRequested;
        private bool _captureSignalDisposed;
        private Exception? _captureError;

        public TrackWriter(IWaveIn capture, string outputPath, bool voiceQuality)
        {
            _capture = capture;
            if (!voiceQuality)
            {
                _writer = new WaveFileWriter(outputPath, capture.WaveFormat);
            }
            else
            {
                // BufferedWaveProvider hace de puente entre el callback de captura,
                // que empuja, y la cadena de conversion de NAudio, que tira.
                _pending = new BufferedWaveProvider(capture.WaveFormat)
                {
                    BufferDuration = TimeSpan.FromSeconds(10),
                    // Losing samples silently produces a transcript that looks valid
                    // but is missing speech. Let NAudio surface the overflow instead.
                    DiscardOnBufferOverflow = false,
                    // The default pads every read with silence. The converter is
                    // drained from the capture callback, so padding would stretch
                    // each small callback into a full buffer of mostly-zero audio.
                    ReadFully = false,
                };

                ISampleProvider source = _pending.ToSampleProvider();
                if (source.WaveFormat.Channels > 1)
                {
                    source = new MonoDownmixSampleProvider(source);
                }

                _converted = source.WaveFormat.SampleRate == VoiceSampleRate
                    ? source
                    : new WdlResamplingSampleProvider(source, VoiceSampleRate);
                _writer = new WaveFileWriter(outputPath, new WaveFormat(VoiceSampleRate, 16, 1));
            }

            _capture.DataAvailable += Write;
            _capture.RecordingStopped += OnRecordingStopped;
        }

        public event Action<Exception?>? CaptureStopped;

        public DateTimeOffset? FirstBufferAt => _firstBufferTicks == 0
            ? null
            : new DateTimeOffset(_firstBufferTicks, TimeSpan.Zero);

        /// <summary>Mientras esta en pausa se descarta lo que llega, sin cerrar el WAV.</summary>
        public bool IsPaused { get; set; }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _capture.DataAvailable -= Write;
            _capture.RecordingStopped -= OnRecordingStopped;
            lock (_writeLock)
            {
                try
                {
                    // Lo que quede en el puente todavia no esta en el archivo.
                    DrainConverted();
                }
                finally
                {
                    _writer.Dispose();
                }
            }

            RequestSignalDispose(force: true);
        }

        public bool WaitForCaptureStopped(TimeSpan timeout) => _captureStopped.Wait(timeout);

        public Exception? CaptureError => _captureError;

        private void OnRecordingStopped(object? sender, StoppedEventArgs eventArgs)
        {
            var disposeSignal = false;
            lock (_captureSignalLock)
            {
                _captureError ??= eventArgs.Exception;
                try
                {
                    _captureStopped.Set();
                }
                catch (ObjectDisposedException)
                {
                    // Stop timed out and the signal was released; the late callback
                    // still carries its error through CaptureStopped.
                }
                if (_disposeSignalRequested && !_captureSignalDisposed)
                {
                    _captureSignalDisposed = true;
                    disposeSignal = true;
                }
            }

            CaptureStopped?.Invoke(eventArgs.Exception);
            if (disposeSignal)
            {
                _captureStopped.Dispose();
            }
        }

        private void RequestSignalDispose(bool force)
        {
            var disposeSignal = false;
            lock (_captureSignalLock)
            {
                _disposeSignalRequested = true;
                if ((force || _captureStopped.IsSet) && !_captureSignalDisposed)
                {
                    _captureSignalDisposed = true;
                    disposeSignal = true;
                }
            }

            if (disposeSignal)
            {
                _captureStopped.Dispose();
            }
        }

        private void Write(object? sender, WaveInEventArgs eventArgs)
        {
            if (eventArgs.BytesRecorded == 0 || _disposed || IsPaused)
            {
                return;
            }

            lock (_writeLock)
            {
                if (_disposed || IsPaused)
                {
                    return;
                }

                Interlocked.CompareExchange(ref _firstBufferTicks, DateTimeOffset.UtcNow.Ticks, 0);

                try
                {
                    if (_pending is null)
                    {
                        _writer.Write(eventArgs.Buffer, 0, eventArgs.BytesRecorded);
                        return;
                    }

                    _pending.AddSamples(eventArgs.Buffer, 0, eventArgs.BytesRecorded);
                    DrainConverted();
                }
                catch (Exception exception) when (
                    exception is IOException or InvalidOperationException or ObjectDisposedException)
                {
                    _captureError ??= exception;
                    throw;
                }
            }
        }

        /// <summary>Debe llamarse con _writeLock tomado.</summary>
        private void DrainConverted()
        {
            if (_converted is null)
            {
                return;
            }

            // El remuestreo reduce las muestras, asi que un buffer del tamano de lo
            // que entra siempre basta para lo que sale.
            var capacity = Math.Max(4_096, _pending!.BufferedBytes / sizeof(float) + 1);
            if (_conversionBuffer.Length < capacity)
            {
                _conversionBuffer = new float[capacity];
            }

            int read;
            while ((read = _converted.Read(_conversionBuffer, 0, _conversionBuffer.Length)) > 0)
            {
                _writer.WriteSamples(_conversionBuffer, 0, read);
                if (read < _conversionBuffer.Length)
                {
                    break;
                }
            }
        }
    }

    private void OnCaptureStopped(Exception? exception)
    {
        lock (_captureStateLock)
        {
            _captureError ??= exception;
        }

        if (exception is not null)
        {
            CaptureFailed?.Invoke(exception);
        }
    }
}

internal sealed record AudioDeviceChange(bool Microphone, bool System);
