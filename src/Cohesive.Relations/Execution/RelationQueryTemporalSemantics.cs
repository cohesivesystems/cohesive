using Cohesive.Relations.IR;

namespace Cohesive.Relations.Execution;

/// <summary>Result of evaluating one canonical temporal match over materialized operands.</summary>
internal enum RelationQueryTemporalEvaluationKind
{
    /// <summary>The temporal operands conclusively do not match.</summary>
    NoMatch = 0,

    /// <summary>The temporal operands conclusively match.</summary>
    Match = 1,

    /// <summary>At least one materialized operand does not represent a value in the declared temporal domain.</summary>
    InvalidOperand = 2,

    /// <summary>At least one interval has a finite lower bound after its finite upper bound.</summary>
    InvalidInterval = 3
}

/// <summary>Classification of a materialized canonical temporal interval.</summary>
internal enum RelationQueryTemporalIntervalKind
{
    /// <summary>The interval contains one or more points.</summary>
    NonEmpty = 0,

    /// <summary>The interval is valid but contains no points.</summary>
    Empty = 1,

    /// <summary>A finite bound does not represent a value in the declared temporal domain.</summary>
    InvalidOperand = 2,

    /// <summary>The finite lower bound follows the finite upper bound.</summary>
    InvalidInterval = 3
}

/// <summary>Materialized state of one canonical temporal interval bound.</summary>
internal enum RelationQueryTemporalBoundValueKind
{
    /// <summary>The bound has a concrete endpoint.</summary>
    Finite = 0,

    /// <summary>The bound is explicitly unbounded.</summary>
    Unbounded = 1,

    /// <summary>The bound could not be materialized as a concrete endpoint or an explicit unbounded endpoint.</summary>
    Invalid = 2
}

/// <summary>A canonical temporal bound after its expression value has been materialized.</summary>
internal readonly record struct RelationQueryTemporalBoundValue
{
    RelationQueryTemporalBoundValue(
        RelationQueryTemporalBoundValueKind kind,
        ObservationValue value,
        TemporalBoundaryInclusion inclusion)
    {
        Kind = kind;
        Value = value;
        Inclusion = inclusion;
    }

    /// <summary>Materialized bound state.</summary>
    public RelationQueryTemporalBoundValueKind Kind { get; }

    /// <summary>Finite endpoint, or <see cref="ObservationValue.Undefined"/> for another state.</summary>
    public ObservationValue Value { get; }

    /// <summary>Whether a finite endpoint belongs to its interval.</summary>
    public TemporalBoundaryInclusion Inclusion { get; }

    /// <summary>Creates a concrete finite bound.</summary>
    public static RelationQueryTemporalBoundValue Finite(
        ObservationValue value,
        TemporalBoundaryInclusion inclusion) =>
        new(RelationQueryTemporalBoundValueKind.Finite, value, inclusion);

    /// <summary>Creates an explicit unbounded endpoint.</summary>
    public static RelationQueryTemporalBoundValue Unbounded() =>
        new(
            RelationQueryTemporalBoundValueKind.Unbounded,
            ObservationValue.Undefined,
            TemporalBoundaryInclusion.Exclusive);

    /// <summary>Creates an invalid materialized endpoint.</summary>
    public static RelationQueryTemporalBoundValue Invalid() =>
        new(
            RelationQueryTemporalBoundValueKind.Invalid,
            ObservationValue.Undefined,
            TemporalBoundaryInclusion.Exclusive);
}

/// <summary>A canonical temporal interval after both endpoint expressions have been materialized.</summary>
/// <param name="Lower">Materialized lower endpoint.</param>
/// <param name="Upper">Materialized upper endpoint.</param>
internal readonly record struct RelationQueryTemporalIntervalValue(
    RelationQueryTemporalBoundValue Lower,
    RelationQueryTemporalBoundValue Upper);

/// <summary>Pure canonical value semantics for valid-time point and interval matching.</summary>
internal static class RelationQueryTemporalSemantics
{
    /// <summary>
    /// Resolves one declared bound from its evaluated expression value. A CLR <see langword="null"/>
    /// indicates that an expression-backed bound was not evaluated.
    /// </summary>
    public static RelationQueryTemporalBoundValue ResolveBound(
        TemporalIntervalBound bound,
        ObservationValue? evaluatedValue = null)
    {
        ArgumentNullException.ThrowIfNull(bound);

        return bound switch
        {
            UnboundedTemporalIntervalBound => RelationQueryTemporalBoundValue.Unbounded(),
            ExpressionTemporalIntervalBound expressionBound => ResolveExpressionBound(
                expressionBound,
                evaluatedValue),
            _ => RelationQueryTemporalBoundValue.Invalid()
        };
    }

