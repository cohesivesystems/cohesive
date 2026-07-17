using System.Linq.Expressions;
using Cohesive.Relations.IR;
using Cohesive.Relations.Model;

namespace Cohesive.Relations.Authoring;

/// <summary>
/// Lowers typed temporal operands while constructing one explicit canonical temporal-join match.
/// </summary>
/// <remarks>
/// The builder is transient and valid only during its enclosing temporal-join callback. It never
/// compiles or executes an operand expression. It retains authoring-only provenance so the returned
/// match, its intervals, its bounds, its exact temporal domain, and each operand's join side can be
/// validated before the canonical temporal-join node is committed.
/// </remarks>
public sealed class RelationQueryExpressionTemporalMatchBuilder
{
    readonly RelationQueryExpressionAuthoring owner;
    readonly string sourceReference;
    readonly Func<RelationQueryExpressionValueBinding, bool> isLeftBindingVisible;
    readonly Func<RelationQueryExpressionValueBinding, bool> isRightBindingVisible;
    readonly Dictionary<ExpressionTemporalIntervalBound, TemporalOperand> expressionBounds =
        new(ReferenceEqualityComparer.Instance);
    readonly HashSet<UnboundedTemporalIntervalBound> unboundedBounds =
        new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<TemporalInterval, TemporalIntervalOperands> intervals =
        new(ReferenceEqualityComparer.Instance);
    readonly HashSet<TemporalJoinMatch> matches = new(ReferenceEqualityComparer.Instance);
    bool isActive = true;

    internal RelationQueryExpressionTemporalMatchBuilder(
        RelationQueryExpressionAuthoring owner,
        string sourceReference,
        Func<RelationQueryExpressionValueBinding, bool> isLeftBindingVisible,
        Func<RelationQueryExpressionValueBinding, bool> isRightBindingVisible)
    {
        this.owner = owner;
        this.sourceReference = sourceReference;
        this.isLeftBindingVisible = isLeftBindingVisible;
        this.isRightBindingVisible = isRightBindingVisible;
    }

    internal void Seal() => isActive = false;

    internal void RequireOwnedMatch(TemporalJoinMatch match)
    {
        if (!matches.Contains(match))
        {
            throw new ArgumentException(
                "The temporal-match callback must return a match created by the supplied match builder. "
                + "Raw or previously authored temporal matches cannot retain the operand provenance needed "
                + "to validate join-side scope.",
                nameof(match));
        }
    }

    /// <summary>Creates an expression-backed temporal interval endpoint.</summary>
    /// <param name="value">Temporal value lambda whose parameters correspond to <paramref name="bindings"/>.</param>
    /// <param name="bindings">Visible bindings corresponding positionally to the value parameters.</param>
    /// <param name="inclusion">Whether the endpoint belongs to the interval.</param>
    /// <param name="nullBehavior">How an explicitly present null endpoint is interpreted.</param>
    /// <param name="operandSourceReference">Optional stable producer reference for this operand.</param>
    /// <returns>A canonical expression-backed interval endpoint.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="value"/> or <paramref name="bindings"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// A binding belongs to another session, does not match its lambda parameter, the value is not an
    /// exact supported temporal CLR type, or a nullable value does not declare unbounded-null behavior.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="inclusion"/> or <paramref name="nullBehavior"/> is unsupported.
    /// </exception>
    /// <exception cref="InvalidOperationException">The enclosing temporal-match callback has completed.</exception>
    /// <exception cref="RelationQueryExpressionAuthoringException">The temporal value cannot be lowered exactly.</exception>
    public ExpressionTemporalIntervalBound Bound(
        LambdaExpression value,
        IReadOnlyList<RelationQueryExpressionValueBinding> bindings,
        TemporalBoundaryInclusion inclusion,
        TemporalNullBoundBehavior nullBehavior = TemporalNullBoundBehavior.Invalid,
        string? operandSourceReference = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(bindings);
        RequireActive();
        if (!Enum.IsDefined(inclusion))
        {
            throw new ArgumentOutOfRangeException(nameof(inclusion), inclusion, "Unsupported temporal boundary inclusion.");
        }

        if (!Enum.IsDefined(nullBehavior))
        {
            throw new ArgumentOutOfRangeException(nameof(nullBehavior), nullBehavior, "Unsupported temporal null-bound behavior.");
        }

        var domain = RequireTemporalType(
            value.ReturnType,
            allowNullable: nullBehavior == TemporalNullBoundBehavior.Unbounded,
            nameof(value),
            "A temporal interval bound");
        var reference = operandSourceReference ?? $"{sourceReference}/bound";
        var handles = owner.RequireBindings(value, bindings);
        var lowered = owner.ExpressionLowerer
            .LowerValue(value, handles, reference)
            .RequireValue();
        var result = new ExpressionTemporalIntervalBound(lowered.Value, inclusion, nullBehavior);
        expressionBounds.Add(result, new(handles, domain));
        return result;
    }

