using System.Collections;

namespace Cohesive.Model;

// This view prevents a nested physical layout from claiming a root domain shape identity.
// Its storage is freshly owned/immutable; canonical traversal unwraps it without boxing.
internal sealed class ObservationNestedFields(IReadOnlyDictionary<string, ObservationValue> storage)
    : IReadOnlyDictionary<string, ObservationValue>
{
    internal IReadOnlyDictionary<string, ObservationValue> Storage => storage;
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
    public Enumerator GetEnumerator() => new((IOrdinalObservationFieldReader)storage);
    IEnumerator<KeyValuePair<string, ObservationValue>> IEnumerable<KeyValuePair<string, ObservationValue>>.GetEnumerator() => GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    // Generic CLR projection boxes this view. Keep only cursor state in the box rather than
    // boxing the storage enumerator's cached key/value pair; canonical traversal stays unboxed.
    internal struct Enumerator(IOrdinalObservationFieldReader reader) : IEnumerator<KeyValuePair<string, ObservationValue>>
    {
        int ordinal = -1;
        public KeyValuePair<string, ObservationValue> Current
        {
            get { reader.TryGetField(ordinal, out var value); return new(reader.Layout.FieldIdentities[ordinal], value); }
        }
        object IEnumerator.Current => Current;
        public bool MoveNext()
        {
            while (++ordinal < reader.Layout.Count) if (reader.TryGetField(ordinal, out _)) return true;
            return false;
        }
        public void Dispose() { }
        public void Reset() => throw new NotSupportedException();
    }
}
