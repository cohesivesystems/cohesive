using System.Buffers;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;

namespace Cohesive.Model.Serialization;

/// <summary>Reads plain JSON directly into observation value storage.</summary>
/// <remarks>
/// This is the shared JSON-to-observation interpretation used by the JSON converter and shape-bound physical
/// hydration. It streams from <see cref="Utf8JsonReader"/> without constructing a <see cref="JsonDocument"/>.
/// </remarks>
public static class ObservationJsonReader
{
    /// <summary>Reads the complete JSON value at the reader's current token.</summary>
    /// <param name="reader">Reader positioned on the first token of a complete JSON value.</param>
    /// <returns>The portable observation value represented by the current JSON value.</returns>
    /// <exception cref="JsonException">The current token does not begin a supported complete JSON value.</exception>
    public static ObservationValue ReadValue(ref Utf8JsonReader reader) => ReadValueCore(ref reader);

    /// <summary>
    /// Reads a plain JSON object directly into ordinal-aligned storage and validates it against an exact shape.
    /// </summary>
    /// <param name="reader">Reader positioned on the root <see cref="JsonTokenType.StartObject"/> token.</param>
    /// <param name="shape">Exact graph and shape governing the JSON object.</param>
    /// <param name="layout">Layout assigning JSON field identities to destination ordinals.</param>
    /// <param name="valuesByOrdinal">Writable destination containing one value slot per layout ordinal.</param>
    /// <param name="hasValueBitMask">Writable packed presence bitmap for the destination ordinals.</param>
    /// <param name="validationError">Semantic validation failure when the parsed object does not satisfy the shape.</param>
    /// <returns><see langword="true"/> when the complete object satisfies the shape; otherwise false.</returns>
    /// <remarks>
    /// The destination spans are cleared before parsing. Root property matching does not materialize property-name
    /// strings on the successful path. Schema-specific primitives such as dates and bytes are restored before the
    /// existing <see cref="ObservationValidator"/> performs authoritative semantic validation.
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="shape"/> is default or <paramref name="layout"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The layout belongs to another shape or either destination span has an invalid length.
    /// </exception>
    /// <exception cref="JsonException">
    /// The JSON root is not an object, is incomplete, contains an unknown or duplicate root property, or otherwise
    /// contains invalid JSON.
    /// </exception>
    public static bool TryReadShape(
        scoped ref Utf8JsonReader reader,
        GraphShapeId shape,
        ObservationLayout layout,
        scoped Span<ObservationValue> valuesByOrdinal,
        scoped Span<ulong> hasValueBitMask,
        out string? validationError)
    {
        ArgumentNullException.ThrowIfNull(shape.Graph);
        ArgumentNullException.ThrowIfNull(layout);
        if (layout.ShapeId != shape.QualifiedId)
        {
            throw new ArgumentException(
                $"Observation layout shape '{layout.ShapeId}' does not match supplied shape '{shape.QualifiedId}'.",
                nameof(layout));
        }
        if (valuesByOrdinal.Length != layout.Count)
        {
            throw new ArgumentException(
                "Ordinal-aligned observation values must contain one slot per layout field.",
                nameof(valuesByOrdinal));
        }

        var requiredWords = layout.Count == 0 ? 0 : ((layout.Count - 1) >> 6) + 1;
        if (hasValueBitMask.Length != requiredWords)
        {
            throw new ArgumentException(
                "Observation presence bitmap length does not match the layout field count.",
                nameof(hasValueBitMask));
        }

        valuesByOrdinal.Clear();
        hasValueBitMask.Clear();
        ReadShapeObject(ref reader, shape.Graph, layout, valuesByOrdinal, hasValueBitMask);
        return ObservationValidator.TryValidateAgainstShape(
            shape,
            layout,
            valuesByOrdinal,
            hasValueBitMask,
            out validationError);
    }

