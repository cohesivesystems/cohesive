using System.Globalization;
using System.Text;
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
        List<JsonPointerSegment> segments = [];
        return TryFindDuplicateProperty(element, path, segments, out duplicateLocation);
    }

    static bool TryFindDuplicateProperty(
        JsonElement element,
        string rootPath,
        List<JsonPointerSegment> segments,
        out string duplicateLocation)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                HashSet<string> names = new(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name))
                    {
                        duplicateLocation = BuildJsonPointer(rootPath, segments, property.Name);
                        return true;
                    }

                    segments.Add(JsonPointerSegment.Property(property.Name));
                    if (TryFindDuplicateProperty(property.Value, rootPath, segments, out duplicateLocation))
                    {
                        return true;
                    }
                    segments.RemoveAt(segments.Count - 1);
                }
                break;
            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    segments.Add(JsonPointerSegment.ArrayIndex(index));
                    if (TryFindDuplicateProperty(item, rootPath, segments, out duplicateLocation))
                    {
                        return true;
                    }
                    segments.RemoveAt(segments.Count - 1);

                    index++;
                }
                break;
        }

        duplicateLocation = string.Empty;
        return false;
    }

    static string BuildJsonPointer(
        string rootPath,
        IReadOnlyList<JsonPointerSegment> segments,
        string duplicateProperty)
    {
        StringBuilder pointer = new(rootPath);
        foreach (var segment in segments)
        {
            pointer.Append('/');
            if (segment.PropertyName is { } propertyName)
                AppendEscapedJsonPointerSegment(pointer, propertyName);
            else
                pointer.Append(segment.Index.ToString(CultureInfo.InvariantCulture));
        }

        pointer.Append('/');
        AppendEscapedJsonPointerSegment(pointer, duplicateProperty);
        return pointer.ToString();
    }

    static void AppendEscapedJsonPointerSegment(StringBuilder output, string value)
    {
        foreach (var character in value)
        {
            switch (character)
            {
                case '~':
                    output.Append("~0");
                    break;
                case '/':
                    output.Append("~1");
                    break;
                default:
                    output.Append(character);
                    break;
            }
        }
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

    /// <summary>
    /// Gets the canonical UTF-8 JSON representation of one typed portable-document object.
    /// </summary>
    /// <typeparam name="T">Closed object contract used for serialization.</typeparam>
    /// <param name="value">Typed object to encode.</param>
    /// <param name="options">
    /// Strict serializer options describing the closed wire contract. Arrays retain sequence order while
    /// object properties and exact base-10 number spellings are canonicalized deterministically.
    /// </param>
    /// <returns>Canonical compact UTF-8 JSON bytes for <paramref name="value"/>.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="value"/> or <paramref name="options"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="JsonException">A value cannot be written under <paramref name="options"/>.</exception>
    /// <exception cref="NotSupportedException">
    /// <typeparamref name="T"/> or one of its values has no serializer under <paramref name="options"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">The typed value has no canonical JSON representation.</exception>
    public static byte[] GetCanonicalBytes<T>(T value, JsonSerializerOptions options)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(options);
        var node = JsonSerializer.SerializeToNode(value, typeof(T), options)
            ?? throw new InvalidOperationException($"Failed to materialize {typeof(T).Name} JSON.");
        return GetCanonicalBytes(node, options);
    }

    /// <summary>
    /// Attempts to read one typed object through the strict canonical portable-document wire contract.
    /// </summary>
    /// <remarks>
    /// Reading rejects empty or malformed JSON, non-object roots, duplicate properties at any depth,
    /// unknown members according to <paramref name="options"/>, and inputs whose typed projection changes
    /// their canonical semantic JSON. This operation validates the wire representation only; callers remain
    /// responsible for schema compatibility, linking, and domain invariants.
    /// </remarks>
    /// <typeparam name="T">Closed object contract used for deserialization and typed reprojection.</typeparam>
    /// <param name="json">JSON text to inspect and deserialize.</param>
    /// <param name="options">Strict serializer options describing the closed wire contract.</param>
    /// <param name="contractName">Human-readable contract name used in failure messages.</param>
    /// <param name="value">
    /// Deserialized typed value when typed projection succeeds, including a projection reported as
    /// <see cref="StrictDocumentJsonReadFailure.WireNonCanonical"/>; otherwise <see langword="null"/>.
    /// </param>
    /// <param name="error">
    /// Structured wire failure when the method returns <see langword="false"/>; otherwise the default value.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when <paramref name="json"/> has one canonical typed object projection;
    /// otherwise <see langword="false"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="options"/> or <paramref name="contractName"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="contractName"/> is empty or white space.</exception>
    public static bool TryReadCanonicalObject<T>(
        string json,
        JsonSerializerOptions options,
        string contractName,
        out T? value,
        out StrictDocumentJsonReadError error)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(contractName);
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
        CanonicalJsonWriter.GetCanonicalSequenceBytes(
            node,
            options,
            numberSemantics: CanonicalJsonNumberSemantics.ExactDecimalRational);

    static bool IsCanonicalWireFailure(Exception exception) =>
        exception is JsonException
            or ArgumentException
            or InvalidOperationException
            or NotSupportedException
            or FormatException
            or OverflowException;

    readonly record struct JsonPointerSegment(string? PropertyName, int Index)
    {
        public static JsonPointerSegment Property(string name) => new(name, 0);

        public static JsonPointerSegment ArrayIndex(int index) => new(null, index);
    }
}

