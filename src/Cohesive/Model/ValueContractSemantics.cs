namespace Cohesive.Model;

/// <summary>Local compatibility of a constant with a portable value type.</summary>
internal enum ValueConstantCompatibility
{
    /// <summary>External type resolution is required to decide compatibility.</summary>
    Unknown = 0,

    /// <summary>Every locally resolvable constraint is satisfied and no unresolved constraint remains.</summary>
    Compatible = 1,

    /// <summary>At least one locally resolvable constraint is violated.</summary>
    Incompatible = 2
}

/// <summary>Shared portable constant/type compatibility used by expression, execution, and site analysis.</summary>
internal static class ValueContractSemantics
{
    const double Int64InclusiveLowerBound = -9_223_372_036_854_775_808d;
    const double Int64ExclusiveUpperBound = 9_223_372_036_854_775_808d;

    /// <summary>Evaluates a non-null, present constant against every locally resolvable part of a portable type.</summary>
    /// <param name="type">Portable type expected by the value contract.</param>
    /// <param name="value">Constant value to evaluate.</param>
    /// <returns>
    /// Compatible or incompatible when local semantics decide the result; otherwise unknown when external type
    /// resolution is required.
    /// </returns>
    public static ValueConstantCompatibility Evaluate(TypeRef? type, ObservationValue value)
    {
        if (type is null)
            return ValueConstantCompatibility.Unknown;
        if (value.Kind is ObservationValueKind.Undefined or ObservationValueKind.Null)
            return ValueConstantCompatibility.Incompatible;

        return type.Match(
            onNamedTypeRef: static _ => ValueConstantCompatibility.Unknown,
            onOpaqueRuntimeTypeRef: static _ => ValueConstantCompatibility.Unknown,
            onScalarTypeRef: scalar => FromBoolean(MatchesScalar(scalar.Kind, value)),
            onEnumTypeRef: @enum => FromBoolean(
                value.Kind == ObservationValueKind.String
                && value.String is { } member
                && @enum.Members.Contains(member, StringComparer.Ordinal)),
            onEntityReferenceTypeRef: _ => FromBoolean(
                value.Kind == ObservationValueKind.String
                && !string.IsNullOrWhiteSpace(value.String)),
            onArrayTypeRef: array => EvaluateArray(array, value),
            onObjectTypeRef: objectType => EvaluateObject(objectType, value),
            onQuantityTypeRef: quantity => FromBoolean(MatchesScalar(quantity.BaseKind, value)),
            onJsonTypeRef: json => FromBoolean(MatchesJson(json.Kind, value)));
    }

    static ValueConstantCompatibility EvaluateArray(ArrayTypeRef type, ObservationValue value)
    {
        if (value.Kind != ObservationValueKind.Array)
            return ValueConstantCompatibility.Incompatible;

        var result = ValueConstantCompatibility.Compatible;
        foreach (var item in value.EnumerateArray())
        {
            var itemResult = Evaluate(type.ElementType, item);
            if (itemResult == ValueConstantCompatibility.Incompatible)
                return itemResult;
            if (itemResult == ValueConstantCompatibility.Unknown)
                result = ValueConstantCompatibility.Unknown;
        }

        return result;
    }

    static ValueConstantCompatibility EvaluateObject(ObjectTypeRef type, ObservationValue value)
    {
        if (value.Kind != ObservationValueKind.Object || value.Fields is null)
            return ValueConstantCompatibility.Incompatible;

        var result = ValueConstantCompatibility.Compatible;
        foreach (var field in type.Fields)
        {
            if (field is null
                || string.IsNullOrWhiteSpace(field.Name)
                || field.Type is null
                || !Enum.IsDefined(field.Cardinality)
                || !Enum.IsDefined(field.Presence)
                || !Enum.IsDefined(field.Nullability))
            {
                result = ValueConstantCompatibility.Unknown;
                continue;
            }
            if (!value.Fields.TryGetValue(field.Name, out var fieldValue))
            {
                if (field.Presence == FieldPresence.Required)
                    return ValueConstantCompatibility.Incompatible;
                continue;
            }

            if (fieldValue.Kind == ObservationValueKind.Undefined)
            {
                if (field.Presence == FieldPresence.Optional)
                    continue;
                return ValueConstantCompatibility.Incompatible;
            }
            if (fieldValue.Kind == ObservationValueKind.Null)
            {
                if (field.Nullability == FieldNullability.Nullable)
                    continue;
                return ValueConstantCompatibility.Incompatible;
            }

            var effectiveType = field.Cardinality == FieldCardinality.Many
                ? new ArrayTypeRef(field.Type)
                : field.Type;
            var fieldResult = Evaluate(effectiveType, fieldValue);
            if (fieldResult == ValueConstantCompatibility.Incompatible)
                return fieldResult;
            if (fieldResult == ValueConstantCompatibility.Unknown)
                result = ValueConstantCompatibility.Unknown;
        }

        return result;
    }

