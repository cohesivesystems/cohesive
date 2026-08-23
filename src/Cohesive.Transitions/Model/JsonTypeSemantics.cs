using System.Buffers.Text;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cohesive.Transitions.Model;

static class JsonTypeSemantics
{
    public static string DescribeType(TypeRef type) => type switch
    {
        OpaqueRuntimeTypeRef opaque => $"Opaque({opaque.RuntimeType})",
        JsonTypeRef json => $"Json({json.Kind})",
        ScalarTypeRef scalar => scalar.Kind.ToString(),
        EnumTypeRef enumType => $"Enum({enumType.Name})",
        EntityReferenceTypeRef entityRef => $"EntityRef({entityRef.Entity.Value})",
        NamedTypeRef named => $"Named({named.TypeId.Value})",
        ArrayTypeRef array => $"Array({DescribeType(array.ElementType)})",
        ObjectTypeRef => "Object",
        QuantityTypeRef quantity => $"Quantity({quantity.Quantity},{quantity.BaseKind})",
        _ => type.GetType().Name
    };

    static bool MatchesType(TypeRef type, JsonNode? value)
    {
        if (value is null)
            return false;

        switch (type)
        {
            case OpaqueRuntimeTypeRef:
                return true;

            case JsonTypeRef json:
                return MatchesJsonType(json.Kind, value);

            case ScalarTypeRef scalar:
                return MatchesScalarType(scalar.Kind, value);

            case EnumTypeRef enumType:
                return TryGetString(value, out var enumValue)
                    && enumType.Members.Contains(enumValue, StringComparer.Ordinal);

            case EntityReferenceTypeRef:
                return TryGetString(value, out var entityRef) && !string.IsNullOrWhiteSpace(entityRef);

            case ArrayTypeRef arrayType:
                if (value is not JsonArray array)
                    return false;

                foreach (var item in array)
                {
                    if (!MatchesType(type: arrayType.ElementType, value: item))
                        return false;
                }

                return true;

            case ObjectTypeRef objectType:
                if (value is not JsonObject obj)
                    return false;

                foreach (var field in objectType.Fields)
                {
                    if (!TryGetObjectProperty(obj, field.Name, out var fieldValue))
                    {
                        if (field.Presence == FieldPresence.Required)
                            return false;

                        continue;
                    }

                    if (fieldValue is null && field.Presence == FieldPresence.Required)
                        return false;

                    if (fieldValue is not null && !MatchesType(type: field.Type, value: fieldValue))
                        return false;
                }

                return true;

            case QuantityTypeRef quantityType:
                return MatchesQuantityType(type: quantityType, value: value);
        }

        throw new InvalidOperationException(message: $"Unsupported type reference '{type.GetType().Name}'.");
    }

    public static bool MatchesType(
        TypeRef type,
        ObservationValue value,
        ShapeGraph? graph = null)
    {
        if (value.Kind is ObservationValueKind.Null or ObservationValueKind.Undefined)
            return false;

        switch (type)
        {
            case OpaqueRuntimeTypeRef:
                return true;

            case JsonTypeRef json:
                return MatchesJsonType(json.Kind, value);

            case ScalarTypeRef scalar:
                return MatchesScalarType(scalar.Kind, value);

            case EnumTypeRef enumType:
                return TryGetString(value, out var enumValue)
                    && enumType.Members.Contains(enumValue, StringComparer.Ordinal);

            case EntityReferenceTypeRef:
                return TryGetString(value, out var entityRef) && !string.IsNullOrWhiteSpace(entityRef);

            case ArrayTypeRef arrayType:
                if (value.Kind != ObservationValueKind.Array)
                    return false;

                foreach (var item in value.EnumerateArray())
                {
                    if (!MatchesType(type: arrayType.ElementType, value: item, graph: graph))
                        return false;
                }

                return true;

            case ObjectTypeRef objectType:
                if (value.Kind != ObservationValueKind.Object || value.Fields is null)
                    return false;

                foreach (var field in objectType.Fields)
                {
                    if (!TryGetObjectProperty(value.Fields, field.Name, out var fieldValue))
                    {
                        if (field.Presence == FieldPresence.Required)
                            return false;

                        continue;
                    }

                    if (fieldValue.Kind == ObservationValueKind.Undefined)
                    {
                        if (field.Presence == FieldPresence.Required)
                            return false;
                        continue;
                    }

                    if (fieldValue.Kind == ObservationValueKind.Null)
                    {
                        if (field.Nullability == FieldNullability.NonNullable)
                            return false;
                        continue;
                    }

                    if (field.Cardinality == FieldCardinality.Many)
                    {
                        if (fieldValue.Kind != ObservationValueKind.Array)
                            return false;
                        foreach (var item in fieldValue.EnumerateArray())
                        {
                            if (!MatchesType(field.Type, item, graph))
                                return false;
                        }
                        continue;
                    }

                    if (!MatchesType(type: field.Type, value: fieldValue, graph: graph))
                        return false;
                }

                return true;

            case NamedTypeRef namedType:
                return graph is not null
                    && graph.TryGetType(namedType.TypeId, out var definition)
                    && MatchesNamedType(definition, value, graph);

            case QuantityTypeRef quantityType:
                return MatchesQuantityType(type: quantityType, value: value);
        }

        throw new InvalidOperationException(message: $"Unsupported type reference '{type.GetType().Name}'.");
    }

