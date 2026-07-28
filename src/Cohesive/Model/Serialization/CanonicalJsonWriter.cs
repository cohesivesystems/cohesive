using System.Buffers;
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

    static void WriteCanonicalObservationValue(Utf8JsonWriter writer, ObservationValue value)
    {
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
                    writer.WriteRawValue(
                        exactDecimal.ToString("G29", CultureInfo.InvariantCulture),
                        skipInputValidation: true);
                }
                else
                {
                    writer.WriteNumberValue(normalizedDouble);
                }
                return;
            case ObservationValueKind.Decimal:
                writer.WriteRawValue(
                    value.Decimal.ToString("G29", CultureInfo.InvariantCulture),
                    skipInputValidation: true);
                return;
            case ObservationValueKind.Bool:
                writer.WriteBooleanValue(value.Bool);
                return;
            case ObservationValueKind.String:
                writer.WriteStringValue(value.String);
                return;
            case ObservationValueKind.Bytes:
                writer.WriteBase64StringValue(value.Bytes.Span);
                return;
            case ObservationValueKind.DateTimeOffset:
            case ObservationValueKind.DateOnly:
            case ObservationValueKind.TimeOnly:
            case ObservationValueKind.TimeSpan:
                writer.WriteStringValue(value.String);
                return;
            case ObservationValueKind.Object:
                writer.WriteStartObject();
                if (value.Fields is not null)
                {
                    foreach (var (property, child) in value.Fields.OrderBy(
                                 static property => property.Key,
                                 StringComparer.Ordinal))
                    {
                        writer.WritePropertyName(property);
                        WriteCanonicalObservationValue(writer, child);
                    }
                }
                writer.WriteEndObject();
                return;
            case ObservationValueKind.Array:
                writer.WriteStartArray();
                if (!value.Array.IsDefault)
                {
                    foreach (var item in value.Array)
                        WriteCanonicalObservationValue(writer, item);
                }
                writer.WriteEndArray();
                return;
            default:
                throw new InvalidOperationException(
                    $"Observation value kind '{value.Kind}' does not have a canonical portable JSON encoding.");
        }
    }
}
