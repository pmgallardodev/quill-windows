using System.Diagnostics;
using Quill.Core.Live;
using Quill.Core.Ports;
using Quill.Windows.Audio;

namespace Quill.Windows.Transcription;

/// <summary>
/// Live transcription for one recording: a <see cref="LiveCaptureTap"/> handed to
/// <see cref="RecordingSession.Start"/>, a <see cref="LiveTranscriptionPipeline"/> fed from it, and
/// live/transcript.json kept up to date. Exists only when the AI pack has the LiveWhisper model;
/// any later failure switches live text off for the recording without touching capture.
/// </summary>
/// <remarks>
/// Lifecycle: <see cref="TryCreate"/> before capture starts, <see cref="Start"/> once the session
/// folder exists, <see cref="StopAsync"/> after <see cref="RecordingSession.Stop"/>. The model loads
/// in the background; audio captured meanwhile waits in the tap.
/// </remarks>
internal sealed class LiveTranscriptionModule : IAsyncDisposable
{
    /// <summary>Tap capacity: about 15 s of buffers for both tracks, enough to cover the model load.</summary>
    internal const int TapCapacity = 3_000;

    private readonly string _modelPath;
    private readonly Func<string, IStreamingTranscriber> _createTranscriber;
    private readonly object _lock = new();
    private Task _starting = Task.CompletedTask;
    private LiveTranscriptionPipeline? _pipeline;
    private bool _stopped;
    private int _disposed;

    private LiveTranscriptionModule(string modelPath, Func<string, IStreamingTranscriber> createTranscriber)
    {
        _modelPath = modelPath;
        _createTranscriber = createTranscriber;
        Tap = new LiveCaptureTap(TapCapacity);
    }

    public LiveCaptureTap Tap { get; }

    /// <summary>Live segments of both tracks in time order. Raises <see cref="LiveTranscriptBuffer.SegmentAdded"/> off the UI thread.</summary>
    public LiveTranscriptBuffer Buffer { get; } = new();

    /// <summary>Why live text stopped or never started, if it did.</summary>
    public Exception? Fault => StartError ?? _pipeline?.Fault ?? Tap.Fault;

    /// <summary>Failure to load the model or start the pipeline.</summary>
    public Exception? StartError { get; private set; }

    /// <summary>
    /// Returns null, without error, when the AI pack has no LiveWhisper model: recording then works
    /// exactly as before. Cheap: the model is only loaded by <see cref="Start"/>.
    /// </summary>
    /// <param name="createTranscriber">Builds the transcriber from the model path; may be slow.</param>
    public static LiveTranscriptionModule? TryCreate(
        AiPackStatus aiPack,
        Func<string, IStreamingTranscriber> createTranscriber)
    {
        ArgumentNullException.ThrowIfNull(aiPack);
        ArgumentNullException.ThrowIfNull(createTranscriber);
        var modelPath = aiPack.PathOf(AiPackComponent.LiveWhisper);
        return modelPath is not null && File.Exists(modelPath)
            ? new LiveTranscriptionModule(modelPath, createTranscriber)
            : null;
    }

    /// <summary>Loads the model in the background and starts delivering captured audio.</summary>
    public void Start(ISessionStore store, string sessionId)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        lock (_lock)
        {
            if (_stopped)
            {
                return;
            }

            _starting = Task.Run(() => StartCore(store, sessionId));
        }
    }

    /// <summary>
    /// Transcribes the audio still queued and writes the last live/transcript.json, waiting at most
    /// <paramref name="timeout"/>, then releases the model. Never throws.
    /// </summary>
    public async Task StopAsync(TimeSpan timeout)
    {
        lock (_lock)
        {
            _stopped = true;
        }

        using var deadline = new CancellationTokenSource(timeout);
        try
        {
            await _starting.WaitAsync(deadline.Token).ConfigureAwait(false);
            await Tap.CompleteAsync(deadline.Token).ConfigureAwait(false);
            if (_pipeline is { } pipeline)
            {
                await pipeline.CompleteAsync(deadline.Token).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Trace.TraceWarning($"quill: live transcription did not finish cleanly: {exception.Message}");
        }

        await DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        lock (_lock)
        {
            _stopped = true;
        }

        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            await _starting.ConfigureAwait(false);
            await Tap.DisposeAsync().ConfigureAwait(false);
            if (_pipeline is { } pipeline)
            {
                await pipeline.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Trace.TraceWarning($"quill: live transcription cleanup failed: {exception.Message}");
        }
    }

    private void StartCore(ISessionStore store, string sessionId)
    {
        IStreamingTranscriber? transcriber = null;
        try
        {
            transcriber = _createTranscriber(_modelPath);
            var info = new TranscriptInfo("whisper", Path.GetFileNameWithoutExtension(_modelPath));
            var pipeline = new LiveTranscriptionPipeline(
                transcriber,
                Buffer,
                new LiveTranscriptPersistence(store, sessionId, info));
            transcriber = null;
            _pipeline = pipeline;
            pipeline.Start();
            Tap.Start(pipeline.PushAsync);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            StartError = exception;
            Trace.TraceWarning($"quill: live transcription is off: {exception.Message}");
            // Not started, so this only stops queueing and releases what is queued.
            _ = Tap.CompleteAsync(CancellationToken.None);
            if (transcriber is not null)
            {
                transcriber.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }
    }
}