    static void ReadShapeObject(
        scoped ref Utf8JsonReader reader,
        ShapeGraph graph,
        ObservationLayout layout,
        scoped Span<ObservationValue> valuesByOrdinal,
        scoped Span<ulong> hasValueBitMask)
    {
        RequireToken(reader.TokenType, JsonTokenType.StartObject);
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                return;
            RequireToken(reader.TokenType, JsonTokenType.PropertyName);

            if (!layout.TryGetJsonOrdinal(ref reader, out var ordinal))
            {
                throw new JsonException(
                    $"JSON property '{reader.GetString()}' is not part of observation layout '{layout.ShapeId}'.");
            }
            if (HasValue(hasValueBitMask, ordinal))
            {
                throw new JsonException(
                    $"JSON property '{layout.FieldIdentities[ordinal]}' occurs more than once.");
            }
            if (!reader.Read())
                throw new JsonException("The JSON object ended before its property value.");

            valuesByOrdinal[ordinal] = ReadFieldValue(
                ref reader,
                layout.GetFieldDefinition(ordinal),
                graph);
            SetHasValue(hasValueBitMask, ordinal);
        }

        throw new JsonException("The JSON object is incomplete.");
    }

    static ObservationValue ReadFieldValue(
        ref Utf8JsonReader reader,
        FieldDefinition field,
        ShapeGraph graph)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return ObservationValue.Null;
        return field.Cardinality == FieldCardinality.Many
            ? ReadTypedArray(ref reader, field.Type, graph)
            : ReadTypedValue(ref reader, field.Type, graph);
    }

    static ObservationValue ReadTypedValue(
        ref Utf8JsonReader reader,
        TypeRef type,
        ShapeGraph graph)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return ObservationValue.Null;

        switch (type)
        {
            case ScalarTypeRef scalar:
                return ReadScalar(ref reader, scalar.Kind);
            case EnumTypeRef:
            case EntityReferenceTypeRef:
            case JsonTypeRef:
                return ReadValueCore(ref reader);
            case ArrayTypeRef array:
                return ReadTypedArray(ref reader, array.ElementType, graph);
            case ObjectTypeRef objectType:
                return ReadObject(ref reader, objectType, graph);
            case NamedTypeRef named:
                return ReadNamed(ref reader, named, graph);
            case QuantityTypeRef quantity:
                return ReadQuantity(ref reader, quantity);
            case OpaqueRuntimeTypeRef opaque:
                return ReadOpaque(ref reader, opaque);
        }

        throw new JsonException($"Unsupported observation type reference '{type.GetType().Name}'.");
    }

    static ObservationValue ReadScalar(ref Utf8JsonReader reader, ScalarTypeKind kind)
    {
        if (reader.TokenType != JsonTokenType.String)
            return ReadValueCore(ref reader);

        switch (kind)
        {
            case ScalarTypeKind.Date when TryGetDateOnly(ref reader, out var date):
                return ObservationValue.FromDateOnly(date);
            case ScalarTypeKind.DateTime or ScalarTypeKind.Instant
                when reader.TryGetDateTimeOffset(out var dateTime):
                return ObservationValue.FromDateTimeOffset(dateTime);
            case ScalarTypeKind.Bytes when reader.TryGetBytesFromBase64(out var bytes):
                return ObservationValue.FromOwnedBytes(bytes);
            default:
                return ObservationValue.FromString(reader.GetString());
        }
    }

    static ObservationValue ReadOpaque(ref Utf8JsonReader reader, OpaqueRuntimeTypeRef opaque)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            if (opaque.RuntimeType == "DateOnly" && TryGetDateOnly(ref reader, out var date))
                return ObservationValue.FromDateOnly(date);
            if (opaque.RuntimeType == "TimeOnly" && TryGetTimeOnly(ref reader, out var time))
                return ObservationValue.FromTimeOnly(time);
        }

        return ReadValueCore(ref reader);
    }

    static ObservationValue ReadTypedArray(
        ref Utf8JsonReader reader,
        TypeRef elementType,
        ShapeGraph graph)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
            return ReadValueCore(ref reader);

        var items = ImmutableArray.CreateBuilder<ObservationValue>(CountArrayItems(reader));
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
                return ObservationValue.FromImmutableArray(items.MoveToImmutable());
            items.Add(ReadTypedValue(ref reader, elementType, graph));
        }

        throw new JsonException("The JSON array is incomplete.");
    }

    static ObservationValue ReadObject(
        ref Utf8JsonReader reader,
        ObjectTypeRef objectType,
        ShapeGraph graph)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            return ReadValueCore(ref reader);

        var values = new Dictionary<string, ObservationValue>(CountObjectProperties(reader), StringComparer.Ordinal);
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                return ObservationValue.FromOwnedObject(values);
            RequireToken(reader.TokenType, JsonTokenType.PropertyName);
            var name = reader.GetString()!;
            if (!reader.Read())
                throw new JsonException("The JSON object ended before its property value.");

            ObjectFieldTypeDef? field = null;
            foreach (var candidate in objectType.Fields)
            {
                if (string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    field = candidate;
                    break;
                }
            }

            values[name] = field is null
                ? ReadValueCore(ref reader)
                : ReadObjectField(ref reader, field.Type, field.Cardinality, graph);
        }

        throw new JsonException("The JSON object is incomplete.");
    }

    static ObservationValue ReadStructural(
        ref Utf8JsonReader reader,
        TypeDefinition.Structural structural,
        ShapeGraph graph)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            return ReadValueCore(ref reader);

        var values = new Dictionary<string, ObservationValue>(CountObjectProperties(reader), StringComparer.Ordinal);
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                return ObservationValue.FromOwnedObject(values);
            RequireToken(reader.TokenType, JsonTokenType.PropertyName);
            var name = reader.GetString()!;
            if (!reader.Read())
                throw new JsonException("The JSON object ended before its property value.");

            StructuralField? field = null;
            foreach (var candidate in structural.Fields)
            {
                if (string.Equals(candidate.Name.Value, name, StringComparison.OrdinalIgnoreCase))
                {
                    field = candidate;
                    break;
                }
            }

            values[name] = field is null
                ? ReadValueCore(ref reader)
                : ReadObjectField(ref reader, field.Type, field.Cardinality, graph);
        }

        throw new JsonException("The JSON object is incomplete.");
    }

    static ObservationValue ReadObjectField(
        ref Utf8JsonReader reader,
        TypeRef type,
        FieldCardinality cardinality,
        ShapeGraph graph) =>
        reader.TokenType == JsonTokenType.Null
            ? ObservationValue.Null
            : cardinality == FieldCardinality.Many
                ? ReadTypedArray(ref reader, type, graph)
                : ReadTypedValue(ref reader, type, graph);

    static ObservationValue ReadNamed(
        ref Utf8JsonReader reader,
        NamedTypeRef named,
        ShapeGraph graph)
    {
        if (!graph.TryGetType(named.TypeId, out var definition))
            return ReadValueCore(ref reader);

        return definition switch
        {
            TypeDefinition.Structural structural => ReadStructural(ref reader, structural, graph),
            TypeDefinition.Enum enumType => ReadPrimitive(ref reader, enumType.Underlying),
            TypeDefinition.Union union => ReadUnion(ref reader, union, graph),
            _ => ReadValueCore(ref reader)
        };
    }

    static ObservationValue ReadUnion(
        ref Utf8JsonReader reader,
        TypeDefinition.Union union,
        ShapeGraph graph)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            return ReadValueCore(ref reader);

        var probe = reader;
        var selectedType = FindUnionCase(ref probe, union);
        return selectedType is null
            ? ReadValueCore(ref reader)
            : ReadTypedValue(ref reader, selectedType, graph);
    }

    static TypeRef? FindUnionCase(ref Utf8JsonReader reader, TypeDefinition.Union union)
    {
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                return null;
            RequireToken(reader.TokenType, JsonTokenType.PropertyName);
            var isDiscriminator = PropertyNameEqualsIgnoreCase(
                ref reader,
                union.Discriminator.FieldName);
            if (!reader.Read())
                throw new JsonException("The JSON object ended before its property value.");
            if (!isDiscriminator)
            {
                reader.Skip();
                continue;
            }

            var discriminator = ReadPrimitive(ref reader, union.Discriminator.Type);
            return ObservationValidator.TryResolveUnionCase(union, discriminator);
        }

        throw new JsonException("The JSON object is incomplete.");
    }

    static ObservationValue ReadQuantity(
        ref Utf8JsonReader reader,
        QuantityTypeRef quantity)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            return ReadScalar(ref reader, quantity.BaseKind);

        var values = new Dictionary<string, ObservationValue>(CountObjectProperties(reader), StringComparer.Ordinal);
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                return ObservationValue.FromOwnedObject(values);
            RequireToken(reader.TokenType, JsonTokenType.PropertyName);
            var name = reader.GetString()!;
            if (!reader.Read())
                throw new JsonException("The JSON object ended before its property value.");
            if (string.Equals(name, "baseValue", StringComparison.OrdinalIgnoreCase))
            {
                values[name] = ReadScalar(ref reader, quantity.BaseKind);
            }
            else if (string.Equals(name, "value", StringComparison.OrdinalIgnoreCase)
                     && reader.TokenType == JsonTokenType.StartObject)
            {
                values[name] = ReadQuantityValue(ref reader, quantity);
            }
            else
            {
                values[name] = ReadValueCore(ref reader);
            }
        }

        throw new JsonException("The JSON object is incomplete.");
    }

    static ObservationValue ReadQuantityValue(
        ref Utf8JsonReader reader,
        QuantityTypeRef quantity)
    {
        var values = new Dictionary<string, ObservationValue>(CountObjectProperties(reader), StringComparer.Ordinal);
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                return ObservationValue.FromOwnedObject(values);
            RequireToken(reader.TokenType, JsonTokenType.PropertyName);
            var name = reader.GetString()!;
            if (!reader.Read())
                throw new JsonException("The JSON object ended before its property value.");
            values[name] = string.Equals(name, "baseValue", StringComparison.OrdinalIgnoreCase)
                ? ReadScalar(ref reader, quantity.BaseKind)
                : ReadValueCore(ref reader);
        }

        throw new JsonException("The JSON object is incomplete.");
    }

    static ObservationValue ReadPrimitive(ref Utf8JsonReader reader, PrimitiveType primitive) =>
        primitive switch
        {
            PrimitiveType.Bool => ReadScalar(ref reader, ScalarTypeKind.Bool),
            PrimitiveType.Int32 => ReadScalar(ref reader, ScalarTypeKind.Int32),
            PrimitiveType.Int64 => ReadScalar(ref reader, ScalarTypeKind.Int64),
            PrimitiveType.Decimal => ReadScalar(ref reader, ScalarTypeKind.Decimal),
            PrimitiveType.String => ReadScalar(ref reader, ScalarTypeKind.String),
            PrimitiveType.Guid => ReadScalar(ref reader, ScalarTypeKind.Guid),
            PrimitiveType.Date => ReadScalar(ref reader, ScalarTypeKind.Date),
            PrimitiveType.DateTime => ReadScalar(ref reader, ScalarTypeKind.DateTime),
            PrimitiveType.Instant => ReadScalar(ref reader, ScalarTypeKind.Instant),
            PrimitiveType.Bytes => ReadScalar(ref reader, ScalarTypeKind.Bytes),
            _ => ReadValueCore(ref reader)
        };

    static ObservationValue ReadValueCore(ref Utf8JsonReader reader) => reader.TokenType switch
    {
        JsonTokenType.Null => ObservationValue.Null,
        JsonTokenType.True => ObservationValue.FromBool(true),
        JsonTokenType.False => ObservationValue.FromBool(false),
        JsonTokenType.String => ObservationValue.FromString(reader.GetString()),
        JsonTokenType.Number when reader.TryGetInt64(out var integer) => ObservationValue.FromInt64(integer),
        JsonTokenType.Number when TryGetExactDecimal(ref reader, out var dec) => ObservationValue.FromDecimal(dec),
        JsonTokenType.Number => ObservationValue.FromDouble(reader.GetDouble()),
        JsonTokenType.StartObject => ReadUntypedObject(ref reader),
        JsonTokenType.StartArray => ReadUntypedArray(ref reader),
        _ => throw new JsonException($"Token '{reader.TokenType}' does not begin a JSON value.")
    };

    static ObservationValue ReadUntypedObject(ref Utf8JsonReader reader)
    {
        var values = new Dictionary<string, ObservationValue>(CountObjectProperties(reader), StringComparer.Ordinal);
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                return ObservationValue.FromOwnedObject(values);
            RequireToken(reader.TokenType, JsonTokenType.PropertyName);
            var name = reader.GetString()!;
            if (!reader.Read())
                throw new JsonException("The JSON object ended before its property value.");
            values[name] = ReadValueCore(ref reader);
        }

        throw new JsonException("The JSON object is incomplete.");
    }

    static ObservationValue ReadUntypedArray(ref Utf8JsonReader reader)
    {
        var items = ImmutableArray.CreateBuilder<ObservationValue>(CountArrayItems(reader));
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
                return ObservationValue.FromImmutableArray(items.MoveToImmutable());
            items.Add(ReadValueCore(ref reader));
        }

        throw new JsonException("The JSON array is incomplete.");
    }

    static bool TryGetExactDecimal(ref Utf8JsonReader reader, out decimal value)
    {
        var length = checked((int)(reader.HasValueSequence ? reader.ValueSequence.Length : reader.ValueSpan.Length));
        char[]? rented = null;
        Span<char> characters = length <= 128
            ? stackalloc char[length]
            : (rented = ArrayPool<char>.Shared.Rent(length));
        var destination = characters[..length];

        if (reader.HasValueSequence)
        {
            var index = 0;
            foreach (var segment in reader.ValueSequence)
            {
                foreach (var character in segment.Span)
                    destination[index++] = (char)character;
            }
        }
        else
        {
            var source = reader.ValueSpan;
            for (var index = 0; index < source.Length; index++)
                destination[index] = (char)source[index];
        }

        var parsed = ObservationValue.TryParseExactJsonDecimal(destination, out value);
        if (rented is not null)
            ArrayPool<char>.Shared.Return(rented);
        return parsed;
    }

    static int CountObjectProperties(Utf8JsonReader reader)
    {
        var count = 0;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                return count;
            RequireToken(reader.TokenType, JsonTokenType.PropertyName);
            if (!reader.Read())
                throw new JsonException("The JSON object ended before its property value.");
            count++;
            reader.Skip();
        }

        throw new JsonException("The JSON object is incomplete.");
    }

    static int CountArrayItems(Utf8JsonReader reader)
    {
        var count = 0;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
                return count;
            count++;
            reader.Skip();
        }

        throw new JsonException("The JSON array is incomplete.");
    }

    static bool TryGetDateOnly(ref Utf8JsonReader reader, out DateOnly value)
    {
        if (reader.HasValueSequence || reader.ValueIsEscaped)
        {
            return DateOnly.TryParseExact(
                reader.GetString(),
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out value);
        }

        var source = reader.ValueSpan;
        if (source.Length > 32)
        {
            value = default;
            return false;
        }
        Span<char> characters = stackalloc char[source.Length];
        for (var index = 0; index < source.Length; index++)
            characters[index] = (char)source[index];
        return DateOnly.TryParseExact(
            characters,
            "O",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out value);
    }

    static bool TryGetTimeOnly(ref Utf8JsonReader reader, out TimeOnly value)
    {
        if (reader.HasValueSequence || reader.ValueIsEscaped)
        {
            return TimeOnly.TryParseExact(
                reader.GetString(),
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out value);
        }

        var source = reader.ValueSpan;
        if (source.Length > 32)
        {
            value = default;
            return false;
        }
        Span<char> characters = stackalloc char[source.Length];
        for (var index = 0; index < source.Length; index++)
            characters[index] = (char)source[index];
        return TimeOnly.TryParseExact(
            characters,
            "O",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out value);
    }

    static bool PropertyNameEqualsIgnoreCase(ref Utf8JsonReader reader, string expected)
    {
        if (reader.ValueTextEquals(expected))
            return true;
        if (reader.HasValueSequence || reader.ValueIsEscaped)
            return string.Equals(reader.GetString(), expected, StringComparison.OrdinalIgnoreCase);

        var actual = reader.ValueSpan;
        if (actual.Length != expected.Length)
            return false;
        for (var index = 0; index < actual.Length; index++)
        {
            var expectedCharacter = expected[index];
            if (expectedCharacter > 0x7F)
            {
                return string.Equals(
                    reader.GetString(),
                    expected,
                    StringComparison.OrdinalIgnoreCase);
            }

            var actualCharacter = actual[index];
            if (actualCharacter == expectedCharacter)
                continue;
            if (actualCharacter is >= (byte)'A' and <= (byte)'Z')
                actualCharacter = (byte)(actualCharacter + ('a' - 'A'));
            if (expectedCharacter is >= 'A' and <= 'Z')
                expectedCharacter = (char)(expectedCharacter + ('a' - 'A'));
            if (actualCharacter != expectedCharacter)
                return false;
        }

        return true;
    }

    static bool HasValue(ReadOnlySpan<ulong> bitmap, int ordinal) =>
        (bitmap[ordinal >> 6] & (1UL << (ordinal & 63))) != 0;

    static void SetHasValue(Span<ulong> bitmap, int ordinal) =>
        bitmap[ordinal >> 6] |= 1UL << (ordinal & 63);

    static void RequireToken(JsonTokenType actual, JsonTokenType expected)
    {
        if (actual != expected)
            throw new JsonException($"Expected JSON token '{expected}', but found '{actual}'.");
    }
}
