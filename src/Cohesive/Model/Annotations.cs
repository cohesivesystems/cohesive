using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.Json;
using Cohesive.Model.Serialization;

namespace Cohesive.Model;

/// <summary>
/// Annotation key for extensible metadata.
/// </summary>
[JsonConverter(typeof(AnnotationKeyJsonConverter))]
public readonly record struct AnnotationKey
{
    /// <summary>
    /// Creates an annotation key value.
    /// </summary>
    [JsonConstructor]
    public AnnotationKey(string value)
    {
        Value = Guard.RequireNotNullOrWhiteSpace(value);
    }

    /// <summary>
    /// Raw key text.
    /// </summary>
    public string Value { get; }

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>
/// Annotation value supporting scalar, array, and object JSON-compatible values.
/// </summary>
[JsonConverter(typeof(AnnotationValueJsonConverter))]
public sealed record AnnotationValue
{
    int cachedHash;
    /// <summary>
    /// Creates an annotation value.
    /// </summary>
    internal AnnotationValue(JsonElement value)
    {
        Value = value.Clone();
    }

    /// <summary>Owned, read-only JSON snapshot of the annotation.</summary>
    public JsonElement Value { get; }

    /// <summary>Compares annotation values using structural JSON equality.</summary>
    public bool Equals(AnnotationValue? other) => ReferenceEquals(this, other)
        || other is not null && JsonElement.DeepEquals(Value, other.Value);

    /// <summary>Computes a hash aligned with structural JSON equality, including exact numeric equivalence.</summary>
    public override int GetHashCode()
    {
        var cached = Volatile.Read(ref cachedHash);
        if (cached != 0) return cached;
        HashCode hash = new();
        hash.AddBytes(CanonicalJsonWriter.GetCanonicalSequenceBytes(Value));
        var computed = hash.ToHashCode();
        // Zero is the unprepared sentinel; collapsing it to one preserves equality consistency.
        if (computed == 0) computed = 1;
        var previous = Interlocked.CompareExchange(ref cachedHash, computed, 0);
        return previous != 0 ? previous : computed;
    }

    /// <summary>Creates a string annotation value.</summary>
    public static AnnotationValue FromString(string value) => new(JsonSerializer.SerializeToElement(value));

    /// <summary>Creates a boolean annotation value.</summary>
    public static AnnotationValue FromBool(bool value) => new(JsonSerializer.SerializeToElement(value));

    /// <summary>Creates a numeric annotation value.</summary>
    public static AnnotationValue FromNumber(decimal value) => new(JsonSerializer.SerializeToElement(value));

    /// <summary>Creates an array annotation value.</summary>
    public static AnnotationValue FromArray(IEnumerable<AnnotationValue> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return new(JsonSerializer.SerializeToElement(values.Select(static value => value.Value)));
    }

    /// <summary>Creates an object annotation value.</summary>
    public static AnnotationValue FromObject(IEnumerable<KeyValuePair<AnnotationKey, AnnotationValue>> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        Dictionary<string, JsonElement> properties = new(StringComparer.Ordinal);
        foreach (var (key, value) in values)
            properties[key.Value] = value.Value;
        return new(JsonSerializer.SerializeToElement(properties));
    }

    /// <summary>Creates an owned annotation snapshot by projecting through <see cref="ObservationValue"/>.</summary>
    public static AnnotationValue FromObject<TValue>(TValue value)
    {
        if (value is AnnotationValue annotationValue)
            return annotationValue;
        return new(JsonSerializer.SerializeToElement(ObservationValue.FromObject(value)));
    }

}

/// <summary>
/// Shared helper for annotation maps.
/// </summary>
public static class AnnotationMap
{
    /// <summary>
    /// Normalizes and freezes annotation values.
    /// </summary>
    public static ImmutableDictionary<AnnotationKey, AnnotationValue> Normalize(ImmutableDictionary<AnnotationKey, AnnotationValue>? annotations)
        => annotations ?? [];
    
    /// <summary>Merges annotation sets using later values for duplicate keys.</summary>
    public static ImmutableDictionary<AnnotationKey, AnnotationValue> Merge(params ImmutableDictionary<AnnotationKey, AnnotationValue>[] annotationSets)
    {
        var builder = ImmutableDictionary.CreateBuilder<AnnotationKey, AnnotationValue>();
        foreach (var set in annotationSets)
        {
            foreach (var (key, value) in set)
                builder[key] = value;
        }
        return [..builder];
    }

    /// <summary>Projects nonempty annotation scalars into dotted property and indexed array paths.</summary>
    /// <param name="annotations">The canonical annotation values to traverse.</param>
    /// <param name="comparer">Path identity policy; ordinal comparison is the default.</param>
    /// <returns>An immutable scalar map. Nulls and empty strings are omitted; later traversal entries win path collisions.</returns>
    public static ImmutableDictionary<string, string> FlattenScalars(
        IEnumerable<KeyValuePair<AnnotationKey, AnnotationValue>> annotations, StringComparer? comparer = null)
    {
        ArgumentNullException.ThrowIfNull(annotations);
        var scalars = ImmutableDictionary.CreateBuilder<string, string>(comparer ?? StringComparer.Ordinal);
        foreach (var (key, value) in annotations) Flatten(key.Value, value.Value);
        return scalars.ToImmutable();

        void Flatten(string path, JsonElement value)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            switch (value.ValueKind)
            {
                case JsonValueKind.Null:
                case JsonValueKind.Undefined:
                    return;
                case JsonValueKind.Object:
                    foreach (var property in value.EnumerateObject())
                        if (!string.IsNullOrWhiteSpace(property.Name)) Flatten($"{path}.{property.Name}", property.Value);
                    return;
                case JsonValueKind.Array:
                    var index = 0;
                    foreach (var item in value.EnumerateArray()) Flatten($"{path}[{(index++).ToString(CultureInfo.InvariantCulture)}]", item);
                    return;
                default:
                    var text = value.ValueKind switch
                    {
                        JsonValueKind.String => value.GetString(),
                        JsonValueKind.True => "true",
                        JsonValueKind.False => "false",
                        _ when value.TryGetDecimal(out var number) => number.ToString(CultureInfo.InvariantCulture),
                        _ when value.TryGetInt64(out var integer) => integer.ToString(CultureInfo.InvariantCulture),
                        _ when value.TryGetDouble(out var number) => number.ToString(CultureInfo.InvariantCulture),
                        _ => value.GetRawText()
                    };
                    if (!string.IsNullOrWhiteSpace(text)) scalars[path] = text;
                    return;
            }
        }
    }

    /// <summary>Creates an annotation dictionary containing one value.</summary>
    public static ImmutableDictionary<AnnotationKey, AnnotationValue> Create(string key, AnnotationValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return ImmutableDictionary.CreateRange([(new AnnotationKey(key), value)]);
    }

    /// <summary>Creates an annotation dictionary from a typed value.</summary>
    public static ImmutableDictionary<AnnotationKey, AnnotationValue> Create<TValue>(string key, TValue value) =>
        Create(key, AnnotationValue.FromObject(value));
}
