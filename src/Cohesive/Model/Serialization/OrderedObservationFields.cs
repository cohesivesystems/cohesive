using System.Buffers;
using System.Collections.Immutable;

namespace Cohesive.Model.Serialization;

// Canonical observation encodings share ordinal property ordering. Already-sorted immutable input
// needs no copy; other inputs use a scoped pooled buffer cleared on every exit, including exceptions.
internal struct OrderedObservationFields : IDisposable
{
    ImmutableSortedDictionary<string, ObservationValue>? sorted;
    OrdinalObservationFields? ordinal;
    int count;
    Lease? lease;
    long generation;

    internal OrderedObservationFields(IReadOnlyDictionary<string, ObservationValue>? fields, ArrayPool<KeyValuePair<string, ObservationValue>>? pool = null)
    {
        lease = null;
        generation = 0;
        ordinal = fields as OrdinalObservationFields;
        if (ordinal is not null)
        {
            sorted = null;
            count = 0;
        }
        else if (fields is ImmutableSortedDictionary<string, ObservationValue> ordered
            && ReferenceEquals(ordered.KeyComparer, StringComparer.Ordinal))
        {
            sorted = ordered;
            count = 0;
        }
        else
        {
            sorted = null;
            pool ??= ArrayPool<KeyValuePair<string, ObservationValue>>.Shared;
            var buffer = RentOrderedObservationProperties(fields, pool, out count);
            if (buffer is not null)
            {
                lease = Lease.Acquire(buffer, count, pool);
                generation = lease.Generation;
            }
        }
    }

    internal object? RentalIdentity => lease;

    public Enumerator GetEnumerator() => new(sorted, ordinal, lease, generation, count);
    // Copies share a generation-stamped lease: stale copies cannot return a later rental.
    public void Dispose()
    {
        lease?.Return(generation);
        lease = null;
        sorted = null;
        ordinal = null;
        count = 0;
    }

    internal struct Enumerator : IDisposable
    {
        readonly Lease? lease;
        readonly long generation;
        readonly int count;
        readonly bool isSorted;
        readonly OrdinalObservationFields? ordinal;
        OrdinalObservationFields.Enumerator ordinalEnumerator;
        ImmutableSortedDictionary<string, ObservationValue>.Enumerator sorted;
        int index;

        internal Enumerator(ImmutableSortedDictionary<string, ObservationValue>? fields,
            OrdinalObservationFields? ordinal, Lease? lease, long generation, int count)
        {
            this.ordinal = ordinal;
            ordinalEnumerator = ordinal is null ? default : ordinal.GetCanonicalEnumerator();
            this.lease = lease;
            this.generation = generation;
            this.count = count;
            isSorted = fields is not null;
            sorted = fields is null ? default : fields.GetEnumerator();
            index = -1;
        }

        public KeyValuePair<string, ObservationValue> Current => ordinal is not null ? ordinalEnumerator.Current : isSorted ? sorted.Current : lease!.Read(generation, index);
        public bool MoveNext()
        {
            if (ordinal is null)
            {
                if (isSorted) return sorted.MoveNext();
                lease?.RequireActive(generation);
                return ++index < count;
            }
            return ordinalEnumerator.MoveNext();
        }
        public void Dispose() { if (isSorted) sorted.Dispose(); }
    }

    static KeyValuePair<string, ObservationValue>[]? RentOrderedObservationProperties(
        IReadOnlyDictionary<string, ObservationValue>? properties,
        ArrayPool<KeyValuePair<string, ObservationValue>> pool,
        out int count)
    {
        count = 0;
        if (properties is null || properties.Count == 0)
            return null;

        var ordered = pool.Rent(properties.Count);
        try
        {
            var copier = new FieldCopier(ordered);
            try { ObservationFieldTraversal.Visit(properties, ref copier); }
            finally { count = copier.Count; }

            for (var index = 1; index < count; index++)
            {
                if (StringComparer.Ordinal.Compare(ordered[index - 1].Key, ordered[index].Key) <= 0)
                    continue;
                ordered.AsSpan(0, count).Sort(
                    static (left, right) => StringComparer.Ordinal.Compare(left.Key, right.Key));
                break;
            }
            return ordered;
        }
        catch
        {
            ReturnOrderedObservationProperties(ordered, count, pool);
            throw;
        }
    }

    static void ReturnOrderedObservationProperties(
        KeyValuePair<string, ObservationValue>[]? properties,
        int count, ArrayPool<KeyValuePair<string, ObservationValue>> pool)
    {
        if (properties is null)
            return;

        properties.AsSpan(0, count).Clear();
        pool.Return(properties);
    }

    struct FieldCopier(KeyValuePair<string, ObservationValue>[] buffer) : IObservationFieldVisitor
    {
        internal int Count;
        public void Visit<TEnumerator>(TEnumerator fields, bool canonical)
            where TEnumerator : IEnumerator<KeyValuePair<string, ObservationValue>>
        {
            try { while (fields.MoveNext()) buffer[Count++] = fields.Current; }
            finally { fields.Dispose(); }
        }
    }

    internal sealed class Lease
    {
        // Synchronous traversals reuse only their own thread's metadata. No shared queue or lock.
        [ThreadStatic] static Stack<Lease>? available;
        KeyValuePair<string, ObservationValue>[]? buffer;
        ArrayPool<KeyValuePair<string, ObservationValue>> pool = null!;
        int count;
        long state = 1; // odd = returned, even = active
        internal long Generation => Volatile.Read(ref state);

        internal static Lease Acquire(KeyValuePair<string, ObservationValue>[] buffer, int count,
            ArrayPool<KeyValuePair<string, ObservationValue>> pool)
        {
            var lease = available is { Count: > 0 } ? available.Pop() : new Lease();
            lease.buffer = buffer;
            lease.count = count;
            lease.pool = pool;
            Interlocked.Increment(ref lease.state);
            return lease;
        }

        internal void RequireActive(long generation)
        {
            if (Volatile.Read(ref state) != generation)
                throw new ObjectDisposedException(nameof(OrderedObservationFields));
        }

        internal KeyValuePair<string, ObservationValue> Read(long generation, int index)
        {
            RequireActive(generation);
            return buffer![index];
        }

        internal void Return(long generation)
        {
            if (Interlocked.CompareExchange(ref state, generation + 1, generation) != generation) return;
            var rented = buffer;
            buffer = null;
            var rentalPool = pool;
            pool = null!;
            var rentalCount = count;
            count = 0;
            ReturnOrderedObservationProperties(rented, rentalCount, rentalPool);
            // Keep no values/buffers alive; overflow leases are collected. Never recycle at wraparound.
            if ((available?.Count ?? 0) < 256 && generation < long.MaxValue - 1)
                (available ??= new()).Push(this);
        }
    }
}
