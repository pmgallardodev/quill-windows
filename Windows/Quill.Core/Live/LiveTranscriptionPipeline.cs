using Quill.Core.Domain;
using Quill.Core.Ports;

namespace Quill.Core.Live;

/// <summary>Where and how the live transcript is persisted (live/transcript.json).</summary>
public sealed record LiveTranscriptPersistence(ISessionStore Store, string SessionId, TranscriptInfo Info);

/// <summary>
/// Live transcription for one recording: per-track <see cref="SpeechChunker"/>s turn captured
/// 16 kHz mono samples into utterance chunks for an <see cref="IStreamingTranscriber"/>, and the
/// segments it returns are collected in a <see cref="LiveTranscriptBuffer"/> (and persisted after
/// each accepted segment when <see cref="LiveTranscriptPersistence"/> is given).
/// </summary>
/// <remarks>
/// <see cref="PushAsync"/> must be called from a single producer (the capture tap consumer).
/// Failures of the transcriber or of persistence never throw to the producer: they are recorded in
/// <see cref="Fault"/> / <see cref="LastPersistenceError"/> so live text degrades without ever
/// affecting capture.
/// </remarks>
public sealed class LiveTranscriptionPipeline : IAsyncDisposable
{
    private readonly IStreamingTranscriber _transcriber;
    private readonly LiveTranscriptPersistence? _persistence;
    private readonly SpeechChunkerOptions _options;
    private readonly LiveTimeline _timeline = new();
    private readonly Dictionary<Track, SpeechChunker> _chunkers = new();
    private readonly CancellationTokenSource _stopping = new();
    private Task _reader = Task.CompletedTask;
    private long _chunksFed;
    private bool _started;
    private bool _completed;

    /// <param name="transcriber">Owned by the pipeline: disposed in <see cref="DisposeAsync"/>.</param>
    public LiveTranscriptionPipeline(
        IStreamingTranscriber transcriber,
        LiveTranscriptBuffer buffer,
        LiveTranscriptPersistence? persistence = null,
        SpeechChunkerOptions? options = null)
    {
        _transcriber = transcriber ?? throw new ArgumentNullException(nameof(transcriber));
        Buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
        _persistence = persistence;
        _options = options ?? new SpeechChunkerOptions();
    }

    public LiveTranscriptBuffer Buffer { get; }

    /// <summary>Chunks handed to the transcriber so far.</summary>
    public long ChunksFed => Interlocked.Read(ref _chunksFed);

    /// <summary>Error that ended the segment stream early, if any.</summary>
    public Exception? Fault { get; private set; }

    /// <summary>Most recent failure to write live/transcript.json, if any.</summary>
    public Exception? LastPersistenceError { get; private set; }

    /// <summary>Starts consuming transcriber segments. Idempotent.</summary>
    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _reader = Task.Run(ReadSegmentsAsync);
    }

    /// <summary>Feeds captured samples of one track. Ignored after <see cref="CompleteAsync"/>.</summary>
    /// <param name="trackStartedAt">Wall time of the track's first captured buffer.</param>
    public async ValueTask PushAsync(
        Track track,
        DateTimeOffset trackStartedAt,
        ReadOnlyMemory<float> samples,
        CancellationToken cancellationToken)
    {
        if (_completed)
        {
            return;
        }

        if (!_chunkers.TryGetValue(track, out var chunker))
        {
            chunker = new SpeechChunker(track, _timeline.OffsetOf(track, trackStartedAt), _options);
            _chunkers[track] = chunker;
        }

        foreach (var chunk in chunker.Push(samples.Span))
        {
            await FeedAsync(chunk, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Flushes pending speech of every track, lets the transcriber drain and waits until every
    /// segment is in the buffer (and persisted). Never throws for transcriber failures.
    /// </summary>
    public async Task CompleteAsync(CancellationToken cancellationToken)
    {
        if (_completed)
        {
            await _reader.WaitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        _completed = true;
        Start();
        foreach (var chunker in _chunkers.Values)
        {
            foreach (var chunk in chunker.Flush())
            {
                await FeedAsync(chunk, cancellationToken).ConfigureAwait(false);
            }
        }

        try
        {
            await _transcriber.CompleteAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Fault ??= exception;
            _stopping.Cancel();
        }

        await _reader.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _completed = true;
        _stopping.Cancel();
        await _reader.ConfigureAwait(false);
        await _transcriber.DisposeAsync().ConfigureAwait(false);
        _stopping.Dispose();
    }

    private async ValueTask FeedAsync(AudioChunk chunk, CancellationToken cancellationToken)
    {
        if (Fault is not null)
        {
            return;
        }

        try
        {
            await _transcriber.FeedAsync(chunk, cancellationToken).ConfigureAwait(false);
            Interlocked.Increment(ref _chunksFed);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Fault ??= exception;
        }
    }

    private async Task ReadSegmentsAsync()
    {
        try
        {
            await foreach (var segment in _transcriber.ReadSegmentsAsync(_stopping.Token).ConfigureAwait(false))
            {
                if (Buffer.Add(segment))
                {
                    await PersistAsync().ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Fault ??= exception;
        }
    }

    private async Task PersistAsync()
    {
        if (_persistence is null)
        {
            return;
        }

        try
        {
            await Buffer.SaveAsync(_persistence.Store, _persistence.SessionId, _persistence.Info, CancellationToken.None)
                .ConfigureAwait(false);
            LastPersistenceError = null;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            LastPersistenceError = exception;
        }
    }
}
