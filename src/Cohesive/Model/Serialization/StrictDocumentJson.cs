using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Cohesive.Model.Serialization;

/// <summary>Formatting modes for strict portable semantic-document JSON.</summary>
public enum PortableDocumentJsonFormatting
{
    /// <summary>Compact JSON without insignificant white space.</summary>
    Compact = 0,

    /// <summary>Human-readable indented JSON.</summary>
    Indented = 1
}

/// <summary>
/// Shared strict JSON behavior for persisted portable Cohesive semantic documents.
/// </summary>
public static class StrictDocumentJson
{
    /// <summary>Creates serializer options for a closed, case-sensitive portable wire contract.</summary>
    /// <param name="formatting">Compact or human-readable indented output formatting.</param>
    /// <returns>Serializer options configured for strict portable document JSON.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="formatting"/> is not recognized.</exception>
    public static JsonSerializerOptions CreateOptions(
        PortableDocumentJsonFormatting formatting = PortableDocumentJsonFormatting.Compact)
    {
        if (!Enum.IsDefined(formatting))
        {
            throw new ArgumentOutOfRangeException(nameof(formatting), formatting, "Unsupported JSON formatting mode.");
        }

        JsonSerializerOptions options = new(JsonSerializerDefaults.Web)
        {
            AllowOutOfOrderMetadataProperties = true,
            NumberHandling = JsonNumberHandling.Strict,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            WriteIndented = formatting == PortableDocumentJsonFormatting.Indented
        };
        options.Converters.Add(new StrictFieldPathSegmentJsonConverter());
        options.Converters.Add(SingleValueWrapperJsonConverter.ScalarOnly);
        options.Converters.Add(new StrictStringEnumJsonConverterFactory());
        return options;
    }

    /// <summary>Finds the first duplicate JSON object property using ordinal property-name equality.</summary>
    /// <param name="element">JSON element to inspect recursively.</param>
    /// <param name="path">JSON Pointer path of <paramref name="element"/> without a trailing slash.</param>
    /// <param name="duplicateLocation">JSON Pointer location of the first duplicate property when found.</param>
    /// <returns><see langword="true"/> when a duplicate property is found; otherwise <see langword="false"/>.</returns>
    public static bool TryFindDuplicateProperty(
        JsonElement element,
        string path,
        out string duplicateLocation)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                HashSet<string> names = new(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    var propertyPath = $"{path}/{EscapeJsonPointerSegment(property.Name)}";
                    if (!names.Add(property.Name))
                    {
                        duplicateLocation = propertyPath;
                        return true;
                    }

                    if (TryFindDuplicateProperty(property.Value, propertyPath, out duplicateLocation))
                    {
                        return true;
                    }
                }
                break;
            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    if (TryFindDuplicateProperty(item, $"{path}/{index}", out duplicateLocation))
                    {
                        return true;
                    }

