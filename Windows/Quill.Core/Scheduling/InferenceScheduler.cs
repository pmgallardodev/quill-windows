using Quill.Core.Ports;

namespace Quill.Core.Scheduling;

/// <summary>
/// Default <see cref="IInferenceScheduler"/>: one background worker runs one job at a time,
/// lowest <see cref="InferencePriority"/> value first and FIFO within a priority (design §7.2).
/// Jobs are never preempted: a running Background job finishes before a newly queued live
/// chunk starts, so callers keep background jobs reasonably small.
/// </summary>
/// <remarks>
/// Cancelling a queued job removes it without running it. Cancelling a running job signals the
/// token passed to the job; the job decides how fast it stops. Disposing cancels every queued
/// job, signals the running one and waits for it to return.
/// </remarks>
public sealed class InferenceScheduler : IInferenceScheduler, IAsyncDisposable, IDisposable
{
    private static readonly int PriorityCount = Enum.GetValues<InferencePriority>().Length;

    private readonly object _lock = new();
    private readonly LinkedList<WorkItem>[] _queues;
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _worker;
    private WorkItem? _running;
    private int _queuedCount;
    private bool _disposed;

    public InferenceScheduler()
    {
        _queues = new LinkedList<WorkItem>[PriorityCount];
        for (var index = 0; index < _queues.Length; index++)
        {
            _queues[index] = new LinkedList<WorkItem>();
        }

        _worker = Task.Run(WorkerLoopAsync);
    }

    public int PendingCount
    {
        get
        {
            lock (_lock)
            {
                return _queuedCount + (_running is null ? 0 : 1);
            }
        }
    }

    public Task<T> RunAsync<T>(
        InferencePriority priority,
        Func<CancellationToken, Task<T>> job,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        var item = new WorkItem<T>(this, Validate(priority), job, cancellationToken);
        Enqueue(item, skipIfBusy: false);
        return item.Task;
    }

    public async Task<(bool Ran, T? Result)> TryRunAsync<T>(
        InferencePriority priority,
        Func<CancellationToken, Task<T>> job,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        var item = new WorkItem<T>(this, Validate(priority), job, cancellationToken);
        if (!Enqueue(item, skipIfBusy: true))
        {
            return (false, default);
        }

        return (true, await item.Task.ConfigureAwait(false));
    }

    public async ValueTask DisposeAsync()
    {
        List<WorkItem> cancelled;
        lock (_lock)
        {
            if (_disposed)
            {
                cancelled = [];
            }
            else
            {
                _disposed = true;
                cancelled = _queues.SelectMany(queue => queue).ToList();
                foreach (var queue in _queues)
                {
                    queue.Clear();
                }

                _queuedCount = 0;
            }
        }

        foreach (var item in cancelled)
        {
            item.CancelQueued(_shutdown.Token);
        }

        try
        {
            _shutdown.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Second dispose after the first one finished.
        }

        _signal.Release();
        await _worker.ConfigureAwait(false);
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    private static InferencePriority Validate(InferencePriority priority) =>
        (int)priority >= 0 && (int)priority < PriorityCount
            ? priority
            : throw new ArgumentOutOfRangeException(nameof(priority), priority, "unknown inference priority");

    /// <returns>False when <paramref name="skipIfBusy"/> is set and same or higher priority work is pending.</returns>
    private bool Enqueue(WorkItem item, bool skipIfBusy)
    {
        if (item.CallerToken.IsCancellationRequested)
        {
            item.CancelQueued(item.CallerToken);
            return true;
        }

        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (skipIfBusy && HasWorkAtOrAbove(item.Priority))
            {
                return false;
            }

            item.Node = _queues[(int)item.Priority].AddLast(item);
            _queuedCount++;
        }

        item.RegisterCancellation();
        _signal.Release();
        return true;
    }