    static bool MatchesNamedType(
        TypeDefinition definition,
        ObservationValue value,
        ShapeGraph graph) => definition switch
    {
        TypeDefinition.Structural structural => MatchesStructuralType(structural, value, graph),
        TypeDefinition.Enum enumeration => enumeration.Values.Any(enumValue =>
            PrimitiveTypeSemantics.MatchesLiteral(
                enumeration.Underlying,
                enumValue.Value ?? enumValue.Name,
                value)),
        TypeDefinition.Union union => MatchesUnionType(union, value, graph),
        _ => false
    };

    static bool MatchesStructuralType(
        TypeDefinition.Structural structural,
        ObservationValue value,
        ShapeGraph graph)
    {
        if (value.Kind != ObservationValueKind.Object || value.Fields is null)
            return false;

        foreach (var field in structural.Fields)
        {
            if (!value.Fields.TryGetValue(field.Name.Value, out var fieldValue))
            {
                if (field.Presence == FieldPresence.Required)
                    return false;
                continue;
            }

            if (!MatchesField(
                    field.Type,
                    field.Cardinality,
                    field.Nullability,
                    fieldValue,
                    graph))
            {
                return false;
            }
        }

        return true;
    }

    static bool MatchesUnionType(
        TypeDefinition.Union union,
        ObservationValue value,
        ShapeGraph graph)
    {
        if (value.Kind != ObservationValueKind.Object
            || value.Fields is null
            || !value.Fields.TryGetValue(union.Discriminator.FieldName, out var discriminator))
        {
            return false;
        }

        foreach (var unionCase in union.Cases)
        {
            if (PrimitiveTypeSemantics.MatchesLiteral(
                    union.Discriminator.Type,
                    unionCase.DiscriminatorValue,
                    discriminator))
            {
                return MatchesType(unionCase.Type, value, graph);
            }
        }

        return false;
    }

    static bool MatchesField(
        TypeRef type,
        FieldCardinality cardinality,
        FieldNullability nullability,
        ObservationValue value,
        ShapeGraph graph)
    {
        if (value.Kind == ObservationValueKind.Null)
            return nullability == FieldNullability.Nullable;
        if (value.Kind == ObservationValueKind.Undefined)
            return false;
        if (cardinality == FieldCardinality.Single)
            return MatchesType(type, value, graph);
        if (value.Kind != ObservationValueKind.Array)
            return false;

        foreach (var item in value.EnumerateArray())
        {
            if (!MatchesType(type, item, graph))
                return false;
        }
        return true;
    }