    /// <summary>Creates an expression-backed temporal endpoint using one typed binding.</summary>
    /// <typeparam name="TBinding">CLR type represented by the operand binding.</typeparam>
    /// <typeparam name="TValue">CLR temporal value type.</typeparam>
    /// <param name="value">Temporal value expression.</param>
    /// <param name="binding">Binding corresponding to the lambda parameter.</param>
    /// <param name="inclusion">Whether the endpoint belongs to the interval.</param>
    /// <param name="nullBehavior">How an explicitly present null endpoint is interpreted.</param>
    /// <param name="operandSourceReference">Optional stable producer reference for this operand.</param>
    /// <returns>A canonical expression-backed interval endpoint.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="binding"/> belongs to another session, does not match its lambda parameter, the
    /// value is not an exact supported temporal CLR type, or a nullable value does not declare
    /// unbounded-null behavior.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="inclusion"/> or <paramref name="nullBehavior"/> is unsupported.
    /// </exception>
    /// <exception cref="InvalidOperationException">The enclosing temporal-match callback has completed.</exception>
    /// <exception cref="RelationQueryExpressionAuthoringException">The temporal value cannot be lowered exactly.</exception>
    public ExpressionTemporalIntervalBound Bound<TBinding, TValue>(
        Expression<Func<TBinding, TValue>> value,
        RelationQueryExpressionValueBinding<TBinding> binding,
        TemporalBoundaryInclusion inclusion,
        TemporalNullBoundBehavior nullBehavior = TemporalNullBoundBehavior.Invalid,
        string? operandSourceReference = null)
        where TBinding : notnull =>
        Bound(value, [binding], inclusion, nullBehavior, operandSourceReference);

    /// <summary>Creates a structurally unbounded temporal interval endpoint.</summary>
    /// <returns>A canonical unbounded endpoint.</returns>
    /// <exception cref="InvalidOperationException">The enclosing temporal-match callback has completed.</exception>
    public UnboundedTemporalIntervalBound Unbounded()
    {
        RequireActive();
        var result = new UnboundedTemporalIntervalBound();
        unboundedBounds.Add(result);
        return result;
    }

    /// <summary>Creates an interval from independently bounded or unbounded endpoints.</summary>
    /// <param name="lower">Lower endpoint.</param>
    /// <param name="upper">Upper endpoint.</param>
    /// <returns>A canonical temporal interval.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="lower"/> or <paramref name="upper"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// An endpoint was not created by this builder or the endpoint temporal domains differ.
    /// </exception>
    /// <exception cref="InvalidOperationException">The enclosing temporal-match callback has completed.</exception>
    public TemporalInterval Interval(TemporalIntervalBound lower, TemporalIntervalBound upper)
    {
        ArgumentNullException.ThrowIfNull(lower);
        ArgumentNullException.ThrowIfNull(upper);
        RequireActive();
        var lowerOperand = RequireOwnedBound(lower, nameof(lower));
        var upperOperand = RequireOwnedBound(upper, nameof(upper));
        RequireSameDomain(lowerOperand.Domain, upperOperand.Domain, nameof(upper));

        var result = new TemporalInterval(lower, upper);
        intervals.Add(
            result,
            new(
                CombineBindings(lowerOperand.Bindings, upperOperand.Bindings),
                lowerOperand.Domain ?? upperOperand.Domain));
        return result;
    }

