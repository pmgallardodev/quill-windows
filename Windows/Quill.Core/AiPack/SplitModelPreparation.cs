using Quill.Core.Ports;

namespace Quill.Core.AiPack;

/// <summary>
/// Joins split AI pack models in the background, once per model, without any setting:
/// <see cref="ManifestAiPackLocator"/> calls <see cref="Ensure"/> when it finds the parts, and
/// features see the component as missing (with a <see cref="ModelPreparationState.Preparing"/>
/// state) until the verified file is in place. Joining runs on a thread-pool thread and only
/// reads and writes files, so recording is never blocked. A failure caused by bad parts is kept
/// for the life of the process; a failure caused by space or permissions is retried by the next
/// <see cref="Ensure"/> after <see cref="RetryAfter"/>.
/// </summary>
public sealed class SplitModelPreparation
{
    /// <summary>Process-wide instance used by default by <see cref="ManifestAiPackLocator"/>.</summary>
    public static SplitModelPreparation Shared { get; } = new();

    private readonly SplitModelAssembler _assembler;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public SplitModelPreparation(SplitModelAssembler? assembler = null, TimeProvider? time = null, TimeSpan? retryAfter = null)
    {
        _assembler = assembler ?? new SplitModelAssembler();
        _time = time ?? TimeProvider.System;
        RetryAfter = retryAfter ?? TimeSpan.FromMinutes(5);
    }

    public TimeSpan RetryAfter { get; }

    /// <summary>
    /// Raised from a background thread when a model starts joining, advances by at least one
    /// percent, becomes ready or fails. UI code must marshal to its own thread.
    /// </summary>
    public event EventHandler<ModelPreparation>? Changed;

    /// <summary>Starts joining <paramref name="job"/> unless it is running, done or failed for good; returns its state.</summary>
    public ModelPreparation Ensure(SplitModelJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        ModelPreparation state;
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_entries.TryGetValue(Key(job), out var entry) && !CanRetry(entry))
            {
                return entry.State;
            }

            entry = new Entry(new ModelPreparation(job.Component, ModelPreparationState.Preparing, 0, null));
            _entries[Key(job)] = entry;
            state = entry.State;
            entry.Task = Task.Run(async () =>
            {
                await start.Task.ConfigureAwait(false);
                await RunAsync(job, entry).ConfigureAwait(false);
            });
        }

        // "Preparing" is announced before the join may report progress or completion.
        Raise(state);
        start.SetResult();
        return state;
    }

    /// <summary>Completes when the current join of <paramref name="job"/> (if any) has finished. Never throws.</summary>
    public Task WhenFinishedAsync(SplitModelJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        lock (_gate)
        {
            return _entries.TryGetValue(Key(job), out var entry) ? entry.Task : Task.CompletedTask;
        }
    }

    /// <summary>Completes when every current join of <paramref name="component"/> has finished. Never throws.</summary>
    public Task WhenFinishedAsync(AiPackComponent component)
    {
        lock (_gate)
        {
            return Task.WhenAll(_entries.Values.Where(entry => entry.State.Component == component).Select(entry => entry.Task));
        }
    }

    private static string Key(SplitModelJob job) => $"{job.Sha256}|{job.TargetPath}";

    private bool CanRetry(Entry entry) =>
        entry.State.State == ModelPreparationState.Failed &&
        entry.Retryable &&
        _time.GetUtcNow() - entry.FinishedAt >= RetryAfter;

    private async Task RunAsync(SplitModelJob job, Entry entry)
    {
        var lastPercent = 0;
        var progress = new InlineProgress(bytes =>
        {
            var percent = job.Size <= 0 ? 0 : (int)(bytes * 100 / job.Size);
            if (percent <= lastPercent)
            {
                return;
            }

            lastPercent = percent;
            Update(entry, entry.State with { Progress = percent / 100.0 });
        });

        ModelPreparation final;
        try
        {
            var result = await _assembler.AssembleAsync(job, progress, CancellationToken.None).ConfigureAwait(false);
            entry.Retryable = result.Retryable;
            final = result.Succeeded
                ? entry.State with { State = ModelPreparationState.Ready, Progress = 1 }
                : entry.State with { State = ModelPreparationState.Failed, Error = result.Error };
        }
        catch (Exception exception)
        {
            entry.Retryable = exception is IOException or UnauthorizedAccessException;
            final = entry.State with { State = ModelPreparationState.Failed, Error = exception.Message };
        }

        entry.FinishedAt = _time.GetUtcNow();
        Update(entry, final);
    }

    private void Update(Entry entry, ModelPreparation state)
    {
        lock (_gate)
        {
            entry.State = state;
        }

        Raise(state);
    }

    private void Raise(ModelPreparation state)
    {
        try
        {
            Changed?.Invoke(this, state);
        }
        catch (Exception)
        {
            // A faulty subscriber must not stop the join.
        }
    }

    private sealed class Entry(ModelPreparation state)
    {
        public ModelPreparation State { get; set; } = state;

        public Task Task { get; set; } = Task.CompletedTask;

        public bool Retryable { get; set; }

        public DateTimeOffset FinishedAt { get; set; }
    }

    private sealed class InlineProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }
}