    static bool MatchesType(TypeRef type, JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return false;

        switch (type)
        {
            case OpaqueRuntimeTypeRef:
                return true;

            case JsonTypeRef json:
                return MatchesJsonType(json.Kind, value);

            case ScalarTypeRef scalar:
                return MatchesScalarType(scalar.Kind, value);

            case EnumTypeRef enumType:
                return TryGetString(value, out var enumValue)
                    && enumType.Members.Contains(enumValue, StringComparer.Ordinal);

            case EntityReferenceTypeRef:
                return TryGetString(value, out var entityRef) && !string.IsNullOrWhiteSpace(entityRef);

            case ArrayTypeRef arrayType:
                if (value.ValueKind != JsonValueKind.Array)
                    return false;

                foreach (var item in value.EnumerateArray())
                {
                    if (!MatchesType(type: arrayType.ElementType, value: item))
                        return false;
                }

                return true;

            case ObjectTypeRef objectType:
                if (value.ValueKind != JsonValueKind.Object)
                    return false;

                foreach (var field in objectType.Fields)
                {
                    if (!TryGetObjectProperty(value, field.Name, out var fieldValue))
                    {
                        if (field.Presence == FieldPresence.Required)
                            return false;

                        continue;
                    }

                    if (fieldValue.ValueKind == JsonValueKind.Null && field.Presence == FieldPresence.Required)
                        return false;

                    if (fieldValue.ValueKind != JsonValueKind.Null && !MatchesType(type: field.Type, value: fieldValue))
                        return false;
                }

                return true;

            case QuantityTypeRef quantityType:
                return MatchesQuantityType(type: quantityType, value: value);
        }

        throw new InvalidOperationException(message: $"Unsupported type reference '{type.GetType().Name}'.");
    }

