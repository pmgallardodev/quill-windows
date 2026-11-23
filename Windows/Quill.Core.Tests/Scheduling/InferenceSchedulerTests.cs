using Quill.Core.Ports;
using Quill.Core.Scheduling;
using Xunit;

namespace Quill.Core.Tests.Scheduling;

public sealed class InferenceSchedulerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task RunAsync_ReturnsJobResult()
    {
        await using var scheduler = new InferenceScheduler();

        var result = await scheduler.RunAsync(InferencePriority.Background, _ => Task.FromResult(42));

        Assert.Equal(42, result);
    }

    [Fact]
    public async Task RunsHigherPriorityFirstAndFifoWithinPriority()
    {
        await using var scheduler = new InferenceScheduler();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new List<string>();

        var blocker = scheduler.RunAsync(InferencePriority.Background, async _ =>
        {
            started.SetResult();
            await gate.Task;
            return 0;
        });
        await started.Task.WaitAsync(Timeout);

        Task<int> Queue(InferencePriority priority, string name) =>
            scheduler.RunAsync(priority, _ =>
            {
                lock (order)
                {
                    order.Add(name);
                }

                return Task.FromResult(0);
            });

        var jobs = new[]
        {
            Queue(InferencePriority.Background, "background-1"),
            Queue(InferencePriority.LiveSummary, "summary"),
            Queue(InferencePriority.LiveTranscription, "live-1"),
            Queue(InferencePriority.Interactive, "interactive"),
            Queue(InferencePriority.LiveTranscription, "live-2"),
            Queue(InferencePriority.Background, "background-2"),
        };
        Assert.Equal(7, scheduler.PendingCount);

        gate.SetResult();
        await Task.WhenAll(jobs.Append(blocker)).WaitAsync(Timeout);

        Assert.Equal(
            new[] { "live-1", "live-2", "interactive", "summary", "background-1", "background-2" },
            order);
        Assert.Equal(0, scheduler.PendingCount);
    }

    [Fact]
    public async Task RunsOneJobAtATime()
    {
        await using var scheduler = new InferenceScheduler();
        var running = 0;
        var maxRunning = 0;

        var jobs = Enumerable.Range(0, 20).Select(index => scheduler.RunAsync(
            (InferencePriority)(index % 4),
            async _ =>
            {
                var now = Interlocked.Increment(ref running);
                InterlockedMax(ref maxRunning, now);
                await Task.Delay(1);
                Interlocked.Decrement(ref running);
                return index;
            })).ToArray();
        await Task.WhenAll(jobs).WaitAsync(Timeout);

        Assert.Equal(1, maxRunning);
    }

    [Fact]
    public async Task JobExceptionPropagatesAndWorkerKeepsRunning()
    {
        await using var scheduler = new InferenceScheduler();

        var failing = scheduler.RunAsync<int>(
            InferencePriority.LiveTranscription,
            _ => throw new InvalidOperationException("boom"));
        var asyncFailing = scheduler.RunAsync<int>(
            InferencePriority.LiveTranscription,
            async _ =>
            {
                await Task.Yield();
                throw new FormatException("async boom");
            });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => failing.WaitAsync(Timeout));
        Assert.Equal("boom", error.Message);
        await Assert.ThrowsAsync<FormatException>(() => asyncFailing.WaitAsync(Timeout));
        Assert.Equal(7, await scheduler.RunAsync(InferencePriority.Background, _ => Task.FromResult(7)).WaitAsync(Timeout));
    }

    [Fact]
    public async Task CancellingQueuedJobRemovesItWithoutRunningIt()
    {
        await using var scheduler = new InferenceScheduler();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocker = await StartBlockerAsync(scheduler, gate);
        using var cancellation = new CancellationTokenSource();
        var ran = false;

        var queued = scheduler.RunAsync(InferencePriority.Background, _ =>
        {
            ran = true;
            return Task.FromResult(1);
        }, cancellation.Token);
        Assert.Equal(2, scheduler.PendingCount);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.WaitAsync(Timeout));
        Assert.Equal(1, scheduler.PendingCount);
        gate.SetResult();
        await blocker.WaitAsync(Timeout);
        Assert.False(ran);
        Assert.Equal(0, scheduler.PendingCount);
    }

    [Fact]
    public async Task AlreadyCancelledTokenFailsImmediately()
    {
        await using var scheduler = new InferenceScheduler();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var job = scheduler.RunAsync(InferencePriority.Background, _ => Task.FromResult(1), cancellation.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => job.WaitAsync(Timeout));
        Assert.Equal(0, scheduler.PendingCount);
    }

    [Fact]
    public async Task CancellingRunningJobSignalsItsToken()
    {
        await using var scheduler = new InferenceScheduler();
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var running = scheduler.RunAsync(InferencePriority.Interactive, async token =>
        {
            started.SetResult();
            await Task.Delay(Timeout, token);
            return 1;
        }, cancellation.Token);
        await started.Task.WaitAsync(Timeout);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(Timeout));
        Assert.Equal(5, await scheduler.RunAsync(InferencePriority.Background, _ => Task.FromResult(5)).WaitAsync(Timeout));
    }

    [Fact]
    public async Task TryRunAsync_RunsWhenOnlyLowerPriorityWorkIsPending()
    {
        await using var scheduler = new InferenceScheduler();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocker = await StartBlockerAsync(scheduler, gate, InferencePriority.Background);

        var attempt = scheduler.TryRunAsync(InferencePriority.LiveSummary, _ => Task.FromResult("summary"));
        Assert.False(attempt.IsCompleted);
        gate.SetResult();

        var (ran, result) = await attempt.WaitAsync(Timeout);
        Assert.True(ran);
        Assert.Equal("summary", result);
        await blocker.WaitAsync(Timeout);
    }

    [Fact]
    public async Task TryRunAsync_SkipsWhenSamePriorityIsRunning()
    {
        await using var scheduler = new InferenceScheduler();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocker = await StartBlockerAsync(scheduler, gate, InferencePriority.LiveSummary);
        var ran = false;

        var (skippedRan, result) = await scheduler.TryRunAsync(InferencePriority.LiveSummary, _ =>
        {
            ran = true;
            return Task.FromResult("x");
        }).WaitAsync(Timeout);

        Assert.False(skippedRan);
        Assert.Null(result);
        Assert.Equal(1, scheduler.PendingCount);
        gate.SetResult();
        await blocker.WaitAsync(Timeout);
        Assert.False(ran);
    }

    [Fact]
    public async Task TryRunAsync_SkipsWhenHigherPriorityIsQueued()
    {
        await using var scheduler = new InferenceScheduler();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocker = await StartBlockerAsync(scheduler, gate, InferencePriority.Background);
        var live = scheduler.RunAsync(InferencePriority.LiveTranscription, _ => Task.FromResult(1));

        var (ran, _) = await scheduler.TryRunAsync(InferencePriority.LiveSummary, _ => Task.FromResult(2)).WaitAsync(Timeout);

        Assert.False(ran);
        gate.SetResult();
        await Task.WhenAll(blocker, live).WaitAsync(Timeout);
    }

    [Fact]
    public async Task TryRunAsync_SkipsWhenSamePriorityIsQueued()
    {
        await using var scheduler = new InferenceScheduler();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocker = await StartBlockerAsync(scheduler, gate, InferencePriority.Background);
        var first = scheduler.TryRunAsync(InferencePriority.LiveSummary, _ => Task.FromResult(1));

        var second = await scheduler.TryRunAsync(InferencePriority.LiveSummary, _ => Task.FromResult(2)).WaitAsync(Timeout);

        Assert.False(second.Ran);
        gate.SetResult();
        Assert.Equal((true, 1), await first.WaitAsync(Timeout));
        await blocker.WaitAsync(Timeout);
    }

    [Fact]
    public async Task TryRunAsync_PropagatesJobException()
    {
        await using var scheduler = new InferenceScheduler();

        await Assert.ThrowsAsync<InvalidOperationException>(() => scheduler.TryRunAsync<int>(
            InferencePriority.LiveSummary,
            _ => throw new InvalidOperationException()).WaitAsync(Timeout));
    }

    [Fact]
    public async Task DisposeCancelsQueuedJobsAndRejectsNewOnes()
    {
        var scheduler = new InferenceScheduler();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocker = await StartBlockerAsync(scheduler, gate);
        var queued = scheduler.RunAsync(InferencePriority.Background, _ => Task.FromResult(1));

        var disposing = scheduler.DisposeAsync().AsTask();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.WaitAsync(Timeout));
        gate.SetResult();
        await disposing.WaitAsync(Timeout);
        await blocker.WaitAsync(Timeout);

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => scheduler.RunAsync(InferencePriority.Background, _ => Task.FromResult(1)));
        Assert.Equal(0, scheduler.PendingCount);
    }

    [Fact]
    public async Task NullJobIsRejected()
    {
        await using var scheduler = new InferenceScheduler();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => scheduler.RunAsync<int>(InferencePriority.Background, null!));
    }

    private static async Task<Task<int>> StartBlockerAsync(
        InferenceScheduler scheduler,
        TaskCompletionSource gate,
        InferencePriority priority = InferencePriority.Background)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocker = scheduler.RunAsync(priority, async _ =>
        {
            started.SetResult();
            await gate.Task;
            return 0;
        });
        await started.Task.WaitAsync(Timeout);
        return blocker;
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value &&
               Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }
}
