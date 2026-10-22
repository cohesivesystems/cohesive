using System.Collections;
using System.Text.Json;

namespace Cohesive.Model;

// Owns a bounded, sorted field vector. No caller can access its mutable array; lookup is linear
// over a bounded number of keys. Unlike graph-qualified ordinal layouts, names belong to this value.
internal sealed class SmallObservationFields : IReadOnlyDictionary<string, ObservationValue>
{
    internal const int JsonFieldLimit = 8;
    readonly KeyValuePair<string, ObservationValue>[] fields;
    public int Count { get; }
    SmallObservationFields(KeyValuePair<string, ObservationValue>[] fields, int count)
    { this.fields = fields; Count = count; }

    internal static SmallObservationFields Read(JsonElement element, int capacity, ObservationJsonPropertyNames? names, bool rejectDuplicates = false)
    {
        System.Diagnostics.Debug.Assert(capacity is > 0 and <= JsonFieldLimit);
        var fields = new KeyValuePair<string, ObservationValue>[capacity];
        var count = 0;
        foreach (var property in element.EnumerateObject())
        {
            var key = names?.Get(property) ?? property.Name;
            var value = ObservationValue.FromJsonElement(property.Value, names, rejectDuplicates);
            var index = 0;
            while (index < count && !StringComparer.Ordinal.Equals(fields[index].Key, key)) index++;
            if (rejectDuplicates && index < count) throw new JsonException("Duplicate object property.");
            fields[index] = new(key, value); // Token acquisition retains last-property-wins semantics.
            if (index == count) count++;
        }
        fields.AsSpan(0, count).Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Key, right.Key));
        return new(fields, count);
    }

    // Prepared framework contracts have a fixed small member set. The caller transfers exclusive
    // ownership of this vector; unlike Read, names already come from frozen serializer metadata.
    internal static SmallObservationFields FromOwnedProperties(KeyValuePair<string, ObservationValue>[] fields, int count)
    {
        System.Diagnostics.Debug.Assert(count <= 16 && count <= fields.Length);
        fields.AsSpan(0, count).Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Key, right.Key));
        for (var i = 1; i < count; i++)
            if (StringComparer.Ordinal.Equals(fields[i - 1].Key, fields[i].Key)) throw new JsonException("Duplicate graph property.");
        return new(fields, count);
    }

    internal SmallObservationFields Without(string name)
    {
        var index = 0;
        while (index < Count && !StringComparer.Ordinal.Equals(fields[index].Key, name)) index++;
        if (index == Count) return this;
        var remaining = new KeyValuePair<string, ObservationValue>[Count - 1];
        fields.AsSpan(0, index).CopyTo(remaining);
        fields.AsSpan(index + 1, Count - index - 1).CopyTo(remaining.AsSpan(index));
        return new(remaining, remaining.Length);
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
