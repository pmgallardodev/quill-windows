using System.Runtime.InteropServices;
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
        _microphone = new WasapiCapture(microphone);
        _systemAudio = new WasapiLoopbackCapture(speakers);
        var voiceQuality = ConfigStore.Load().Recording.RecordsVoiceQuality;
        _microphoneWriter = new TrackWriter(_microphone, Path.Combine(directory, "mic.wav"), voiceQuality);
        _systemAudioWriter = new TrackWriter(_systemAudio, Path.Combine(directory, "system.wav"), voiceQuality);
    }

    public string Directory { get; }

    public string MicrophoneDeviceId { get; }

    public string SystemDeviceId { get; }

    public bool IsPaused => _pausedAt is not null;

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
    public bool DefaultDevicesChanged()
    {
        try
        {
            using var microphone = _devices.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
            using var speakers = _devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return microphone.ID != MicrophoneDeviceId || speakers.ID != SystemDeviceId;
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException)
        {
            // Quedarse sin dispositivo por defecto tambien es un cambio.
            return true;
        }
    }

    public static RecordingSession Start(string recordingsRoot)
    {
        System.IO.Directory.CreateDirectory(recordingsRoot);
        var directory = CreateSessionDirectory(recordingsRoot);
        var devices = new MMDeviceEnumerator();
        MMDevice? microphone = null;
        MMDevice? speakers = null;
        try
        {
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
            devices.Dispose();
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

            _capturing = false;
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

        if (stopError is not null)
        {
            throw stopError;
        }

        if (!completedCapture)
        {
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
        AtomicFile.Write(
            Path.Combine(Directory, "meta.json"),
            System.Text.Json.JsonSerializer.Serialize(metadata, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
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
        var candidate = Path.Combine(root, baseName);
        for (var suffix = 2; System.IO.Directory.Exists(candidate); suffix++)
        {
            candidate = Path.Combine(root, $"{baseName}-{suffix}");
        }

        return System.IO.Directory.CreateDirectory(candidate).FullName;
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
                    DiscardOnBufferOverflow = true,
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
        }

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
            lock (_writeLock)
            {
                // Lo que quede en el puente todavia no esta en el archivo.
                DrainConverted();
                _writer.Dispose();
            }
        }

        private void Write(object? sender, WaveInEventArgs eventArgs)
        {
            if (eventArgs.BytesRecorded == 0 || _disposed || IsPaused)
            {
                return;
            }

            Interlocked.CompareExchange(ref _firstBufferTicks, DateTimeOffset.UtcNow.Ticks, 0);
            lock (_writeLock)
            {
                if (_disposed)
                {
                    return;
                }

                if (_pending is null)
                {
                    _writer.Write(eventArgs.Buffer, 0, eventArgs.BytesRecorded);
                    return;
                }

                _pending.AddSamples(eventArgs.Buffer, 0, eventArgs.BytesRecorded);
                DrainConverted();
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
}
