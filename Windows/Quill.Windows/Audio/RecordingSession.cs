using NAudio.CoreAudioApi;
using NAudio.Wave;
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
        _microphone = new WasapiCapture(microphone);
        _systemAudio = new WasapiLoopbackCapture(speakers);
        _microphoneWriter = new TrackWriter(_microphone, Path.Combine(directory, "mic.wav"));
        _systemAudioWriter = new TrackWriter(_systemAudio, Path.Combine(directory, "system.wav"));
    }

    public string Directory { get; }

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
        var microphoneStarted = _microphoneWriter.FirstBufferAt ?? _startedAt;
        var systemStarted = _systemAudioWriter.FirstBufferAt ?? _startedAt;
        var earliest = microphoneStarted <= systemStarted ? microphoneStarted : systemStarted;
        var metadata = new SessionMetadata
        {
            Started = _startedAt,
            Ended = endedAt,
            DurationSeconds = (long)(endedAt - _startedAt).TotalSeconds,
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

    private sealed class TrackWriter : IDisposable
    {
        private readonly IWaveIn _capture;
        private readonly WaveFileWriter _writer;
        private readonly object _writeLock = new();
        private long _firstBufferTicks;
        private bool _disposed;

        public TrackWriter(IWaveIn capture, string outputPath)
        {
            _capture = capture;
            _writer = new WaveFileWriter(outputPath, capture.WaveFormat);
            _capture.DataAvailable += Write;
        }

        public DateTimeOffset? FirstBufferAt => _firstBufferTicks == 0
            ? null
            : new DateTimeOffset(_firstBufferTicks, TimeSpan.Zero);

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
                _writer.Dispose();
            }
        }

        private void Write(object? sender, WaveInEventArgs eventArgs)
        {
            if (eventArgs.BytesRecorded == 0 || _disposed)
            {
                return;
            }

            Interlocked.CompareExchange(ref _firstBufferTicks, DateTimeOffset.UtcNow.Ticks, 0);
            lock (_writeLock)
            {
                if (!_disposed)
                {
                    _writer.Write(eventArgs.Buffer, 0, eventArgs.BytesRecorded);
                }
            }
        }
    }
}
