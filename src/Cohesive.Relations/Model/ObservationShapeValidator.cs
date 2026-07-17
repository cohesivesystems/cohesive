using System.Globalization;

namespace Cohesive.Relations.Model;

/// <summary>
/// Validates observation payloads against shape semantics.
/// </summary>
public static class ObservationShapeValidator
{
    const int MaxValidationDepth = 64;

    /// <summary>
    /// Validates that an observation adheres to the supplied shape semantics.
    /// </summary>
    /// <param name="observation">Observation payload to validate.</param>
    /// <param name="shape">Expected semantic shape.</param>
    /// <param name="validationError">Validation failure reason when validation fails.</param>
    /// <param name="graph">Optional shape graph used to resolve named type references.</param>
    /// <returns><c>true</c> when the observation satisfies all field and type constraints; otherwise <c>false</c>.</returns>
    /// <exception cref="ArgumentNullException"></exception>
    public static bool TryValidateAgainstShape(
        Observation observation,
        Shape shape,
        out string? validationError,
        ShapeGraph? graph = null)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(shape);

        if (observation.ShapeId != shape.Id)
        {
            validationError = $"Observation shape '{observation.ShapeId.Value}' does not match expected shape '{shape.Id.Value}'.";
            return false;
        }

        for (var ordinal = 0; ordinal < observation.Layout.Count; ordinal++)
        {
            if (!observation.TryGetField(ordinal, out var value))
                continue;

            var fieldName = observation.Layout.FieldNames[ordinal];
            if (!shape.TryGetField(fieldName, out var field))
            {
                validationError = $"Observation contains unknown field '{fieldName}' for shape '{shape.Id.Value}'.";
                return false;
            }

            if (!TryValidateFieldValue(
                    value: value,
                    field: field,
                    graph: graph,
                    context: $"field '{field.Name.Value}'",
                    out validationError))
            {
                return false;
            }
        }

        foreach (var field in shape.Fields)
        {
            if (field.Presence != FieldPresence.Required)
                continue;

            if (!observation.TryGetField(field, out var value)
                || value.Kind is ObservationValueKind.Null or ObservationValueKind.Undefined)
            {
                validationError = $"Observation is missing required field '{field.Name.Value}'.";
                return false;
            }
        }

