using Cohesive.Storage;

namespace Cohesive.Tests.Storage;

public sealed class KeyedAsyncLockTests
{
    [Fact]
    public async Task SameKey_WaiterSharesEntryAndRetiresAfterFinalLease()
    {
        var keyedLock = new KeyedAsyncLock<string>();
        using var first = await keyedLock.AcquireAsync("shared");

        var secondAcquisition = keyedLock.AcquireAsync("shared").AsTask();

        Assert.False(secondAcquisition.IsCompleted);
        Assert.Equal(1, keyedLock.RetainedKeyCount);
        Assert.Equal(2, keyedLock.RegisteredLeaseCount);

        first.Dispose();
        using var second = await secondAcquisition;
        Assert.Equal(1, keyedLock.RetainedKeyCount);
        Assert.Equal(1, keyedLock.RegisteredLeaseCount);

        second.Dispose();
        Assert.Equal(0, keyedLock.RetainedKeyCount);
        Assert.Equal(0, keyedLock.RegisteredLeaseCount);
    }

    [Fact]
    public async Task DifferentKeys_AcquireIndependently()
    {
        var keyedLock = new KeyedAsyncLock<string>();
        using var first = await keyedLock.AcquireAsync("first");

        var secondAcquisition = keyedLock.AcquireAsync("second").AsTask();

        Assert.True(secondAcquisition.IsCompletedSuccessfully);
        using var second = await secondAcquisition;
        Assert.Equal(2, keyedLock.RetainedKeyCount);
        Assert.Equal(2, keyedLock.RegisteredLeaseCount);
    }

    [Fact]
    public async Task CancellationBeforeAndAfterQueuing_DoesNotRetainAReference()
    {
        var keyedLock = new KeyedAsyncLock<string>();
        using var alreadyCancelled = new CancellationTokenSource();
        alreadyCancelled.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await keyedLock.AcquireAsync("before", alreadyCancelled.Token));
        Assert.Equal(0, keyedLock.RetainedKeyCount);
        Assert.Equal(0, keyedLock.RegisteredLeaseCount);

        using var holder = await keyedLock.AcquireAsync("after");
        using var queuedCancellation = new CancellationTokenSource();
        var queued = keyedLock.AcquireAsync("after", queuedCancellation.Token).AsTask();
        Assert.Equal(2, keyedLock.RegisteredLeaseCount);

        queuedCancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => queued);
        Assert.Equal(1, keyedLock.RetainedKeyCount);
        Assert.Equal(1, keyedLock.RegisteredLeaseCount);

        holder.Dispose();
        Assert.Equal(0, keyedLock.RetainedKeyCount);
        Assert.Equal(0, keyedLock.RegisteredLeaseCount);
    }

    [Fact]
    public async Task ProtectedActionException_ReleasesAndRetiresEntry()
    {
        var keyedLock = new KeyedAsyncLock<string>();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            using var lease = await keyedLock.AcquireAsync("failure");
            throw new InvalidOperationException("expected");
        });

        Assert.Equal(0, keyedLock.RetainedKeyCount);
        Assert.Equal(0, keyedLock.RegisteredLeaseCount);
    }

    [Fact]
    public async Task RetirementAndReacquisition_PreserveSameKeyMutualExclusion()
    {
        var keyedLock = new KeyedAsyncLock<string>();
        var active = 0;
        var maximumActive = 0;

        var workers = Enumerable.Range(0, 8).Select(async _ =>
        {
            for (var iteration = 0; iteration < 250; iteration++)
            {
                using var lease = await keyedLock.AcquireAsync("contended");
                var current = Interlocked.Increment(ref active);
                UpdateMaximum(ref maximumActive, current);
                await Task.Yield();
                Interlocked.Decrement(ref active);
            }
        });

        await Task.WhenAll(workers);

        Assert.Equal(1, maximumActive);
        Assert.Equal(0, active);
        Assert.Equal(0, keyedLock.RetainedKeyCount);
        Assert.Equal(0, keyedLock.RegisteredLeaseCount);
    }

    [Fact]
    public async Task HistoricalHighCardinality_RetainsNoIdleKeys()
    {
        var keyedLock = new KeyedAsyncLock<int>();

        for (var key = 0; key < 10_000; key++)
        {
            using var lease = await keyedLock.AcquireAsync(key);
        }

        Assert.Equal(0, keyedLock.RetainedKeyCount);
        Assert.Equal(0, keyedLock.RegisteredLeaseCount);
    }

    static void UpdateMaximum(ref int location, int value)
    {
        var current = Volatile.Read(ref location);
        while (current < value)
        {
            var observed = Interlocked.CompareExchange(ref location, value, current);
            if (observed == current)
            {
                return;
            }
            current = observed;
        }
    }
}