    /// <summary>Creates a point-in-interval match using an arbitrary-width typed point expression.</summary>
    /// <param name="point">Temporal point lambda whose parameters correspond to <paramref name="bindings"/>.</param>
    /// <param name="bindings">Visible bindings corresponding positionally to the point parameters.</param>
    /// <param name="interval">Right-side interval tested for membership.</param>
    /// <param name="operandSourceReference">Optional stable producer reference for the point operand.</param>
    /// <returns>A canonical point-in-interval match.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="point"/>, <paramref name="bindings"/>, or <paramref name="interval"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// A binding belongs to another session, does not match its lambda parameter, or is not visible on
    /// the left input; the interval was not produced by this builder, contains a binding not visible on
    /// the right input, or does not share the point's exact temporal domain; or the point is nullable or
    /// does not use a supported temporal CLR type.
    /// </exception>
    /// <exception cref="InvalidOperationException">The enclosing temporal-match callback has completed.</exception>
    /// <exception cref="RelationQueryExpressionAuthoringException">The point cannot be lowered exactly.</exception>
    public TemporalPointInIntervalMatch PointInInterval(
        LambdaExpression point,
        IReadOnlyList<RelationQueryExpressionValueBinding> bindings,
        TemporalInterval interval,
        string? operandSourceReference = null)
    {
        ArgumentNullException.ThrowIfNull(point);
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(interval);
        RequireActive();
        var pointDomain = RequireTemporalType(
            point.ReturnType,
            allowNullable: false,
            nameof(point),
            "A temporal point");
        var intervalOperands = RequireOwnedInterval(interval, nameof(interval));
        var reference = operandSourceReference ?? $"{sourceReference}/point";
        var handles = owner.RequireBindings(point, bindings);
        RequireVisibleBindings(handles, isLeftBindingVisible, "left-side point", nameof(bindings));
        RequireVisibleBindings(
            intervalOperands.Bindings,
            isRightBindingVisible,
            "right-side interval",
            nameof(interval));
        RequireSameDomain(pointDomain, intervalOperands.Domain, nameof(interval));
        var lowered = owner.ExpressionLowerer
            .LowerValue(point, handles, reference)
            .RequireValue();
        var result = new TemporalPointInIntervalMatch(lowered.Value, interval);
        matches.Add(result);
        return result;
    }

    /// <summary>Creates a point-in-interval match using one typed point binding.</summary>
    /// <typeparam name="TBinding">CLR type represented by the point binding.</typeparam>
    /// <typeparam name="TValue">CLR temporal point type.</typeparam>
    /// <param name="point">Temporal point expression.</param>
    /// <param name="binding">Binding corresponding to the point parameter.</param>
    /// <param name="interval">Right-side interval tested for membership.</param>
    /// <param name="operandSourceReference">Optional stable producer reference for the point operand.</param>
    /// <returns>A canonical point-in-interval match.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="point"/> or <paramref name="interval"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="binding"/> is invalid or is not visible on the left input; the interval is invalid,
    /// is not visible on the right input, or uses a different temporal domain; or the point type is not
    /// an exact supported non-nullable temporal CLR type.
    /// </exception>
    /// <exception cref="InvalidOperationException">The enclosing temporal-match callback has completed.</exception>
    /// <exception cref="RelationQueryExpressionAuthoringException">The point cannot be lowered exactly.</exception>
    public TemporalPointInIntervalMatch PointInInterval<TBinding, TValue>(
        Expression<Func<TBinding, TValue>> point,
        RelationQueryExpressionValueBinding<TBinding> binding,
        TemporalInterval interval,
        string? operandSourceReference = null)
        where TBinding : notnull =>
        PointInInterval(point, [binding], interval, operandSourceReference);

    /// <summary>Creates an interval-overlap match from explicit left and right intervals.</summary>
    /// <param name="left">Interval evaluated against the left input.</param>
    /// <param name="right">Interval evaluated against the right input.</param>
    /// <returns>A canonical interval-overlap match.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="left"/> or <paramref name="right"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// An interval was not produced by this builder, contains a binding not visible on its declared join
    /// side, or uses a different temporal domain than the other interval.
    /// </exception>
    /// <exception cref="InvalidOperationException">The enclosing temporal-match callback has completed.</exception>
    public TemporalIntervalOverlapMatch IntervalOverlap(
        TemporalInterval left,
        TemporalInterval right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        RequireActive();
        var leftOperands = RequireOwnedInterval(left, nameof(left));
        var rightOperands = RequireOwnedInterval(right, nameof(right));
        RequireVisibleBindings(
            leftOperands.Bindings,
            isLeftBindingVisible,
            "left-side interval",
            nameof(left));
        RequireVisibleBindings(
            rightOperands.Bindings,
            isRightBindingVisible,
            "right-side interval",
            nameof(right));
        RequireSameDomain(leftOperands.Domain, rightOperands.Domain, nameof(right));

        var result = new TemporalIntervalOverlapMatch(left, right);
        matches.Add(result);
        return result;
    }

    TemporalOperand RequireOwnedBound(TemporalIntervalBound bound, string parameterName)
    {
        if (bound is ExpressionTemporalIntervalBound expression
            && expressionBounds.TryGetValue(expression, out var operand))
        {
            return operand;
        }
        if (bound is UnboundedTemporalIntervalBound unbounded && unboundedBounds.Contains(unbounded))
        {
            return TemporalOperand.Unbounded;
        }

        throw new ArgumentException(
            "A temporal interval endpoint must be created by the same active temporal-match builder.",
            parameterName);
    }

