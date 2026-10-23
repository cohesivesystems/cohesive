using System.Buffers;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;

namespace Cohesive.Model.Serialization;

// Parser-local mutable construction. Small objects freeze into the same bounded storage used by
// JsonElement projection; larger objects use the existing owned dictionary boundary. Duplicate
// decoded names deliberately retain the plain reader's last-property-wins policy.
internal struct ObservationJsonObjectBuilder
{
    [InlineArray(SmallObservationFields.JsonFieldLimit)]
    struct Fields { KeyValuePair<string, ObservationValue> first; }
    Fields fields;
    int count;
    Dictionary<string, ObservationValue>? dictionary;

    internal bool TryAdd(string name, ObservationValue value)
    {
        if (dictionary is not null) return dictionary.TryAdd(name, value);
        for (var index = 0; index < count; index++)
            if (StringComparer.Ordinal.Equals(fields[index].Key, name)) return false;
        Set(name, value);
        return true;
    }

    internal void Set(string name, ObservationValue value)
    {
        if (dictionary is not null) { dictionary[name] = value; return; }
        for (var index = 0; index < count; index++)
            if (StringComparer.Ordinal.Equals(fields[index].Key, name))
            { fields[index] = new(name, value); return; }
        if (count < SmallObservationFields.JsonFieldLimit) { fields[count++] = new(name, value); return; }
        dictionary = new(capacity: SmallObservationFields.JsonFieldLimit * 2, StringComparer.Ordinal);
        for (var index = 0; index < count; index++) dictionary.Add(fields[index].Key, fields[index].Value);
        fields = default;
        dictionary.Add(name, value);
    }

    internal ObservationValue Build()
    {
        if (dictionary is not null) return ObservationValue.FromOwnedObject(dictionary);
        if (count == 0) return ObservationValue.EmptyObject;
        var owned = new KeyValuePair<string, ObservationValue>[count];
        for (var index = 0; index < count; index++) owned[index] = fields[index];
        return ObservationValue.FromObject(SmallObservationFields.FromOwnedProperties(owned, count));
    }
}

// Sole owner of scratch storage, never of the frozen array. Keep one instance and dispose it in
// finally, including malformed-input exits. Inline small arrays avoid a pool rental per leaf.
internal ref struct ObservationJsonArrayBuilder
{
    const int InlineCapacity = 8;
    [InlineArray(InlineCapacity)]
    struct Items { ObservationValue first; }
    Items items;
    ObservationValue[]? rented;
    int count;

    internal void Add(ObservationValue value)
    {
        if (rented is null && count < InlineCapacity) { items[count++] = value; return; }
        if (rented is null || count == rented.Length)
        {
            var expanded = ArrayPool<ObservationValue>.Shared.Rent(rented is null ? InlineCapacity * 2 : checked(rented.Length * 2));
            if (rented is null)
            {
                for (var index = 0; index < count; index++) expanded[index] = items[index];
                items = default;
            }
            else
            {
                rented.AsSpan(0, count).CopyTo(expanded);
                ArrayPool<ObservationValue>.Shared.Return(rented, clearArray: true);
            }
            rented = expanded;
        }
        rented[count++] = value;
    }

    internal readonly ObservationValue Build()
    {
        if (count == 0) return ObservationValue.FromImmutableArray([]);
        // Copy once to exact retained storage. Pooled storage never escapes into an immutable value.
        ReadOnlySpan<ObservationValue> values = rented is null ? items[..count] : rented.AsSpan(0, count);
        return ObservationValue.FromImmutableArray(ImmutableArray.Create(values));
    }

    public void Dispose()
    {
        if (rented is not null) ArrayPool<ObservationValue>.Shared.Return(rented, clearArray: true);
        this = default;
    }
}