    /// <summary>Classifies whether a materialized interval is non-empty, empty, or invalid.</summary>
    public static RelationQueryTemporalIntervalKind ClassifyInterval(
        ScalarTypeKind domain,
        RelationQueryTemporalIntervalValue interval)
    {
        ValidateDomain(domain);
        if (interval.Lower.Kind == RelationQueryTemporalBoundValueKind.Invalid
            || interval.Upper.Kind == RelationQueryTemporalBoundValueKind.Invalid)
        {
            return RelationQueryTemporalIntervalKind.InvalidOperand;
        }

        if (interval.Lower.Kind == RelationQueryTemporalBoundValueKind.Unbounded
            || interval.Upper.Kind == RelationQueryTemporalBoundValueKind.Unbounded)
        {
            var finite = interval.Lower.Kind == RelationQueryTemporalBoundValueKind.Finite
                ? interval.Lower
                : interval.Upper;
            return finite.Kind == RelationQueryTemporalBoundValueKind.Finite
                && !TryCompare(domain, finite.Value, finite.Value, out _)
                    ? RelationQueryTemporalIntervalKind.InvalidOperand
                    : RelationQueryTemporalIntervalKind.NonEmpty;
        }

        if (!TryCompare(domain, interval.Lower.Value, interval.Upper.Value, out var compared))
            return RelationQueryTemporalIntervalKind.InvalidOperand;
        if (compared > 0)
            return RelationQueryTemporalIntervalKind.InvalidInterval;
        if (!RelationQueryTemporalValueSemantics.TryGetOrdinal(
                domain,
                interval.Lower.Value,
                out var first)
            || !RelationQueryTemporalValueSemantics.TryGetOrdinal(
                domain,
                interval.Upper.Value,
                out var last))
        {
            return RelationQueryTemporalIntervalKind.InvalidOperand;
        }

        if (interval.Lower.Inclusion == TemporalBoundaryInclusion.Exclusive)
            first++;
        if (interval.Upper.Inclusion == TemporalBoundaryInclusion.Exclusive)
            last--;
        return first <= last
            ? RelationQueryTemporalIntervalKind.NonEmpty
            : RelationQueryTemporalIntervalKind.Empty;
    }

    /// <summary>Evaluates whether a temporal point belongs to a materialized interval.</summary>
    public static RelationQueryTemporalEvaluationKind PointInInterval(
        ScalarTypeKind domain,
        ObservationValue point,
        RelationQueryTemporalIntervalValue interval)
    {
        ValidateDomain(domain);
        if (!TryCompare(domain, point, point, out _))
            return RelationQueryTemporalEvaluationKind.InvalidOperand;

        var intervalKind = ClassifyInterval(domain, interval);
        if (intervalKind == RelationQueryTemporalIntervalKind.InvalidOperand)
            return RelationQueryTemporalEvaluationKind.InvalidOperand;
        if (intervalKind == RelationQueryTemporalIntervalKind.InvalidInterval)
            return RelationQueryTemporalEvaluationKind.InvalidInterval;
        if (intervalKind == RelationQueryTemporalIntervalKind.Empty)
            return RelationQueryTemporalEvaluationKind.NoMatch;

        if (!SatisfiesLowerBound(domain, point, interval.Lower)
            || !SatisfiesUpperBound(domain, point, interval.Upper))
        {
            return RelationQueryTemporalEvaluationKind.NoMatch;
        }

        return RelationQueryTemporalEvaluationKind.Match;
    }