    TemporalIntervalOperands RequireOwnedInterval(TemporalInterval interval, string parameterName)
    {
        if (intervals.TryGetValue(interval, out var operands))
        {
            return operands;
        }

        throw new ArgumentException(
            "A temporal interval must be assembled by the same active temporal-match builder.",
            parameterName);
    }

    static ScalarTypeKind RequireTemporalType(
        Type type,
        bool allowNullable,
        string parameterName,
        string operandDescription)
    {
        var underlying = Nullable.GetUnderlyingType(type);
        if (underlying is not null && !allowNullable)
        {
            throw new ArgumentException(
                $"{operandDescription} cannot use nullable CLR type '{type}'. "
                + "Only an interval bound with TemporalNullBoundBehavior.Unbounded may be nullable.",
                parameterName);
        }

        var normalized = underlying ?? type;
        if (normalized == typeof(DateOnly))
        {
            return ScalarTypeKind.Date;
        }

        if (normalized == typeof(DateTime))
        {
            return ScalarTypeKind.DateTime;
        }

        if (normalized == typeof(DateTimeOffset))
        {
            return ScalarTypeKind.Instant;
        }

        throw new ArgumentException(
            $"{operandDescription} must return DateOnly, DateTime, or DateTimeOffset, not '{type}'.",
            parameterName);
    }

    static void RequireSameDomain(
        ScalarTypeKind? first,
        ScalarTypeKind? second,
        string parameterName)
    {
        if (first is null || second is null || first == second)
        {
            return;
        }

        throw new ArgumentException(
            $"Temporal operands must use one exact canonical domain, but found '{first}' and '{second}'.",
            parameterName);
    }

    static void RequireVisibleBindings(
        IReadOnlyList<RelationQueryExpressionValueBinding> bindings,
        Func<RelationQueryExpressionValueBinding, bool> isVisible,
        string operandDescription,
        string parameterName)
    {
        foreach (var binding in bindings)
        {
            if (isVisible(binding))
            {
                continue;
            }

            throw new ArgumentException(
                $"Binding '{binding.Id.Value}' is not visible to the {operandDescription}.",
                parameterName);
        }
    }

    static RelationQueryExpressionValueBinding[] CombineBindings(
        IReadOnlyList<RelationQueryExpressionValueBinding> first,
        IReadOnlyList<RelationQueryExpressionValueBinding> second) =>
        [
            .. first
                .Concat(second)
                .DistinctBy(static binding => binding.Id)
        ];

    void RequireActive()
    {
        if (!isActive)
        {
            throw new InvalidOperationException(
                "A temporal-match builder cannot be used after its enclosing callback has completed.");
        }
    }

    sealed record TemporalOperand(
        IReadOnlyList<RelationQueryExpressionValueBinding> Bindings,
        ScalarTypeKind? Domain)
    {
        public static TemporalOperand Unbounded { get; } = new([], null);
    }

    sealed record TemporalIntervalOperands(
        IReadOnlyList<RelationQueryExpressionValueBinding> Bindings,
        ScalarTypeKind? Domain);
}

public sealed partial class RelationQueryExpressionAuthoring
{
    /// <summary>Authors an explicit temporal join from typed correlation and temporal operands.</summary>
    /// <typeparam name="TLeft">Canonical type of the left logical node.</typeparam>
    /// <typeparam name="TRight">Canonical type of the right logical node.</typeparam>
    /// <param name="left">Left logical branch.</param>
    /// <param name="right">Right logical branch.</param>
    /// <param name="kind">Join null-extension semantics.</param>
    /// <param name="correlation">Boolean correlation lambda whose parameters correspond to <paramref name="bindings"/>.</param>
    /// <param name="bindings">Visible bindings corresponding positionally to correlation parameters.</param>
    /// <param name="match">
    /// Callback constructing point-in-interval or interval-overlap semantics from typed temporal operands.
    /// </param>
    /// <param name="sourceReference">Optional stable producer reference for provenance and diagnostics.</param>
    /// <returns>A structural handle for the canonical temporal-join node.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="correlation"/>, <paramref name="bindings"/>, or <paramref name="match"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Correlation bindings are invalid or not visible in either input, the correlation is non-Boolean,
    /// <paramref name="match"/> returns <see langword="null"/> or a match not produced by its supplied
    /// builder, or a temporal operand violates its declared side, type, domain, or provenance contract.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is unsupported.</exception>
    /// <exception cref="RelationQueryExpressionAuthoringException">
    /// The correlation or a temporal operand cannot be lowered exactly.
    /// </exception>
    public RelationQueryNodeHandle<TemporalJoinQueryNode> TemporalJoin<TLeft, TRight>(
        RelationQueryNodeHandle<TLeft> left,
        RelationQueryNodeHandle<TRight> right,
        JoinKind kind,
        LambdaExpression correlation,
        IReadOnlyList<RelationQueryExpressionValueBinding> bindings,
        Func<RelationQueryExpressionTemporalMatchBuilder, TemporalJoinMatch> match,
        string? sourceReference = null)
        where TLeft : LogicalQueryNode
        where TRight : LogicalQueryNode
    {
        ArgumentNullException.ThrowIfNull(match);
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported temporal join kind.");
        }