    static ValueConstantCompatibility FromBoolean(bool value) => value
        ? ValueConstantCompatibility.Compatible
        : ValueConstantCompatibility.Incompatible;

    static bool MatchesScalar(ScalarTypeKind kind, ObservationValue value) => kind switch
    {
        ScalarTypeKind.Bool => value.Kind == ObservationValueKind.Bool,
        ScalarTypeKind.Int32 => MatchesInt32(value),
        ScalarTypeKind.Int64 => MatchesInt64(value),
        ScalarTypeKind.Decimal => IsNumeric(value) && value.TryGetDecimal(out _),
        ScalarTypeKind.String => value.Kind == ObservationValueKind.String,
        ScalarTypeKind.Guid => value.Kind == ObservationValueKind.String
            && Guid.TryParse(value.String, out _),
        ScalarTypeKind.Date => value.TryGetDateOnly(out _),
        ScalarTypeKind.DateTime => value.TryGetDateTimeOffset(out _),
        ScalarTypeKind.Instant => value.TryGetInstant(out _),
        ScalarTypeKind.Bytes => value.TryGetBytes(out _),
        _ => false
    };

    static bool IsNumeric(ObservationValue value) =>
        value.Kind is ObservationValueKind.Int64
            or ObservationValueKind.Double
            or ObservationValueKind.Decimal;

    static bool MatchesInt32(ObservationValue value) => value.Kind switch
    {
        ObservationValueKind.Int64 => value.Int64 is >= int.MinValue and <= int.MaxValue,
        ObservationValueKind.Double => double.IsFinite(value.Double)
            && value.Double >= int.MinValue
            && value.Double <= int.MaxValue
            && Math.Truncate(value.Double) == value.Double,
        ObservationValueKind.Decimal => value.Decimal is >= int.MinValue and <= int.MaxValue
            && decimal.Truncate(value.Decimal) == value.Decimal,
        _ => false
    };

    static bool MatchesInt64(ObservationValue value) => value.Kind switch
    {
        ObservationValueKind.Int64 => true,
        // long.MaxValue rounds to 2^63 as a double, so the positive limit must be exclusive.
        ObservationValueKind.Double => double.IsFinite(value.Double)
            && value.Double >= Int64InclusiveLowerBound
            && value.Double < Int64ExclusiveUpperBound
            && Math.Truncate(value.Double) == value.Double,
        ObservationValueKind.Decimal => value.Decimal is >= long.MinValue and <= long.MaxValue
            && decimal.Truncate(value.Decimal) == value.Decimal,
        _ => false
    };

    static bool MatchesJson(JsonTypeKind kind, ObservationValue value) => kind switch
    {
        JsonTypeKind.Any => true,
        JsonTypeKind.Object => value.Kind == ObservationValueKind.Object,
        JsonTypeKind.Array => value.Kind == ObservationValueKind.Array,
        JsonTypeKind.String => value.Kind is ObservationValueKind.String
            or ObservationValueKind.DateTimeOffset
            or ObservationValueKind.DateOnly
            or ObservationValueKind.TimeOnly
            or ObservationValueKind.TimeSpan,
        JsonTypeKind.Number => value.Kind is ObservationValueKind.Int64
            or ObservationValueKind.Double
            or ObservationValueKind.Decimal,
        JsonTypeKind.Boolean => value.Kind == ObservationValueKind.Bool,
        _ => false
    };
}
