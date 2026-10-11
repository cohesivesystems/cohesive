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
    internal Enumerator GetCanonicalEnumerator() => new(this, true);
    IEnumerator<KeyValuePair<string, ObservationValue>> IEnumerable<KeyValuePair<string, ObservationValue>>.GetEnumerator() => GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public struct Enumerator : IEnumerator<KeyValuePair<string, ObservationValue>>
    {
        readonly OrdinalObservationFields fields;
        readonly bool canonical;
        int ordinal;
        public Enumerator(OrdinalObservationFields fields) : this(fields, false) { }
        internal Enumerator(OrdinalObservationFields fields, bool canonical)
        {
            this.fields = fields;
            this.canonical = canonical;
            ordinal = -1;
            Current = default;
        }
        public KeyValuePair<string, ObservationValue> Current { get; private set; }
        object IEnumerator.Current => Current;
        public bool MoveNext()
        {
            while (++ordinal < fields.Layout.Count)
                if (fields.TryGetField(canonical ? fields.Layout.CanonicalJsonOrdinals[ordinal] : ordinal, out var field))
                {
                    Current = new(fields.Layout.FieldIdentities[canonical ? fields.Layout.CanonicalJsonOrdinals[ordinal] : ordinal], field);
                    return true;
                }
            return false;
        }
        public void Dispose() { }
        public void Reset() => throw new NotSupportedException();
    }
}