                    index++;
                }
                break;
        }

        duplicateLocation = string.Empty;
        return false;
    }

    /// <summary>Creates a one-error structured validation result.</summary>
    /// <param name="code">Stable diagnostic code.</param>
    /// <param name="message">Human-readable diagnostic message.</param>
    /// <param name="location">JSON Pointer or root location associated with the error.</param>
    /// <returns>A validation result containing the supplied error.</returns>
    public static DocumentValidationResult Error(string code, string message, string location) =>
        DocumentValidationResult.FromDiagnostics([
            new(
                Code: code,
                Severity: DiagnosticSeverity.Error,
                Message: message,
                Location: location)
        ]);

    internal static byte[] GetCanonicalBytes<T>(T value, JsonSerializerOptions options)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(options);
        var node = JsonSerializer.SerializeToNode(value, typeof(T), options)
            ?? throw new InvalidOperationException($"Failed to materialize {typeof(T).Name} JSON.");
        return GetCanonicalBytes(node, options);
    }

    internal static bool TryReadCanonicalObject<T>(
        string json,
        JsonSerializerOptions options,
        string contractName,
        out T? value,
        out StrictDocumentJsonReadError error)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(options);
        value = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            error = new(
                StrictDocumentJsonReadFailure.Empty,
                $"{contractName} JSON cannot be empty.",
                "$");
            return false;
        }

        JsonDocument parsed;
        try
        {
            parsed = JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            error = new(
                StrictDocumentJsonReadFailure.InvalidJson,
                exception.Message,
                exception.Path ?? "$");
            return false;
        }

        byte[] persistedBytes;
        using (parsed)
        {
            if (parsed.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = new(
                    StrictDocumentJsonReadFailure.RootInvalid,
                    $"A {contractName} must be encoded as a JSON object.",
                    "$");
                return false;
            }
            if (TryFindDuplicateProperty(parsed.RootElement, string.Empty, out var duplicateLocation))
            {
                error = new(
                    StrictDocumentJsonReadFailure.DuplicateProperty,
                    $"{contractName} JSON cannot contain duplicate object properties.",
                    string.IsNullOrEmpty(duplicateLocation) ? "$" : duplicateLocation);
                return false;
            }

            try
            {
                var node = JsonNode.Parse(parsed.RootElement.GetRawText())
                    ?? throw new InvalidOperationException($"Failed to materialize {contractName} JSON.");
                persistedBytes = GetCanonicalBytes(node, options);
            }
            catch (Exception exception) when (IsCanonicalWireFailure(exception))
            {
                error = new(StrictDocumentJsonReadFailure.InvalidJson, exception.Message, "$");
                return false;
            }
        }

        try
        {
            value = JsonSerializer.Deserialize<T>(json, options);
        }
        catch (Exception exception) when (IsCanonicalWireFailure(exception))
        {
            error = new(
                StrictDocumentJsonReadFailure.DeserializationInvalid,
                exception.Message,
                exception is JsonException jsonException ? jsonException.Path ?? "$" : "$");
            return false;
        }
        if (value is null)
        {
            error = new(
                StrictDocumentJsonReadFailure.DeserializationNull,
                $"{contractName} JSON unexpectedly produced a null value.",
                "$");
            return false;
        }

        byte[] projectedBytes;
        try
        {
            projectedBytes = GetCanonicalBytes(value, options);
        }
        catch (Exception exception) when (IsCanonicalWireFailure(exception))
        {
            error = new(StrictDocumentJsonReadFailure.DeserializationInvalid, exception.Message, "$");
            return false;
        }
        if (!persistedBytes.AsSpan().SequenceEqual(projectedBytes))
        {
            error = new(
                StrictDocumentJsonReadFailure.WireNonCanonical,
                $"The supplied {contractName} differs from its unique canonical typed wire representation.",
                "$");
            return false;
        }

        error = default;
        return true;
    }

    static byte[] GetCanonicalBytes(JsonNode node, JsonSerializerOptions options) =>
        CanonicalJsonWriter.GetCanonicalBytes(
            node,
            options,
            static _ => CanonicalJsonArrayOrdering.Sequence,
            numberSemantics: CanonicalJsonNumberSemantics.ExactDecimalRational);

    static bool IsCanonicalWireFailure(Exception exception) =>
        exception is JsonException
            or ArgumentException
            or InvalidOperationException
            or NotSupportedException
            or FormatException
            or OverflowException;

    static string EscapeJsonPointerSegment(string value) =>
        value.Replace("~", "~0", StringComparison.Ordinal)
            .Replace("/", "~1", StringComparison.Ordinal);
}

internal enum StrictDocumentJsonReadFailure
{
    None = 0,
    Empty = 1,
    InvalidJson = 2,
    RootInvalid = 3,
    DuplicateProperty = 4,
    DeserializationInvalid = 5,
    DeserializationNull = 6,
    WireNonCanonical = 7
}