    static bool TryGetObjectProperty(JsonObject obj, string name, out JsonNode? value)
    {
        if (obj.TryGetPropertyValue(name, out value))
            return true;

        foreach (var property in obj)
        {
            if (string.Equals(property.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = null;
        return false;
    }

    static bool TryGetObjectProperty(JsonElement obj, string name, out JsonElement value)
    {
        if (obj.TryGetProperty(name, out value))
            return true;

        foreach (var property in obj.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    static bool TryGetObjectProperty(IReadOnlyDictionary<string, ObservationValue> obj, string name, out ObservationValue value)
    {
        if (obj.TryGetValue(name, out value))
            return true;

        foreach (var property in obj)
        {
            if (string.Equals(property.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    static bool MatchesJsonType(JsonTypeKind kind, JsonNode value)
    {
        return kind switch
        {
            JsonTypeKind.Any => true,
            JsonTypeKind.Object => value is JsonObject,
            JsonTypeKind.Array => value is JsonArray,
            JsonTypeKind.String => value is JsonValue jsonValue && jsonValue.TryGetValue<string>(out _),
            JsonTypeKind.Number => IsJsonNumber(value),
            JsonTypeKind.Boolean => value is JsonValue jsonValue && jsonValue.TryGetValue<bool>(out _),
            _ => false
        };
    }

    static bool MatchesJsonType(JsonTypeKind kind, ObservationValue value)
    {
        return kind switch
        {
            JsonTypeKind.Any => true,
            JsonTypeKind.Object => value.Kind == ObservationValueKind.Object,
            JsonTypeKind.Array => value.Kind == ObservationValueKind.Array,
            JsonTypeKind.String => value.Kind == ObservationValueKind.String,
            JsonTypeKind.Number => value.Kind is ObservationValueKind.Int64
                or ObservationValueKind.Double
                or ObservationValueKind.Decimal,
            JsonTypeKind.Boolean => value.Kind == ObservationValueKind.Bool,
            _ => false
        };
    }

    static bool MatchesJsonType(JsonTypeKind kind, JsonElement value)
    {
        return kind switch
        {
            JsonTypeKind.Any => true,
            JsonTypeKind.Object => value.ValueKind == JsonValueKind.Object,
            JsonTypeKind.Array => value.ValueKind == JsonValueKind.Array,
            JsonTypeKind.String => value.ValueKind == JsonValueKind.String,
            JsonTypeKind.Number => value.ValueKind == JsonValueKind.Number,
            JsonTypeKind.Boolean => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            _ => false
        };
    }

    static bool IsJsonNumber(JsonNode value)
    {
        if (value is not JsonValue jsonValue)
            return false;

        return jsonValue.TryGetValue<int>(out _)
               || jsonValue.TryGetValue<long>(out _)
               || jsonValue.TryGetValue<double>(out _)
               || jsonValue.TryGetValue<decimal>(out _);
    }

    public static bool TryGetBoolean(JsonNode? value, out bool result)
    {
        if (value is JsonValue jsonValue)
        {
            if (jsonValue.TryGetValue(out result))
                return true;

            if (jsonValue.TryGetValue<string>(out var text) && bool.TryParse(text, out result))
                return true;
        }

        result = false;
        return false;
    }

    static bool TryGetString(JsonNode? value, out string result)
    {
        if (value is JsonValue jsonValue && jsonValue.TryGetValue(out result!))
            return true;

        result = string.Empty;
        return false;
    }

    public static bool TryGetInt32(JsonNode? value, out int result)
    {
        if (value is JsonValue jsonValue)
        {
            if (jsonValue.TryGetValue(out result))
                return true;

            if (jsonValue.TryGetValue<long>(out var asLong) && asLong >= int.MinValue && asLong <= int.MaxValue)
            {
                result = (int)asLong;
                return true;
            }

            if (jsonValue.TryGetValue<decimal>(out var asDecimal)
                && asDecimal == Math.Truncate(asDecimal)
                && asDecimal >= int.MinValue
                && asDecimal <= int.MaxValue)
            {
                result = (int)asDecimal;
                return true;
            }
        }

        result = 0;
        return false;
    }

    static bool TryGetInt64(JsonNode? value, out long result)
    {
        if (value is JsonValue jsonValue)
        {
            if (jsonValue.TryGetValue(out result))
                return true;

            if (jsonValue.TryGetValue<decimal>(out var asDecimal)
                && asDecimal == decimal.Truncate(asDecimal)
                && asDecimal >= long.MinValue
                && asDecimal <= long.MaxValue)
            {
                result = (long)asDecimal;
                return true;
            }

            if (jsonValue.TryGetValue<string>(out var text)
                && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out result))
            {
                return true;
            }
        }

        result = 0;
        return false;
    }

    static bool TryGetDecimal(JsonNode? value, out decimal result)
    {
        if (value is JsonValue jsonValue)
        {
            if (jsonValue.TryGetValue(out result))
                return true;

            if (jsonValue.TryGetValue<int>(out var intValue))
            {
                result = intValue;
                return true;
            }

            if (jsonValue.TryGetValue<long>(out var longValue))
            {
                result = longValue;
                return true;
            }

            if (jsonValue.TryGetValue<double>(out var doubleValue))
            {
                result = Convert.ToDecimal(doubleValue, CultureInfo.InvariantCulture);
                return true;
            }

            if (jsonValue.TryGetValue<string>(out var text)
                && decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out result))
            {
                return true;
            }
        }

        result = 0;
        return false;
    }

    static bool TryGetBoolean(JsonElement value, out bool result)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.True:
                result = true;
                return true;

            case JsonValueKind.False:
                result = false;
                return true;

            case JsonValueKind.String when bool.TryParse(value.GetString(), out result):
                return true;
        }

        result = false;
        return false;
    }

    static bool TryGetString(JsonElement value, out string result)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            result = value.GetString() ?? string.Empty;
            return true;
        }

        result = string.Empty;
        return false;
    }

    static bool TryGetInt32(JsonElement value, out int result)
    {
        if (value.ValueKind == JsonValueKind.Number)
        {
            if (value.TryGetInt32(out result))
                return true;

            if (value.TryGetInt64(out var asLong) && asLong >= int.MinValue && asLong <= int.MaxValue)
            {
                result = (int)asLong;
                return true;
            }

            if (value.TryGetDecimal(out var asDecimal)
                && asDecimal == Math.Truncate(asDecimal)
                && asDecimal >= int.MinValue
                && asDecimal <= int.MaxValue)
            {
                result = (int)asDecimal;
                return true;
            }
        }

        if (value.ValueKind == JsonValueKind.String
            && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result))
        {
            return true;
        }

        result = 0;
        return false;
    }

    static bool TryGetInt64(JsonElement value, out long result)
    {
        if (value.ValueKind == JsonValueKind.Number)
        {
            if (value.TryGetInt64(out result))
                return true;

            if (value.TryGetDecimal(out var asDecimal)
                && asDecimal == decimal.Truncate(asDecimal)
                && asDecimal >= long.MinValue
                && asDecimal <= long.MaxValue)
            {
                result = (long)asDecimal;
                return true;
            }
        }

        if (value.ValueKind == JsonValueKind.String
            && long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result))
        {
            return true;
        }

        result = 0;
        return false;
    }

    static bool TryGetDecimal(JsonElement value, out decimal result)
    {
        if (value.ValueKind == JsonValueKind.Number)
        {
            if (value.TryGetDecimal(out result))
                return true;

            if (value.TryGetInt32(out var intValue))
            {
                result = intValue;
                return true;
            }

            if (value.TryGetInt64(out var longValue))
            {
                result = longValue;
                return true;
            }

            if (value.TryGetDouble(out var doubleValue))
            {
                try
                {
                    result = Convert.ToDecimal(doubleValue, CultureInfo.InvariantCulture);
                    return true;
                }
                catch (OverflowException)
                {
                }
            }
        }

        if (value.ValueKind == JsonValueKind.String
            && decimal.TryParse(value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out result))
        {
            return true;
        }

        result = 0;
        return false;
    }

    static bool TryGetBoolean(ObservationValue value, out bool result) => value.TryGetBoolean(out result);

    static bool TryGetString(ObservationValue value, out string result)
    {
        if (value.Kind is ObservationValueKind.String
            or ObservationValueKind.DateTimeOffset
            or ObservationValueKind.DateOnly
            or ObservationValueKind.TimeOnly
            or ObservationValueKind.TimeSpan)
        {
            result = value.GetString() ?? string.Empty;
            return true;
        }

        result = string.Empty;
        return false;
    }

    static bool TryGetInt32(ObservationValue value, out int result) => value.TryGetInt32(out result);

    static bool TryGetInt64(ObservationValue value, out long result) => value.TryGetInt64(out result);

    static bool TryGetDecimal(ObservationValue value, out decimal result) => value.TryGetDecimal(out result);

    static bool MatchesScalarType(ScalarTypeKind scalarType, JsonNode value)
    {
        return scalarType switch
        {
            ScalarTypeKind.String => TryGetString(value, out _),
            ScalarTypeKind.Int32 => TryGetInt32(value, out _),
            ScalarTypeKind.Int64 => TryGetInt64(value, out _),
            ScalarTypeKind.Decimal => TryGetDecimal(value, out _),
            ScalarTypeKind.Bool => TryGetBoolean(value, out _),
            ScalarTypeKind.Guid => TryGetString(value, out var guidValue) && Guid.TryParse(guidValue, out _),
            ScalarTypeKind.Date => TryGetString(value, out var date)
                && ObservationValue.FromString(date).TryGetDateOnly(out _),
            ScalarTypeKind.DateTime => TryGetString(value, out var timestamp) && DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _),
            ScalarTypeKind.Instant => TryGetString(value, out var instant)
                && ObservationValue.FromString(instant).TryGetInstant(out _),
            ScalarTypeKind.Bytes => MatchesBytes(value),
            _ => false
        };
    }

