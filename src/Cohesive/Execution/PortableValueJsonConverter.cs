using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Execution;

/// <summary>
/// Reads and writes the tagged, lossless local JSON representation of <see cref="PortableValue"/>.
/// </summary>
/// <remarks>
/// <see cref="ObservationValue"/>'s ordinary JSON projection intentionally follows JSON value semantics and
/// therefore cannot distinguish every observation kind. This converter uses an explicit kind tag at every node so
/// undefined, null, exact decimals, bytes, temporal values, objects, and arrays all survive a round trip unchanged.
/// </remarks>
public sealed class PortableValueJsonConverter : JsonConverter<PortableValue>
{
    /// <summary>Lossless tagged codec for detached observation values, using the same node format as PortableValue.</summary>
    /// <remarks>
    /// Register explicitly in a serializer profile to preserve byte, temporal, numeric, and undefined kinds.
    /// This changes the wire representation from ordinary JSON and requires an explicit format revision.
    /// The converter is stateless and safe to share across immutable serializer options.
    /// </remarks>
    public static JsonConverter<ObservationValue> TaggedObservationValues { get; } = new TaggedObservationConverter();

    sealed class TaggedObservationConverter : JsonConverter<ObservationValue>
    {
        public override ObservationValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            return ReadObservation(document.RootElement);
        }