    private bool HasWorkAtOrAbove(InferencePriority priority)
    {
        if (_running is not null && _running.Priority <= priority)
        {
            return true;
        }

        for (var index = 0; index <= (int)priority; index++)
        {
            if (_queues[index].Count > 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Called from the caller token's registration while the item may still be queued.</summary>
    private void RemoveQueued(WorkItem item)
    {
        bool removed;
        lock (_lock)
        {
            removed = item.Node?.List is not null;
            if (removed)
            {
                _queues[(int)item.Priority].Remove(item.Node!);
                _queuedCount--;
            }
        }

        if (removed)
        {
            item.CancelQueued(item.CallerToken);
        }
    }

    private async Task WorkerLoopAsync()
    {
        while (true)
        {
            await _signal.WaitAsync().ConfigureAwait(false);
            WorkItem? item = null;
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }

                foreach (var queue in _queues)
                {
                    if (queue.First is { } first)
                    {
                        item = first.Value;
                        queue.RemoveFirst();
                        _queuedCount--;
                        break;
                    }
                }

                // Cancelled items leave their signal behind; an empty pass is expected.
                _running = item;
            }

            if (item is null)
            {
                continue;
            }

            await item.ExecuteAsync(_shutdown.Token).ConfigureAwait(false);
            lock (_lock)
            {
                _running = null;
            }

            // Completed after _running is cleared so PendingCount is already accurate for awaiters.
            item.Publish();
        }
    }

    private abstract class WorkItem
    {
        protected WorkItem(InferencePriority priority, CancellationToken callerToken)
        {
            Priority = priority;
            CallerToken = callerToken;
        }

        public InferencePriority Priority { get; }

        public CancellationToken CallerToken { get; }

        public LinkedListNode<WorkItem>? Node { get; set; }

        public abstract void RegisterCancellation();

        public abstract void CancelQueued(CancellationToken token);

        /// <summary>Runs the job and stores its outcome. Never throws.</summary>
        public abstract Task ExecuteAsync(CancellationToken shutdown);

        /// <summary>Completes the caller's task with the stored outcome.</summary>
        public abstract void Publish();
    }

    private sealed class WorkItem<T> : WorkItem
    {
        private readonly InferenceScheduler _owner;
        private readonly Func<CancellationToken, Task<T>> _job;
        private readonly TaskCompletionSource<T> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private CancellationTokenRegistration _registration;
        private Action? _outcome;

        public WorkItem(
            InferenceScheduler owner,
            InferencePriority priority,
            Func<CancellationToken, Task<T>> job,
            CancellationToken callerToken)
            : base(priority, callerToken)
        {
            _owner = owner;
            _job = job;
        }

        public Task<T> Task => _completion.Task;

        public override void RegisterCancellation()
        {
            if (CallerToken.CanBeCanceled)
            {
                _registration = CallerToken.Register(static state =>
                {
                    var item = (WorkItem<T>)state!;
                    item._owner.RemoveQueued(item);
                }, this);
            }
        }

        public override void CancelQueued(CancellationToken token)
        {
            _registration.Dispose();
            _completion.TrySetCanceled(token);
        }

        public override async Task ExecuteAsync(CancellationToken shutdown)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(CallerToken, shutdown);
            try
            {
                var result = await _job(linked.Token).ConfigureAwait(false);
                _outcome = () => _completion.TrySetResult(result);
            }
            catch (OperationCanceledException exception) when (linked.IsCancellationRequested)
            {
                var token = CallerToken.IsCancellationRequested ? CallerToken : exception.CancellationToken;
                _outcome = () => _completion.TrySetCanceled(token);
            }
            catch (Exception exception)
            {
                _outcome = () => _completion.TrySetException(exception);
            }
            finally
            {
                // Disposing waits for an in-flight callback, which only touches the queue, never this item.
                await _registration.DisposeAsync().ConfigureAwait(false);
            }
        }

        public override void Publish() => _outcome?.Invoke();
    }
}
