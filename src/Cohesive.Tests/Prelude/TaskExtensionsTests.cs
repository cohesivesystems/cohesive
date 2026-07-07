using System.Diagnostics;

namespace Cohesive.Tests.Prelude;

public sealed class TaskExtensionsTests
{
    [Fact]
    public async Task WhenAllThrottled_WithSelector_RespectsMaxConcurrency_AndPreservesOrder()
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var inFlight = 0;
        var maxObservedConcurrency = 0;
        var started = 0;

        var task = Task.WhenAllThrottled(
            source: Enumerable.Range(0, 5),
            selector: async item =>
            {
                var currentConcurrency = Interlocked.Increment(ref inFlight);
                RecordMax(ref maxObservedConcurrency, currentConcurrency);
                Interlocked.Increment(ref started);

                await release.Task.ConfigureAwait(false);

                Interlocked.Decrement(ref inFlight);
                return item * 10;
            },
            options: new(maxConcurrency: 2)
        );

        await WaitUntilAsync(() => Volatile.Read(ref started) == 2);

        Assert.Equal(2, Volatile.Read(ref maxObservedConcurrency));
        Assert.Equal(2, Volatile.Read(ref inFlight));

        release.SetResult(true);

        var results = await task;

        Assert.Equal([0, 10, 20, 30, 40], results);
        Assert.Equal(0, Volatile.Read(ref inFlight));
    }

    [Fact]
    public async Task WhenAllThrottled_WithSelector_FailFast_ThrowsAggregateExceptionForOriginalFailure()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var task = Task.WhenAllThrottled(
            source: [0, 1, 2],
            selector: async item =>
            {
                if (item == 0)
                {
                    entered.TrySetResult(true);
                    await release.Task.ConfigureAwait(false);
                    throw new InvalidOperationException("boom");
                }
                return item;
            },
            options: new(maxConcurrency: 1, failFast: true)
        );

        await entered.Task;
        release.SetResult(true);

        var exception = await Assert.ThrowsAsync<AggregateException>(() => task);
        var failure = Assert.Single(exception.InnerExceptions);

        Assert.IsType<InvalidOperationException>(failure);
        Assert.Equal("boom", failure.Message);
    }

    [Fact]
    public async Task AwaitAll_PreservesSourceOrder()
    {
        var first = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var third = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        var task = Task.AwaitAll([first.Task, second.Task, third.Task]);

        second.SetResult(2);
        third.SetResult(3);
        first.SetResult(1);

        var results = await task;

        Assert.Equal([1, 2, 3], results);
    }

    [Fact]
    public async Task AwaitAll_FaultedTasks_ThrowsAggregateException()
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = FaultAfterReleaseAsync();
        var second = FaultImmediatelyAsync();

        var task = Task.AwaitAll([first, second]);

        release.SetResult(true);

        var exception = await Assert.ThrowsAsync<AggregateException>(() => task);
        Assert.Equal(2, exception.InnerExceptions.Count);
        Assert.All(exception.InnerExceptions, inner => Assert.IsType<InvalidOperationException>(inner));

        var messages = exception.InnerExceptions.Select(inner => inner.Message).OrderBy(x => x).ToArray();
        Assert.Equal(["boom", "boom-2"], messages);

        async Task<int> FaultAfterReleaseAsync()
        {
            await release.Task.ConfigureAwait(false);
            throw new InvalidOperationException("boom");
        }

        static Task<int> FaultImmediatelyAsync() =>
            Task.FromException<int>(new InvalidOperationException("boom-2"));
    }

    [Fact]
    public async Task AwaitAll_ExternalCancellation_ThrowsOperationCanceledException()
    {
        using var cts = new CancellationTokenSource();
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var task = Task.AwaitAll([WaitAsync()], cts.Token);

        await started.Task;
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);

        async Task<int> WaitAsync()
        {
            started.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cts.Token).ConfigureAwait(false);
            return 0;
        }
    }

    [Fact]
    public async Task WhenAllThrottled_WithSelector_ExternalCancellation_ThrowsOperationCanceledException()
    {
        using var cts = new CancellationTokenSource();
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var task = Task.WhenAllThrottled(
            source: [0, 1, 2],
            selector: async _ =>
            {
                started.TrySetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, cts.Token).ConfigureAwait(false);
                return 0;
            },
            options: new(maxConcurrency: 1),
            ct: cts.Token
        );

        await started.Task;
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    static void RecordMax(ref int currentMax, int candidate)
    {
        while (true)
        {
            var snapshot = Volatile.Read(ref currentMax);
            if (candidate <= snapshot)
                return;

            if (Interlocked.CompareExchange(ref currentMax, candidate, snapshot) == snapshot)
                return;
        }
    }

    static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var startedAt = Stopwatch.GetTimestamp();
        while (!predicate())
        {
            if (Stopwatch.GetElapsedTime(startedAt) > TimeSpan.FromSeconds(5))
                throw new TimeoutException("Timed out waiting for the expected task state.");

            await Task.Delay(10).ConfigureAwait(false);
        }
    }
}