/// <summary>Classification of a failed strict typed portable-document JSON read.</summary>
public enum StrictDocumentJsonReadFailure
{
    /// <summary>No read failure occurred.</summary>
    None = 0,

    /// <summary>The supplied JSON text is null, empty, or consists only of white space.</summary>
    Empty = 1,

    /// <summary>The supplied text is not valid JSON or cannot be canonicalized as JSON.</summary>
    InvalidJson = 2,

    /// <summary>The JSON root is not an object.</summary>
    RootInvalid = 3,

    /// <summary>An object at the reported location repeats a property using ordinal name equality.</summary>
    DuplicateProperty = 4,

    /// <summary>The JSON cannot be deserialized under the closed typed wire contract.</summary>
    DeserializationInvalid = 5,

    /// <summary>Typed deserialization unexpectedly produced a null object.</summary>
    DeserializationNull = 6,

    /// <summary>The input differs from the unique canonical JSON projected by its typed value.</summary>
    WireNonCanonical = 7
}

/// <summary>Structured failure from a strict typed portable-document JSON read.</summary>
/// <param name="Failure">Stable failure classification.</param>
/// <param name="Message">Human-readable diagnostic message.</param>
/// <param name="Location">JSON Pointer location, or <c>$</c> for the document root.</param>
/// <remarks>The value is meaningful only when <see cref="StrictDocumentJson.TryReadCanonicalObject{T}"/> fails.</remarks>
public readonly record struct StrictDocumentJsonReadError(
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

/// <summary>Case-sensitive canonical string encoding for declared enum values.</summary>
/// <remarks>
/// Numeric encodings, numeric aliases, undefined values, and case-insensitive spellings are rejected. This
/// converter may be installed on a specific enum with <see cref="JsonConverterAttribute"/> or added to portable
/// document serializer options as a fallback factory for all otherwise-unattributed enums.
/// </remarks>
public sealed class StrictStringEnumJsonConverterFactory : JsonConverterFactory
{
    /// <summary>Creates a strict canonical string-enum converter factory.</summary>
    public StrictStringEnumJsonConverterFactory()
    {
    }

    /// <inheritdoc />
    public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum;

    /// <inheritdoc />
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
        static readonly SerializedEnumMemberCatalog Catalog = CreateCatalog();

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
                || !Catalog.TryGetClrName(text, out var clrName)
                || !Enum.TryParse<TEnum>(clrName, ignoreCase: false, out var value)
                || !string.Equals(value.ToString(), clrName, StringComparison.Ordinal))
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
            var clrName = value.ToString();
            if (!Catalog.TryGetWireName(clrName, out var wireName))
            {
                throw new JsonException(
                    $"Value '{clrName}' is not a declared value of enum '{typeof(TEnum).Name}'.");
            }

            writer.WriteStringValue(wireName);
        }

        static SerializedEnumMemberCatalog CreateCatalog()
        {
            if (SerializedEnumMemberCatalog.TryCreate(
                    typeof(TEnum),
                    out var catalog,
                    out var failure,
                    out var unsupportedConverter,
                    useClrNamesForUnsupportedConverter: true))
            {
                return catalog!;
            }

            throw new NotSupportedException(failure switch
            {
                SerializedEnumMemberCatalogFailure.UnsupportedConverter =>
                    $"Enum '{typeof(TEnum).Name}' declares unsupported JSON converter "
                    + $"'{unsupportedConverter?.FullName ?? typeof(JsonConverterAttribute).FullName}'.",
                SerializedEnumMemberCatalogFailure.AmbiguousWireMember =>
                    $"Enum '{typeof(TEnum).Name}' maps multiple members to the same canonical JSON string.",
                _ => $"Enum '{typeof(TEnum).Name}' has no discoverable serialized-member catalog."
            });
        }
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
