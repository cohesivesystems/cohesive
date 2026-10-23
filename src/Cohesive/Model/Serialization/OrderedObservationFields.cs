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
    ObservationFieldStorage canonical;
    ObservationFieldBuffer rental;

    internal OrderedObservationFields(IReadOnlyDictionary<string, ObservationValue>? fields, ArrayPool<KeyValuePair<string, ObservationValue>>? pool = null)
    {
        rental = default;
        // Storage that is already in ordinal key order, including packed sparse records, is enumerated in place.
        var storage = ObservationFieldStorage.Of(fields);
        if (storage.IsCanonicallyOrdered)
        {
            canonical = storage;
        }
        else
        {
            canonical = default;
            rental = RentOrderedObservationProperties(storage.Fields, pool ?? ArrayPool<KeyValuePair<string, ObservationValue>>.Shared);
        }
    }

    public Enumerator GetEnumerator() => new(canonical, rental.Buffer, rental.Count);

    // Move the sole rental into an empty private streaming-stack slot.
    internal void MoveBufferTo(ref ObservationFieldBuffer target) => rental.MoveTo(ref target);

    public void Dispose()
    {
        rental.Dispose();
        canonical = default;
    }

    internal struct Enumerator : IDisposable
    {
        readonly KeyValuePair<string, ObservationValue>[]? buffer;
        readonly int count;
        readonly bool inPlace;
        ObservationFieldStorage.CanonicalEnumerator storage;
        int index;

        internal Enumerator(ObservationFieldStorage canonical, KeyValuePair<string, ObservationValue>[]? buffer, int count)
        {
            inPlace = canonical.IsCanonicallyOrdered;
            storage = inPlace ? canonical.GetCanonicalEnumerator() : default;
            this.buffer = buffer;
            this.count = count;
            index = -1;
        }

        public KeyValuePair<string, ObservationValue> Current => inPlace ? storage.Current : buffer![index];
        public bool MoveNext() => inPlace ? storage.MoveNext() : ++index < count;
        public void Dispose() { if (inPlace) storage.Dispose(); }
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
