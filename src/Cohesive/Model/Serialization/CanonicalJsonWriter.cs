using System.Buffers;
using System.Buffers.Text;
using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cohesive.Model.Serialization;

/// <summary>Numeric semantics used when canonicalizing untyped JSON number tokens.</summary>
public enum CanonicalJsonNumberSemantics
{
    /// <summary>
    /// Interpret numbers through the portable <see cref="ObservationValue"/> Int64, Decimal, and Double domain.
    /// </summary>
    PortableObservation = 0,

    /// <summary>
    /// Interpret each JSON number as an exact finite base-10 rational without machine-number coercion.
    /// </summary>
    /// <remarks>
    /// Zero has the single spelling <c>0</c>. Nonzero coefficients omit insignificant zeroes, use fixed
    /// notation for adjusted exponents from -6 through 20, and otherwise use lowercase scientific notation
    /// without a leading plus sign.
    /// </remarks>
    ExactDecimalRational = 1
}

/// <summary>Semantic ordering assigned to a canonical JSON array.</summary>
public enum CanonicalJsonArrayOrderingKind
{
    /// <summary>Preserve array item order because the array represents a sequence.</summary>
    Sequence = 0,

    /// <summary>Order unique JSON string items using ordinal comparison.</summary>
    StringSet = 1,

    /// <summary>Order unique JSON object items by an ordinal string property.</summary>
    ObjectSet = 2
}

/// <summary>Describes the semantic ordering of one canonical JSON array.</summary>
public readonly record struct CanonicalJsonArrayOrdering
{
    CanonicalJsonArrayOrdering(
        CanonicalJsonArrayOrderingKind kind,
        string? objectSortProperty)
    {
        Kind = kind;
        ObjectSortProperty = objectSortProperty;
    }

    /// <summary>Gets sequence semantics that retain authored array order.</summary>
    public static CanonicalJsonArrayOrdering Sequence { get; } = default;

    /// <summary>Gets set semantics for unique JSON string items ordered ordinally.</summary>
    public static CanonicalJsonArrayOrdering StringSet { get; } =
        new(CanonicalJsonArrayOrderingKind.StringSet, objectSortProperty: null);

    /// <summary>Gets the semantic ordering kind.</summary>
    public CanonicalJsonArrayOrderingKind Kind { get; }

    /// <summary>
    /// Gets the string property used to order object-set items, or <see langword="null"/> for other kinds.
    /// </summary>
    public string? ObjectSortProperty { get; }

    /// <summary>Creates set semantics for unique JSON objects ordered by a string property.</summary>
    /// <param name="sortProperty">String property present on every object-set item.</param>
    /// <returns>Object-set ordering using <paramref name="sortProperty"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="sortProperty"/> is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="sortProperty"/> is <see langword="null"/>.</exception>
    public static CanonicalJsonArrayOrdering ObjectSet(string sortProperty)
    {
        ArgumentException.ThrowIfNullOrEmpty(sortProperty);
        return new(CanonicalJsonArrayOrderingKind.ObjectSet, sortProperty);
    }
}

/// <summary>Stable structural path identifying an array in a canonical JSON document.</summary>
/// <remarks>
/// <para>
/// Paths are rooted at the empty string. Object properties append a slash-delimited segment and array
/// items append <c>/*</c>, so an array nested on every item of <c>/nodes</c> can be addressed as
/// <c>/nodes/*/parameters</c>. Array indices are intentionally excluded, making classification independent
/// of authored or canonical item order.
/// </para>
/// <para>
/// Property segments escape <c>~</c>, <c>/</c>, and <c>*</c> as <c>~0</c>, <c>~1</c>, and <c>~2</c>,
/// respectively. The root array, when present, has the empty path.
/// </para>
/// </remarks>
public readonly record struct CanonicalJsonArrayPath
{
    readonly string? value;

    internal CanonicalJsonArrayPath(string value)
    {
        this.value = value.Length == 0 ? null : value;
    }

    /// <summary>Gets the stable structural path value.</summary>
    public string Value => value ?? string.Empty;

    /// <summary>Returns the stable structural path value.</summary>
    /// <returns>The path supplied to the array-classification callback.</returns>
    public override string ToString() => Value;
}

/// <summary>
/// Writes canonical UTF-8 JSON for portable Cohesive semantic documents and fingerprint profiles.
/// </summary>
public static class CanonicalJsonWriter
{
    static ReadOnlySpan<byte> ObservationFormatPropertyToken => "{\"format\":"u8;
    static ReadOnlySpan<byte> ObservationGraphIdPropertyToken => ",\"graphId\":"u8;
    static ReadOnlySpan<byte> ObservationShapeIdPropertyToken => ",\"shapeId\":"u8;
    static ReadOnlySpan<byte> ObservationValuePropertyToken => ",\"value\":"u8;

