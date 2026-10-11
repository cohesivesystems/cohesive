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

    internal OrderedObservationFields(IReadOnlyDictionary<string, ObservationValue>? fields)
    {
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
            buffer = RentOrderedObservationProperties(fields, out count);
        }
    }

    public Enumerator GetEnumerator() => new(sorted, ordinal, buffer, count);
    // Ownership is transferred, never duplicated: dispose only the owning instance. Copies must
    // not both be disposed. Streaming frames move ownership and clear the old slot after transfer.
    public void Dispose()
    {
        ReturnOrderedObservationProperties(buffer, count);
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
        KeyValuePair<string, ObservationValue> current;
        ImmutableSortedDictionary<string, ObservationValue>.Enumerator sorted;
        int index;

        internal Enumerator(ImmutableSortedDictionary<string, ObservationValue>? fields,
            OrdinalObservationFields? ordinal, KeyValuePair<string, ObservationValue>[]? buffer, int count)
        {
            this.ordinal = ordinal;
            current = default;
            this.buffer = buffer;
            this.count = count;
            isSorted = fields is not null;
            sorted = fields is null ? default : fields.GetEnumerator();
            index = -1;
        }

        public KeyValuePair<string, ObservationValue> Current => ordinal is not null ? current : isSorted ? sorted.Current : buffer![index];
        public bool MoveNext()
        {
            if (ordinal is null) return isSorted ? sorted.MoveNext() : ++index < count;
            var order = ordinal.Layout.CanonicalJsonOrdinals;
            while (++index < order.Length)
            {
                var fieldIndex = order[index];
                if (!ordinal.TryGetField(fieldIndex, out var field)) continue;
                current = new(ordinal.Layout.FieldIdentities[fieldIndex], field);
                return true;
            }
            return false;
        }
        public void Dispose() { if (isSorted) sorted.Dispose(); }
    }

    static KeyValuePair<string, ObservationValue>[]? RentOrderedObservationProperties(
        IReadOnlyDictionary<string, ObservationValue>? properties,
        out int count)
    {
        count = 0;
        if (properties is null || properties.Count == 0)
            return null;

        var ordered = ArrayPool<KeyValuePair<string, ObservationValue>>.Shared.Rent(properties.Count);
        try
        {
            switch (properties)
            {
                case ImmutableDictionary<string, ObservationValue> immutable:
                    foreach (var property in immutable)
                        ordered[count++] = property;
                    break;
                case ImmutableSortedDictionary<string, ObservationValue> sorted:
                    foreach (var property in sorted)
                        ordered[count++] = property;
                    break;
                case Dictionary<string, ObservationValue> dictionary:
                    foreach (var property in dictionary)
                        ordered[count++] = property;
                    break;
                case OwnedObservationFields owned:
                    foreach (var property in owned)
                        ordered[count++] = property;
                    break;
                default:
                    foreach (var property in properties)
                        ordered[count++] = property;
                    break;
            }

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
            ReturnOrderedObservationProperties(ordered, count);
            throw;
        }
    }

    static void ReturnOrderedObservationProperties(
        KeyValuePair<string, ObservationValue>[]? properties,
        int count)
    {
        if (properties is null)
            return;

        properties.AsSpan(0, count).Clear();
        ArrayPool<KeyValuePair<string, ObservationValue>>.Shared.Return(properties);
    }
}
