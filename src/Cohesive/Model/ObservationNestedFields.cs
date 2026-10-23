using System.Collections;

namespace Cohesive.Model;

// This view prevents a nested physical layout from claiming a root domain shape identity.
// Its storage is freshly owned/immutable; canonical traversal unwraps it without boxing.
internal sealed class ObservationNestedFields(IOrdinalObservationStorage storage)
    : IReadOnlyDictionary<string, ObservationValue>
{
    internal IOrdinalObservationStorage Storage => storage;
    public int Count => storage.Count;
    public IEnumerable<string> Keys => storage.Keys;
    public IEnumerable<ObservationValue> Values => storage.Values;
    public ObservationValue this[string key] => TryGetValue(key, out var value) ? value : throw new KeyNotFoundException(key);
    public bool ContainsKey(string key) => TryGetValue(key, out _);
    public bool TryGetValue(string key, out ObservationValue value)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (string.IsNullOrWhiteSpace(key)) { value = default; return false; }
        return storage.TryGetValue(key, out value);
    }
    public Enumerator GetEnumerator() => new(storage);
    IEnumerator<KeyValuePair<string, ObservationValue>> IEnumerable<KeyValuePair<string, ObservationValue>>.GetEnumerator() => GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    // Generic CLR projection boxes this view. Keep only cursor state in the box rather than
    // boxing the storage enumerator's cached key/value pair; canonical traversal stays unboxed.
    internal struct Enumerator(IOrdinalObservationStorage storage) : IEnumerator<KeyValuePair<string, ObservationValue>>
    {
        int ordinal = -1;
        // Each present field is located once by MoveNext and read once by Current; absent ordinals are skipped.
        public readonly KeyValuePair<string, ObservationValue> Current
        {
            get { storage.TryGetField(ordinal, out var value); return new(storage.Layout.FieldIdentities[ordinal], value); }
        }
        readonly object IEnumerator.Current => Current;
        public bool MoveNext() => (ordinal = storage.NextPresentOrdinal(ordinal)) >= 0;
        public readonly void Dispose() { }
        public readonly void Reset() => throw new NotSupportedException();
    }
}