    internal static void WriteCanonicalObservation(
        IBufferWriter<byte> output,
        Observation observation)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(observation);
        var writer = CanonicalObservationJsonWriterPool.Rent(output);
        var completed = false;
        try
        {
            WriteCanonicalObservationEnvelopeStart(writer, observation.ShapeId);
            WriteCanonicalObservationValue(writer, observation.Value);
            writer.WriteEndObject();
            writer.Flush();
            completed = true;
        }
        finally
        {
            if (completed)
                CanonicalObservationJsonWriterPool.Return(writer);
        }
    }

    /// <summary>
    /// Writes a qualified observation directly from exact ordinal-aligned storage as canonical portable UTF-8 JSON.
    /// </summary>
    /// <param name="output">Caller-owned destination that receives the complete canonical representation.</param>
    /// <param name="observation">Validated ordinal reader whose exact layout identifies every readable field.</param>
    /// <remarks>
    /// Top-level fields are emitted using canonical ordinal name order cached by the shared layout. The operation
    /// neither projects the reader to a dictionary-backed <see cref="Observation"/> nor retains destination storage.
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="output"/> or <paramref name="observation"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The reader and layout have different qualified shapes or a retained value has no canonical encoding.
    /// </exception>
    public static void WriteCanonicalObservation(
        IBufferWriter<byte> output,
        IOrdinalObservationFieldReader observation)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(observation);
        var layout = observation.Layout;
        if (layout.ShapeId != observation.ShapeId)
        {
            throw new InvalidOperationException(
                $"Observation reader shape '{observation.ShapeId}' does not match layout shape '{layout.ShapeId}'.");
        }

        var writer = CanonicalObservationJsonWriterPool.Rent(output);
        var completed = false;
        try
        {
            WriteCanonicalObservationEnvelopeStart(writer, observation.ShapeId);
            writer.WriteStartObject();
            foreach (var ordinal in layout.CanonicalJsonOrdinals)
            {
                if (!observation.TryGetField(ordinal, out var value))
                    continue;

                writer.WritePropertyName(layout.GetJsonPropertyName(ordinal));
                WriteCanonicalObservationValue(writer, value);
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.Flush();
            completed = true;
        }
        finally
        {
            if (completed)
                CanonicalObservationJsonWriterPool.Return(writer);
        }
    }

    static void WriteCanonicalObservationEnvelopeStart(
        Utf8JsonWriter writer,
        QualifiedShapeId shapeId)
    {
        writer.WriteStartObject();
        writer.WriteString(GetObservationPropertyName(ObservationFormatPropertyToken), Observation.CanonicalFormat);
        writer.WriteString(GetObservationPropertyName(ObservationGraphIdPropertyToken), shapeId.GraphId.Value);
        writer.WriteString(GetObservationPropertyName(ObservationShapeIdPropertyToken), shapeId.ShapeId.Value);
        writer.WritePropertyName(GetObservationPropertyName(ObservationValuePropertyToken));
    }

    static ReadOnlySpan<byte> GetObservationPropertyName(ReadOnlySpan<byte> token) => token[2..^2];

    internal static void WriteCanonicalObservationStreaming(
        IBufferWriter<byte> output,
        Observation observation)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(observation);
        new CanonicalObservationUtf8Writer(output, ObservationBytesJsonEncoding.Base64String)
            .WriteObservation(observation);
    }

    /// <summary>Writes a JSON node using canonical object and configured set-like collection ordering.</summary>
    /// <param name="node">JSON value to canonicalize.</param>
    /// <param name="options">Serializer options used when writing scalar JSON values.</param>
    /// <param name="getArrayOrdering">
    /// Classifies each array by its stable structural path. Every set-like array must be declared explicitly;
    /// undeclared arrays retain sequence order. Classification never depends on array contents.
    /// </param>
    /// <param name="numberSemantics">Semantics used to normalize untyped JSON number tokens.</param>
    /// <returns>Canonical UTF-8 JSON bytes.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="node"/>, <paramref name="options"/>, or <paramref name="getArrayOrdering"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="numberSemantics"/> is not recognized.</exception>
    /// <exception cref="InvalidOperationException">
    /// A JSON node, set-like collection item, or observation value has no canonical encoding.
    /// </exception>
    /// <exception cref="JsonException">A scalar JSON value cannot be written using <paramref name="options"/>.</exception>
    /// <exception cref="NotSupportedException">
    /// A scalar JSON value uses a runtime type unsupported by <paramref name="options"/>.
    /// </exception>
    public static byte[] GetCanonicalBytes(
        JsonNode node,
        JsonSerializerOptions options,
        Func<CanonicalJsonArrayPath, CanonicalJsonArrayOrdering> getArrayOrdering,
        CanonicalJsonNumberSemantics numberSemantics = CanonicalJsonNumberSemantics.PortableObservation)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(getArrayOrdering);
        if (!Enum.IsDefined(numberSemantics))
        {
            throw new ArgumentOutOfRangeException(
                nameof(numberSemantics),
                numberSemantics,
                "Unsupported canonical JSON number semantics.");
        }

        return GetCanonicalBytesCore(node, options, getArrayOrdering, numberSemantics);
    }

    /// <summary>Writes canonical UTF-8 JSON when every array has sequence semantics.</summary>
    /// <param name="node">JSON value to canonicalize.</param>
    /// <param name="options">Serializer options used when writing scalar JSON values.</param>
    /// <param name="numberSemantics">Semantics used to normalize untyped JSON number tokens.</param>
    /// <returns>Canonical UTF-8 JSON bytes with authored array order preserved.</returns>
    /// <remarks>
    /// This fixed policy avoids constructing structural array paths that no caller can observe. Use
    /// <see cref="GetCanonicalBytes(JsonNode, JsonSerializerOptions, Func{CanonicalJsonArrayPath, CanonicalJsonArrayOrdering}, CanonicalJsonNumberSemantics)"/>
    /// when any array has set semantics or its ordering depends on location.
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="node"/> or <paramref name="options"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="numberSemantics"/> is not recognized.</exception>
    /// <exception cref="InvalidOperationException">A JSON node or observation value has no canonical encoding.</exception>
    /// <exception cref="JsonException">A scalar JSON value cannot be written using <paramref name="options"/>.</exception>
    /// <exception cref="NotSupportedException">
    /// A scalar JSON value uses a runtime type unsupported by <paramref name="options"/>.
    /// </exception>
    public static byte[] GetCanonicalSequenceBytes(
        JsonNode node,
        JsonSerializerOptions options,
        CanonicalJsonNumberSemantics numberSemantics = CanonicalJsonNumberSemantics.PortableObservation)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(options);
        if (!Enum.IsDefined(numberSemantics))
        {
            throw new ArgumentOutOfRangeException(
                nameof(numberSemantics),
                numberSemantics,
                "Unsupported canonical JSON number semantics.");
        }

        return GetCanonicalBytesCore(node, options, getArrayOrdering: null, numberSemantics);
    }

    /// <summary>Canonicalizes immutable JSON using explicit structural array ordering.</summary>
    /// <param name="element">Caller-owned JSON, borrowed only for this operation.</param>
    /// <param name="getArrayOrdering">Classifies arrays using the same escaped structural paths as the node writer.</param>
    /// <param name="numberSemantics">Numeric profile applied to JSON number tokens.</param>
    /// <returns>Owned canonical UTF-8 bytes; the input is neither mutated nor retained.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="getArrayOrdering"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="numberSemantics"/> is unsupported.</exception>
    /// <exception cref="InvalidOperationException">The element or a set item has no canonical encoding.</exception>
    /// <exception cref="ArgumentException">An object contains duplicate properties.</exception>
    internal static byte[] GetCanonicalBytes(JsonElement element,
        Func<CanonicalJsonArrayPath, CanonicalJsonArrayOrdering> getArrayOrdering,
        CanonicalJsonNumberSemantics numberSemantics = CanonicalJsonNumberSemantics.PortableObservation)
    {
        ArgumentNullException.ThrowIfNull(getArrayOrdering);
        if (!Enum.IsDefined(numberSemantics))
            throw new ArgumentOutOfRangeException(nameof(numberSemantics), numberSemantics, "Unsupported canonical JSON number semantics.");
        ArrayBufferWriter<byte> buffer = new();
        using (var writer = CreateElementWriter(buffer))
            WriteCanonical(writer, element, getArrayOrdering, numberSemantics);
        return buffer.WrittenSpan.ToArray();
    }

    // Typed strict documents use the same exact-number sequence profile as execution documents.
    internal static byte[] GetCanonicalSequenceBytes(JsonElement element)
    {
        ArrayBufferWriter<byte> buffer = new();
        using (var writer = CreateElementWriter(buffer))
            WriteCanonicalSequence(writer, element);
        return buffer.WrittenSpan.ToArray();
    }

    static Utf8JsonWriter CreateElementWriter(IBufferWriter<byte> output) => new(output, new JsonWriterOptions
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Indented = false
    });

    internal static void WriteCanonicalSequence(Utf8JsonWriter writer, JsonElement element)
    {
        using PropertyNames names = new();
        WriteCanonicalValue<ElementValue, ElementItems>(writer, new(element), null, string.Empty, CanonicalJsonNumberSemantics.ExactDecimalRational, names);
    }

    // Semantic blocks can stream the same canonical profile into their existing digest writer.
    internal static void WriteCanonical(Utf8JsonWriter writer, JsonElement element,
        Func<CanonicalJsonArrayPath, CanonicalJsonArrayOrdering> getArrayOrdering,
        CanonicalJsonNumberSemantics numberSemantics = CanonicalJsonNumberSemantics.PortableObservation)
    {
        using PropertyNames names = new();
        WriteCanonicalValue<ElementValue, ElementItems>(writer, new(element), getArrayOrdering, string.Empty, numberSemantics, names);
    }

    // Both JSON representations use one structural walk. Adapters own only storage access and
    // typed scalar serialization; ordering, duplicate detection, paths and set policy live here.
    interface ICanonicalValue<T, TItems> where T : struct where TItems : struct, ICanonicalItems<T>
    {
        JsonValueKind Kind { get; }
        int PropertyCount { get; }
        int ArrayCount { get; }
        void CopyProperties(Span<KeyValuePair<string, T>> target, PropertyNames names);
        TItems Items();
        bool TryString(out string text);
        bool TryProperty(string name, out T value);
        void WriteScalar(Utf8JsonWriter writer, CanonicalJsonNumberSemantics semantics);
    }

    interface ICanonicalItems<T> where T : struct
    {
        bool MoveNext();
        T Current { get; }
    }

    static void WriteCanonicalValue<T, TItems>(Utf8JsonWriter writer, T value,
        Func<CanonicalJsonArrayPath, CanonicalJsonArrayOrdering>? orderingPolicy,
        string path, CanonicalJsonNumberSemantics semantics, PropertyNames names)
        where T : struct, ICanonicalValue<T, TItems> where TItems : struct, ICanonicalItems<T>
    {
        if (value.Kind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            var count = value.PropertyCount;
            if (count > 0)
            {
                var properties = ArrayPool<KeyValuePair<string, T>>.Shared.Rent(count);
                try
                {
                    value.CopyProperties(properties.AsSpan(0, count), names);
                    properties.AsSpan(0, count).Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Key, right.Key));
                    for (var index = 0; index < count; index++)
                    {
                        var property = properties[index];
                        if (index > 0 && StringComparer.Ordinal.Equals(properties[index - 1].Key, property.Key))
                            throw new ArgumentException($"Duplicate JSON property '{property.Key}'.");
                        writer.WritePropertyName(property.Key);
                        WriteCanonicalValue<T, TItems>(writer, property.Value, orderingPolicy,
                            orderingPolicy is null ? string.Empty : AppendPropertyPath(path, property.Key), semantics, names);
                    }
                }
                finally
                {
                    properties.AsSpan(0, count).Clear();
                    ArrayPool<KeyValuePair<string, T>>.Shared.Return(properties);
                }
            }
            writer.WriteEndObject();
            return;
        }
        if (value.Kind != JsonValueKind.Array)
        {
            value.WriteScalar(writer, semantics);
            return;
        }
        var ordering = orderingPolicy is null ? CanonicalJsonArrayOrdering.Sequence : orderingPolicy(new(path));
        var itemPath = orderingPolicy is null ? string.Empty : AppendArrayItemPath(path);
        writer.WriteStartArray();
        var items = value.Items();
        if (ordering.Kind == CanonicalJsonArrayOrderingKind.Sequence)
        {
            while (items.MoveNext())
                WriteCanonicalValue<T, TItems>(writer, items.Current, orderingPolicy, itemPath, semantics, names);
        }
        else
        {
            var property = ordering.Kind switch
            {
                CanonicalJsonArrayOrderingKind.StringSet => null,
                CanonicalJsonArrayOrderingKind.ObjectSet => ordering.ObjectSortProperty
                    ?? throw new InvalidOperationException("Object-set ordering requires a sort property."),
                _ => throw new InvalidOperationException($"Unsupported canonical JSON array ordering '{ordering.Kind}' at '{path}'.")
            };
            var count = value.ArrayCount;
            var ordered = ArrayPool<KeyValuePair<string, T>>.Shared.Rent(count);
            try
            {
                HashSet<string> seen = new(StringComparer.Ordinal);
                var index = 0;
                while (items.MoveNext())
                {
                    var item = items.Current;
                    string key;
                    if (property is null)
                    {
                        if (!item.TryString(out key)) throw StringSetItemError(path);
                    }
                    else if (item.Kind != JsonValueKind.Object || !item.TryProperty(property, out var sortValue)
                        || !sortValue.TryString(out key)) throw ObjectSetItemError(path, property);
                    ValidateSetKey(seen, key, path, property);
                    ordered[index++] = new(key, item);
                }
                ordered.AsSpan(0, count).Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Key, right.Key));
                for (index = 0; index < count; index++)
                    WriteCanonicalValue<T, TItems>(writer, ordered[index].Value, orderingPolicy, itemPath, semantics, names);
            }
            finally
            {
                ordered.AsSpan(0, count).Clear();
                ArrayPool<KeyValuePair<string, T>>.Shared.Return(ordered);
            }
        }
        writer.WriteEndArray();
    }

    readonly struct ElementValue(JsonElement element) : ICanonicalValue<ElementValue, ElementItems>
    {
        public JsonValueKind Kind => element.ValueKind;
        public int PropertyCount => element.GetPropertyCount();
        public int ArrayCount => element.GetArrayLength();
        public ElementItems Items() => new(element.EnumerateArray());
        public void CopyProperties(Span<KeyValuePair<string, ElementValue>> target, PropertyNames names)
        {
            var index = 0;
            foreach (var property in element.EnumerateObject())
            {
                var name = target.Length > PropertyNames.Capacity ? property.Name : names.Get(property, target.Length, index);
                target[index++] = new(name, new(property.Value));
            }
        }
        public bool TryString(out string text)
        {
            text = element.ValueKind == JsonValueKind.String ? element.GetString()! : null!;
            return text is not null;
        }
        public bool TryProperty(string name, out ElementValue value)
        {
            var found = element.TryGetProperty(name, out var child);
            value = new(child);
            return found;
        }
        public void WriteScalar(Utf8JsonWriter writer, CanonicalJsonNumberSemantics semantics)
        {
            if (element.ValueKind == JsonValueKind.Undefined)
                throw new InvalidOperationException("Undefined JSON has no canonical encoding.");
            if (element.ValueKind == JsonValueKind.Number)
                WriteNumber(writer, element, semantics);
            else element.WriteTo(writer);
        }
    }

    struct ElementItems(JsonElement.ArrayEnumerator items) : ICanonicalItems<ElementValue>
    {
        public bool MoveNext() => items.MoveNext();
        public ElementValue Current => new(items.Current);
    }

    readonly struct NodeValue(JsonNode? node, JsonSerializerOptions options) : ICanonicalValue<NodeValue, NodeItems>
    {
        // Customized JsonValues stay scalar: their serializer/ObservationValue contract owns
        // conversion, matching the established node API rather than interpreting a second tree.
        public JsonValueKind Kind => node is JsonObject ? JsonValueKind.Object : node is JsonArray ? JsonValueKind.Array : JsonValueKind.Null;
        public int PropertyCount => ((JsonObject)node!).Count;
        public int ArrayCount => ((JsonArray)node!).Count;
        public NodeItems Items() => new((JsonArray)node!, options);
        public void CopyProperties(Span<KeyValuePair<string, NodeValue>> target, PropertyNames names)
        {
            var index = 0;
            foreach (var property in (JsonObject)node!) target[index++] = new(property.Key, new(property.Value, options));
        }
        public bool TryString(out string text)
        {
            text = null!;
            return node is JsonValue value && value.TryGetValue(out text!);
        }
        public bool TryProperty(string name, out NodeValue value)
        {
            var found = ((JsonObject)node!).TryGetPropertyValue(name, out var child);
            value = new(child, options);
            return found;
        }
        public void WriteScalar(Utf8JsonWriter writer, CanonicalJsonNumberSemantics semantics)
        {
            if (node is null) { writer.WriteNullValue(); return; }
            var value = (JsonValue)node;
            if (value.TryGetValue<ObservationValue>(out var observation))
                WriteCanonicalObservationValue(writer, observation);
            else if (value.TryGetValue<JsonElement>(out var element) && element.ValueKind == JsonValueKind.Number)
                WriteNumber(writer, element, semantics);
            else if (semantics == CanonicalJsonNumberSemantics.ExactDecimalRational && value.GetValueKind() == JsonValueKind.Number)
                WriteExactDecimalRational(writer, value.ToJsonString(options));
            else if (value.TryGetValue<double>(out var number) && BitConverter.DoubleToInt64Bits(number) == long.MinValue)
                writer.WriteNumberValue(0);
            else value.WriteTo(writer, options);
        }
    }

    struct NodeItems(JsonArray array, JsonSerializerOptions options) : ICanonicalItems<NodeValue>
    {
        int index = -1;
        public bool MoveNext() => ++index < array.Count;
        public NodeValue Current => new(array[index], options);
    }

    static void WriteNumber(Utf8JsonWriter writer, JsonElement element, CanonicalJsonNumberSemantics semantics)
    {
        if (semantics == CanonicalJsonNumberSemantics.ExactDecimalRational) WriteExactDecimalRational(writer, element);
        else WriteCanonicalObservationValue(writer, ObservationValue.FromJsonElement(element));
    }

    // Advisory reuse for repeated small-object layouts. One exact comparison protects every hit;
    // collisions only replace a candidate. Wide objects avoid probing and evicting reusable names.
    // Storage belongs to this write and is cleared on both success and failure before pool return.
    readonly struct PropertyNames : IDisposable
    {
        internal const int Capacity = 32;
        readonly string?[] names;
        public PropertyNames() => names = ArrayPool<string?>.Shared.Rent(Capacity);
        public void Dispose() => ArrayPool<string?>.Shared.Return(names, clearArray: true);

        internal string Get(JsonProperty property, int count, int index)
        {
            var slot = (count * 3 + index) & (Capacity - 1);
            var candidate = names[slot];
            if (candidate is not null && property.NameEquals(candidate))
                return candidate;
            return names[slot] = property.Name;
        }
    }

    static InvalidOperationException ObjectSetItemError(string path, string propertyName) =>
        new($"Every item in canonical object-set array '{path}' must contain string property '{propertyName}'.");

    static InvalidOperationException StringSetItemError(string path) =>
        new($"Canonical string-set array '{path}' can contain only JSON string values.");

    static void ValidateSetKey(HashSet<string> seen, string key, string path, string? property)
    {
        if (!seen.Add(key))
            throw new InvalidOperationException(property is null
                ? $"Canonical string-set array '{path}' repeats value '{key}'."
                : $"Canonical object-set array '{path}' repeats sort value '{key}' for property '{property}'.");
    }

    static byte[] GetCanonicalBytesCore(
        JsonNode node,
        JsonSerializerOptions options,
        Func<CanonicalJsonArrayPath, CanonicalJsonArrayOrdering>? getArrayOrdering,
        CanonicalJsonNumberSemantics numberSemantics)
    {
        ArrayBufferWriter<byte> buffer = new();
        using (Utf8JsonWriter writer = new(buffer, new JsonWriterOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Indented = false
        }))
        {
            using PropertyNames names = new();
            WriteCanonicalValue<NodeValue, NodeItems>(writer, new(node, options), getArrayOrdering,
                string.Empty, numberSemantics, names);
        }

        return buffer.WrittenSpan.ToArray();
    }

    static string AppendPropertyPath(string path, string propertyName) =>
        string.Concat(path, "/", EscapePathSegment(propertyName));

    static string AppendArrayItemPath(string path) => string.Concat(path, "/*");

    static string EscapePathSegment(string value) =>
        value
            .Replace("~", "~0", StringComparison.Ordinal)
            .Replace("/", "~1", StringComparison.Ordinal)
            .Replace("*", "~2", StringComparison.Ordinal);

    static void WriteExactDecimalRational(Utf8JsonWriter writer, JsonElement element)
    {
        // Every Int64 fits the profile's fixed-notation range. Preserve the exact fallback for
        // fractional/exponential tokens and integers outside Int64, without coercion or rounding.
        if (element.TryGetInt64(out var integer))
            writer.WriteNumberValue(integer);
        else
            WriteExactDecimalRational(writer, element.GetRawText());
    }

    static void WriteExactDecimalRational(Utf8JsonWriter writer, ReadOnlySpan<char> text)
    {
        var canonical = GetExactDecimalRationalText(text);
        writer.WriteRawValue(canonical, skipInputValidation: true);
    }

    static string GetExactDecimalRationalText(ReadOnlySpan<char> text)
    {
        var negative = text[0] == '-';
        var mantissaStart = negative ? 1 : 0;
        var exponentMarker = text[mantissaStart..].IndexOfAny('e', 'E');
        var mantissaEnd = exponentMarker < 0
            ? text.Length
            : mantissaStart + exponentMarker;

        var exponent = BigInteger.Zero;
        if (mantissaEnd < text.Length)
        {
            if (!BigInteger.TryParse(
                    text[(mantissaEnd + 1)..],
                    NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture,
                    out exponent))
            {
                throw new InvalidOperationException($"JSON number '{text.ToString()}' has an invalid exponent.");
            }
        }

        var decimalPoint = text[mantissaStart..mantissaEnd].IndexOf('.');
        if (decimalPoint >= 0)
            decimalPoint += mantissaStart;
        var fractionalDigits = decimalPoint < 0 ? 0 : mantissaEnd - decimalPoint - 1;

        var coefficientBuffer = new char[mantissaEnd - mantissaStart];
        var coefficientLength = 0;
        for (var index = mantissaStart; index < mantissaEnd; index++)
        {
            var character = text[index];
            if (character == '.')
                continue;
            if (character is not (>= '0' and <= '9'))
            {
                throw new InvalidOperationException($"JSON number '{text.ToString()}' has an invalid coefficient.");
            }

            coefficientBuffer[coefficientLength++] = character;
        }

        var firstSignificant = 0;
        while (firstSignificant < coefficientLength && coefficientBuffer[firstSignificant] == '0')
            firstSignificant++;
        if (firstSignificant == coefficientLength)
            return "0";

        var lastSignificant = coefficientLength - 1;
        while (coefficientBuffer[lastSignificant] == '0')
            lastSignificant--;

        var removedTrailingZeros = coefficientLength - lastSignificant - 1;
        exponent -= fractionalDigits;
        exponent += removedTrailingZeros;
        var digits = new string(
            coefficientBuffer,
            firstSignificant,
            lastSignificant - firstSignificant + 1);
        var scientificExponent = exponent + digits.Length - 1;
        var signLength = negative ? 1 : 0;

        if (scientificExponent >= -6 && scientificExponent < 21)
        {
            var decimalPosition = checked((int)scientificExponent + 1);
            if (decimalPosition <= 0)
            {
                StringBuilder fixedValue = new(signLength + 2 - decimalPosition + digits.Length);
                if (negative)
                    fixedValue.Append('-');
                fixedValue.Append("0.");
                fixedValue.Append('0', -decimalPosition);
                fixedValue.Append(digits);
                return fixedValue.ToString();
            }

            if (decimalPosition >= digits.Length)
            {
                StringBuilder fixedValue = new(signLength + decimalPosition);
                if (negative)
                    fixedValue.Append('-');
                fixedValue.Append(digits);
                fixedValue.Append('0', decimalPosition - digits.Length);
                return fixedValue.ToString();
            }

            StringBuilder fixedValueWithFraction = new(signLength + digits.Length + 1);
            if (negative)
                fixedValueWithFraction.Append('-');
            fixedValueWithFraction.Append(digits.AsSpan(0, decimalPosition));
            fixedValueWithFraction.Append('.');
            fixedValueWithFraction.Append(digits.AsSpan(decimalPosition));
            return fixedValueWithFraction.ToString();
        }

        StringBuilder scientificValue = new(signLength + digits.Length + 24);
        if (negative)
            scientificValue.Append('-');
        scientificValue.Append(digits[0]);
        if (digits.Length > 1)
        {
            scientificValue.Append('.');
            scientificValue.Append(digits.AsSpan(1));
        }
        scientificValue.Append('e');
        scientificValue.Append(scientificExponent.ToString(CultureInfo.InvariantCulture));
        return scientificValue.ToString();
    }

    /// <summary>Writes one observation value using canonical portable JSON scalar, object, and array semantics.</summary>
    /// <param name="writer">Destination writer that owns JSON framing and output buffering.</param>
    /// <param name="value">Observation value to encode.</param>
    /// <param name="bytesEncoding">Canonical representation permitted for binary values.</param>
    /// <exception cref="ArgumentNullException"><paramref name="writer"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// A value is non-finite, binary values are forbidden by <paramref name="bytesEncoding"/>, or a value kind has
    /// no canonical portable JSON representation.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="bytesEncoding"/> is unsupported.</exception>
    public static void WriteCanonicalObservationValue(
        Utf8JsonWriter writer,
        ObservationValue value,
        ObservationBytesJsonEncoding bytesEncoding = ObservationBytesJsonEncoding.Base64String)
    {
        ArgumentNullException.ThrowIfNull(writer);
        switch (value.Kind)
        {
            case ObservationValueKind.Undefined:
            case ObservationValueKind.Null:
                writer.WriteNullValue();
                return;
            case ObservationValueKind.Int64:
                writer.WriteNumberValue(value.Int64);
                return;
            case ObservationValueKind.Double:
                var normalizedDouble = value.Double == 0d ? 0d : value.Double;
                if (!double.IsFinite(normalizedDouble))
                {
                    throw new InvalidOperationException(
                        "A non-finite Double has no canonical portable JSON encoding.");
                }
                if (Math.TryGetCanonicalDecimalFromDouble(normalizedDouble, out var exactDecimal))
                {
                    WriteCanonicalDecimal(writer, exactDecimal);
                }
                else
                {
                    writer.WriteNumberValue(normalizedDouble);
                }
                return;
            case ObservationValueKind.Decimal:
                WriteCanonicalDecimal(writer, value.Decimal);
                return;
            case ObservationValueKind.Bool:
                writer.WriteBooleanValue(value.Bool);
                return;
            case ObservationValueKind.String:
                writer.WriteStringValue(value.String);
                return;
            case ObservationValueKind.Bytes:
                switch (bytesEncoding)
                {
                    case ObservationBytesJsonEncoding.Throw:
                        throw new InvalidOperationException(
                            "ObservationValue bytes cannot be encoded as JSON with the current policy.");
                    case ObservationBytesJsonEncoding.Base64String:
                        writer.WriteBase64StringValue(value.Bytes.Span);
                        return;
                    default:
                        throw new ArgumentOutOfRangeException(
                            nameof(bytesEncoding),
                            bytesEncoding,
                            "Unsupported observation bytes JSON encoding.");
                }
            case ObservationValueKind.DateTimeOffset:
            case ObservationValueKind.DateOnly:
            case ObservationValueKind.TimeOnly:
            case ObservationValueKind.TimeSpan:
                writer.WriteStringValue(value.String);
                return;
            case ObservationValueKind.Object:
                writer.WriteStartObject();
                using (var fields = new OrderedObservationFields(value.Fields))
                {
                    foreach (var (property, child) in fields)
                    {
                        writer.WritePropertyName(property);
                        WriteCanonicalObservationValue(writer, child, bytesEncoding);
                    }
                }
                writer.WriteEndObject();
                return;
            case ObservationValueKind.Array:
                writer.WriteStartArray();
                if (!value.Array.IsDefault)
                {
                    foreach (var item in value.Array)
                        WriteCanonicalObservationValue(writer, item, bytesEncoding);
                }
                writer.WriteEndArray();
                return;
            default:
                throw new InvalidOperationException(
                    $"Observation value kind '{value.Kind}' does not have a canonical portable JSON encoding.");
        }
    }

    static void WriteCanonicalDecimal(Utf8JsonWriter writer, decimal value)
    {
        Span<char> formatted = stackalloc char[32];
        if (!value.TryFormat(
                formatted,
                out var written,
                "G29",
                CultureInfo.InvariantCulture))
        {
            throw new InvalidOperationException("A Decimal value could not be canonically formatted.");
        }

        writer.WriteRawValue(formatted[..written], skipInputValidation: true);
    }

    /// <summary>Streams one observation value as canonical portable UTF-8 JSON without token-sized buffering.</summary>
    /// <param name="output">Destination that receives bounded chunks of canonical UTF-8 JSON.</param>
    /// <param name="value">Observation value to encode.</param>
    /// <param name="bytesEncoding">Canonical representation permitted for binary values.</param>
    /// <remarks>
    /// This overload preserves the same scalar, escaping, property-ordering, and collection semantics as the
    /// <see cref="Utf8JsonWriter"/> overload while bounding each request to <paramref name="output"/>. It is intended
    /// for hashing and counting representations that can be much larger than one contiguous buffer.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="output"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// A value is non-finite, binary values are forbidden by <paramref name="bytesEncoding"/>, or a value kind has
    /// no canonical portable JSON representation.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="bytesEncoding"/> is unsupported.</exception>
    public static void WriteCanonicalObservationValue(
        IBufferWriter<byte> output,
        ObservationValue value,
        ObservationBytesJsonEncoding bytesEncoding = ObservationBytesJsonEncoding.Base64String)
        => WriteCanonicalObservationValue(output, value, bytesEncoding, enclosingDepth: 0);

    /// <summary>
    /// Streams one observation value as a canonical portable UTF-8 JSON fragment within an existing JSON container
    /// depth.
    /// </summary>
    /// <param name="output">Destination that receives bounded chunks of canonical UTF-8 JSON.</param>
    /// <param name="value">Observation value to encode.</param>
    /// <param name="bytesEncoding">Canonical representation permitted for binary values.</param>
    /// <param name="enclosingDepth">
    /// Number of open JSON objects or arrays surrounding <paramref name="value"/>. The default canonical JSON writer
    /// permits at most 1,000 simultaneously open containers.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="output"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="bytesEncoding"/> is unsupported, or <paramref name="enclosingDepth"/> is outside the canonical
    /// writer's supported range.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The value exceeds the canonical JSON depth limit, is non-finite, contains forbidden binary data, or has no
    /// canonical portable JSON representation.
    /// </exception>
    public static void WriteCanonicalObservationValue(
        IBufferWriter<byte> output,
        ObservationValue value,
        ObservationBytesJsonEncoding bytesEncoding,
        int enclosingDepth)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (enclosingDepth is < 0 or > CanonicalObservationUtf8Writer.MaximumDepth)
        {
            throw new ArgumentOutOfRangeException(
                nameof(enclosingDepth),
                enclosingDepth,
                $"A canonical JSON enclosing depth must be from 0 through {CanonicalObservationUtf8Writer.MaximumDepth}.");
        }
        new CanonicalObservationUtf8Writer(output, bytesEncoding).Write(value, enclosingDepth);
    }

    static class CanonicalObservationJsonWriterPool
    {
        static readonly JsonWriterOptions Options = new()
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Indented = false
        };

        [ThreadStatic]
        static Utf8JsonWriter? cachedWriter;

        internal static Utf8JsonWriter Rent(IBufferWriter<byte> output)
        {
            var writer = cachedWriter;
            cachedWriter = null;
            if (writer is null)
                return new(output, Options);

            writer.Reset(output);
            return writer;
        }

        internal static void Return(Utf8JsonWriter writer)
        {
            writer.Reset(DetachedBufferWriter.Instance);
            if (cachedWriter is null)
            {
                cachedWriter = writer;
                return;
            }

            writer.Dispose();
        }
    }

    sealed class DetachedBufferWriter : IBufferWriter<byte>
    {
        internal static DetachedBufferWriter Instance { get; } = new();

        public void Advance(int count) => throw new InvalidOperationException("A detached JSON writer cannot advance output.");

        public Memory<byte> GetMemory(int sizeHint = 0) =>
            throw new InvalidOperationException("A detached JSON writer cannot request output.");

        public Span<byte> GetSpan(int sizeHint = 0) =>
            throw new InvalidOperationException("A detached JSON writer cannot request output.");
    }

    readonly struct CanonicalObservationUtf8Writer(
        IBufferWriter<byte> output,
        ObservationBytesJsonEncoding bytesEncoding)
    {
        const int MaximumChunkBytes = 4 * 1024;
        internal const int MaximumDepth = 1_000;
        static readonly JavaScriptEncoder Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;

        internal void WriteObservation(Observation observation)
        {
            WriteRaw(ObservationFormatPropertyToken);
            WriteString(Observation.CanonicalFormat);
            WriteRaw(ObservationGraphIdPropertyToken);
            WriteString(observation.ShapeId.GraphId.Value);
            WriteRaw(ObservationShapeIdPropertyToken);
            WriteString(observation.ShapeId.ShapeId.Value);
            WriteRaw(ObservationValuePropertyToken);
            Write(observation.Value, enclosingDepth: 1);
            WriteRaw("}"u8);
        }

        internal void Write(ObservationValue value, int enclosingDepth)
        {
            if (value.Kind is not (ObservationValueKind.Object or ObservationValueKind.Array))
            {
                WriteScalar(value);
                return;
            }

            ContainerFrame[]? containers = null;
            var containerCount = 0;
            var current = value;
            var currentDepth = enclosingDepth;
            try
            {
                while (true)
                {
                    var descended = false;
                    switch (current.Kind)
                    {
                        case ObservationValueKind.Object:
                            {
                                RequireContainerDepth(currentDepth);
                                WriteRaw("{"u8);
                                var frame = ContainerFrame.ForObject(current.Fields, checked(currentDepth + 1));
                                if (frame.TryMoveNext(out var property, out var child))
                                {
                                    Push(ref containers, ref containerCount, frame);
                                    WriteString(property!);
                                    WriteRaw(":"u8);
                                    current = child;
                                    currentDepth = frame.ChildDepth;
                                    descended = true;
                                }
                                else
                                {
                                    frame.Dispose();
                                    WriteRaw("}"u8);
                                }
                                break;
                            }
                        case ObservationValueKind.Array:
                            {
                                RequireContainerDepth(currentDepth);
                                WriteRaw("["u8);
                                var frame = ContainerFrame.ForArray(current.Array, checked(currentDepth + 1));
                                if (frame.TryMoveNext(out _, out var child))
                                {
                                    Push(ref containers, ref containerCount, frame);
                                    current = child;
                                    currentDepth = frame.ChildDepth;
                                    descended = true;
                                }
                                else
                                {
                                    WriteRaw("]"u8);
                                }
                                break;
                            }
                        default:
                            WriteScalar(current);
                            break;
                    }

                    if (descended)
                    {
                        continue;
                    }

                    while (containerCount > 0)
                    {
                        ref var parent = ref containers![containerCount - 1];
                        if (parent.TryMoveNext(out var property, out var child))
                        {
                            WriteRaw(","u8);
                            if (parent.IsObject)
                            {
                                WriteString(property!);
                                WriteRaw(":"u8);
                            }
                            current = child;
                            currentDepth = parent.ChildDepth;
                            descended = true;
                            break;
                        }

                        var isObject = parent.IsObject;
                        parent.Dispose();
                        parent = default;
                        containerCount--;
                        WriteRaw(isObject ? "}"u8 : "]"u8);
                    }

                    if (!descended)
                    {
                        return;
                    }
                }
            }
            finally
            {
                if (containers is not null)
                {
                    for (var index = 0; index < containerCount; index++)
                    {
                        containers[index].Dispose();
                        containers[index] = default;
                    }
                    ArrayPool<ContainerFrame>.Shared.Return(containers);
                }
            }
        }

        void WriteScalar(ObservationValue value)
        {
            switch (value.Kind)
            {
                case ObservationValueKind.Undefined:
                case ObservationValueKind.Null:
                    WriteRaw("null"u8);
                    return;
                case ObservationValueKind.Int64:
                    {
                        Span<byte> formatted = stackalloc byte[32];
                        if (!Utf8Formatter.TryFormat(value.Int64, formatted, out var written))
                            throw new InvalidOperationException("An Int64 value could not be canonically formatted.");
                        WriteRaw(formatted[..written]);
                        return;
                    }
                case ObservationValueKind.Double:
                    {
                        var normalized = value.Double == 0d ? 0d : value.Double;
                        if (!double.IsFinite(normalized))
                        {
                            throw new InvalidOperationException(
                                "A non-finite Double has no canonical portable JSON encoding.");
                        }
                        if (Math.TryGetCanonicalDecimalFromDouble(normalized, out var exactDecimal))
                        {
                            WriteDecimal(exactDecimal);
                            return;
                        }

                        Span<byte> formatted = stackalloc byte[32];
                        if (!Utf8Formatter.TryFormat(normalized, formatted, out var written))
                            throw new InvalidOperationException("A Double value could not be canonically formatted.");
                        WriteRaw(formatted[..written]);
                        return;
                    }
                case ObservationValueKind.Decimal:
                    WriteDecimal(value.Decimal);
                    return;
                case ObservationValueKind.Bool:
                    WriteRaw(value.Bool ? "true"u8 : "false"u8);
                    return;
                case ObservationValueKind.String:
                case ObservationValueKind.DateTimeOffset:
                case ObservationValueKind.DateOnly:
                case ObservationValueKind.TimeOnly:
                case ObservationValueKind.TimeSpan:
                    WriteString(value.String
                        ?? throw new InvalidOperationException(
                            $"Observation value kind '{value.Kind}' has no retained string representation."));
                    return;
                case ObservationValueKind.Bytes:
                    WriteBytes(value.Bytes.Span);
                    return;
                default:
                    throw new InvalidOperationException(
                        $"Observation value kind '{value.Kind}' does not have a canonical portable JSON scalar encoding.");
            }
        }

        struct ContainerFrame
        {
            ImmutableArray<ObservationValue> items;
            OrderedObservationFields properties;
            OrderedObservationFields.Enumerator propertyEnumerator;
            int nextItemIndex;

            internal bool IsObject { get; private init; }
            internal int ChildDepth { get; private init; }

            internal static ContainerFrame ForArray(ImmutableArray<ObservationValue> items, int childDepth) =>
                new() { IsObject = false, ChildDepth = childDepth, items = items.IsDefault ? [] : items };

            internal static ContainerFrame ForObject(
                IReadOnlyDictionary<string, ObservationValue>? fields, int childDepth)
            {
                var properties = new OrderedObservationFields(fields);
                try
                {
                    return new() { IsObject = true, ChildDepth = childDepth,
                        properties = properties, propertyEnumerator = properties.GetEnumerator() };
                }
                catch { properties.Dispose(); throw; }
            }

            internal bool TryMoveNext(out string? property, out ObservationValue value)
            {
                property = null;
                value = default;
                if (IsObject)
                {
                    if (!propertyEnumerator.MoveNext()) return false;
                    var current = propertyEnumerator.Current;
                    property = current.Key;
                    value = current.Value;
                    return true;
                }
                if (nextItemIndex >= items.Length) return false;
                value = items[nextItemIndex++];
                return true;
            }

            internal void Dispose()
            {
                propertyEnumerator.Dispose();
                properties.Dispose();
                this = default;
            }
        }

        static void Push(
            ref ContainerFrame[]? containers,
            ref int containerCount,
            ContainerFrame frame)
        {
            try
            {
                containers ??= ArrayPool<ContainerFrame>.Shared.Rent(minimumLength: 8);
                if (containerCount == containers.Length)
                {
                    var replacement = ArrayPool<ContainerFrame>.Shared.Rent(checked(containerCount * 2));
                    containers.AsSpan(0, containerCount).CopyTo(replacement);
                    containers.AsSpan(0, containerCount).Clear();
                    ArrayPool<ContainerFrame>.Shared.Return(containers);
                    containers = replacement;
                }

                containers[containerCount++] = frame;
            }
            catch
            {
                frame.Dispose();
                throw;
            }
        }

        static void RequireContainerDepth(int enclosingDepth)
        {
            if (enclosingDepth >= MaximumDepth)
            {
                throw new InvalidOperationException(
                    $"The observation value exceeds the canonical JSON maximum depth of {MaximumDepth}.");
            }
        }

        void WriteDecimal(decimal value)
        {
            Span<char> formatted = stackalloc char[32];
            if (!value.TryFormat(
                    formatted,
                    out var written,
                    "G29",
                    CultureInfo.InvariantCulture))
            {
                throw new InvalidOperationException("A Decimal value could not be canonically formatted.");
            }
            WriteAscii(formatted[..written]);
        }

        void WriteString(string value)
        {
            WriteRaw("\""u8);
            var remaining = value.AsSpan();
            Span<char> encoded = stackalloc char[1024];
            do
            {
                var status = Encoder.Encode(
                    remaining,
                    encoded,
                    out var consumed,
                    out var written,
                    isFinalBlock: true);
                WriteUtf8(encoded[..written]);
                remaining = remaining[consumed..];
                if (status == OperationStatus.Done)
                    break;
                if (status != OperationStatus.DestinationTooSmall || consumed == 0 && written == 0)
                {
                    throw new InvalidOperationException(
                        "A string value could not be canonically JSON-escaped.");
                }
            }
            while (true);
            WriteRaw("\""u8);
        }

        void WriteBytes(ReadOnlySpan<byte> value)
        {
            if (bytesEncoding == ObservationBytesJsonEncoding.Throw)
            {
                throw new InvalidOperationException(
                    "ObservationValue bytes cannot be encoded as JSON with the current policy.");
            }
            if (bytesEncoding != ObservationBytesJsonEncoding.Base64String)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(bytesEncoding),
                    bytesEncoding,
                    "Unsupported observation bytes JSON encoding.");
            }

            WriteRaw("\""u8);
            Span<byte> encoded = stackalloc byte[MaximumChunkBytes];
            do
            {
                var status = Base64.EncodeToUtf8(
                    value,
                    encoded,
                    out var consumed,
                    out var written,
                    isFinalBlock: true);
                WriteRaw(encoded[..written]);
                value = value[consumed..];
                if (status == OperationStatus.Done)
                    break;
                if (status != OperationStatus.DestinationTooSmall || consumed == 0 && written == 0)
                    throw new InvalidOperationException("A binary value could not be canonically Base64-encoded.");
            }
            while (true);
            WriteRaw("\""u8);
        }

        void WriteAscii(ReadOnlySpan<char> value)
        {
            Span<byte> encoded = stackalloc byte[64];
            if (value.Length > encoded.Length)
                throw new InvalidOperationException("A canonical numeric token exceeded its bounded representation.");
            for (var index = 0; index < value.Length; index++)
                encoded[index] = checked((byte)value[index]);
            WriteRaw(encoded[..value.Length]);
        }

        void WriteUtf8(ReadOnlySpan<char> value)
        {
            Span<byte> encoded = stackalloc byte[MaximumChunkBytes];
            var written = Encoding.UTF8.GetBytes(value, encoded);
            WriteRaw(encoded[..written]);
        }

        void WriteRaw(ReadOnlySpan<byte> value)
        {
            while (!value.IsEmpty)
            {
                var requested = Math.Min(value.Length, MaximumChunkBytes);
                var destination = output.GetSpan(requested);
                if (destination.Length < requested)
                {
                    throw new InvalidOperationException(
                        "The canonical JSON destination returned a buffer smaller than requested.");
                }
                value[..requested].CopyTo(destination);
                output.Advance(requested);
                value = value[requested..];
            }
        }
    }
}
