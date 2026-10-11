using System.Buffers;
using System.Collections.Immutable;

namespace Cohesive.Model.Serialization;

// Canonical observation encodings share ordinal property ordering. Already-sorted immutable input
// needs no copy; other inputs use a scoped pooled buffer cleared on every exit, including exceptions.
// Keep a sole owner: do not copy it. Enumerators borrow its rental and must finish before disposal.
internal ref struct OrderedObservationFields
{
    ImmutableSortedDictionary<string, ObservationValue>? sorted;
    OrdinalObservationFields? ordinal;
    int count;
    KeyValuePair<string, ObservationValue>[]? buffer;
    ArrayPool<KeyValuePair<string, ObservationValue>>? pool;

    internal OrderedObservationFields(IReadOnlyDictionary<string, ObservationValue>? fields, ArrayPool<KeyValuePair<string, ObservationValue>>? pool = null)
    {
        buffer = null;
        this.pool = pool ?? ArrayPool<KeyValuePair<string, ObservationValue>>.Shared;
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
            buffer = RentOrderedObservationProperties(fields, this.pool, out count);
        }
    }

    public Enumerator GetEnumerator() => new(sorted, ordinal, buffer, count);

    // Move the sole rental into a private streaming-stack slot; this scoped owner becomes empty.
    internal void MoveBufferTo(out KeyValuePair<string, ObservationValue>[]? target,
        out int targetCount, out ArrayPool<KeyValuePair<string, ObservationValue>>? targetPool)
    {
        target = buffer;
        targetCount = count;
        targetPool = pool;
        buffer = null;
        count = 0;
    }

    public void Dispose()
    {
        ReturnBuffer(buffer, count, pool!);
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
            if (ordinal is null)
            {
                if (isSorted) return sorted.MoveNext();
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
            ReturnBuffer(ordered, count, pool);
            throw;
        }
    }

    internal static void ReturnBuffer(
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

}
