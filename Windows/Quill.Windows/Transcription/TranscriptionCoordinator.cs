using System.Collections.Concurrent;
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

    public TranscriptionCoordinator(string recordingsRoot)
    {
        _recordingsRoot = recordingsRoot;
        _worker = Task.Run(DrainAsync);
    }

    public event Action<TranscriptionStatus>? StatusChanged;

    public void ResumePending()
    {
        if (!ConfigStore.Load().Transcription.Enabled || !Directory.Exists(_recordingsRoot))
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(_recordingsRoot).OrderBy(Path.GetFileName))
        {
            if (File.Exists(Path.Combine(directory, "meta.json")) &&
                !File.Exists(Path.Combine(directory, "transcript.json")))
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

        _queue.Writer.TryWrite(sessionDirectory);
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
                Publish(new TranscriptionStatus(sessionDirectory, _pending.Count, false, null));
                try
                {
                    await TranscribeSessionAsync(sessionDirectory, _shutdown.Token);
                    Publish(new TranscriptionStatus(sessionDirectory, _pending.Count, true, null));
                }
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    Log(sessionDirectory, $"transcription failed: {exception}");
                    Publish(new TranscriptionStatus(sessionDirectory, _pending.Count, true, exception.Message));
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
    }

    private async Task TranscribeSessionAsync(string sessionDirectory, CancellationToken cancellationToken)
    {
        var metadata = SessionMetadata.Read(sessionDirectory);
        var transcriber = _transcriber ??= new WhisperTranscriber(ConfigStore.ModelPath);
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

            try
            {
                Log(sessionDirectory, $"transcribing {fileName} with Whisper large-v3");
                var offset = metadata.StartOffsetMs.GetValueOrDefault(name);
                merged.AddRange(await transcriber.TranscribeAsync(audioPath, speaker, offset, cancellationToken));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Log(sessionDirectory, $"skipping {fileName}: {exception.Message}");
            }
        }

        merged.Sort(static (left, right) => left.StartMs.CompareTo(right.StartMs));
        new TranscriptDocument
        {
            Engine = "whisper.cpp",
            Model = "large-v3-q5_0",
            CreatedAt = DateTimeOffset.UtcNow,
            Segments = merged,
        }.Write(sessionDirectory);
        Log(sessionDirectory, $"done: {merged.Count} segments");
    }

    private static void Log(string sessionDirectory, string message)
    {
        File.AppendAllText(
            Path.Combine(sessionDirectory, "transcribe.log"),
            $"{DateTimeOffset.UtcNow:O} {message}{Environment.NewLine}");
    }

    private void Publish(TranscriptionStatus status) => StatusChanged?.Invoke(status);
}

internal sealed record TranscriptionStatus(string SessionDirectory, int Queued, bool Finished, string? Error);