        public override void Write(Utf8JsonWriter writer, ObservationValue value, JsonSerializerOptions options) => WriteObservation(writer, value);
    }

    const string ContractProperty = "contract";
    const string StateProperty = "state";
    const string ValueProperty = "value";
    const string FailureProperty = "failure";
    const string FailureCodeProperty = "code";
    const string KindTagProperty = "$kind";
    const string TaggedValueProperty = "$value";

    /// <summary>
    /// Projects a serialized portable value onto the stable content used by semantic fingerprinting.
    /// </summary>
    /// <remarks>
    /// Failed values retain their contract, state, and machine-readable failure code. Human-readable
    /// diagnostic prose and persisted or schema locations remain durable wire attribution, not semantic
    /// identity. Other portable-value states already contain only semantic content and remain unchanged.
    /// </remarks>
    /// <param name="node">Serialized portable-value object to project in place.</param>
    /// <param name="value">Portable value represented by <paramref name="node"/>.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="node"/> or <paramref name="value"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="value"/> is failed but does not contain its required diagnostic.
    /// </exception>
    internal static void ProjectSemanticFingerprint(JsonObject node, PortableValue value)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(value);
        if (value.State != PortableValueState.Failed)
            return;

        var failure = value.Failure
            ?? throw new InvalidOperationException("A failed portable value has no diagnostic.");
        node[FailureProperty] = new JsonObject
        {
            [FailureCodeProperty] = failure.Code
        };
    }

    /// <summary>Reads a portable value from its tagged JSON representation.</summary>
    /// <param name="reader">Reader positioned at the portable value.</param>
    /// <param name="typeToConvert">The requested target type.</param>
    /// <param name="options">Serializer options used for nested semantic contracts and diagnostics.</param>
    /// <returns>The reconstructed portable value.</returns>
    /// <exception cref="JsonException">
    /// The JSON representation is malformed, uses an unknown tag, or describes an invalid state/payload combination.
    /// </exception>
    public override PortableValue Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("A portable value must be encoded as a JSON object.");
        ValidatePortableValueProperties(root);

        var contractElement = GetRequiredProperty(root, ContractProperty);
        var contract = contractElement.Deserialize<ValueContract>(options)
            ?? throw new JsonException("A portable value requires a non-null semantic contract.");

        var stateElement = GetRequiredProperty(root, StateProperty);
        if (stateElement.ValueKind != JsonValueKind.String)
            throw new JsonException("Portable value 'state' must be a string.");
        var state = ParseState(stateElement.GetString());

        var hasValue = root.TryGetProperty(ValueProperty, out var valueElement);
        var hasFailure = root.TryGetProperty(FailureProperty, out var failureElement);

        try
        {
            return state switch
            {
                PortableValueState.Missing when !hasValue && !hasFailure => PortableValue.Missing(contract),
                PortableValueState.Absent when !hasValue && !hasFailure => PortableValue.Absent(contract),
                PortableValueState.Null when !hasValue && !hasFailure => PortableValue.Null(contract),
                PortableValueState.Unknown when !hasValue && !hasFailure => PortableValue.Unknown(contract),
                PortableValueState.Failed when !hasValue && hasFailure => PortableValue.Failed(
                    contract,
                    failureElement.Deserialize<DocumentValidationDiagnostic>(options)
                        ?? throw new JsonException("A failed portable value requires a non-null diagnostic.")),
                PortableValueState.Concrete when hasValue && !hasFailure => PortableValue.Concrete(
                    contract,
                    ReadObservation(valueElement)),
                _ => throw new JsonException(
                    $"Portable value state '{state}' has an invalid value or failure payload combination.")
            };
        }
        catch (ArgumentException exception)
        {
            throw new JsonException("The portable value violates its structural invariants.", exception);
        }
    }

    /// <summary>Writes a portable value using its tagged, lossless JSON representation.</summary>
    /// <param name="writer">Writer receiving the portable value.</param>
    /// <param name="value">Portable value to write.</param>
    /// <param name="options">Serializer options used for nested semantic contracts and diagnostics.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">
    /// The value contains an unknown observation kind, malformed observation payload, or non-finite double.
    /// </exception>
    public override void Write(Utf8JsonWriter writer, PortableValue value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(value);

        writer.WriteStartObject();
        writer.WritePropertyName(ContractProperty);
        JsonSerializer.Serialize(writer, value.Contract, options);
        writer.WriteString(StateProperty, FormatState(value.State));

        switch (value.State)
        {
            case PortableValueState.Failed:
                writer.WritePropertyName(FailureProperty);
                JsonSerializer.Serialize(
                    writer,
                    value.Failure ?? throw new JsonException("A failed portable value has no diagnostic."),
                    options);
                break;
            case PortableValueState.Concrete:
                writer.WritePropertyName(ValueProperty);
                WriteObservation(
                    writer,
                    value.Value ?? throw new JsonException("A concrete portable value has no observation payload."));
                break;
            case PortableValueState.Missing:
            case PortableValueState.Absent:
            case PortableValueState.Null:
            case PortableValueState.Unknown:
                break;
            default:
                throw new JsonException($"Unknown portable value state '{value.State}'.");
        }

        writer.WriteEndObject();
    }

    static JsonElement GetRequiredProperty(JsonElement value, string propertyName)
    {
        if (value.TryGetProperty(propertyName, out var property))
            return property;

        throw new JsonException($"Portable value property '{propertyName}' is required.");
    }

    static void ValidatePortableValueProperties(JsonElement value)
    {
        var hasContract = false;
        var hasState = false;
        var hasValue = false;
        var hasFailure = false;
        foreach (var property in value.EnumerateObject())
        {
            var duplicate = property.Name switch
            {
                ContractProperty => MarkSeen(ref hasContract),
                StateProperty => MarkSeen(ref hasState),
                ValueProperty => MarkSeen(ref hasValue),
                FailureProperty => MarkSeen(ref hasFailure),
                _ => throw new JsonException($"Unknown portable value property '{property.Name}'.")
            };
            if (duplicate)
                throw new JsonException($"Portable value property '{property.Name}' is declared more than once.");
        }
    }

    static PortableValueState ParseState(string? value) => value switch
    {
        "missing" => PortableValueState.Missing,
        "absent" => PortableValueState.Absent,
        "null" => PortableValueState.Null,
        "unknown" => PortableValueState.Unknown,
        "failed" => PortableValueState.Failed,
        "concrete" => PortableValueState.Concrete,
        _ => throw new JsonException($"Unknown portable value state '{value}'.")
    };

    static string FormatState(PortableValueState value) => value switch
    {
        PortableValueState.Missing => "missing",
        PortableValueState.Absent => "absent",
        PortableValueState.Null => "null",
        PortableValueState.Unknown => "unknown",
        PortableValueState.Failed => "failed",
        PortableValueState.Concrete => "concrete",
        _ => throw new JsonException($"Unknown portable value state '{value}'.")
    };

    static void WriteObservation(Utf8JsonWriter writer, ObservationValue value)
    {
        writer.WriteStartObject();
        writer.WriteString(KindTagProperty, FormatObservationKind(value.Kind));

        switch (value.Kind)
        {
            case ObservationValueKind.Undefined:
            case ObservationValueKind.Null:
                break;
            case ObservationValueKind.Int64:
                writer.WriteString(TaggedValueProperty, value.Int64.ToString(CultureInfo.InvariantCulture));
                break;
            case ObservationValueKind.Double:
                if (!double.IsFinite(value.Double))
                    throw new JsonException("Portable observation values cannot contain non-finite doubles.");
                writer.WriteNumber(TaggedValueProperty, value.Double);
                break;
            case ObservationValueKind.Decimal:
                writer.WriteString(TaggedValueProperty, value.Decimal.ToString("G29", CultureInfo.InvariantCulture));
                break;
            case ObservationValueKind.Bool:
                writer.WriteBoolean(TaggedValueProperty, value.Bool);
                break;
            case ObservationValueKind.String:
            case ObservationValueKind.DateTimeOffset:
            case ObservationValueKind.DateOnly:
            case ObservationValueKind.TimeOnly:
            case ObservationValueKind.TimeSpan:
                writer.WriteString(
                    TaggedValueProperty,
                    value.String ?? throw new JsonException($"Observation kind '{value.Kind}' requires text."));
                break;
            case ObservationValueKind.Bytes:
                writer.WriteBase64String(TaggedValueProperty, value.Bytes.Span);
                break;
            case ObservationValueKind.Object:
                writer.WritePropertyName(TaggedValueProperty);
                writer.WriteStartObject();
                var fields = value.Fields
                    ?? throw new JsonException("An object observation requires a property collection.");
                foreach (var field in fields.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(field.Key);
                    WriteObservation(writer, field.Value);
                }
                writer.WriteEndObject();
                break;
            case ObservationValueKind.Array:
                writer.WritePropertyName(TaggedValueProperty);
                writer.WriteStartArray();
                foreach (var item in value.Array)
                    WriteObservation(writer, item);
                writer.WriteEndArray();
                break;
            default:
                throw new JsonException($"Unknown observation value kind '{value.Kind}'.");
        }

        writer.WriteEndObject();
    }

    static ObservationValue ReadObservation(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new JsonException("A tagged observation value must be a JSON object.");
        ValidateTaggedObservationProperties(element);

        var kindElement = GetRequiredTaggedProperty(element, KindTagProperty);
        if (kindElement.ValueKind != JsonValueKind.String)
            throw new JsonException("Tagged observation '$kind' must be a string.");
        var kind = ParseObservationKind(kindElement.GetString());

        if (kind is ObservationValueKind.Undefined or ObservationValueKind.Null)
        {
            if (element.TryGetProperty(TaggedValueProperty, out _))
                throw new JsonException($"Observation kind '{kind}' cannot contain '$value'.");
            return kind == ObservationValueKind.Undefined
                ? ObservationValue.Undefined
                : ObservationValue.Null;
        }

        var payload = GetRequiredTaggedProperty(element, TaggedValueProperty);
        try
        {
            return kind switch
            {
                ObservationValueKind.Int64 => new(
                    ObservationValueKind.Int64,
                    int64: long.Parse(RequireString(payload, kind), NumberStyles.Integer, CultureInfo.InvariantCulture)),
                ObservationValueKind.Double => ReadDouble(payload),
                ObservationValueKind.Decimal => new(
                    ObservationValueKind.Decimal,
                    dec: decimal.Parse(RequireString(payload, kind), NumberStyles.Float, CultureInfo.InvariantCulture)),
                ObservationValueKind.Bool when payload.ValueKind is JsonValueKind.True or JsonValueKind.False =>
                    new(ObservationValueKind.Bool, b: payload.GetBoolean()),
                ObservationValueKind.Bool => throw new JsonException("A Boolean observation requires a Boolean payload."),
                ObservationValueKind.String => new(ObservationValueKind.String, s: RequireString(payload, kind)),
                ObservationValueKind.DateTimeOffset => new(
                    ObservationValueKind.DateTimeOffset,
                    s: RequireString(payload, kind)),
                ObservationValueKind.DateOnly => new(ObservationValueKind.DateOnly, s: RequireString(payload, kind)),
                ObservationValueKind.TimeOnly => new(ObservationValueKind.TimeOnly, s: RequireString(payload, kind)),
                ObservationValueKind.TimeSpan => new(ObservationValueKind.TimeSpan, s: RequireString(payload, kind)),
                ObservationValueKind.Bytes => ObservationValue.FromBytes(payload.GetBytesFromBase64()),
                ObservationValueKind.Object => ReadObject(payload),
                ObservationValueKind.Array => ReadArray(payload),
                _ => throw new JsonException($"Unknown observation value kind '{kind}'.")
            };
        }
        catch (FormatException exception)
        {
            throw new JsonException($"Observation kind '{kind}' has an invalid payload.", exception);
        }
        catch (OverflowException exception)
        {
            throw new JsonException($"Observation kind '{kind}' has an out-of-range payload.", exception);
        }
        catch (InvalidOperationException exception)
        {
            throw new JsonException($"Observation kind '{kind}' has an invalid JSON payload.", exception);
        }
    }

    static JsonElement GetRequiredTaggedProperty(JsonElement value, string propertyName)
    {
        if (value.TryGetProperty(propertyName, out var property))
            return property;

        throw new JsonException($"Tagged observation property '{propertyName}' is required.");
    }

    static void ValidateTaggedObservationProperties(JsonElement value)
    {
        var hasKind = false;
        var hasValue = false;
        foreach (var property in value.EnumerateObject())
        {
            var duplicate = property.Name switch
            {
                KindTagProperty => MarkSeen(ref hasKind),
                TaggedValueProperty => MarkSeen(ref hasValue),
                _ => throw new JsonException($"Unknown tagged observation property '{property.Name}'.")
            };
            if (duplicate)
                throw new JsonException($"Tagged observation property '{property.Name}' is declared more than once.");
        }
    }

    static bool MarkSeen(ref bool value)
    {
        var previous = value;
        value = true;
        return previous;
    }

    static string RequireString(JsonElement element, ObservationValueKind kind)
    {
        if (element.ValueKind == JsonValueKind.String && element.GetString() is { } value)
            return value;

        throw new JsonException($"Observation kind '{kind}' requires a string payload.");
    }

    static ObservationValue ReadDouble(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Number || !payload.TryGetDouble(out var value))
            throw new JsonException("A Double observation requires a numeric payload.");
        if (!double.IsFinite(value))
            throw new JsonException("Portable observation values cannot contain non-finite doubles.");
        return new(ObservationValueKind.Double, d: value);
    }

    static ObservationValue ReadObject(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            throw new JsonException("An Object observation requires an object payload.");

        var fields = ImmutableSortedDictionary.CreateBuilder<string, ObservationValue>(StringComparer.Ordinal);
        foreach (var property in payload.EnumerateObject())
        {
            if (!fields.TryAdd(property.Name, ReadObservation(property.Value)))
                throw new JsonException($"Object observation contains duplicate property '{property.Name}'.");
        }
        return ObservationValue.FromObject(fields.ToImmutable());
    }

    static ObservationValue ReadArray(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Array)
            throw new JsonException("An Array observation requires an array payload.");

        var values = ImmutableArray.CreateBuilder<ObservationValue>(payload.GetArrayLength());
        foreach (var element in payload.EnumerateArray())
            values.Add(ReadObservation(element));
        return ObservationValue.FromImmutableArray(values.MoveToImmutable());
    }

    static string FormatObservationKind(ObservationValueKind kind) => kind switch
    {
        ObservationValueKind.Undefined => "undefined",
        ObservationValueKind.Null => "null",
        ObservationValueKind.Int64 => "int64",
        ObservationValueKind.Double => "double",
        ObservationValueKind.Decimal => "decimal",
        ObservationValueKind.Bool => "bool",
        ObservationValueKind.String => "string",
        ObservationValueKind.Object => "object",
        ObservationValueKind.Array => "array",
        ObservationValueKind.Bytes => "bytes",
        ObservationValueKind.DateTimeOffset => "dateTimeOffset",
        ObservationValueKind.DateOnly => "dateOnly",
        ObservationValueKind.TimeOnly => "timeOnly",
        ObservationValueKind.TimeSpan => "timeSpan",
        _ => throw new JsonException($"Unknown observation value kind '{kind}'.")
    };

    static ObservationValueKind ParseObservationKind(string? kind) => kind switch
    {
        "undefined" => ObservationValueKind.Undefined,
        "null" => ObservationValueKind.Null,
        "int64" => ObservationValueKind.Int64,
        "double" => ObservationValueKind.Double,
        "decimal" => ObservationValueKind.Decimal,
        "bool" => ObservationValueKind.Bool,
        "string" => ObservationValueKind.String,
        "object" => ObservationValueKind.Object,
        "array" => ObservationValueKind.Array,
        "bytes" => ObservationValueKind.Bytes,
        "dateTimeOffset" => ObservationValueKind.DateTimeOffset,
        "dateOnly" => ObservationValueKind.DateOnly,
        "timeOnly" => ObservationValueKind.TimeOnly,
        "timeSpan" => ObservationValueKind.TimeSpan,
        _ => throw new JsonException($"Unknown observation value kind '{kind}'.")
    };
}
