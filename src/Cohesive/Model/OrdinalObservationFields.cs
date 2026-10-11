using System.Collections;
using System.Collections.Immutable;

namespace Cohesive.Model;

// One owned value vector; names and ordinals are shared by all observations with this layout.
// The dictionary interface is a view, not another field-value store.
internal sealed class OrdinalObservationFields(ObservationLayout layout, ImmutableArray<ObservationValue> values, int count)
    : IReadOnlyDictionary<string, ObservationValue>, IOrdinalObservationFieldReader
{
    public ObservationLayout Layout => layout;
    public QualifiedShapeId ShapeId => layout.ShapeId;
    public int Count => count;
    public ObservationValue this[string key] => TryGetValue(key, out var value) ? value : throw new KeyNotFoundException(key);
    public IEnumerable<string> Keys { get { foreach (var pair in this) yield return pair.Key; } }
    public IEnumerable<ObservationValue> Values { get { foreach (var pair in this) yield return pair.Value; } }
    public bool ContainsKey(string key) => TryGetValue(key, out _);
    public bool TryGetValue(string key, out ObservationValue value) => TryGetField(key, out value);
    public bool TryGetField(string fieldIdentity, out ObservationValue field)
    {
        if (layout.TryGetOrdinal(fieldIdentity, out var ordinal)) return TryGetField(ordinal, out field);
        field = default;
        return false;
    }
    public bool TryGetField(int ordinal, out ObservationValue field)
    {
        field = (uint)ordinal < (uint)values.Length ? values[ordinal] : default;
        return field.Kind != ObservationValueKind.Undefined;
    }
    public Enumerator GetEnumerator() => new(this);
    IEnumerator<KeyValuePair<string, ObservationValue>> IEnumerable<KeyValuePair<string, ObservationValue>>.GetEnumerator() => GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public struct Enumerator(OrdinalObservationFields fields) : IEnumerator<KeyValuePair<string, ObservationValue>>
    {
        int ordinal = -1;
        public KeyValuePair<string, ObservationValue> Current { get; private set; }
        object IEnumerator.Current => Current;
        public bool MoveNext()
        {
            while (++ordinal < fields.Layout.Count)
                if (fields.TryGetField(ordinal, out var field))
                {
                    Current = new(fields.Layout.FieldIdentities[ordinal], field);
                    return true;
                }
            return false;
        }
        public void Dispose() { }
        public void Reset() => throw new NotSupportedException();
    }
}
