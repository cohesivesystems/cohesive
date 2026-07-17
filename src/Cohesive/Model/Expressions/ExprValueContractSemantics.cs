namespace Cohesive.Model.Expressions;

/// <summary>Local compatibility of a constant with a portable value type.</summary>
internal enum ExprConstantCompatibility
{
    /// <summary>External type resolution is required to decide compatibility.</summary>
    Unknown = 0,

    /// <summary>Every locally resolvable constraint is satisfied and no unresolved constraint remains.</summary>
    Compatible = 1,

    /// <summary>At least one locally resolvable constraint is violated.</summary>
    Incompatible = 2
}

/// <summary>Shared portable constant/type compatibility used by expression and site analysis.</summary>
internal static class ExprValueContractSemantics
{
    const double Int64InclusiveLowerBound = -9_223_372_036_854_775_808d;
    const double Int64ExclusiveUpperBound = 9_223_372_036_854_775_808d;

    /// <summary>Evaluates a non-null, present constant against every locally resolvable part of a portable type.</summary>
    /// <param name="type">Portable type expected by the expression boundary.</param>
    /// <param name="value">Constant value to evaluate.</param>
    /// <returns>
    /// Compatible or incompatible when local semantics decide the result; otherwise unknown when external type
    /// resolution is required.
    /// </returns>
    public static ExprConstantCompatibility Evaluate(TypeRef? type, ObservationValue value)
    {
        if (type is null)
            return ExprConstantCompatibility.Unknown;
        if (value.Kind is ObservationValueKind.Undefined or ObservationValueKind.Null)
            return ExprConstantCompatibility.Incompatible;

        return type.Match(
            onNamedTypeRef: static _ => ExprConstantCompatibility.Unknown,
            onOpaqueRuntimeTypeRef: static _ => ExprConstantCompatibility.Unknown,
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

    static ExprConstantCompatibility EvaluateArray(ArrayTypeRef type, ObservationValue value)
    {
        if (value.Kind != ObservationValueKind.Array)
            return ExprConstantCompatibility.Incompatible;

        var result = ExprConstantCompatibility.Compatible;
        foreach (var item in value.EnumerateArray())
        {
            var itemResult = Evaluate(type.ElementType, item);
            if (itemResult == ExprConstantCompatibility.Incompatible)
                return itemResult;
            if (itemResult == ExprConstantCompatibility.Unknown)
                result = ExprConstantCompatibility.Unknown;
        }

        return result;
    }

    static ExprConstantCompatibility EvaluateObject(ObjectTypeRef type, ObservationValue value)
    {
        if (value.Kind != ObservationValueKind.Object || value.Fields is null)
            return ExprConstantCompatibility.Incompatible;

        var result = ExprConstantCompatibility.Compatible;
        foreach (var field in type.Fields)
        {
            if (field is null
                || string.IsNullOrWhiteSpace(field.Name)
                || field.Type is null)
            {
                result = ExprConstantCompatibility.Unknown;
                continue;
            }
            if (!value.Fields.TryGetValue(field.Name, out var fieldValue))
            {
                if (field.Presence == FieldPresence.Required)
                    return ExprConstantCompatibility.Incompatible;
                continue;
            }

            var fieldResult = Evaluate(field.Type, fieldValue);
            if (fieldResult == ExprConstantCompatibility.Incompatible)
                return fieldResult;
            if (fieldResult == ExprConstantCompatibility.Unknown)
                result = ExprConstantCompatibility.Unknown;
        }

        return result;
    }

    static ExprConstantCompatibility FromBoolean(bool value) => value
        ? ExprConstantCompatibility.Compatible
        : ExprConstantCompatibility.Incompatible;

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
