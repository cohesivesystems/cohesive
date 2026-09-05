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
    public IEnumerator<KeyValuePair<string, ObservationValue>> GetEnumerator()
    {
        for (var ordinal = 0; ordinal < values.Length; ordinal++)
            if (TryGetField(ordinal, out var field)) yield return new(layout.FieldIdentities[ordinal], field);
    }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
