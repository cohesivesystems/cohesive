using System.Globalization;
using Cohesive.Relations.Model;

namespace Cohesive.Relations.Queries;

/// <summary>
/// In-memory evaluator for structured observation predicates.
/// </summary>
public static class EntityPredicateEvaluator
{
    /// <summary>
    /// Evaluates a structured predicate against an observation in memory.
    /// </summary>
    public static bool Evaluate(Observation observation, EntityPredicate predicate)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(predicate);
        return Evaluate(ObservationValue.FromObject(observation.Fields), predicate);
    }

    /// <summary>
    /// Evaluates a structured predicate against an object-shaped observation value in memory.
    /// </summary>
    public static bool Evaluate(ObservationValue root, EntityPredicate predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);

        var normalized = predicate.Predicate.Normalize();
        return predicate.Scope is { } scope 
            ? EvaluateScoped(root, scope, normalized)
            : EvaluateFieldPredicate(root, normalized);
    }

    static bool TryResolveField(
        Observation observation,
        FieldPath field,
        out ObservationValue value,
        out bool exists
        )
    {
        ArgumentNullException.ThrowIfNull(observation);
        return TryResolveField(ObservationValue.FromObject(observation.Fields), field, out value, out exists);
    }

    static bool EvaluateScoped(ObservationValue root, FieldPath scope, BoolExpr<FieldPredicate> predicate)
    {
        foreach (var candidate in ResolveScopeCandidates(root, scope))
        {
            if (candidate.Kind != ObservationValueKind.Object)
                continue;

            if (EvaluateFieldPredicate(candidate, predicate))
                return true;
        }

        return false;
    }

    static bool EvaluateFieldPredicate(ObservationValue current, BoolExpr<FieldPredicate> expr) => expr switch
    {
        Atom<FieldPredicate> atom => EvaluateFieldPredicate(current, atom.Term),
        And<FieldPredicate> conjunction => conjunction.Terms.All(term => EvaluateFieldPredicate(current, term)),
        Or<FieldPredicate> disjunction => disjunction.Terms.Any(term => EvaluateFieldPredicate(current, term)),
        Not<FieldPredicate> negation => !EvaluateFieldPredicate(current, negation.Term),
        _ => throw new InvalidOperationException($"Unknown boolean-expression node '{expr.GetType().Name}'.")
    };

    static bool EvaluateFieldPredicate(ObservationValue current, FieldPredicate predicate)
    {
        if (!TryResolveField(current, predicate.Field, out var value, out var exists))
            return false;

        return EvaluateValuePredicate(value, exists, predicate.Predicate.Normalize());
    }

    static bool EvaluateValuePredicate(ObservationValue value, bool exists, BoolExpr<ValuePredicate> expr) => expr switch
    {
        Atom<ValuePredicate> atom => EvaluateValuePredicate(value, exists, atom.Term),
        And<ValuePredicate> conjunction => conjunction.Terms.All(term => EvaluateValuePredicate(value, exists, term)),
        Or<ValuePredicate> disjunction => disjunction.Terms.Any(term => EvaluateValuePredicate(value, exists, term)),
        Not<ValuePredicate> negation => !EvaluateValuePredicate(value, exists, negation.Term),
        _ => throw new InvalidOperationException($"Unknown boolean-expression node '{expr.GetType().Name}'.")
    };

    static bool EvaluateValuePredicate(ObservationValue value, bool exists, ValuePredicate predicate)
    {
        if (predicate is ExistsValuePredicate)
            return exists;

        if (!exists)
            return false;

        return predicate switch
        {
            ExactValuePredicate exact => MatchesExact(value, exact),
            BoolValuePredicate flag => ObservationValue.DeepEquals(value, ObservationValue.FromBool(flag.Value)),
            IntValuePredicate integer => ObservationValue.DeepEquals(value, ObservationValue.FromInt64(integer.Value)),
            LongValuePredicate integer => ObservationValue.DeepEquals(value, ObservationValue.FromInt64(integer.Value)),
            DoubleValuePredicate number => ObservationValue.DeepEquals(value, ObservationValue.FromDouble(number.Value)),
            DecimalValuePredicate number => ObservationValue.DeepEquals(value, ObservationValue.FromDecimal(number.Value)),
            DateValuePredicate date => value.TryGetDateTimeOffset(out var actual) && actual == date.Value,
            PrefixValuePredicate prefix => TryGetText(value, out var text) && text.StartsWith(prefix.Prefix, GetStringComparison(prefix.CaseSensitive)),
            SuffixValuePredicate suffix => TryGetText(value, out var text) && text.EndsWith(suffix.Suffix, GetStringComparison(suffix.CaseSensitive)),
            ContainsValuePredicate contains => TryGetText(value, out var text) && text.Contains(contains.Value, GetStringComparison(contains.CaseSensitive)),
            FullTextValuePredicate fullText => TryGetText(value, out var text) && text.Contains(fullText.Text, StringComparison.OrdinalIgnoreCase),
            DateRangeValuePredicate range => MatchesDateRange(value, range),
            NumberRangeValuePredicate range => MatchesNumberRange(value, range),
            InValuePredicate set => MatchesSetMembership(value, set),
            AnyValuePredicate any => MatchesAnyValue(value, any),
            AnyFieldPredicate any => MatchesAnyField(value, any),
            GeoDistanceValuePredicate distance => MatchesGeoDistance(value, distance),
            ExistsValuePredicate => exists,
            _ => throw new InvalidOperationException($"Unsupported value-predicate type '{predicate.GetType().Name}'.")
        };
    }

    static StringComparison GetStringComparison(bool caseSensitive) =>
        caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    static bool MatchesExact(ObservationValue value, ExactValuePredicate exact) =>
        exact.CaseSensitive
            ? ObservationValue.DeepEquals(value, ObservationValue.FromString(exact.Value))
            : TryGetText(value, out var text) && string.Equals(text, exact.Value, StringComparison.OrdinalIgnoreCase);

    static bool MatchesDateRange(ObservationValue value, DateRangeValuePredicate range)
    {
        if (!value.TryGetDateTimeOffset(out var actual))
            return false;

        if (range.Start is { } start)
        {
            var inRange = range.StartExclusive == true
                ? actual > start
                : actual >= start;
            if (!inRange)
                return false;
        }

        if (range.End is { } end)
        {
            var inRange = range.EndExclusive == true
                ? actual < end
                : actual <= end;
            if (!inRange)
                return false;
        }

        return true;
    }

    static bool MatchesNumberRange(ObservationValue value, NumberRangeValuePredicate range)
    {
        if (!value.TryGetDouble(out var actual))
            return false;

        if (range.Start is { } start)
        {
            var inRange = range.StartExclusive == true
                ? actual > start
                : actual >= start;
            if (!inRange)
                return false;
        }

        if (range.End is { } end)
        {
            var inRange = range.EndExclusive == true
                ? actual < end
                : actual <= end;
            if (!inRange)
                return false;
        }

        return true;
    }

    static bool MatchesSetMembership(ObservationValue value, InValuePredicate set)
    {
        foreach (var candidate in set.Values)
        {
            if (ObservationValue.DeepEquals(value, ToObservationValue(candidate)))
                return true;
        }

        return false;
    }

    static bool MatchesAnyValue(ObservationValue value, AnyValuePredicate predicate)
    {
        if (value.Kind != ObservationValueKind.Array || value.Array.IsDefault)
            return false;

        var normalized = predicate.Predicate.Normalize();
        foreach (var item in value.Array)
        {
            if (EvaluateValuePredicate(item, exists: true, normalized))
                return true;
        }

        return false;
    }

    static bool MatchesAnyField(ObservationValue value, AnyFieldPredicate predicate)
    {
        if (value.Kind != ObservationValueKind.Array || value.Array.IsDefault)
            return false;

        var normalized = predicate.Predicate.Normalize();
        foreach (var item in value.Array)
        {
            if (item.Kind != ObservationValueKind.Object)
                continue;

            if (EvaluateFieldPredicate(item, normalized))
                return true;
        }

        return false;
    }

    static bool MatchesGeoDistance(ObservationValue value, GeoDistanceValuePredicate predicate)
    {
        if (!TryGetCoordinates(value, out var latitude, out var longitude))
            return false;

        return CalculateDistanceMiles(
            latitude1: latitude,
            longitude1: longitude,
            latitude2: predicate.Latitude,
            longitude2: predicate.Longitude) <= predicate.DistanceMi;
    }

    static bool TryResolveField(
        ObservationValue current,
        FieldPath field,
        out ObservationValue value,
        out bool exists)
    {
        value = current;
        exists = true;

        foreach (var segment in field.Segments)
        {
            switch (segment.Kind)
            {
                case SegmentKind.Field:
                    if (value.Kind != ObservationValueKind.Object || value.Fields is null || !value.Fields.TryGetValue(segment.Segment!, out value))
                    {
                        value = default;
                        exists = false;
                        return true;
                    }

                    break;
                case SegmentKind.Element:
                    throw new NotSupportedException(
                        $"In-memory field evaluation does not support element segment '{field}'. Use '{nameof(AnyValuePredicate)}', '{nameof(AnyFieldPredicate)}', or a scoped predicate.");
                default:
                    throw new InvalidOperationException($"Unsupported field-path segment kind '{segment.Kind}'.");
            }
        }

        return true;
    }

    static IEnumerable<ObservationValue> ResolveScopeCandidates(ObservationValue root, FieldPath scope)
    {
        IEnumerable<ObservationValue> current = [root];
        foreach (var segment in scope.Segments)
        {
            current = segment.Kind switch
            {
                SegmentKind.Field => ResolveObjectSegment(current, segment.Segment!),
                SegmentKind.Element => ResolveArrayElements(current),
                _ => throw new InvalidOperationException($"Unsupported field-path segment kind '{segment.Kind}'.")
            };
        }

        foreach (var candidate in current)
        {
            if (candidate.Kind == ObservationValueKind.Array && !candidate.Array.IsDefault)
            {
                foreach (var item in candidate.Array)
                    yield return item;
                continue;
            }

            yield return candidate;
        }
    }

    static IEnumerable<ObservationValue> ResolveObjectSegment(IEnumerable<ObservationValue> values, string propertyName)
    {
        foreach (var value in values)
        {
            if (value.Kind != ObservationValueKind.Object || value.Fields is null)
                continue;

            if (value.Fields.TryGetValue(propertyName, out var property))
                yield return property;
        }
    }

    static IEnumerable<ObservationValue> ResolveArrayElements(IEnumerable<ObservationValue> values)
    {
        foreach (var value in values)
        {
            if (value.Kind != ObservationValueKind.Array || value.Array.IsDefault)
                continue;

            foreach (var item in value.Array)
                yield return item;
        }
    }

    static ObservationValue ToObservationValue(object value) => Guard.RequireNotNull(value) switch
    {
        ObservationValue observed => observed,
        string text => ObservationValue.FromString(text),
        bool flag => ObservationValue.FromBool(flag),
        byte number => ObservationValue.FromInt64(number),
        short number => ObservationValue.FromInt64(number),
        int number => ObservationValue.FromInt64(number),
        long number => ObservationValue.FromInt64(number),
        float number => ObservationValue.FromDouble(number),
        double number => ObservationValue.FromDouble(number),
        decimal number => ObservationValue.FromDecimal(number),
        DateTimeOffset dateTimeOffset => ObservationValue.FromDateTimeOffset(dateTimeOffset),
        DateTime dateTime => ObservationValue.FromDateTimeOffset(new DateTimeOffset(dateTime)),
        DateOnly dateOnly => ObservationValue.FromDateOnly(dateOnly),
        TimeOnly timeOnly => ObservationValue.FromTimeOnly(timeOnly),
        TimeSpan timeSpan => ObservationValue.FromTimeSpan(timeSpan),
        _ => throw new NotSupportedException($"In-memory set membership does not support CLR type '{value.GetType().FullName}'.")
    };

    static bool TryGetCoordinates(ObservationValue value, out double latitude, out double longitude)
    {
        latitude = 0;
        longitude = 0;

        if (value.Kind == ObservationValueKind.Array
            && !value.Array.IsDefault
            && value.Array.Length >= 2)
        {
            if (value.Array[0].TryGetDouble(out latitude) && value.Array[1].TryGetDouble(out longitude))
                return true;
        }

        if (value.Kind != ObservationValueKind.Object || value.Fields is null)
            return false;

        return TryGetCoordinate(value.Fields, LatitudeCoordinateNames, out latitude)
            && TryGetCoordinate(value.Fields, LongitudeCoordinateNames, out longitude);
    }

    static readonly string[] LatitudeCoordinateNames = ["latitude", "Latitude", "lat", "Lat"];
    static readonly string[] LongitudeCoordinateNames = ["longitude", "Longitude", "lon", "Lon"];
    
    static bool TryGetCoordinate(
        IReadOnlyDictionary<string, ObservationValue> fields,
        string[] names,
        out double value)
    {
        foreach (var name in names)
        {
            if (fields.TryGetValue(name, out var candidate) && candidate.TryGetDouble(out value))
                return true;
        }

        value = 0;
        return false;
    }

    static double CalculateDistanceMiles(double latitude1, double longitude1, double latitude2, double longitude2)
    {
        static double ToRadians(double value) => value * Math.PI / 180d;

        const double earthRadiusMiles = 3958.7613d;
        var dLatitude = ToRadians(latitude2 - latitude1);
        var dLongitude = ToRadians(longitude2 - longitude1);
        var startLatitude = ToRadians(latitude1);
        var endLatitude = ToRadians(latitude2);

        var a =
            Math.Sin(dLatitude / 2d) * Math.Sin(dLatitude / 2d)
            + Math.Cos(startLatitude) * Math.Cos(endLatitude) * Math.Sin(dLongitude / 2d) * Math.Sin(dLongitude / 2d);
        var c = 2d * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1d - a));
        return earthRadiusMiles * c;
    }

    static bool TryGetText(ObservationValue value, out string text)
    {
        var resolved = value.GetString();
        if (resolved is null)
        {
            text = string.Empty;
            return false;
        }

        text = resolved;
        return true;
    }

    internal static int CompareFieldValues(Observation left, Observation right, FieldPath path)
    {
        TryResolveField(left, path, out var leftValue, out var leftExists);
        TryResolveField(right, path, out var rightValue, out var rightExists);

        if (!leftExists && !rightExists)
            return 0;
        if (!leftExists)
            return 1;
        if (!rightExists)
            return -1;

        return CompareValues(leftValue, rightValue);
    }

    static int CompareValues(ObservationValue left, ObservationValue right)
    {
        if (left.Kind is ObservationValueKind.Null or ObservationValueKind.Undefined)
            return right.Kind is ObservationValueKind.Null or ObservationValueKind.Undefined ? 0 : 1;
        if (right.Kind is ObservationValueKind.Null or ObservationValueKind.Undefined)
            return -1;

        if (left.TryGetDateTimeOffset(out var leftDateTime) && right.TryGetDateTimeOffset(out var rightDateTime))
            return leftDateTime.CompareTo(rightDateTime);
        if (left.TryGetDouble(out var leftNumber) && right.TryGetDouble(out var rightNumber))
            return leftNumber.CompareTo(rightNumber);
        if (left.TryGetBoolean(out var leftBool) && right.TryGetBoolean(out var rightBool))
            return leftBool.CompareTo(rightBool);
        if (TryGetText(left, out var leftText) && TryGetText(right, out var rightText))
            return StringComparer.Ordinal.Compare(leftText, rightText);

        return StringComparer.Ordinal.Compare(
            left.ToScalarString(CultureInfo.InvariantCulture, ObservationBytesJsonEncoding.Base64String),
            right.ToScalarString(CultureInfo.InvariantCulture, ObservationBytesJsonEncoding.Base64String));
    }
}