    /// <summary>Evaluates whether two materialized intervals have a non-empty intersection.</summary>
    public static RelationQueryTemporalEvaluationKind IntervalsOverlap(
        ScalarTypeKind domain,
        RelationQueryTemporalIntervalValue left,
        RelationQueryTemporalIntervalValue right)
    {
        ValidateDomain(domain);
        var leftKind = ClassifyInterval(domain, left);
        var rightKind = ClassifyInterval(domain, right);
        if (leftKind == RelationQueryTemporalIntervalKind.InvalidOperand
            || rightKind == RelationQueryTemporalIntervalKind.InvalidOperand)
        {
            return RelationQueryTemporalEvaluationKind.InvalidOperand;
        }
        if (leftKind == RelationQueryTemporalIntervalKind.InvalidInterval
            || rightKind == RelationQueryTemporalIntervalKind.InvalidInterval)
        {
            return RelationQueryTemporalEvaluationKind.InvalidInterval;
        }
        if (leftKind == RelationQueryTemporalIntervalKind.Empty
            || rightKind == RelationQueryTemporalIntervalKind.Empty)
        {
            return RelationQueryTemporalEvaluationKind.NoMatch;
        }

        return EndsBefore(domain, left.Upper, right.Lower)
            || EndsBefore(domain, right.Upper, left.Lower)
                ? RelationQueryTemporalEvaluationKind.NoMatch
                : RelationQueryTemporalEvaluationKind.Match;
    }

    /// <summary>
    /// Compares two values in one exact temporal domain without coercing between Date, DateTime, and Instant.
    /// </summary>
    public static bool TryCompare(
        ScalarTypeKind domain,
        ObservationValue left,
        ObservationValue right,
        out int comparison) =>
        RelationQueryTemporalValueSemantics.TryCompare(domain, left, right, out comparison);

    static RelationQueryTemporalBoundValue ResolveExpressionBound(
        ExpressionTemporalIntervalBound bound,
        ObservationValue? evaluatedValue)
    {
        if (evaluatedValue is not { } value)
            return RelationQueryTemporalBoundValue.Invalid();
        if (value.Kind == ObservationValueKind.Null)
        {
            return bound.NullBehavior == TemporalNullBoundBehavior.Unbounded
                ? RelationQueryTemporalBoundValue.Unbounded()
                : RelationQueryTemporalBoundValue.Invalid();
        }
        if (value.Kind == ObservationValueKind.Undefined)
            return RelationQueryTemporalBoundValue.Invalid();

        return RelationQueryTemporalBoundValue.Finite(value, bound.Inclusion);
    }

    static bool SatisfiesLowerBound(
        ScalarTypeKind domain,
        ObservationValue point,
        RelationQueryTemporalBoundValue lower)
    {
        if (lower.Kind == RelationQueryTemporalBoundValueKind.Unbounded)
            return true;

        _ = TryCompare(domain, point, lower.Value, out var compared);
        return compared > 0
            || compared == 0 && lower.Inclusion == TemporalBoundaryInclusion.Inclusive;
    }

    static bool SatisfiesUpperBound(
        ScalarTypeKind domain,
        ObservationValue point,
        RelationQueryTemporalBoundValue upper)
    {
        if (upper.Kind == RelationQueryTemporalBoundValueKind.Unbounded)
            return true;

        _ = TryCompare(domain, point, upper.Value, out var compared);
        return compared < 0
            || compared == 0 && upper.Inclusion == TemporalBoundaryInclusion.Inclusive;
    }

    static bool EndsBefore(
        ScalarTypeKind domain,
        RelationQueryTemporalBoundValue upper,
        RelationQueryTemporalBoundValue lower)
    {
        if (upper.Kind == RelationQueryTemporalBoundValueKind.Unbounded
            || lower.Kind == RelationQueryTemporalBoundValueKind.Unbounded)
        {
            return false;
        }

        _ = RelationQueryTemporalValueSemantics.TryGetOrdinal(domain, upper.Value, out var last);
        _ = RelationQueryTemporalValueSemantics.TryGetOrdinal(domain, lower.Value, out var first);
        if (upper.Inclusion == TemporalBoundaryInclusion.Exclusive)
            last--;
        if (lower.Inclusion == TemporalBoundaryInclusion.Exclusive)
            first++;
        return last < first;
    }

    static void ValidateDomain(ScalarTypeKind domain)
    {
        if (domain is not (ScalarTypeKind.Date or ScalarTypeKind.DateTime or ScalarTypeKind.Instant))
        {
            throw new ArgumentOutOfRangeException(
                nameof(domain),
                domain,
                "Temporal matching requires the Date, DateTime, or Instant domain.");
        }
    }
}
