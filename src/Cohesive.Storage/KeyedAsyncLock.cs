using System.Collections.Concurrent;

namespace Cohesive.Storage;

/// <summary>
/// Provides asynchronous mutual exclusion per key without retaining historical keys after their work completes.
/// </summary>
/// <typeparam name="TKey">Stable key that defines one exclusion scope.</typeparam>
/// <remarks>
/// A key entry remains retained while any caller holds or waits for its lease. The final lease retires and disposes
/// the entry, so storage is proportional to active or queued work rather than historical key cardinality.
/// Cancellation applies only while acquiring a lease. A returned lease owns the exclusion until disposed.
/// Different keys proceed independently; same-key waiter fairness follows <see cref="SemaphoreSlim"/> and is not
/// guaranteed. The owner of this lock owns its entries for the lock's lifetime, but callers do not dispose the lock
/// itself because idle entries retire eagerly.
/// </remarks>
internal sealed class KeyedAsyncLock<TKey>
    where TKey : notnull
{
    readonly ConcurrentDictionary<TKey, Entry> entries = [];
    int registeredLeaseCount;

    /// <summary>Number of key entries currently retained for held or queued leases.</summary>
    /// <remarks>The value is an instantaneous diagnostic snapshot when acquisitions are concurrent.</remarks>
    internal int RetainedKeyCount => entries.Count;

    /// <summary>Number of leases currently held or queued across all retained keys.</summary>
    /// <remarks>The value is an instantaneous diagnostic snapshot when acquisitions are concurrent.</remarks>
    internal int RegisteredLeaseCount => Volatile.Read(ref registeredLeaseCount);

    /// <summary>Acquires asynchronous mutual exclusion for one key.</summary>
    /// <param name="key">Key whose active and queued callers share one exclusion entry.</param>
    /// <param name="cancellationToken">Cancellation observed only while waiting to acquire the key.</param>
    /// <returns>A lease that releases the key when disposed.</returns>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> is cancelled before the lease is acquired.
    /// </exception>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is <see langword="null"/>.</exception>
    internal async ValueTask<Lease> AcquireAsync(
        TKey key,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Entry entry;
        while (true)
        {
            entry = entries.GetOrAdd(key, static _ => new());
            lock (entry)
            {
                if (entry.IsRetired)
                {
                    continue;
                }

                checked
                {
                    entry.ReferenceCount++;
                }
                Interlocked.Increment(ref registeredLeaseCount);
                break;
            }
        }

        try
        {
            await entry.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new(this, key, entry);
        }
        catch
        {
            ReleaseReference(key, entry);
            throw;
        }
    }

    void Release(TKey key, Entry entry)
    {
        entry.Semaphore.Release();
        ReleaseReference(key, entry);
    }

    void ReleaseReference(TKey key, Entry entry)
    {
        var retire = false;
        lock (entry)
        {
            entry.ReferenceCount--;
            Interlocked.Decrement(ref registeredLeaseCount);
            if (entry.ReferenceCount == 0)
            {
                entry.IsRetired = true;
                retire = true;
            }
        }

        if (!retire)
        {
            return;
        }

        if (!entries.TryRemove(key, out var removed) || !ReferenceEquals(removed, entry))
        {
            throw new InvalidOperationException(
                "A retired keyed-lock entry was not the current entry for its key.");
        }
        entry.Semaphore.Dispose();
    }

    internal sealed class Entry
    {
        internal readonly SemaphoreSlim Semaphore = new(initialCount: 1, maxCount: 1);

        internal int ReferenceCount;

        internal bool IsRetired;
    }

    /// <summary>Owned same-key exclusion lease.</summary>
    internal sealed class Lease : IDisposable
    {
        KeyedAsyncLock<TKey>? owner;
        readonly TKey key;
        readonly Entry entry;

        internal Lease(KeyedAsyncLock<TKey> owner, TKey key, Entry entry)
        {
            this.owner = owner;
            this.key = key;
            this.entry = entry;
        }

        /// <summary>Releases the exclusion and its retained key reference exactly once.</summary>
        public void Dispose()
        {
            var currentOwner = Interlocked.Exchange(ref owner, null);
            currentOwner?.Release(key, entry);
        }
    }
}
