using System.Buffers;
using System.Collections.Immutable;

namespace Cohesive.Model.Serialization;

// Canonical observation encodings share ordinal property ordering. Already-sorted immutable input
// needs no copy; other inputs use a scoped pooled buffer cleared on every exit, including exceptions.
// A ref struct is still copyable: keep a sole owner and never dispose a copy. Enumerators borrow
// its rental; reading after owner disposal or transfer completion is unsupported and may read reused
// storage. Finish enumeration before disposal. Streaming frames transfer ownership by reference.
internal ref struct OrderedObservationFields
{
    ImmutableSortedDictionary<string, ObservationValue>? sorted;
    OrdinalObservationFields? ordinal;
    SmallObservationFields? small;
    ObservationFieldBuffer rental;

    internal OrderedObservationFields(IReadOnlyDictionary<string, ObservationValue>? fields, ArrayPool<KeyValuePair<string, ObservationValue>>? pool = null)
    {
        rental = default;
        pool ??= ArrayPool<KeyValuePair<string, ObservationValue>>.Shared;
        ordinal = fields as OrdinalObservationFields;
        small = fields as SmallObservationFields;
        if (ordinal is not null || small is not null)
        {
            sorted = null;
        }
        else if (fields is ImmutableSortedDictionary<string, ObservationValue> ordered
            && ReferenceEquals(ordered.KeyComparer, StringComparer.Ordinal))
        {
            sorted = ordered;
        }
        else
        {
            sorted = null;
            rental = RentOrderedObservationProperties(fields, pool);
        }
    }

    public Enumerator GetEnumerator() => new(sorted, ordinal, small, rental.Buffer, rental.Count);

    // Move the sole rental into an empty private streaming-stack slot.
    internal void MoveBufferTo(ref ObservationFieldBuffer target) => rental.MoveTo(ref target);

    public void Dispose()
    {
        rental.Dispose();
        sorted = null;
        ordinal = null;
        small = null;
    }

    internal struct Enumerator : IDisposable
    {
        readonly KeyValuePair<string, ObservationValue>[]? buffer;
        readonly int count;
        readonly bool isSorted;
        readonly OrdinalObservationFields? ordinal;
        readonly SmallObservationFields? small;
        OrdinalObservationFields.Enumerator ordinalEnumerator;
        ImmutableSortedDictionary<string, ObservationValue>.Enumerator sorted;
        int index;

        internal Enumerator(ImmutableSortedDictionary<string, ObservationValue>? fields,
            OrdinalObservationFields? ordinal, SmallObservationFields? small, KeyValuePair<string, ObservationValue>[]? buffer, int count)
        {
            this.ordinal = ordinal;
            this.small = small;
            ordinalEnumerator = ordinal is null ? default : ordinal.GetCanonicalEnumerator();
            this.buffer = buffer;
            this.count = small?.Count ?? count;
            isSorted = fields is not null;
            sorted = fields is null ? default : fields.GetEnumerator();
            index = -1;
        }

        public KeyValuePair<string, ObservationValue> Current => small is not null ? small.At(index) : ordinal is not null ? ordinalEnumerator.Current : isSorted ? sorted.Current : buffer![index];
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

    static ObservationFieldBuffer RentOrderedObservationProperties(
        IReadOnlyDictionary<string, ObservationValue>? properties,
        ArrayPool<KeyValuePair<string, ObservationValue>> pool)
    {
        var count = 0;
        if (properties is null || properties.Count == 0)
            return default;

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
            return new ObservationFieldBuffer(ordered, count, pool);
        }
        catch
        {
            var failed = new ObservationFieldBuffer(ordered, count, pool);
            failed.Dispose();
            throw;
        }
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

// Sole owner of a pooled field buffer. Copying does not duplicate ownership: use MoveTo for transfers
// and never dispose or enumerate a stale copy. Private frame relocation clears the previous slots.
internal struct ObservationFieldBuffer(
    KeyValuePair<string, ObservationValue>[] buffer,
    int count,
    ArrayPool<KeyValuePair<string, ObservationValue>> pool)
{
    internal KeyValuePair<string, ObservationValue>[]? Buffer { get; private set; } = buffer;
    internal int Count { get; private set; } = count;
    ArrayPool<KeyValuePair<string, ObservationValue>>? pool = pool;

    internal void MoveTo(ref ObservationFieldBuffer target)
    {
        System.Diagnostics.Debug.Assert(target.Buffer is null, "Ownership must move into an empty slot.");
        target = this;
        this = default;
    }

    internal void Dispose()
    {
        if (Buffer is null) return;
        var returned = Buffer;
        var returnedCount = Count;
        var returnedPool = pool!;
        this = default;
        returned.AsSpan(0, returnedCount).Clear();
        returnedPool.Return(returned);
    }
}
