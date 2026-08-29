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
            writer.WriteStartObject();
            writer.WriteString(GetObservationPropertyName(ObservationFormatPropertyToken), Observation.CanonicalFormat);
            writer.WriteString(
                GetObservationPropertyName(ObservationGraphIdPropertyToken),
                observation.ShapeId.GraphId.Value);
            writer.WriteString(
                GetObservationPropertyName(ObservationShapeIdPropertyToken),
                observation.ShapeId.ShapeId.Value);
            writer.WritePropertyName(GetObservationPropertyName(ObservationValuePropertyToken));
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

        ArrayBufferWriter<byte> buffer = new();
        using (Utf8JsonWriter writer = new(buffer, new JsonWriterOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Indented = false
        }))
        {
            WriteCanonical(
                writer,
                node,
                options,
                getArrayOrdering,
                path: string.Empty,
                numberSemantics);
        }

        return buffer.WrittenSpan.ToArray();
    }

    static void WriteCanonical(
        Utf8JsonWriter writer,
        JsonNode? node,
        JsonSerializerOptions options,
        Func<CanonicalJsonArrayPath, CanonicalJsonArrayOrdering> getArrayOrdering,
        string path,
        CanonicalJsonNumberSemantics numberSemantics)
    {
        switch (node)
        {
            case null:
                writer.WriteNullValue();
                return;
            case JsonObject obj:
                writer.WriteStartObject();
                foreach (var property in obj.OrderBy(static property => property.Key, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Key);
                    WriteCanonical(
                        writer,
                        property.Value,
                        options,
                        getArrayOrdering,
                        AppendPropertyPath(path, property.Key),
                        numberSemantics);
                }
                writer.WriteEndObject();
                return;
            case JsonArray array:
                WriteCanonicalArray(
                    writer,
                    array,
                    options,
                    getArrayOrdering,
                    path,
                    numberSemantics);
                return;
            case JsonValue value:
                if (value.TryGetValue<ObservationValue>(out var observationValue))
                {
                    WriteCanonicalObservationValue(writer, observationValue);
                }
                else if (value.TryGetValue<JsonElement>(out var element)
                         && element.ValueKind == JsonValueKind.Number)
                {
                    if (numberSemantics == CanonicalJsonNumberSemantics.ExactDecimalRational)
                        WriteExactDecimalRational(writer, element.GetRawText());
                    else
                        WriteCanonicalObservationValue(writer, ObservationValue.FromJsonElement(element));
                }
                else if (numberSemantics == CanonicalJsonNumberSemantics.ExactDecimalRational
                         && value.GetValueKind() == JsonValueKind.Number)
                {
                    WriteExactDecimalRational(writer, value.ToJsonString(options));
                }
                else if (value.TryGetValue<double>(out var number)
                         && BitConverter.DoubleToInt64Bits(number) == long.MinValue)
                {
                    writer.WriteNumberValue(0);
                }
                else
                {
                    value.WriteTo(writer, options);
                }
                return;
            default:
                throw new InvalidOperationException(
                    $"Unsupported JSON node '{node.GetType().Name}' during canonicalization.");
        }
    }

    static void WriteCanonicalArray(
        Utf8JsonWriter writer,
        JsonArray array,
        JsonSerializerOptions options,
        Func<CanonicalJsonArrayPath, CanonicalJsonArrayOrdering> getArrayOrdering,
        string path,
        CanonicalJsonNumberSemantics numberSemantics)
    {
        var ordering = getArrayOrdering(new(path));
        var itemPath = AppendArrayItemPath(path);
        writer.WriteStartArray();
        switch (ordering.Kind)
        {
            case CanonicalJsonArrayOrderingKind.Sequence:
                WriteItems(array);
                break;
            case CanonicalJsonArrayOrderingKind.StringSet:
                WriteStringSetItems(array, path, WriteItem);
                break;
            case CanonicalJsonArrayOrderingKind.ObjectSet:
                WriteObjectSetItems(
                    array,
                    path,
                    ordering.ObjectSortProperty
                    ?? throw new InvalidOperationException("Object-set ordering requires a sort property."),
                    WriteItem);
                break;
            default:
                throw new InvalidOperationException(
                    $"Unsupported canonical JSON array ordering '{ordering.Kind}' at '{path}'.");
        }
        writer.WriteEndArray();

        void WriteItems(IEnumerable<JsonNode?> items)
        {
            foreach (var item in items)
                WriteItem(item);
        }

        void WriteItem(JsonNode? item)
        {
            WriteCanonical(
                writer,
                item,
                options,
                getArrayOrdering,
                itemPath,
                numberSemantics);
        }
    }

    static void WriteObjectSetItems(
        JsonArray array,
        string path,
        string sortProperty,
        Action<JsonNode?> writeItem)
    {
        List<KeyValuePair<string, JsonNode?>> ordered = new(array.Count);
        HashSet<string> sortValues = new(StringComparer.Ordinal);
        foreach (var item in array)
        {
            var sortValue = GetCanonicalObjectSortValue(item, path, sortProperty);
            if (!sortValues.Add(sortValue))
            {
                throw new InvalidOperationException(
                    $"Canonical object-set array '{path}' repeats sort value '{sortValue}' " +
                    $"for property '{sortProperty}'.");
            }

            ordered.Add(new(sortValue, item));
        }

        ordered.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Key, right.Key));
        foreach (var entry in ordered)
            writeItem(entry.Value);
    }

    static void WriteStringSetItems(
        JsonArray array,
        string path,
        Action<JsonNode?> writeItem)
    {
        List<KeyValuePair<string, JsonNode?>> ordered = new(array.Count);
        HashSet<string> values = new(StringComparer.Ordinal);
        foreach (var item in array)
        {
            var value = GetCanonicalStringSortValue(item, path);
            if (!values.Add(value))
            {
                throw new InvalidOperationException(
                    $"Canonical string-set array '{path}' repeats value '{value}'.");
            }

            ordered.Add(new(value, item));
        }

        ordered.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Key, right.Key));
        foreach (var entry in ordered)
            writeItem(entry.Value);
    }

    static string GetCanonicalObjectSortValue(
        JsonNode? item,
        string path,
        string propertyName)
    {
        if (item is not JsonObject obj
            || obj[propertyName] is not JsonValue value
            || !value.TryGetValue<string>(out var text))
        {
            throw new InvalidOperationException(
                $"Every item in canonical object-set array '{path}' must contain string property '{propertyName}'.");
        }

        return text;
    }

    static string GetCanonicalStringSortValue(JsonNode? item, string path)
    {
        if (item is JsonValue value && value.TryGetValue<string>(out var text))
            return text;

        throw new InvalidOperationException(
            $"Canonical string-set array '{path}' can contain only JSON string values.");
    }

    static string AppendPropertyPath(string path, string propertyName) =>
        string.Concat(path, "/", EscapePathSegment(propertyName));

    static string AppendArrayItemPath(string path) => string.Concat(path, "/*");

    static string EscapePathSegment(string value) =>
        value
            .Replace("~", "~0", StringComparison.Ordinal)
            .Replace("/", "~1", StringComparison.Ordinal)
            .Replace("*", "~2", StringComparison.Ordinal);

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
                var ordered = RentOrderedObservationProperties(value.Fields, out var propertyCount);
                try
                {
                    for (var index = 0; index < propertyCount; index++)
                    {
                        var (property, child) = ordered![index];
                        writer.WritePropertyName(property);
                        WriteCanonicalObservationValue(writer, child, bytesEncoding);
                    }
                }
                finally
                {
                    ReturnOrderedObservationProperties(ordered, propertyCount);
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

    static KeyValuePair<string, ObservationValue>[]? RentOrderedObservationProperties(
        IReadOnlyDictionary<string, ObservationValue>? properties,
        out int count)
    {
        count = 0;
        if (properties is null || properties.Count == 0)
            return null;

        var ordered = ArrayPool<KeyValuePair<string, ObservationValue>>.Shared.Rent(properties.Count);
        try
        {
            switch (properties)
            {
                case ImmutableDictionary<string, ObservationValue> immutable:
                    foreach (var property in immutable)
                        ordered[count++] = property;
                    break;
                case ImmutableSortedDictionary<string, ObservationValue> sorted:
                    foreach (var property in sorted)
                        ordered[count++] = property;
                    break;
                case Dictionary<string, ObservationValue> dictionary:
                    foreach (var property in dictionary)
                        ordered[count++] = property;
                    break;
                case OwnedObservationFields owned:
                    foreach (var property in owned)
                        ordered[count++] = property;
                    break;
                default:
                    foreach (var property in properties)
                        ordered[count++] = property;
                    break;
            }

            ordered.AsSpan(0, count).Sort(
                static (left, right) => StringComparer.Ordinal.Compare(left.Key, right.Key));
            return ordered;
        }
        catch
        {
            ReturnOrderedObservationProperties(ordered, count);
            throw;
        }
    }

    static void ReturnOrderedObservationProperties(
        KeyValuePair<string, ObservationValue>[]? properties,
        int count)
    {
        if (properties is null)
            return;

        properties.AsSpan(0, count).Clear();
        ArrayPool<KeyValuePair<string, ObservationValue>>.Shared.Return(properties);
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
            KeyValuePair<string, ObservationValue>[]? properties;
            int propertyCount;
            int nextItemIndex;

            ContainerFrame(
                bool isObject,
                int childDepth,
                ImmutableArray<ObservationValue> items,
                KeyValuePair<string, ObservationValue>[]? properties,
                int propertyCount)
            {
                IsObject = isObject;
                ChildDepth = childDepth;
                this.items = items;
                this.properties = properties;
                this.propertyCount = propertyCount;
                nextItemIndex = 0;
            }

            internal bool IsObject { get; }

            internal int ChildDepth { get; }

            internal static ContainerFrame ForArray(ImmutableArray<ObservationValue> items, int childDepth) =>
                new(
                    isObject: false,
                    childDepth,
                    items.IsDefault ? [] : items,
                    properties: null,
                    propertyCount: 0);

            internal static ContainerFrame ForObject(
                IReadOnlyDictionary<string, ObservationValue>? properties,
                int childDepth)
            {
                var ordered = RentOrderedObservationProperties(properties, out var count);
                if (ordered is null)
                {
                    return new(
                        isObject: true,
                        childDepth,
                        items: [],
                        properties: null,
                        propertyCount: 0);
                }

                return new(
                    isObject: true,
                    childDepth,
                    items: [],
                    properties: ordered,
                    propertyCount: count);
            }

            internal bool TryMoveNext(out string? property, out ObservationValue value)
            {
                if (IsObject)
                {
                    if (nextItemIndex < propertyCount)
                    {
                        var current = properties![nextItemIndex++];
                        property = current.Key;
                        value = current.Value;
                        return true;
                    }

                    property = null;
                    value = default;
                    return false;
                }

                property = null;
                if (nextItemIndex >= items.Length)
                {
                    value = default;
                    return false;
                }

                value = items[nextItemIndex++];
                return true;
            }

            internal void Dispose()
            {
                if (properties is null)
                    return;

                ReturnOrderedObservationProperties(properties, propertyCount);
                properties = null;
                propertyCount = 0;
                items = [];
                nextItemIndex = 0;
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