    static bool MatchesScalarType(ScalarTypeKind scalarType, JsonElement value)
    {
        return scalarType switch
        {
            ScalarTypeKind.String => TryGetString(value, out _),
            ScalarTypeKind.Int32 => TryGetInt32(value, out _),
            ScalarTypeKind.Int64 => TryGetInt64(value, out _),
            ScalarTypeKind.Decimal => TryGetDecimal(value, out _),
            ScalarTypeKind.Bool => TryGetBoolean(value, out _),
            ScalarTypeKind.Guid => TryGetString(value, out var guidValue) && Guid.TryParse(guidValue, out _),
            ScalarTypeKind.Date => TryGetString(value, out var date)
                && ObservationValue.FromString(date).TryGetDateOnly(out _),
            ScalarTypeKind.DateTime => TryGetString(value, out var timestamp) && DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _),
            ScalarTypeKind.Instant => TryGetString(value, out var instant)
                && ObservationValue.FromString(instant).TryGetInstant(out _),
            ScalarTypeKind.Bytes => value.ValueKind == JsonValueKind.String
                && Base64.IsValid(value.GetString()),
            _ => false
        };
    }

    static bool MatchesScalarType(ScalarTypeKind scalarType, ObservationValue value)
    {
        return scalarType switch
        {
            ScalarTypeKind.String => TryGetString(value, out _),
            ScalarTypeKind.Int32 => TryGetInt32(value, out _),
            ScalarTypeKind.Int64 => TryGetInt64(value, out _),
            ScalarTypeKind.Decimal => TryGetDecimal(value, out _),
            ScalarTypeKind.Bool => TryGetBoolean(value, out _),
            ScalarTypeKind.Guid => TryGetString(value, out var guidValue) && Guid.TryParse(guidValue, out _),
            ScalarTypeKind.Date => value.TryGetDateOnly(out _),
            ScalarTypeKind.DateTime => TryGetString(value, out var timestamp) && DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _),
            ScalarTypeKind.Instant => value.TryGetInstant(out _),
            ScalarTypeKind.Bytes => value.TryGetBytes(out _),
            _ => false
        };
    }

    static bool MatchesBytes(JsonNode value)
    {
        if (value is not JsonValue jsonValue)
            return false;
        if (jsonValue.TryGetValue<byte[]>(out _))
            return true;
        return jsonValue.TryGetValue<string>(out var text) && Base64.IsValid(text);
    }

    static bool MatchesQuantityType(QuantityTypeRef type, JsonNode value)
    {
        if (MatchesScalarType(type.BaseKind, value))
            return true;

        if (value is not JsonObject obj)
            return false;

        if (TryGetPropertyValueIgnoreCase(obj, propertyName: "baseValue", out var directBaseValue)
            && directBaseValue is not null
            && MatchesScalarType(type.BaseKind, directBaseValue))
        {
            return true;
        }

        if (TryGetPropertyValueIgnoreCase(obj, propertyName: "value", out var wrappedValue)
            && wrappedValue is JsonObject wrappedObject
            && TryGetPropertyValueIgnoreCase(wrappedObject, propertyName: "baseValue", out var wrappedBaseValue)
            && wrappedBaseValue is not null
            && MatchesScalarType(type.BaseKind, wrappedBaseValue))
        {
            return true;
        }

        return false;
    }

    static bool MatchesQuantityType(QuantityTypeRef type, JsonElement value)
    {
        if (MatchesScalarType(type.BaseKind, value))
            return true;

        if (value.ValueKind != JsonValueKind.Object)
            return false;

        if (TryGetPropertyValueIgnoreCase(value, propertyName: "baseValue", out var directBaseValue)
            && directBaseValue.ValueKind != JsonValueKind.Null
            && MatchesScalarType(type.BaseKind, directBaseValue))
        {
            return true;
        }

        if (TryGetPropertyValueIgnoreCase(value, propertyName: "value", out var wrappedValue)
            && wrappedValue.ValueKind == JsonValueKind.Object
            && TryGetPropertyValueIgnoreCase(wrappedValue, propertyName: "baseValue", out var wrappedBaseValue)
            && wrappedBaseValue.ValueKind != JsonValueKind.Null
            && MatchesScalarType(type.BaseKind, wrappedBaseValue))
        {
            return true;
        }

        return false;
    }

    static bool MatchesQuantityType(QuantityTypeRef type, ObservationValue value)
    {
        if (MatchesScalarType(type.BaseKind, value))
            return true;

        if (value.Kind != ObservationValueKind.Object || value.Fields is null)
            return false;

        if (TryGetPropertyValueIgnoreCase(value.Fields, propertyName: "baseValue", out var directBaseValue)
            && directBaseValue.Kind is not (ObservationValueKind.Null or ObservationValueKind.Undefined)
            && MatchesScalarType(type.BaseKind, directBaseValue))
        {
            return true;
        }

        if (TryGetPropertyValueIgnoreCase(value.Fields, propertyName: "value", out var wrappedValue)
            && wrappedValue.Kind == ObservationValueKind.Object
            && wrappedValue.Fields is not null
            && TryGetPropertyValueIgnoreCase(wrappedValue.Fields, propertyName: "baseValue", out var wrappedBaseValue)
            && wrappedBaseValue.Kind is not (ObservationValueKind.Null or ObservationValueKind.Undefined)
            && MatchesScalarType(type.BaseKind, wrappedBaseValue))
        {
            return true;
        }

        return false;
    }

    static bool TryGetPropertyValueIgnoreCase(JsonObject obj, string propertyName, out JsonNode? value)
    {
        foreach (var pair in obj)
        {
            if (!string.Equals(pair.Key, propertyName, StringComparison.OrdinalIgnoreCase))
                continue;

            value = pair.Value;
            return true;
        }

        value = null;
        return false;
    }

    static bool TryGetPropertyValueIgnoreCase(JsonElement obj, string propertyName, out JsonElement value)
    {
        foreach (var property in obj.EnumerateObject())
        {
            if (!string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                continue;

            value = property.Value;
            return true;
        }

        value = default;
        return false;
    }

    static bool TryGetPropertyValueIgnoreCase(
        IReadOnlyDictionary<string, ObservationValue> obj,
        string propertyName,
        out ObservationValue value)
    {
        foreach (var pair in obj)
        {
            if (!string.Equals(pair.Key, propertyName, StringComparison.OrdinalIgnoreCase))
                continue;

            value = pair.Value;
            return true;
        }

        value = default;
        return false;
    }
}
