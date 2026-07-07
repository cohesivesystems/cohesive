using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
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

    public override string ToString() => Value;
}

/// <summary>
/// Annotation value supporting scalar, array, and object JSON-compatible values.
/// </summary>
[JsonConverter(typeof(AnnotationValueJsonConverter))]
public sealed record AnnotationValue
{
    /// <summary>
    /// Creates an annotation value.
    /// </summary>
    internal AnnotationValue(JsonNode? value)
    {
        Value = value?.DeepClone();
        ValidateNode(Value);
    }

    /// <summary>
    /// Raw annotation value.
    /// </summary>
    public JsonNode? Value { get; init; }

    /// <summary>
    /// Compares annotation values using structural JSON equality.
    /// </summary>
    public bool Equals(AnnotationValue? other)
    {
        if (ReferenceEquals(this, other))
            return true;
        if (other is null)
            return false;

        return JsonNode.DeepEquals(Value, other.Value);
    }

    /// <summary>
    /// Computes a hash code aligned with structural JSON equality.
    /// </summary>
    public override int GetHashCode() => GetJsonNodeHashCode(Value);

    /// <summary>
    /// Creates a string annotation value.
    /// </summary>
    public static AnnotationValue FromString(string value) => new(JsonValue.Create(value));

    /// <summary>
    /// Creates a boolean annotation value.
    /// </summary>
    public static AnnotationValue FromBool(bool value) => new(JsonValue.Create(value));

    /// <summary>
    /// Creates a numeric annotation value.
    /// </summary>
    public static AnnotationValue FromNumber(decimal value) => new(JsonValue.Create(value));

    /// <summary>
    /// Creates an array annotation value.
    /// </summary>
    public static AnnotationValue FromArray(IEnumerable<AnnotationValue> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return new(new JsonArray([.. values.Select(x => x.Value?.DeepClone())]));
    }

    /// <summary>
    /// Creates an object annotation value.
    /// </summary>
    public static AnnotationValue FromObject(IEnumerable<KeyValuePair<AnnotationKey, AnnotationValue>> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        JsonObject json = [];
        foreach (var (key, value) in values)
            json[key.Value] = value.Value?.DeepClone();
        return new(json);
    }

    /// <summary>
    /// Creates an annotation value from an arbitrary CLR value by projecting it through <see cref="ObservationValue"/>.
    /// </summary>
    public static AnnotationValue FromObject<TValue>(TValue value)
    {
        if (value is AnnotationValue annotationValue)
            return annotationValue;

        var observed = ObservationValue.FromObject(value);
        return new(JsonSerializer.SerializeToNode(observed));
    }

    static void ValidateNode(JsonNode? node)
    {
        if (node is null)
            return;

        switch (node)
        {
            case JsonValue:
                return;

            case JsonArray array:
                foreach (var item in array)
                    ValidateNode(item);
                return;

            case JsonObject obj:
                foreach (var (_, value) in obj)
                    ValidateNode(value);
                return;
        }

        throw new ArgumentException(message: $"Annotation values support only JSON scalar/array/object nodes; found '{node.GetType().Name}'.");
    }

    static int GetJsonNodeHashCode(JsonNode? node)
    {
        if (node is null)
            return 0;

        switch (node)
        {
            case JsonValue value:
                return StringComparer.Ordinal.GetHashCode(value.ToJsonString());

            case JsonArray array:
            {
                HashCode hash = new();
                hash.Add(array.Count);
                foreach (var item in array)
                    hash.Add(GetJsonNodeHashCode(item));
                return hash.ToHashCode();
            }

            case JsonObject obj:
            {
                unchecked
                {
                    var xor = 0;
                    var sum = 0;
                    var product = 1;
                    foreach (var (propertyName, propertyValue) in obj)
                    {
                        var entryHash = HashCode.Combine(
                            StringComparer.Ordinal.GetHashCode(propertyName),
                            GetJsonNodeHashCode(propertyValue));
                        xor ^= entryHash;
                        sum += entryHash;
                        product *= (entryHash | 1);
                    }

                    return HashCode.Combine(obj.Count, xor, sum, product);
                }
            }

            default:
                return StringComparer.Ordinal.GetHashCode(node.ToJsonString());
        }
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

    public static ImmutableDictionary<AnnotationKey, AnnotationValue> Create(string key, AnnotationValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return ImmutableDictionary.CreateRange([(new AnnotationKey(key), value)]);
    }

    public static ImmutableDictionary<AnnotationKey, AnnotationValue> Create<TValue>(string key, TValue value) =>
        Create(key, AnnotationValue.FromObject(value));
}