internal readonly record struct StrictDocumentJsonReadError(
    StrictDocumentJsonReadFailure Failure,
    string Message,
    string Location);

/// <summary>
/// Strict converter for getter-only field-path segments whose kind must remain explicit on input.
/// </summary>
sealed class StrictFieldPathSegmentJsonConverter : JsonConverter<FieldPathSegment>
{
    /// <inheritdoc />
    public override FieldPathSegment Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("A field-path segment must be a JSON object.");
        }

        SegmentKind kind = default;
        string? segment = null;
        var hasKind = false;
        var hasSegment = false;
        var ended = false;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                ended = true;
                break;
            }
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException("A field-path segment contains an invalid JSON token.");
            }

            var property = reader.GetString();
            if (!reader.Read())
            {
                throw new JsonException("A field-path segment ended before its property value.");
            }

            switch (property)
            {
                case "kind" when hasKind:
                case "segment" when hasSegment:
                    throw new JsonException($"A field-path segment contains duplicate property '{property}'.");
                case "kind":
                    {
                        hasKind = true;
                        if (reader.TokenType != JsonTokenType.String)
                        {
                            throw new JsonException("Field-path segment kind must be encoded as a string.");
                        }

                        var text = reader.GetString();
                        if (text is null
                            || !Enum.TryParse(text, ignoreCase: false, out kind)
                            || !Enum.IsDefined(kind)
                            || !string.Equals(kind.ToString(), text, StringComparison.Ordinal))
                        {
                            throw new JsonException($"'{text}' is not a canonical field-path segment kind.");
                        }
                        break;
                    }
                case "segment":
                    hasSegment = true;
                    segment = reader.TokenType switch
                    {
                        JsonTokenType.Null => null,
                        JsonTokenType.String => reader.GetString(),
                        _ => throw new JsonException("Field-path segment text must be a string or null.")
                    };
                    break;
                default:
                    throw new JsonException($"Unknown field-path segment property '{property}'.");
            }
        }

        if (!ended)
        {
            throw new JsonException("A field-path segment JSON object was not terminated.");
        }

        if (!hasKind)
        {
            throw new JsonException("A field-path segment must declare kind.");
        }

        return new(kind, segment);
    }

    /// <inheritdoc />
    public override void Write(
        Utf8JsonWriter writer,
        FieldPathSegment value,
        JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (!Enum.IsDefined(value.Kind))
        {
            throw new JsonException(
                $"Value '{value.Kind}' is not a declared field-path segment kind.");
        }
        writer.WriteStartObject();
        writer.WriteString("kind", value.Kind.ToString());
        if (value.Segment is null)
        {
            writer.WriteNull("segment");
        }
        else
        {
            writer.WriteString("segment", value.Segment);
        }

        writer.WriteEndObject();
    }
}

/// <summary>Case-sensitive canonical string encoding for enum values.</summary>
sealed class StrictStringEnumJsonConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum;

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(typeToConvert);
        ArgumentNullException.ThrowIfNull(options);
        if (!typeToConvert.IsEnum)
        {
            throw new ArgumentException($"Type '{typeToConvert}' is not an enum.", nameof(typeToConvert));
        }

        var converterType = typeof(StrictStringEnumJsonConverter<>).MakeGenericType(typeToConvert);
        return (JsonConverter)(Activator.CreateInstance(converterType)
            ?? throw new InvalidOperationException(
                $"Failed to create a strict string enum converter for '{typeToConvert}'."));
    }

    sealed class StrictStringEnumJsonConverter<TEnum> : JsonConverter<TEnum>
        where TEnum : struct, Enum
    {
        public override TEnum Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String)
            {
                throw new JsonException($"Enum '{typeToConvert.Name}' must be encoded as a string.");
            }

            var text = reader.GetString();
            if (text is null
                || !Enum.TryParse<TEnum>(text, ignoreCase: false, out var value)
                || !string.Equals(value.ToString(), text, StringComparison.Ordinal)
                || IsNumeric(text))
            {
                throw new JsonException(
                    $"'{text}' is not a canonical case-sensitive value of enum '{typeToConvert.Name}'.");
            }

            return value;
        }

        public override void Write(
            Utf8JsonWriter writer,
            TEnum value,
            JsonSerializerOptions options)
        {
            ArgumentNullException.ThrowIfNull(writer);
            var text = value.ToString();
            if (IsNumeric(text))
            {
                throw new JsonException(
                    $"Value '{text}' is not a declared value of enum '{typeof(TEnum).Name}'.");
            }

            writer.WriteStringValue(text);
        }

        static bool IsNumeric(string value) =>
            value.Length > 0 && (char.IsDigit(value[0]) || value[0] is '-' or '+');
    }
}