        validationError = null;
        return true;
    }

    static bool TryValidateFieldValue(
        ObservationValue value,
        FieldDefinition field,
        ShapeGraph? graph,
        string context,
        out string? validationError)
    {
        if (value.Kind is ObservationValueKind.Null or ObservationValueKind.Undefined)
        {
            if (field.Presence == FieldPresence.Required)
            {
                validationError = $"{context} is required and cannot be null.";
                return false;
            }

            if (field.Nullability == FieldNullability.NonNullable)
            {
                validationError = $"{context} is non-nullable and cannot be null.";
                return false;
            }

            validationError = null;
            return true;
        }

        if (field.Cardinality == FieldCardinality.Many)
        {
            if (value.Kind != ObservationValueKind.Array)
            {
                validationError = $"{context} expects an array value.";
                return false;
            }

            var items = value.EnumerateArray();
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item.Kind is ObservationValueKind.Null or ObservationValueKind.Undefined)
                {
                    if (field.Nullability == FieldNullability.NonNullable)
                    {
                        validationError = $"{context} element at index {i.ToString(CultureInfo.InvariantCulture)} is null but the field is non-nullable.";
                        return false;
                    }

                    continue;
                }

                if (!TryMatchType(
                        type: field.Type,
                        value: item,
                        graph: graph,
                        context: $"{context} element at index {i.ToString(CultureInfo.InvariantCulture)}",
                        maxDepth: MaxValidationDepth,
                        out validationError))
                {
                    return false;
                }
            }

            validationError = null;
            return true;
        }

        if (!TryMatchType(
                type: field.Type,
                value: value,
                graph: graph,
                context: context,
                maxDepth: MaxValidationDepth,
                out validationError))
        {
            return false;
        }

        validationError = null;
        return true;
    }

    static bool TryMatchType(
        TypeRef type,
        ObservationValue value,
        ShapeGraph? graph,
        string context,
        int maxDepth,
        out string? validationError)
    {
        if (maxDepth <= 0)
        {
            validationError = $"{context} exceeded maximum validation depth while checking type '{DescribeType(type)}'.";
            return false;
        }

        switch (type)
        {
            case ScalarTypeRef scalar:
                return TryMatchScalarType(scalar.Kind, value, context, out validationError);

            case EnumTypeRef enumType:
            {
                if (!TryGetString(value, out var enumValue))
                {
                    validationError = $"{context} must be a string enum value.";
                    return false;
                }

                if (!enumType.Members.Contains(enumValue, StringComparer.Ordinal))
                {
                    validationError = $"{context} value '{enumValue}' is not a valid member of enum '{enumType.Name}'.";
                    return false;
                }

                validationError = null;
                return true;
            }

            case EntityReferenceTypeRef:
            {
                if (TryGetString(value, out var entityReference) && !string.IsNullOrWhiteSpace(entityReference))
                {
                    validationError = null;
                    return true;
                }

                validationError = $"{context} must contain a non-empty entity reference string.";
                return false;
            }

            case ArrayTypeRef arrayType:
            {
                if (value.Kind != ObservationValueKind.Array)
                {
                    validationError = $"{context} expects an array value.";
                    return false;
                }

                var items = value.EnumerateArray();
                for (var i = 0; i < items.Count; i++)
                {
                    if (!TryMatchType(
                            type: arrayType.ElementType,
                            value: items[i],
                            graph: graph,
                            context: $"{context} element at index {i.ToString(CultureInfo.InvariantCulture)}",
                            maxDepth: maxDepth - 1,
                            out validationError))
                    {
                        return false;
                    }
                }

                validationError = null;
                return true;
            }

            case ObjectTypeRef objectType:
                return TryMatchObjectType(objectType, value, graph, context, maxDepth - 1, out validationError);

            case QuantityTypeRef quantityType:
                return TryMatchQuantityType(quantityType, value, context, out validationError);

            case NamedTypeRef named:
                return TryMatchNamedType(named, value, graph, context, maxDepth - 1, out validationError);

            case OpaqueRuntimeTypeRef opaque:
                return TryMatchOpaqueRuntimeType(opaque, value, context, out validationError);

            case JsonTypeRef json:
                return TryMatchJsonType(json, value, context, out validationError);
        }

        validationError = $"{context} references unsupported type '{type.GetType().Name}'.";
        return false;
    }

    static bool TryMatchOpaqueRuntimeType(
        OpaqueRuntimeTypeRef opaque,
        ObservationValue value,
        string context,
        out string? validationError)
    {
        var matches = opaque.RuntimeType switch
        {
            "DateOnly" => value.TryGetDateOnly(out _),
            "TimeOnly" => value.TryGetTimeOnly(out _),
            _ => false
        };

        if (matches)
        {
            validationError = null;
            return true;
        }

        validationError = $"{context} does not match opaque runtime type '{opaque.RuntimeType}'.";
        return false;
    }

    static bool TryMatchJsonType(
        JsonTypeRef json,
        ObservationValue value,
        string context,
        out string? validationError)
    {
        var matches = json.Kind switch
        {
            JsonTypeKind.Any => value.Kind is not (ObservationValueKind.Null or ObservationValueKind.Undefined),
            JsonTypeKind.Object => value.Kind == ObservationValueKind.Object,
            JsonTypeKind.Array => value.Kind == ObservationValueKind.Array,
            JsonTypeKind.String => value.Kind == ObservationValueKind.String,
            JsonTypeKind.Number => value.Kind is ObservationValueKind.Int64
                or ObservationValueKind.Double
                or ObservationValueKind.Decimal,
            JsonTypeKind.Boolean => value.Kind == ObservationValueKind.Bool,
            _ => false
        };

        if (matches)
        {
            validationError = null;
            return true;
        }

        validationError = $"{context} does not match JSON type '{json.Kind}'.";
        return false;
    }

    static bool TryMatchObjectType(
        ObjectTypeRef objectType,
        ObservationValue value,
        ShapeGraph? graph,
        string context,
        int maxDepth,
        out string? validationError)
    {
        if (value.Kind != ObservationValueKind.Object || value.Fields is null)
        {
            validationError = $"{context} expects an object value.";
            return false;
        }

        foreach (var field in objectType.Fields)
        {
            if (!TryGetPropertyIgnoreCase(value.Fields, field.Name, out var fieldValue))
            {
                if (field.Presence == FieldPresence.Required)
                {
                    validationError = $"{context} is missing required property '{field.Name}'.";
                    return false;
                }

                continue;
            }

            if (fieldValue.Kind is ObservationValueKind.Null or ObservationValueKind.Undefined)
            {
                if (field.Presence == FieldPresence.Required)
                {
                    validationError = $"{context} property '{field.Name}' is required and cannot be null.";
                    return false;
                }

                continue;
            }

            if (!TryMatchType(
                    type: field.Type,
                    value: fieldValue,
                    graph: graph,
                    context: $"{context}.{field.Name}",
                    maxDepth: maxDepth,
                    out validationError))
            {
                return false;
            }
        }

        validationError = null;
        return true;
    }

    static bool TryMatchNamedType(
        NamedTypeRef namedType,
        ObservationValue value,
        ShapeGraph? graph,
        string context,
        int maxDepth,
        out string? validationError)
    {
        if (graph is null)
        {
            validationError = $"{context} references named type '{namedType.TypeId.Value}', but no shape graph was provided for resolution.";
            return false;
        }

        if (!graph.TryGetType(namedType.TypeId, out var definition))
        {
            validationError = $"{context} references missing named type '{namedType.TypeId.Value}'.";
            return false;
        }

        switch (definition)
        {
            case TypeDefinition.Structural structural:
                return TryMatchStructuralType(
                    structural: structural,
                    value: value,
                    graph: graph,
                    context: context,
                    maxDepth: maxDepth,
                    out validationError);

            case TypeDefinition.Enum enumType:
                return TryMatchEnumDefinition(enumType, value, context, out validationError);

            case TypeDefinition.Union union:
                return TryMatchUnionType(union, value, graph, context, maxDepth, out validationError);

            default:
                validationError = $"{context} resolved unsupported named type '{definition.GetType().Name}'.";
                return false;
        }
    }

    static bool TryMatchStructuralType(
        TypeDefinition.Structural structural,
        ObservationValue value,
        ShapeGraph? graph,
        string context,
        int maxDepth,
        out string? validationError)
    {
        if (value.Kind != ObservationValueKind.Object || value.Fields is null)
        {
            validationError = $"{context} expects an object value for type '{structural.Id.Value}'.";
            return false;
        }

        foreach (var field in structural.Fields)
        {
            if (!TryGetPropertyIgnoreCase(value.Fields, field.Name.Value, out var fieldValue))
            {
                if (field.Presence == FieldPresence.Required)
                {
                    validationError = $"{context} is missing required property '{field.Name.Value}' for type '{structural.Id.Value}'.";
                    return false;
                }

                continue;
            }

            if (fieldValue.Kind is ObservationValueKind.Null or ObservationValueKind.Undefined)
            {
                if (field.Presence == FieldPresence.Required || field.Nullability == FieldNullability.NonNullable)
                {
                    validationError = $"{context} property '{field.Name.Value}' for type '{structural.Id.Value}' cannot be null.";
                    return false;
                }

                continue;
            }

            if (field.Cardinality == FieldCardinality.Many)
            {
                if (fieldValue.Kind != ObservationValueKind.Array)
                {
                    validationError = $"{context} property '{field.Name.Value}' for type '{structural.Id.Value}' expects an array value.";
                    return false;
                }

                var items = fieldValue.EnumerateArray();
                for (var i = 0; i < items.Count; i++)
                {
                    var item = items[i];
                    if (item.Kind is ObservationValueKind.Null or ObservationValueKind.Undefined)
                    {
                        if (field.Nullability == FieldNullability.NonNullable)
                        {
                            validationError = $"{context} property '{field.Name.Value}' element at index {i.ToString(CultureInfo.InvariantCulture)} cannot be null.";
                            return false;
                        }

                        continue;
                    }

                    if (!TryMatchType(
                            type: field.Type,
                            value: item,
                            graph: graph,
                            context: $"{context}.{field.Name.Value}[{i.ToString(CultureInfo.InvariantCulture)}]",
                            maxDepth: maxDepth,
                            out validationError))
                    {
                        return false;
                    }
                }

                continue;
            }

            if (!TryMatchType(
                    type: field.Type,
                    value: fieldValue,
                    graph: graph,
                    context: $"{context}.{field.Name.Value}",
                    maxDepth: maxDepth,
                    out validationError))
            {
                return false;
            }
        }

        validationError = null;
        return true;
    }

    static bool TryMatchEnumDefinition(
        TypeDefinition.Enum enumType,
        ObservationValue value,
        string context,
        out string? validationError)
    {
        if (!TryMatchPrimitiveType(enumType.Underlying, value))
        {
            validationError = $"{context} does not match enum type '{enumType.Id.Value}'.";
            return false;
        }

        var literal = value.ToScalarString(CultureInfo.InvariantCulture, ObservationBytesJsonEncoding.Base64String);
        if (literal is null)
        {
            validationError = $"{context} does not match enum type '{enumType.Id.Value}'.";
            return false;
        }

        foreach (var enumValue in enumType.Values)
        {
            if (enumType.Underlying == PrimitiveType.String
                && string.Equals(enumValue.Name, literal, StringComparison.Ordinal))
            {
                validationError = null;
                return true;
            }

            if (string.Equals(enumValue.Value, literal, StringComparison.Ordinal))
            {
                validationError = null;
                return true;
            }
        }

        validationError = $"{context} does not match enum type '{enumType.Id.Value}'.";
        return false;
    }

    static bool TryMatchUnionType(
        TypeDefinition.Union unionType,
        ObservationValue value,
        ShapeGraph graph,
        string context,
        int maxDepth,
        out string? validationError)
    {
        if (value.Kind != ObservationValueKind.Object || value.Fields is null)
        {
            validationError = $"{context} expects an object value for union type '{unionType.Id.Value}'.";
            return false;
        }

        if (!TryGetPropertyIgnoreCase(value.Fields, unionType.Discriminator.FieldName, out var discriminatorValue))
        {
            validationError = $"{context} is missing discriminator field '{unionType.Discriminator.FieldName}' for union type '{unionType.Id.Value}'.";
            return false;
        }

        if (!TryMatchPrimitiveType(unionType.Discriminator.Type, discriminatorValue))
        {
            validationError = $"{context} discriminator field '{unionType.Discriminator.FieldName}' does not match expected primitive type '{unionType.Discriminator.Type}'.";
            return false;
        }

        var discriminator = discriminatorValue.ToScalarString(CultureInfo.InvariantCulture, ObservationBytesJsonEncoding.Base64String);
        var matchingCase = unionType.Cases.FirstOrDefault(x => string.Equals(x.DiscriminatorValue, discriminator, StringComparison.Ordinal));
        if (matchingCase is null)
        {
            validationError = $"{context} discriminator value '{discriminator}' is not valid for union type '{unionType.Id.Value}'.";
            return false;
        }

        if (!TryMatchType(
                type: matchingCase.Type,
                value: value,
                graph: graph,
                context: context,
                maxDepth: maxDepth,
                out validationError))
        {
            return false;
        }

        validationError = null;
        return true;
    }

    static bool TryMatchQuantityType(
        QuantityTypeRef quantityType,
        ObservationValue value,
        string context,
        out string? validationError)
    {
        if (TryMatchScalarType(quantityType.BaseKind, value, context, out _))
        {
            validationError = null;
            return true;
        }

        if (value.Kind != ObservationValueKind.Object || value.Fields is null)
        {
            validationError = $"{context} must be a scalar '{quantityType.BaseKind}' value or a quantity object.";
            return false;
        }

        if (TryGetPropertyIgnoreCase(value.Fields, "baseValue", out var directBaseValue)
            && directBaseValue.Kind is not (ObservationValueKind.Null or ObservationValueKind.Undefined)
            && TryMatchScalarType(quantityType.BaseKind, directBaseValue, context, out _))
        {
            validationError = null;
            return true;
        }

        if (TryGetPropertyIgnoreCase(value.Fields, "value", out var wrappedValue)
            && wrappedValue.Kind == ObservationValueKind.Object
            && wrappedValue.Fields is not null
            && TryGetPropertyIgnoreCase(wrappedValue.Fields, "baseValue", out var wrappedBaseValue)
            && wrappedBaseValue.Kind is not (ObservationValueKind.Null or ObservationValueKind.Undefined)
            && TryMatchScalarType(quantityType.BaseKind, wrappedBaseValue, context, out _))
        {
            validationError = null;
            return true;
        }

        validationError = $"{context} must contain a '{quantityType.BaseKind}' base value for quantity '{quantityType.Quantity}'.";
        return false;
    }

    static bool TryMatchScalarType(
        ScalarTypeKind scalarType,
        ObservationValue value,
        string context,
        out string? validationError)
    {
        var matches = scalarType switch
        {
            ScalarTypeKind.Bool => value.TryGetBoolean(out _),
            ScalarTypeKind.Int32 => value.TryGetInt32(out _),
            ScalarTypeKind.Int64 => value.TryGetInt64(out _),
            ScalarTypeKind.Decimal => value.TryGetDecimal(out _),
            ScalarTypeKind.String => TryGetString(value, out _),
            ScalarTypeKind.Guid => TryGetString(value, out var guidValue) && Guid.TryParse(guidValue, out _),
            ScalarTypeKind.Date => value.TryGetDateOnly(out _),
            ScalarTypeKind.DateTime => value.TryGetDateTimeOffset(out _),
            ScalarTypeKind.Instant => value.TryGetInstant(out _),
            ScalarTypeKind.Bytes => value.Kind == ObservationValueKind.Bytes,
            _ => false
        };

        if (matches)
        {
            validationError = null;
            return true;
        }

        validationError = $"{context} does not match expected scalar type '{scalarType}'.";
        return false;
    }

    static bool TryMatchPrimitiveType(PrimitiveType primitiveType, ObservationValue value)
    {
        if (TryMapPrimitiveType(primitiveType, out var scalarType))
            return TryMatchScalarType(scalarType, value, context: string.Empty, out _);

        return false;
    }

    static bool TryMapPrimitiveType(PrimitiveType primitiveType, out ScalarTypeKind scalarType)
    {
        switch (primitiveType)
        {
            case PrimitiveType.Bool:
                scalarType = ScalarTypeKind.Bool;
                return true;

            case PrimitiveType.Int32:
                scalarType = ScalarTypeKind.Int32;
                return true;

            case PrimitiveType.Int64:
                scalarType = ScalarTypeKind.Int64;
                return true;

            case PrimitiveType.Decimal:
                scalarType = ScalarTypeKind.Decimal;
                return true;

            case PrimitiveType.String:
                scalarType = ScalarTypeKind.String;
                return true;

            case PrimitiveType.Guid:
                scalarType = ScalarTypeKind.Guid;
                return true;

            case PrimitiveType.Date:
                scalarType = ScalarTypeKind.Date;
                return true;

            case PrimitiveType.DateTime:
                scalarType = ScalarTypeKind.DateTime;
                return true;

            case PrimitiveType.Instant:
                scalarType = ScalarTypeKind.Instant;
                return true;

            case PrimitiveType.Bytes:
                scalarType = ScalarTypeKind.Bytes;
                return true;
        }

        scalarType = default;
        return false;
    }

    static bool TryGetString(ObservationValue value, out string result)
    {
        switch (value.Kind)
        {
            case ObservationValueKind.String:
            case ObservationValueKind.DateTimeOffset:
            case ObservationValueKind.DateOnly:
            case ObservationValueKind.TimeOnly:
            case ObservationValueKind.TimeSpan:
            {
                result = value.GetString() ?? string.Empty;
                return true;
            }
            default:
                result = string.Empty;
                return false;
        }
    }

    static bool TryGetPropertyIgnoreCase(
        IReadOnlyDictionary<string, ObservationValue> obj,
        string propertyName,
        out ObservationValue value)
    {
        if (obj.TryGetValue(propertyName, out value))
            return true;

        foreach (var property in obj)
        {
            if (string.Equals(property.Key, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    static string DescribeType(TypeRef type)
    {
        return type switch
        {
            ScalarTypeRef scalar => scalar.Kind.ToString(),
            EnumTypeRef enumType => $"Enum({enumType.Name})",
            EntityReferenceTypeRef entityRef => $"EntityRef({entityRef.Entity.Value})",
            ArrayTypeRef array => $"Array({DescribeType(array.ElementType)})",
            ObjectTypeRef => "Object",
            QuantityTypeRef quantity => $"Quantity({quantity.Quantity},{quantity.BaseKind})",
            NamedTypeRef named => $"Named({named.TypeId.Value})",
            OpaqueRuntimeTypeRef opaque => $"Opaque({opaque.RuntimeType})",
            JsonTypeRef json => $"Json({json.Kind})",
            _ => type.GetType().Name
        };
    }
}
