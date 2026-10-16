using System.Collections;
using System.Text.Json;

namespace Cohesive.Model;

// Owns a bounded, sorted field vector. No caller can access its mutable array; lookup is linear
// over at most eight keys. Unlike graph-qualified ordinal layouts, names belong to this value.
internal sealed class SmallObservationFields : IReadOnlyDictionary<string, ObservationValue>
{
    readonly KeyValuePair<string, ObservationValue>[] fields;
    public int Count { get; }
    SmallObservationFields(KeyValuePair<string, ObservationValue>[] fields, int count)
    { this.fields = fields; Count = count; }

    internal static SmallObservationFields Read(JsonElement element, int capacity, ObservationJsonPropertyNames names)
    {
        System.Diagnostics.Debug.Assert(capacity is > 0 and <= 8);
        var fields = new KeyValuePair<string, ObservationValue>[capacity];
        var count = 0;
        foreach (var property in element.EnumerateObject())
        {
            var key = names.Get(property);
            var value = ObservationValue.FromJsonElement(property.Value, names);
            var index = 0;
            while (index < count && !StringComparer.Ordinal.Equals(fields[index].Key, key)) index++;
            fields[index] = new(key, value); // Token acquisition retains last-property-wins semantics.
            if (index == count) count++;
        }
        fields.AsSpan(0, count).Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Key, right.Key));
        return new(fields, count);
    }

    internal KeyValuePair<string, ObservationValue> At(int index) => fields[index];
    public ObservationValue this[string key] => TryGetValue(key, out var value) ? value : throw new KeyNotFoundException(key);
    public bool ContainsKey(string key) => TryGetValue(key, out _);
    public bool TryGetValue(string key, out ObservationValue value)
    {
        ArgumentNullException.ThrowIfNull(key);
        for (var i = 0; i < Count; i++)
            if (StringComparer.Ordinal.Equals(fields[i].Key, key)) { value = fields[i].Value; return true; }
        value = default;
        return false;
    }
    public IEnumerable<string> Keys { get { for (var i = 0; i < Count; i++) yield return fields[i].Key; } }
    public IEnumerable<ObservationValue> Values { get { for (var i = 0; i < Count; i++) yield return fields[i].Value; } }
    public Enumerator GetEnumerator() => new(this);
    IEnumerator<KeyValuePair<string, ObservationValue>> IEnumerable<KeyValuePair<string, ObservationValue>>.GetEnumerator() => GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    internal struct Enumerator(SmallObservationFields owner) : IEnumerator<KeyValuePair<string, ObservationValue>>
    {
        int index = -1;
        public KeyValuePair<string, ObservationValue> Current => owner.At(index);
        object IEnumerator.Current => Current;
        public bool MoveNext() => ++index < owner.Count;
        public void Dispose() { }
        public void Reset() => throw new NotSupportedException();
    }
}
