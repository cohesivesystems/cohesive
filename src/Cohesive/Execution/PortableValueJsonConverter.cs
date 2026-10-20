using System.Globalization;
using System.Runtime.InteropServices;
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
            => ReadObservation(ref reader);

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
        RequireObject(ref reader, "A portable value must be encoded as a JSON object.");
        ValueContract? contract = null;
        DocumentValidationDiagnostic? failure = null;
        var state = default(PortableValueState);
        var value = default(ObservationValue);
        var hasContract = false;
        var hasState = false;
        var hasValue = false;
        var hasFailure = false;
        var complete = false;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) { complete = true; break; }
            RequireProperty(ref reader);
            if (reader.ValueTextEquals(ContractProperty))
            {
                ReadPropertyValue(ref reader, ref hasContract, "Portable value property 'contract'");
                contract = JsonSerializer.Deserialize<ValueContract>(ref reader, options)
                    ?? throw new JsonException("A portable value requires a non-null semantic contract.");
            }
            else if (reader.ValueTextEquals(StateProperty))
            {
                ReadPropertyValue(ref reader, ref hasState, "Portable value property 'state'");
                if (reader.TokenType != JsonTokenType.String) throw new JsonException("Portable value 'state' must be a string.");
                state = ParseState(reader.GetString());
            }
            else if (reader.ValueTextEquals(ValueProperty))
            {
                ReadPropertyValue(ref reader, ref hasValue, "Portable value property 'value'");
                value = ReadObservation(ref reader);
            }
            else if (reader.ValueTextEquals(FailureProperty))
            {
                ReadPropertyValue(ref reader, ref hasFailure, "Portable value property 'failure'");
                try { failure = JsonSerializer.Deserialize<DocumentValidationDiagnostic>(ref reader, options); }
                catch (ArgumentException exception)
                {
                    throw new JsonException("The portable value violates its structural invariants.", exception);
                }
            }
            else throw new JsonException($"Unknown portable value property '{reader.GetString()}'.");
        }
        if (!complete) throw new JsonException("The portable value is incomplete.");
        if (!hasContract) throw new JsonException("Portable value property 'contract' is required.");
        if (!hasState) throw new JsonException("Portable value property 'state' is required.");
        try
        {
            return state switch
            {
                PortableValueState.Missing when !hasValue && !hasFailure => PortableValue.Missing(contract!),
                PortableValueState.Absent when !hasValue && !hasFailure => PortableValue.Absent(contract!),
                PortableValueState.Null when !hasValue && !hasFailure => PortableValue.Null(contract!),
                PortableValueState.Unknown when !hasValue && !hasFailure => PortableValue.Unknown(contract!),
                PortableValueState.Failed when !hasValue && hasFailure => PortableValue.Failed(contract!,
                    failure ?? throw new JsonException("A failed portable value requires a non-null diagnostic.")),
                PortableValueState.Concrete when hasValue && !hasFailure => PortableValue.Concrete(contract!, value),
                _ => throw new JsonException($"Portable value state '{state}' has an invalid value or failure payload combination.")
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

    static void RequireObject(ref Utf8JsonReader reader, string message)
    {
        if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException(message);
    }

    static void RequireProperty(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException("Expected a JSON property name.");
    }

    // Descriptions are constants; successful decoding constructs no diagnostic text.
    static void ReadPropertyValue(ref Utf8JsonReader reader, ref bool seen, string description)
    {
        if (seen) throw new JsonException($"{description} is declared more than once.");
        seen = true;
        if (!reader.Read()) throw new JsonException("The JSON object ended before its property value.");
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
                using (var ordered = new OrderedObservationFields(fields))
                {
                    foreach (var field in ordered)
                    {
                        writer.WritePropertyName(field.Key);
                        WriteObservation(writer, field.Value);
                    }
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

    static ObservationValue ReadObservation(ref Utf8JsonReader reader)
    {
        var strings = new ObservationJsonPropertyNames.TokenScope();
        try { return ReadObservation(ref reader, ref strings); }
        finally { strings.Dispose(); }
    }

    static ObservationValue ReadObservation(ref Utf8JsonReader reader, ref ObservationJsonPropertyNames.TokenScope strings)
    {
        RequireObject(ref reader, "A tagged observation value must be a JSON object.");
        var kind = default(ObservationValueKind);
        var value = default(ObservationValue);
        var hasKind = false;
        var hasValue = false;
        var complete = false;
        JsonDocument? deferredPayload = null;
        try
        {
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject) { complete = true; break; }
                RequireProperty(ref reader);
                if (reader.ValueTextEquals(KindTagProperty))
                {
                    ReadPropertyValue(ref reader, ref hasKind, "Tagged observation property '$kind'");
                    if (reader.TokenType != JsonTokenType.String) throw new JsonException("Tagged observation '$kind' must be a string.");
                    kind = ParseObservationKind(ref reader);
                }
                else if (reader.ValueTextEquals(TaggedValueProperty))
                {
                    ReadPropertyValue(ref reader, ref hasValue, "Tagged observation property '$value'");
                    // Canonical output declares kind first. Arbitrary input order remains supported
                    // without imposing a recursive lookahead on the successful canonical path.
                    if (hasKind) value = ReadPayload(ref reader, kind, ref strings);
                    else deferredPayload = JsonDocument.ParseValue(ref reader);
                }
                else throw new JsonException($"Unknown tagged observation property '{reader.GetString()}'.");
            }
            if (!complete) throw new JsonException("The tagged observation is incomplete.");
            if (!hasKind) throw new JsonException("Tagged observation property '$kind' is required.");
            if (kind is ObservationValueKind.Undefined or ObservationValueKind.Null)
            {
                if (hasValue) throw new JsonException($"Observation kind '{kind}' cannot contain '$value'.");
                return kind == ObservationValueKind.Undefined ? ObservationValue.Undefined : ObservationValue.Null;
            }
            if (!hasValue) throw new JsonException("Tagged observation property '$value' is required.");
            if (deferredPayload is not null)
            {
                var deferred = new Utf8JsonReader(JsonMarshal.GetRawUtf8Value(deferredPayload.RootElement), reader.CurrentState.Options);
                deferred.Read();
                value = ReadPayload(ref deferred, kind, ref strings);
            }
            return value;
        }
        finally { deferredPayload?.Dispose(); }
    }

    static ObservationValue ReadPayload(ref Utf8JsonReader reader, ObservationValueKind kind, ref ObservationJsonPropertyNames.TokenScope strings)
    {
        try
        {
            return kind switch
            {
                ObservationValueKind.Undefined or ObservationValueKind.Null => throw new JsonException($"Observation kind '{kind}' cannot contain '$value'."),
                ObservationValueKind.Int64 => new(ObservationValueKind.Int64,
                    int64: long.Parse(RequireString(ref reader, kind, ref strings), NumberStyles.Integer, CultureInfo.InvariantCulture)),
                ObservationValueKind.Decimal => new(ObservationValueKind.Decimal,
                    dec: decimal.Parse(RequireString(ref reader, kind, ref strings), NumberStyles.Float, CultureInfo.InvariantCulture)),
                ObservationValueKind.Double => ReadDouble(ref reader),
                ObservationValueKind.Bool when reader.TokenType is JsonTokenType.True or JsonTokenType.False => new(ObservationValueKind.Bool, b: reader.GetBoolean()),
                ObservationValueKind.Bool => throw new JsonException("A Boolean observation requires a Boolean payload."),
                ObservationValueKind.String or ObservationValueKind.DateTimeOffset or ObservationValueKind.DateOnly
                    or ObservationValueKind.TimeOnly or ObservationValueKind.TimeSpan => new(kind, s: RequireString(ref reader, kind, ref strings)),
                ObservationValueKind.Bytes => ObservationValue.FromOwnedBytes(reader.GetBytesFromBase64()),
                ObservationValueKind.Object => ReadObject(ref reader, ref strings),
                ObservationValueKind.Array => ReadArray(ref reader, ref strings),
                _ => throw new JsonException($"Unknown observation value kind '{kind}'.")
            };
        }
        catch (FormatException exception) { throw new JsonException($"Observation kind '{kind}' has an invalid payload.", exception); }
        catch (OverflowException exception) { throw new JsonException($"Observation kind '{kind}' has an out-of-range payload.", exception); }
        catch (InvalidOperationException exception) { throw new JsonException($"Observation kind '{kind}' has an invalid JSON payload.", exception); }
    }

    static string RequireString(ref Utf8JsonReader reader, ObservationValueKind kind, ref ObservationJsonPropertyNames.TokenScope strings)
    {
        if (reader.TokenType == JsonTokenType.String && strings.Get(ref reader) is { } value) return value;
        throw new JsonException($"Observation kind '{kind}' requires a string payload.");
    }

    static ObservationValue ReadDouble(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.Number || !reader.TryGetDouble(out var value))
            throw new JsonException("A Double observation requires a numeric payload.");
        if (!double.IsFinite(value)) throw new JsonException("Portable observation values cannot contain non-finite doubles.");
        return new(ObservationValueKind.Double, d: value);
    }

    static ObservationValue ReadObject(ref Utf8JsonReader reader, ref ObservationJsonPropertyNames.TokenScope strings)
    {
        RequireObject(ref reader, "An Object observation requires an object payload.");
        var fields = new ObservationJsonObjectBuilder();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) return fields.Build();
            RequireProperty(ref reader);
            var name = strings.Get(ref reader);
            if (!reader.Read()) throw new JsonException("The JSON object ended before its property value.");
            if (!fields.TryAdd(name, ReadObservation(ref reader, ref strings)))
                throw new JsonException($"Object observation contains duplicate property '{name}'.");
        }
        throw new JsonException("The object observation is incomplete.");
    }

    static ObservationValue ReadArray(ref Utf8JsonReader reader, ref ObservationJsonPropertyNames.TokenScope strings)
    {
        if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("An Array observation requires an array payload.");
        var values = new ObservationJsonArrayBuilder();
        try
        {
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndArray) return values.Build();
                values.Add(ReadObservation(ref reader, ref strings));
            }
            throw new JsonException("The array observation is incomplete.");
        }
        finally { values.Dispose(); }
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

    // Match fixed wire metadata without allocating or admitting it to the retained-text cache.
    // ValueTextEquals also handles escaped and segmented JSON strings.
    static ObservationValueKind ParseObservationKind(ref Utf8JsonReader reader)
    {
        if (reader.ValueTextEquals("string"u8)) return ObservationValueKind.String;
        if (reader.ValueTextEquals("object"u8)) return ObservationValueKind.Object;
        if (reader.ValueTextEquals("array"u8)) return ObservationValueKind.Array;
        if (reader.ValueTextEquals("int64"u8)) return ObservationValueKind.Int64;
        if (reader.ValueTextEquals("decimal"u8)) return ObservationValueKind.Decimal;
        if (reader.ValueTextEquals("null"u8)) return ObservationValueKind.Null;
        if (reader.ValueTextEquals("bool"u8)) return ObservationValueKind.Bool;
        if (reader.ValueTextEquals("double"u8)) return ObservationValueKind.Double;
        if (reader.ValueTextEquals("bytes"u8)) return ObservationValueKind.Bytes;
        if (reader.ValueTextEquals("undefined"u8)) return ObservationValueKind.Undefined;
        if (reader.ValueTextEquals("dateTimeOffset"u8)) return ObservationValueKind.DateTimeOffset;
        if (reader.ValueTextEquals("dateOnly"u8)) return ObservationValueKind.DateOnly;
        if (reader.ValueTextEquals("timeOnly"u8)) return ObservationValueKind.TimeOnly;
        if (reader.ValueTextEquals("timeSpan"u8)) return ObservationValueKind.TimeSpan;
        throw new JsonException($"Unknown observation value kind '{reader.GetString()}'.");
    }
}