        RequireReturnType(correlation, typeof(bool), nameof(correlation));
        var reference = sourceReference ?? "temporal-join";
        var handles = RequireBindings(correlation, bindings);
        RequireBindingsVisibleInEither(left, right, handles, nameof(bindings));
        var loweredCorrelation = ExpressionLowerer
            .LowerValue(correlation, handles, reference + "/correlation")
            .RequireValue();
        var matchBuilder = new RelationQueryExpressionTemporalMatchBuilder(
            this,
            reference + "/match",
            binding => structural.IsBindingVisible(left, binding.Structural),
            binding => structural.IsBindingVisible(right, binding.Structural));
        TemporalJoinMatch temporalMatch;
        try
        {
            temporalMatch = match(matchBuilder)
                ?? throw new ArgumentException("A temporal-match callback cannot return null.", nameof(match));
            matchBuilder.RequireOwnedMatch(temporalMatch);
        }
        finally
        {
            matchBuilder.Seal();
        }

        return structural.TemporalJoin(
            left,
            right,
            kind,
            loweredCorrelation.Value,
            temporalMatch,
            source: Source(reference, "Expression-authored temporal join."),
            correlationSource: loweredCorrelation.Source,
            matchSource: Source(reference + "/match", "Expression-authored temporal membership."));
    }

    /// <summary>Authors an explicit temporal join using two typed correlation bindings.</summary>
    /// <typeparam name="TLeft">Canonical type of the left logical node.</typeparam>
    /// <typeparam name="TRight">Canonical type of the right logical node.</typeparam>
    /// <typeparam name="TLeftValue">CLR type of the left correlation binding.</typeparam>
    /// <typeparam name="TRightValue">CLR type of the right correlation binding.</typeparam>
    /// <param name="left">Left logical branch.</param>
    /// <param name="right">Right logical branch.</param>
    /// <param name="kind">Join null-extension semantics.</param>
    /// <param name="correlation">Boolean correlation predicate over both bindings.</param>
    /// <param name="leftBinding">Binding corresponding to the first correlation parameter.</param>
    /// <param name="rightBinding">Binding corresponding to the second correlation parameter.</param>
    /// <param name="match">Callback constructing explicit temporal membership semantics.</param>
    /// <param name="sourceReference">Optional stable producer reference.</param>
    /// <returns>A structural handle for the canonical temporal-join node.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="correlation"/> or <paramref name="match"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// A binding is invalid or not visible in either input, <paramref name="match"/> returns null or a
    /// match not produced by its supplied builder, or a temporal operand violates its declared side,
    /// type, domain, or provenance contract.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is unsupported.</exception>
    /// <exception cref="RelationQueryExpressionAuthoringException">
    /// The correlation or a temporal operand cannot be lowered exactly.
    /// </exception>
    public RelationQueryNodeHandle<TemporalJoinQueryNode> TemporalJoin<TLeft, TRight, TLeftValue, TRightValue>(
        RelationQueryNodeHandle<TLeft> left,
        RelationQueryNodeHandle<TRight> right,
        JoinKind kind,
        Expression<Func<TLeftValue, TRightValue, bool>> correlation,
        RelationQueryExpressionValueBinding<TLeftValue> leftBinding,
        RelationQueryExpressionValueBinding<TRightValue> rightBinding,
        Func<RelationQueryExpressionTemporalMatchBuilder, TemporalJoinMatch> match,
        string? sourceReference = null)
        where TLeft : LogicalQueryNode
        where TRight : LogicalQueryNode
        where TLeftValue : notnull
        where TRightValue : notnull =>
        TemporalJoin(
            left,
            right,
            kind,
            correlation,
            [leftBinding, rightBinding],
            match,
            sourceReference);
}
