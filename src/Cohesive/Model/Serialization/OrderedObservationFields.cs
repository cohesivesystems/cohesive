using System.Buffers;
using System.Collections.Immutable;

namespace Cohesive.Model.Serialization;

// Canonical observation encodings share ordinal property ordering. Already-sorted immutable input
// needs no copy; other inputs use a scoped pooled buffer cleared on every exit, including exceptions.
internal struct OrderedObservationFields : IDisposable
{
    ImmutableSortedDictionary<string, ObservationValue>? sorted;
    OrdinalObservationFields? ordinal;
    KeyValuePair<string, ObservationValue>[]? buffer;
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
            buffer = null;
            count = 0;
        }
        else if (fields is ImmutableSortedDictionary<string, ObservationValue> ordered
            && ReferenceEquals(ordered.KeyComparer, StringComparer.Ordinal))
        {
            sorted = ordered;
            buffer = null;
            count = 0;
        }
        else
        {
            sorted = null;
            pool ??= ArrayPool<KeyValuePair<string, ObservationValue>>.Shared;
            buffer = RentOrderedObservationProperties(fields, pool, out count);
            if (buffer is not null)
            {
                lease = Lease.Acquire(buffer, count, pool);
                generation = lease.Generation;
            }
        }
    }

    public Enumerator GetEnumerator() => new(sorted, ordinal, buffer, count);
    // Copies share a generation-stamped lease: stale copies cannot return a later rental.
    public void Dispose()
    {
        lease?.Return(generation);
        lease = null;
        buffer = null;
        sorted = null;
        ordinal = null;
        count = 0;
    }

    internal struct Enumerator : IDisposable
    {
        readonly KeyValuePair<string, ObservationValue>[]? buffer;
        readonly int count;
        readonly bool isSorted;
        readonly OrdinalObservationFields? ordinal;
        OrdinalObservationFields.Enumerator ordinalEnumerator;
        ImmutableSortedDictionary<string, ObservationValue>.Enumerator sorted;
        int index;

        internal Enumerator(ImmutableSortedDictionary<string, ObservationValue>? fields,
            OrdinalObservationFields? ordinal, KeyValuePair<string, ObservationValue>[]? buffer, int count)
        {
            this.ordinal = ordinal;
            ordinalEnumerator = ordinal is null ? default : ordinal.GetCanonicalEnumerator();
            this.buffer = buffer;
            this.count = count;
            isSorted = fields is not null;
            sorted = fields is null ? default : fields.GetEnumerator();
            index = -1;
        }

        public KeyValuePair<string, ObservationValue> Current => ordinal is not null ? ordinalEnumerator.Current : isSorted ? sorted.Current : buffer![index];
        public bool MoveNext()
        {
            if (ordinal is null) return isSorted ? sorted.MoveNext() : ++index < count;
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

    sealed class Lease
    {
        static readonly Queue<Lease> available = new();
        KeyValuePair<string, ObservationValue>[]? buffer;
        ArrayPool<KeyValuePair<string, ObservationValue>> pool = null!;
        int count;
        internal long Generation { get; private set; }

        internal static Lease Acquire(KeyValuePair<string, ObservationValue>[] buffer, int count,
            ArrayPool<KeyValuePair<string, ObservationValue>> pool)
        {
            Lease lease;
            lock (available) lease = available.Count == 0 ? new() : available.Dequeue();
            lock (lease)
            {
                lease.Generation = checked(lease.Generation + 1);
                lease.buffer = buffer;
                lease.count = count;
                lease.pool = pool;
            }
            return lease;
        }

        internal void Return(long generation)
        {
            lock (this)
            {
                if (generation != Generation || buffer is null) return;
                var rented = buffer;
                buffer = null;
                ReturnOrderedObservationProperties(rented, count, pool);
                pool = null!;
                count = 0;
            }
            // Bound retained lease metadata; buffers and their values are never retained.
            lock (available) if (available.Count < 256) available.Enqueue(this);
        }
    }
}
