using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Quill.Core.Domain;
using Quill.Core.Ports;

namespace Quill.Core.Live;

/// <summary>Text recognized in a chunk, with times relative to the chunk's first sample.</summary>
public sealed record RecognizedSpan(TimeSpan Start, TimeSpan End, string Text);

/// <summary>
/// Speech recognizer for one short 16 kHz mono chunk (adapter: Whisper small). Calls are never
/// concurrent; <see cref="ScheduledStreamingTranscriber"/> runs them one at a time.
/// </summary>
public interface IChunkRecognizer : IAsyncDisposable
{
    Task<IReadOnlyList<RecognizedSpan>> RecognizeAsync(ReadOnlyMemory<float> samples, CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="IStreamingTranscriber"/> that recognizes each fed chunk through the shared
/// <see cref="IInferenceScheduler"/> at <see cref="InferencePriority.LiveTranscription"/> and emits
/// provisional, session-relative segments.
/// </summary>
/// <remarks>
/// <see cref="FeedAsync"/> never waits: chunks go to a small bounded queue and, when inference falls
/// behind, the oldest queued chunk is dropped so live text stays current (the final pass covers
/// everything later). A failed chunk is skipped; <see cref="MaxConsecutiveFailures"/> failures in a
/// row end the segment stream with the error, which switches live text off for the recording.
/// </remarks>
public sealed class ScheduledStreamingTranscriber : IStreamingTranscriber
{
    /// <summary>Failures in a row after which the recognizer is considered broken.</summary>
    public const int MaxConsecutiveFailures = 3;

    private readonly IChunkRecognizer _recognizer;
    private readonly IInferenceScheduler _scheduler;
    private readonly Channel<AudioChunk> _input;
    private readonly Channel<TranscriptSegment> _output = Channel.CreateUnbounded<TranscriptSegment>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _worker;
    private long _droppedChunks;
    private long _failedChunks;
    private int _disposed;

    /// <param name="recognizer">Owned: disposed in <see cref="DisposeAsync"/>.</param>
    /// <param name="maxQueuedChunks">Chunks waiting for inference before the oldest is dropped.</param>
    public ScheduledStreamingTranscriber(IChunkRecognizer recognizer, IInferenceScheduler scheduler, int maxQueuedChunks = 4)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxQueuedChunks, 1);
        _recognizer = recognizer ?? throw new ArgumentNullException(nameof(recognizer));
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _input = Channel.CreateBounded<AudioChunk>(
            new BoundedChannelOptions(maxQueuedChunks)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
            },
            _ => Interlocked.Increment(ref _droppedChunks));
        _worker = Task.Run(RunAsync);
    }

    /// <summary>Chunks dropped because inference was behind.</summary>
    public long DroppedChunks => Interlocked.Read(ref _droppedChunks);

    /// <summary>Chunks whose recognition failed.</summary>
    public long FailedChunks => Interlocked.Read(ref _failedChunks);

    /// <summary>Most recent recognition failure, if any.</summary>
    public Exception? LastError { get; private set; }

    public ValueTask FeedAsync(AudioChunk chunk, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        cancellationToken.ThrowIfCancellationRequested();
        var copy = chunk with { Samples = chunk.Samples.ToArray() };
        if (!_input.Writer.TryWrite(copy))
        {
            throw new InvalidOperationException("the live transcriber is already completed");
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask CompleteAsync(CancellationToken cancellationToken)
    {
        _input.Writer.TryComplete();
        await _worker.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public IAsyncEnumerable<TranscriptSegment> ReadSegmentsAsync(CancellationToken cancellationToken)
        => _output.Reader.ReadAllAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _input.Writer.TryComplete();
        _stopping.Cancel();
        try
        {
            await _worker.ConfigureAwait(false);
        }
        finally
        {
            _stopping.Dispose();
            await _recognizer.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Whisper's markers for non-speech, such as "[Música]", "(risas)" or "♪".</summary>
    internal static bool IsNonSpeech(string text)
    {
        var depth = 0;
        var emphasized = false;
        foreach (var character in text)
        {
            switch (character)
            {
                case '[' or '(':
                    depth++;
                    continue;
                case ']' or ')':
                    depth = Math.Max(0, depth - 1);
                    continue;
                case '*':
                    emphasized = !emphasized;
                    continue;
            }

            if (depth == 0 && !emphasized && char.IsLetterOrDigit(character))
            {
                return false;
            }
        }

        return true;
    }

    private async Task RunAsync()
    {
        Exception? fault = null;
        var consecutiveFailures = 0;
        try
        {
            await foreach (var chunk in _input.Reader.ReadAllAsync(_stopping.Token).ConfigureAwait(false))
            {
                IReadOnlyList<RecognizedSpan> spans;
                try
                {
                    spans = await _scheduler.RunAsync(
                            InferencePriority.LiveTranscription,
                            token => _recognizer.RecognizeAsync(chunk.Samples, token),
                            _stopping.Token)
                        .ConfigureAwait(false);
                    consecutiveFailures = 0;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    Interlocked.Increment(ref _failedChunks);
                    LastError = exception;
                    if (++consecutiveFailures >= MaxConsecutiveFailures)
                    {
                        fault = exception;
                        return;
                    }

                    continue;
                }

                foreach (var span in spans)
                {
                    if (ToSegment(chunk, span) is { } segment)
                    {
                        _output.Writer.TryWrite(segment);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            fault = exception;
        }
        finally
        {
            _output.Writer.TryComplete(fault);
        }
    }

    private static TranscriptSegment? ToSegment(AudioChunk chunk, RecognizedSpan span)
    {
        var text = span.Text?.Trim() ?? string.Empty;
        if (text.Length == 0 || IsNonSpeech(text))
        {
            return null;
        }

        var start = Clamp(span.Start, chunk.Duration);
        var end = Clamp(span.End, chunk.Duration);
        if (end < start)
        {
            end = start;
        }

        return new TranscriptSegment(
            chunk.Start + start,
            chunk.Start + end,
            text,
            LiveSpeakers.ProvisionalFor(chunk.Track),
            chunk.Track,
            Editable: false);
    }

    private static TimeSpan Clamp(TimeSpan value, TimeSpan duration) =>
        value < TimeSpan.Zero ? TimeSpan.Zero : value > duration ? duration : value;
}
