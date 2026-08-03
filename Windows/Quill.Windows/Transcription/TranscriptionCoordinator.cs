using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using Quill.Windows.Infrastructure;
using Quill.Windows.Sessions;

namespace Quill.Windows.Transcription;

internal sealed class TranscriptionCoordinator : IDisposable
{
    private readonly string _recordingsRoot;
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>();
    private readonly ConcurrentDictionary<string, byte> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _worker;
    private WhisperTranscriber? _transcriber;
    private TranscriptionOptions? _options;

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
        if (!ConfigStore.Load().Transcription.Enabled || !Directory.Exists(_recordingsRoot))
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(_recordingsRoot).OrderBy(Path.GetFileName, StringComparer.Ordinal))
        {
            var hasMetadata = File.Exists(Path.Combine(directory, "meta.json"));
            var hasTranscript = File.Exists(Path.Combine(directory, "transcript.json"));
            if (ResumePolicy.ShouldEnqueue(hasMetadata, hasTranscript, SessionStatus.TryRead(directory)))
            {
                Enqueue(directory);
            }
        }
    }

    public void Enqueue(string sessionDirectory)
    {
        if (!ConfigStore.Load().Transcription.Enabled ||
            !_pending.TryAdd(sessionDirectory, 0))
        {
            return;
        }

        WriteStatus(sessionDirectory, SessionStatus.Queued, null, null);
        _queue.Writer.TryWrite(sessionDirectory);
        StateChanged?.Invoke(new TranscriptionUpdate(
            sessionDirectory, SessionState.Queued, _pending.Count, Array.Empty<TrackProgress>(), null, TimeSpan.Zero));
    }

    /// <summary>Vuelve a encolar una sesion que fallo. Es la unica forma de reintentar un "failed".</summary>
    public void Retry(string sessionDirectory)
    {
        if (!Directory.Exists(sessionDirectory))
        {
            return;
        }

        try
        {
            File.Delete(SessionStatus.PathFor(sessionDirectory));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log(sessionDirectory, $"could not clear status.json: {exception.Message}");
        }

        Enqueue(sessionDirectory);
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        _queue.Writer.TryComplete();
        var workerCompleted = false;
        try
        {
            workerCompleted = _worker.Wait(TimeSpan.FromSeconds(3));
        }
        catch (AggregateException)
        {
            // Shutdown only needs to leave a session without transcript.json so it resumes later.
        }
        finally
        {
            if (workerCompleted)
            {
                _transcriber?.Dispose();
            }
            _shutdown.Dispose();
        }
    }

    private async Task DrainAsync()
    {
        try
        {
            await foreach (var sessionDirectory in _queue.Reader.ReadAllAsync(_shutdown.Token))
            {
                _pending.TryRemove(sessionDirectory, out _);
                var clock = Stopwatch.StartNew();
                var tracks = new List<TrackProgress>();

                WriteStatus(sessionDirectory, SessionStatus.Running, null, null);
                PublishState(sessionDirectory, SessionState.Running, tracks, null, clock.Elapsed);
                try
                {
                    await TranscribeSessionAsync(sessionDirectory, tracks, clock, _shutdown.Token);
                    WriteStatus(sessionDirectory, SessionStatus.Done, clock.Elapsed.TotalSeconds, null);
                    PublishState(sessionDirectory, SessionState.Done, tracks, null, clock.Elapsed);
                }
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    Log(sessionDirectory, $"transcription failed: {exception}");
                    WriteStatus(sessionDirectory, SessionStatus.Failed, clock.Elapsed.TotalSeconds, exception.Message);
                    PublishState(sessionDirectory, SessionState.Failed, tracks, exception.Message, clock.Elapsed);
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
    }

    private async Task TranscribeSessionAsync(
        string sessionDirectory,
        List<TrackProgress> tracks,
        Stopwatch clock,
        CancellationToken cancellationToken)
    {
        var metadata = SessionMetadata.Read(sessionDirectory);
        var options = _options ??= TranscriptionOptions.FromConfig();
        var transcriber = _transcriber ??= new WhisperTranscriber(options);
        var merged = new List<TranscriptSegment>();

        foreach (var (name, speaker) in new[] { ("mic", "me"), ("system", "them") })
        {
            if (!metadata.Files.TryGetValue(name, out var fileName))
            {
                continue;
            }

            var audioPath = Path.Combine(sessionDirectory, fileName);
            if (!File.Exists(audioPath))
            {
                Log(sessionDirectory, $"missing track: {fileName}");
                continue;
            }

            // Las pistas se procesan en serie, asi que un solo hilo escribe en la lista.
            var index = tracks.Count;
            tracks.Add(new TrackProgress(name, 0d, false));
            var reporter = new TrackReporter(fraction =>
            {
                tracks[index] = new TrackProgress(name, fraction, false);
                ProgressChanged?.Invoke(new TranscriptionUpdate(
                    sessionDirectory, SessionState.Running, _pending.Count, tracks.ToArray(), null, clock.Elapsed));
            });

            try
            {
                Log(sessionDirectory, $"transcribing {fileName} with {options.ModelName} on {options.Threads} threads" +
                    (options.VadModelPath is null ? string.Empty : " (vad)"));
                var offset = metadata.StartOffsetMs.GetValueOrDefault(name);
                merged.AddRange(await transcriber.TranscribeAsync(audioPath, speaker, offset, reporter, cancellationToken));
                tracks[index] = new TrackProgress(name, 1d, true);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Log(sessionDirectory, $"skipping {fileName}: {exception.Message}");
                tracks[index] = tracks[index] with { Completed = true };
            }
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
    }

    /// <summary>
    /// Fallar al escribir el estado no puede interrumpir una transcripcion: el
    /// estado siempre es rederivable de que archivos existen.
    /// </summary>
    private static void WriteStatus(string sessionDirectory, string state, double? elapsedSeconds, string? error)
    {
        try
        {
            new SessionStatus
            {
                State = state,
                UpdatedAt = DateTimeOffset.UtcNow,
                Model = CurrentModelName(),
                ElapsedSeconds = elapsedSeconds,
                Error = error,
            }.Write(sessionDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log(sessionDirectory, $"could not write status.json: {exception.Message}");
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
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private void PublishState(
        string sessionDirectory,
        SessionState state,
        List<TrackProgress> tracks,
        string? error,
        TimeSpan elapsed) =>
        StateChanged?.Invoke(new TranscriptionUpdate(
            sessionDirectory, state, _pending.Count, tracks.ToArray(), error, elapsed));

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