/// <summary>
/// Preserves an unsupported numeric enum declaration so semantic validation can diagnose it after JSON import.
/// </summary>
/// <typeparam name="TEnum">Enum whose declaration boundary intentionally retains unsupported numeric values.</typeparam>
/// <remarks>
/// Declared values retain the strict canonical string representation. Only undefined numeric values use a JSON
/// number, which keeps malformed declarations inspectable without permitting numeric aliases for known values.
/// </remarks>
public sealed class DiagnosticPreservingStringEnumJsonConverter<TEnum>
    : JsonConverter<TEnum>, IJsonUndefinedNumericEnumValueConverter
    where TEnum : struct, Enum
{
    /// <summary>Creates a diagnostic-preserving converter for <typeparamref name="TEnum"/>.</summary>
    public DiagnosticPreservingStringEnumJsonConverter()
    {
    }

    /// <summary>Reads a declared string enum value or retains an unsupported 32-bit numeric value.</summary>
    /// <param name="reader">Reader positioned at the enum value.</param>
    /// <param name="typeToConvert">Enum type requested by the serializer.</param>
    /// <param name="options">Serializer options for the containing document.</param>
    /// <returns>The declared enum value or retained unsupported numeric value.</returns>
    /// <exception cref="JsonException">
    /// The value is not a canonical case-sensitive enum name or an unsupported 32-bit integer.
    /// </exception>
    public override TEnum Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var text = reader.GetString();
            if (text is not null
                && Enum.TryParse<TEnum>(text, ignoreCase: false, out var value)
                && Enum.IsDefined(value)
                && string.Equals(value.ToString(), text, StringComparison.Ordinal))
            {
                return value;
            }

            throw new JsonException(
                $"'{text}' is not a canonical case-sensitive value of enum '{typeToConvert.Name}'.");
        }

        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var numericValue))
        {
            var value = (TEnum)Enum.ToObject(typeof(TEnum), numericValue);
            if (!Enum.IsDefined(value))
            {
                return value;
            }

            throw new JsonException(
                $"Declared value '{value}' of enum '{typeToConvert.Name}' must be encoded as a string.");
        }

        throw new JsonException(
            $"Enum '{typeToConvert.Name}' must be encoded as a canonical string or an undefined 32-bit integer.");
    }

    /// <summary>Writes declared values as strings and unsupported underlying values as numbers.</summary>
    /// <param name="writer">Writer receiving the enum value.</param>
    /// <param name="value">Enum value to write.</param>
    /// <param name="options">Serializer options for the containing document.</param>
    /// <exception cref="ArgumentNullException"><paramref name="writer"/> is <see langword="null"/>.</exception>
    /// <exception cref="OverflowException">
    /// The enum's unsupported underlying value cannot be represented as a 32-bit integer.
    /// </exception>
    public override void Write(
        Utf8JsonWriter writer,
        TEnum value,
        JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (Enum.IsDefined(value))
        {
            writer.WriteStringValue(value.ToString());
            return;
        }

        writer.WriteNumberValue(Convert.ToInt32(value, CultureInfo.InvariantCulture));
    }
}
