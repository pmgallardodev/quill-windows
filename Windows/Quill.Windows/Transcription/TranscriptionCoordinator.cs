using System.Collections.Concurrent;
using System.Diagnostics;
using NAudio.Wave;
using System.Threading.Channels;
using Quill.Windows.Infrastructure;
using Quill.Windows.Sessions;

namespace Quill.Windows.Transcription;

internal sealed class TranscriptionCoordinator : IDisposable
{
    private readonly string _recordingsRoot;
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>();
    private readonly ConcurrentDictionary<string, byte> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _active = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _manualRetries = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _lockAttempts = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _claimGate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _worker;
    private WhisperTranscriber? _transcriber;
    private TranscriptionOptions? _options;
    private bool _disposed;

    public TranscriptionCoordinator(string recordingsRoot)
    {
        _recordingsRoot = recordingsRoot;
        _worker = Task.Run(DrainAsync);
    }

    /// <summary>Transiciones de estado. Unas cinco por sesion: seguro para trabajo caro.</summary>
    public event Action<TranscriptionUpdate>? StateChanged;

    /// <summary>Avance dentro de una pista. Cientos por minuto: el consumidor debe limitar su ritmo.</summary>
    public event Action<TranscriptionUpdate>? ProgressChanged;

    public void ResumePending()
    {
        var config = ConfigStore.Load();
        if (!Directory.Exists(_recordingsRoot))
        {
            return;
        }

        if (!config.Transcription.Enabled)
        {
            MarkDisabledSessions();
            return;
        }

        try
        {
            foreach (var directory in Directory.EnumerateDirectories(_recordingsRoot).OrderBy(Path.GetFileName, StringComparer.Ordinal))
            {
                try
                {
                    if (!SessionCatalog.IsSafeSessionDirectory(_recordingsRoot, directory))
                    {
                        continue;
                    }

                    WhisperTranscriber.CleanupTemporaries(directory);
                    var metadata = SessionMetadata.TryRead(directory);
                    var transcript = TranscriptExport.Read(directory);
                    var status = SessionStatus.TryRead(directory);
                    if (metadata is not null && !SessionStatus.HasFailureMarker(directory) &&
                        ResumePolicy.ShouldEnqueue(true, transcript.Status, status))
                    {
                        Enqueue(directory);
                    }
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException or ArgumentException or
                        InvalidDataException or NotSupportedException)
                {
                    // A single locked or corrupt session must not block the rest.
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A locked or deleted session must not prevent the application from starting.
        }
    }

    private void MarkDisabledSessions()
    {
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(_recordingsRoot))
            {
                try
                {
                    lock (_claimGate)
                    {
                        if (_active.ContainsKey(directory) ||
                            !SessionCatalog.IsSafeSessionDirectory(_recordingsRoot, directory))
                        {
                            continue;
                        }

                        using var sessionLock = TryAcquireSessionLock(directory);
                        if (sessionLock is null ||
                            !SessionCatalog.IsSafeSessionDirectory(_recordingsRoot, directory))
                        {
                            continue;
                        }

                        var metadata = SessionMetadata.TryRead(directory);
                        var transcript = TranscriptExport.Read(directory);
                        var status = SessionStatus.TryRead(directory);
                        if (metadata is not null &&
                            (transcript.Status is TranscriptReadStatus.Missing or TranscriptReadStatus.Invalid) &&
                            status?.State != SessionStatus.Failed &&
                            status?.State != SessionStatus.Disabled)
                        {
                            WriteStatus(directory, SessionStatus.Disabled, null, null, CurrentModelName());
                        }
                    }
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException or ArgumentException or
                        InvalidDataException or NotSupportedException)
                {
                    // A single inaccessible session must not hide the rest.
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    public bool Enqueue(string sessionDirectory) => EnqueueCore(sessionDirectory, allowFailed: false);

    private bool EnqueueCore(string sessionDirectory, bool allowFailed)
    {
        if (_disposed || !SessionCatalog.IsSafeSessionDirectory(_recordingsRoot, sessionDirectory))
        {
            return false;
        }

        var config = ConfigStore.Load();
        if (!config.Transcription.Enabled)
        {
            return MarkSessionDisabled(sessionDirectory);
        }

        lock (_claimGate)
        {
            if (_disposed ||
                _active.ContainsKey(sessionDirectory) ||
                !SessionCatalog.IsSafeSessionDirectory(_recordingsRoot, sessionDirectory))
            {
                return false;
            }

            var currentStatus = SessionStatus.TryRead(sessionDirectory);
            if (currentStatus?.State == SessionStatus.Failed && !allowFailed)
            {
                return false;
            }

            if (SessionStatus.HasFailureMarker(sessionDirectory) && !allowFailed)
            {
                return false;
            }

            if (!_pending.TryAdd(sessionDirectory, 0))
            {
                return false;
            }

            if (allowFailed)
            {
                SessionStatus.ClearFailureMarker(sessionDirectory);
                _manualRetries[sessionDirectory] = 0;
            }

            if (currentStatus?.State != SessionStatus.Running)
            {
                WriteStatus(sessionDirectory, SessionStatus.Queued, null, null, CurrentModelName());
                StateChanged?.Invoke(new TranscriptionUpdate(
                    sessionDirectory,
                    SessionState.Queued,
                    _pending.Count,
                    Array.Empty<TrackProgress>(),
                    null,
                    TimeSpan.Zero,
                    CurrentModelName()));
            }

            // The worker also takes _claimGate before claiming the item, so it
            // cannot publish Running before this queued transition is durable.
            if (!_queue.Writer.TryWrite(sessionDirectory))
            {
                _pending.TryRemove(sessionDirectory, out _);
                return false;
            }

            return true;
        }
    }

    /// <summary>Vuelve a encolar una sesion que fallo. Es la unica forma de reintentar un "failed".</summary>
    public void Retry(string sessionDirectory)
    {
        if (_disposed || !SessionCatalog.IsSafeSessionDirectory(_recordingsRoot, sessionDirectory))
        {
            return;
        }

        if (!ConfigStore.Load().Transcription.Enabled)
        {
            MarkSessionDisabled(sessionDirectory);
            return;
        }

        EnqueueCore(sessionDirectory, allowFailed: true);
    }

    private bool MarkSessionDisabled(string sessionDirectory)
    {
        lock (_claimGate)
        {
            if (_disposed ||
                _active.ContainsKey(sessionDirectory) ||
                !SessionCatalog.IsSafeSessionDirectory(_recordingsRoot, sessionDirectory))
            {
                return false;
            }

            using var sessionLock = TryAcquireSessionLock(sessionDirectory);
            if (sessionLock is null ||
                !SessionCatalog.IsSafeSessionDirectory(_recordingsRoot, sessionDirectory))
            {
                return false;
            }

            if (SessionStatus.TryRead(sessionDirectory)?.State == SessionStatus.Failed)
            {
                _pending.TryRemove(sessionDirectory, out _);
                return false;
            }

            // A queued item may still be waiting in the channel. Removing its
            // claim makes that stale item a no-op after the disabled state lands.
            _pending.TryRemove(sessionDirectory, out _);
            _manualRetries.TryRemove(sessionDirectory, out _);
            WriteStatus(sessionDirectory, SessionStatus.Disabled, null, null, CurrentModelName());
            StateChanged?.Invoke(new TranscriptionUpdate(
                sessionDirectory,
                SessionState.Disabled,
                _pending.Count,
                Array.Empty<TrackProgress>(),
                null,
                TimeSpan.Zero,
                CurrentModelName()));
            return false;
        }
    }

    public bool IsBusy(string sessionDirectory) =>
        _pending.ContainsKey(sessionDirectory) || _active.ContainsKey(sessionDirectory);

    public string? TryDeleteSession(string sessionDirectory)
    {
        lock (_claimGate)
        {
            if (_active.ContainsKey(sessionDirectory))
            {
                return "la sesion se esta transcribiendo";
            }

            var failure = SessionCatalog.TryDelete(_recordingsRoot, sessionDirectory);
            if (failure is null)
            {
                // Leave the stale channel item behind. The worker will discard it
                // because the pending claim is gone; failed deletes keep the claim
                // and therefore keep their existing worker item.
                _pending.TryRemove(sessionDirectory, out _);
            }

            return failure;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _shutdown.Cancel();
        _queue.Writer.TryComplete();
        var workerCompleted = false;
        try
        {
            workerCompleted = _worker.Wait(TimeSpan.FromSeconds(3));
        }
        catch (AggregateException)
        {
            // The worker is complete even when a subscriber faulted it.
            workerCompleted = true;
        }

        if (workerCompleted)
        {
            DisposeResources();
        }
        else
        {
            // Do not dispose the token source or native Whisper context while a
            // native call is still unwinding after cancellation.
            _ = _worker.ContinueWith(
                _ => DisposeResources(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private void DisposeResources()
    {
        try
        {
            _transcriber?.Dispose();
        }
        catch
        {
            // Native cleanup must not prevent the cancellation source from closing.
        }
        finally
        {
            _shutdown.Dispose();
        }
    }

    private async Task DrainAsync()
    {
        try
        {
            await foreach (var sessionDirectory in _queue.Reader.ReadAllAsync(_shutdown.Token))
            {
                lock (_claimGate)
                {
                    if (!_pending.TryRemove(sessionDirectory, out _) ||
                        !SessionCatalog.IsSafeSessionDirectory(_recordingsRoot, sessionDirectory))
                    {
                        continue;
                    }

                    _active[sessionDirectory] = 0;
                }

                FileStream? sessionLock = null;
                try
                {
                    sessionLock = await AcquireSessionLockAsync(sessionDirectory);
                    if (sessionLock is null ||
                        !SessionCatalog.IsSafeSessionDirectory(_recordingsRoot, sessionDirectory))
                    {
                        var attempt = _lockAttempts.AddOrUpdate(
                            sessionDirectory,
                            1,
                            static (_, previous) => previous + 1);
                        if (attempt <= 6 && !_shutdown.IsCancellationRequested)
                        {
                            lock (_claimGate)
                            {
                                if (!_disposed && SessionCatalog.IsSafeSessionDirectory(_recordingsRoot, sessionDirectory))
                                {
                                    _pending[sessionDirectory] = 0;
                                }
                            }

                            try
                            {
                                await Task.Delay(
                                    TimeSpan.FromSeconds(Math.Min(30, 1 << (attempt - 1))),
                                    _shutdown.Token);
                                if (!_queue.Writer.TryWrite(sessionDirectory))
                                {
                                    _pending.TryRemove(sessionDirectory, out _);
                                }
                            }
                            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
                            {
                                _pending.TryRemove(sessionDirectory, out _);
                            }
                        }
                        else
                        {
                            _lockAttempts.TryRemove(sessionDirectory, out _);
                            Log(sessionDirectory, "could not acquire the transcription lock; retry on next launch");
                        }

                        continue;
                    }

                    _lockAttempts.TryRemove(sessionDirectory, out _);

                    bool transcriptionEnabled;
                    try
                    {
                        transcriptionEnabled = ConfigStore.Load().Transcription.Enabled;
                    }
                    catch (Exception exception) when (
                        exception is InvalidOperationException or IOException or UnauthorizedAccessException or
                            ArgumentException or NotSupportedException)
                    {
                        Log(sessionDirectory, $"could not read transcription config: {exception.Message}");
                        continue;
                    }

                    if (!transcriptionEnabled)
                    {
                        WriteStatus(sessionDirectory, SessionStatus.Disabled, null, null, CurrentModelName());
                        continue;
                    }

                    var clock = Stopwatch.StartNew();
                    var tracks = new List<TrackProgress>();
                    TranscriptionOptions? optionsUsed = null;

                    var currentTranscript = TranscriptExport.Read(sessionDirectory);
                    if (currentTranscript.Status is TranscriptReadStatus.Valid or TranscriptReadStatus.Empty)
                    {
                        continue;
                    }

                    var manualRetry = _manualRetries.TryRemove(sessionDirectory, out _);
                    var currentStatus = SessionStatus.TryRead(sessionDirectory);
                    if (!manualRetry &&
                        (currentStatus?.State == SessionStatus.Failed ||
                         SessionStatus.HasFailureMarker(sessionDirectory)))
                    {
                        continue;
                    }

                    WriteStatus(sessionDirectory, SessionStatus.Running, null, null, CurrentModelName());
                    PublishState(sessionDirectory, SessionState.Running, tracks, null, clock.Elapsed, CurrentModelName());
                    try
                    {
                        optionsUsed = await TranscribeSessionAsync(sessionDirectory, tracks, clock, _shutdown.Token);
                        WriteStatus(sessionDirectory, SessionStatus.Done, clock.Elapsed.TotalSeconds, null, optionsUsed.ModelName);
                        PublishState(sessionDirectory, SessionState.Done, tracks, null, clock.Elapsed, optionsUsed.ModelName);
                    }
                    catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception exception)
                    {
                        Log(sessionDirectory, $"transcription failed: {exception}");
                        var statusWritten = WriteStatus(
                            sessionDirectory,
                            SessionStatus.Failed,
                            clock.Elapsed.TotalSeconds,
                            exception.Message,
                            optionsUsed?.ModelName ?? CurrentModelName());
                        if (!statusWritten)
                        {
                            try
                            {
                                SessionStatus.WriteFailureMarker(sessionDirectory, exception.Message);
                            }
                            catch (Exception markerException) when (
                                markerException is IOException or UnauthorizedAccessException or
                                    ArgumentException or NotSupportedException)
                            {
                                Log(sessionDirectory, $"could not write failure marker: {markerException.Message}");
                            }
                        }
                        PublishState(
                            sessionDirectory,
                            SessionState.Failed,
                            tracks,
                            exception.Message,
                            clock.Elapsed,
                            optionsUsed?.ModelName ?? CurrentModelName());
                    }
                }
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
                {
                    return;
                }
                finally
                {
                    try
                    {
                        sessionLock?.Dispose();
                    }
                    catch (Exception exception) when (
                        exception is IOException or UnauthorizedAccessException or ObjectDisposedException)
                    {
                    }

                    lock (_claimGate)
                    {
                        // The FileStream must be closed before deletion is allowed
                        // to observe the session as no longer active.
                        _active.TryRemove(sessionDirectory, out _);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
    }

    private async Task<TranscriptionOptions> TranscribeSessionAsync(
        string sessionDirectory,
        List<TrackProgress> tracks,
        Stopwatch clock,
        CancellationToken cancellationToken)
    {
        var metadata = SessionMetadata.Read(sessionDirectory);
        var options = TranscriptionOptions.FromConfig();
        var transcriber = _transcriber;
        if (transcriber is null || _options != options)
        {
            var previous = transcriber;
            _transcriber = null;
            _options = null;
            previous?.Dispose();
            var replacement = new WhisperTranscriber(options);
            _options = options;
            _transcriber = replacement;
            transcriber = replacement;
        }

        var merged = new List<TranscriptSegment>();
        var allTracksSucceeded = true;

        foreach (var (name, speaker) in new[] { ("mic", "me"), ("system", "them") })
        {
            if (!metadata.Files.TryGetValue(name, out var fileName))
            {
                // An imported single-file session lists only the track it has. Only a
                // session that lists no track at all is broken.
                if (metadata.Files.ContainsKey("mic") || metadata.Files.ContainsKey("system"))
                {
                    Log(sessionDirectory, $"track not recorded: {name}");
                    continue;
                }

                allTracksSucceeded = false;
                Log(sessionDirectory, $"missing track entry: {name}");
                continue;
            }

            if (!TryResolveSessionFile(sessionDirectory, fileName, name, out var audioPath))
            {
                allTracksSucceeded = false;
                Log(sessionDirectory, $"invalid track path: {fileName}");
                continue;
            }

            if (!File.Exists(audioPath))
            {
                allTracksSucceeded = false;
                Log(sessionDirectory, $"missing track: {fileName}");
                continue;
            }

            // Las pistas se procesan en serie, asi que un solo hilo escribe en la lista.
            var index = tracks.Count;
            tracks.Add(new TrackProgress(name, 0d, false, AudioDuration(audioPath)));
            var reporter = new TrackReporter(fraction =>
            {
                tracks[index] = tracks[index] with { Fraction = fraction, Completed = false };
                ProgressChanged?.Invoke(new TranscriptionUpdate(
                    sessionDirectory,
                    SessionState.Running,
                    _pending.Count,
                    tracks.ToArray(),
                    null,
                    clock.Elapsed,
                    options.ModelName));
            });

            try
            {
                Log(sessionDirectory, $"transcribing {fileName} with {options.ModelName} on {options.Threads} threads" +
                    (options.VadModelPath is null ? string.Empty : " (vad)"));
                if (!metadata.StartOffsetMs.TryGetValue(name, out var offset) || offset < 0)
                {
                    allTracksSucceeded = false;
                    Log(sessionDirectory, $"missing track offset: {name}");
                    tracks[index] = tracks[index] with { Completed = true };
                    continue;
                }

                merged.AddRange(await transcriber.TranscribeAsync(audioPath, speaker, offset, reporter, cancellationToken));
                tracks[index] = tracks[index] with { Fraction = 1d, Completed = true };
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                allTracksSucceeded = false;
                Log(sessionDirectory, $"skipping {fileName}: {exception.Message}");
                tracks[index] = tracks[index] with { Completed = true };
            }
        }

        if (!allTracksSucceeded)
        {
            throw new InvalidDataException("una o mas pistas no se pudieron transcribir");
        }

        merged.Sort(static (left, right) => left.StartMs.CompareTo(right.StartMs));
        new TranscriptDocument
        {
            Engine = "whisper.cpp",
            Model = options.ModelName,
            CreatedAt = DateTimeOffset.UtcNow,
            Segments = merged,
        }.Write(sessionDirectory);
        Log(sessionDirectory, $"done: {merged.Count} segments");
        DeleteAudioIfConfigured(sessionDirectory, metadata, merged.Count, allTracksSucceeded);
        return options;
    }

    /// <summary>
    /// Solo se llama con transcript.json ya escrito: si la transcripcion falla, el audio
    /// es lo unico que queda. Un transcript sin segmentos tambien conserva el audio
    /// porque suele indicar que algo fue mal sin llegar a lanzar.
    /// </summary>
    private static void DeleteAudioIfConfigured(
        string sessionDirectory,
        SessionMetadata metadata,
        int segmentCount,
        bool allTracksSucceeded)
    {
        try
        {
            if (!allTracksSucceeded || segmentCount == 0 || !ConfigStore.Load().Recording.DeleteAudioAfterTranscription)
            {
                return;
            }
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            Log(sessionDirectory, $"could not read config to delete audio: {exception.Message}");
            return;
        }

        var deleted = new List<string>();
        var freedBytes = 0L;
        foreach (var name in new[] { "mic", "system" })
        {
            if (!metadata.Files.TryGetValue(name, out var fileName) ||
                !TryResolveSessionFile(sessionDirectory, fileName, name, out var audioPath))
            {
                Log(sessionDirectory, $"could not delete invalid track path: {name}");
                continue;
            }

            try
            {
                var file = new FileInfo(audioPath);
                if (!file.Exists)
                {
                    continue;
                }

                var length = file.Length;
                file.Delete();
                freedBytes += length;
                deleted.Add(fileName);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                // Un WAV bloqueado deja la sesion igual de valida: el transcript ya esta.
                Log(sessionDirectory, $"could not delete {fileName}: {exception.Message}");
            }
        }

        if (deleted.Count > 0)
        {
            Log(sessionDirectory,
                $"deleted audio ({string.Join(", ", deleted)}): freed {freedBytes / 1024 / 1024} MB; " +
                "retry is no longer possible for this session");
        }
    }

    private static bool TryResolveSessionFile(
        string sessionDirectory,
        string? fileName,
        string trackName,
        out string path)
    {
        path = string.Empty;
        if (!string.Equals(fileName, $"{trackName}.wav", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sessionDirectory));
            var candidate = Path.GetFullPath(Path.Combine(root, fileName!));
            var rootPrefix = root + Path.DirectorySeparatorChar;
            if (!candidate.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) ||
                (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0 ||
                (File.Exists(candidate) && (File.GetAttributes(candidate) & FileAttributes.ReparsePoint) != 0))
            {
                return false;
            }

            path = candidate;
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    private static TimeSpan? AudioDuration(string path)
    {
        try
        {
            using var reader = new AudioFileReader(path);
            return reader.TotalTime;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException or
                ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static FileStream? TryAcquireSessionLock(string sessionDirectory)
    {
        try
        {
            return new FileStream(
                Path.Combine(sessionDirectory, ".quill-transcription.lock"),
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private async Task<FileStream?> AcquireSessionLockAsync(string sessionDirectory)
    {
        const int maxAttempts = 3;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            var sessionLock = TryAcquireSessionLock(sessionDirectory);
            if (sessionLock is not null)
            {
                return sessionLock;
            }

            if (attempt + 1 < maxAttempts)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(100 * (attempt + 1)), _shutdown.Token);
                }
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
                {
                    return null;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Fallar al escribir el estado no puede interrumpir una transcripcion: el
    /// estado siempre es rederivable de que archivos existen.
    /// </summary>
    private bool WriteStatus(
        string sessionDirectory,
        string state,
        double? elapsedSeconds,
        string? error,
        string? modelName = null)
    {
        try
        {
            new SessionStatus
            {
                State = state,
                UpdatedAt = DateTimeOffset.UtcNow,
                Model = modelName ?? _options?.ModelName ?? CurrentModelName(),
                ElapsedSeconds = elapsedSeconds,
                Error = error,
            }.Write(sessionDirectory);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Log(sessionDirectory, $"could not write status.json: {exception.Message}");
            return false;
        }
    }

    /// <summary>
    /// Se lee del config y no de las opciones ya resueltas porque el estado se escribe
    /// tambien al encolar, antes de que exista un transcriptor.
    /// </summary>
    private static string? CurrentModelName()
    {
        try
        {
            return Path.GetFileNameWithoutExtension(ConfigStore.Load().Transcription.Model);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private void PublishState(
        string sessionDirectory,
        SessionState state,
        List<TrackProgress> tracks,
        string? error,
        TimeSpan elapsed,
        string? model = null) =>
        StateChanged?.Invoke(new TranscriptionUpdate(
            sessionDirectory, state, _pending.Count, tracks.ToArray(), error, elapsed, model));

    private static void Log(string sessionDirectory, string message)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(sessionDirectory, "transcribe.log"),
                $"{DateTimeOffset.UtcNow:O} {message}{Environment.NewLine}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Perder una linea de log no justifica abortar la transcripcion.
        }
    }

    private sealed class TrackReporter(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
