using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Cohesive.Relations.Model;

namespace Cohesive.Relations.IR;

/// <summary>
/// Whether a finite temporal interval bound includes its endpoint.
/// </summary>
public enum TemporalBoundaryInclusion
{
    /// <summary>The endpoint belongs to the interval.</summary>
    Inclusive = 0,

    /// <summary>The endpoint does not belong to the interval.</summary>
    Exclusive = 1
}

/// <summary>
/// How an explicitly present null value is interpreted for an expression-backed temporal bound.
/// </summary>
public enum TemporalNullBoundBehavior
{
    /// <summary>A null value is invalid and cannot establish temporal membership.</summary>
    Invalid = 0,

    /// <summary>A null value explicitly represents an unbounded endpoint.</summary>
    Unbounded = 1
}

/// <summary>
/// Base definition for one lower or upper endpoint of a canonical temporal interval.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = RelationQueryWireNames.TemporalBoundDiscriminator)]
[JsonDerivedType(typeof(UnboundedTemporalIntervalBound), RelationQueryWireNames.UnboundedTemporalBound)]
[JsonDerivedType(typeof(ExpressionTemporalIntervalBound), RelationQueryWireNames.ExpressionTemporalBound)]
public abstract record TemporalIntervalBound
{
    /// <summary>Initializes a temporal interval-bound definition.</summary>
    protected TemporalIntervalBound()
    {
    }
}

/// <summary>
/// Declares that one temporal interval endpoint is structurally unbounded.
/// </summary>
public sealed record UnboundedTemporalIntervalBound : TemporalIntervalBound
{
    /// <summary>Creates a structurally unbounded temporal interval endpoint.</summary>
    public UnboundedTemporalIntervalBound()
    {
    }
}

/// <summary>
/// Declares a finite temporal interval endpoint computed from a semantic expression.
/// </summary>
public sealed record ExpressionTemporalIntervalBound : TemporalIntervalBound
{
    /// <summary>Creates an expression-backed temporal interval bound.</summary>
    /// <param name="value">Expression producing the temporal endpoint.</param>
    /// <param name="inclusion">Whether the endpoint belongs to the interval.</param>
    /// <param name="nullBehavior">Explicit interpretation of a present null endpoint.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="inclusion"/> or <paramref name="nullBehavior"/> is unsupported.
    /// </exception>
    public ExpressionTemporalIntervalBound(
        Expr value,
        TemporalBoundaryInclusion inclusion,
        TemporalNullBoundBehavior nullBehavior = TemporalNullBoundBehavior.Invalid)
    {
        Value = Guard.RequireNotNull(value);
        Inclusion = inclusion;
        NullBehavior = nullBehavior;

        if (!Enum.IsDefined(Inclusion))
        {
            throw new ArgumentOutOfRangeException(
                nameof(inclusion),
                inclusion,
                "Unsupported temporal boundary inclusion.");
        }
        if (!Enum.IsDefined(NullBehavior))
        {
            throw new ArgumentOutOfRangeException(
                nameof(nullBehavior),
                nullBehavior,
                "Unsupported temporal null-bound behavior.");
        }
    }

    /// <summary>Expression producing the temporal endpoint.</summary>
    [JsonRequired]
    public Expr Value { get; init; }

    /// <summary>Whether the endpoint belongs to the interval.</summary>
    [JsonRequired]
    public TemporalBoundaryInclusion Inclusion { get; init; }

    /// <summary>Explicit interpretation of a present null endpoint.</summary>
    [JsonRequired]
    public TemporalNullBoundBehavior NullBehavior { get; init; }
}

/// <summary>
/// Canonical temporal interval with independently bounded or unbounded endpoints.
/// </summary>
public sealed record TemporalInterval
{
    /// <summary>Creates a temporal interval.</summary>
    /// <param name="lower">Lower interval endpoint.</param>
    /// <param name="upper">Upper interval endpoint.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="lower"/> or <paramref name="upper"/> is <see langword="null"/>.
    /// </exception>
    public TemporalInterval(TemporalIntervalBound lower, TemporalIntervalBound upper)
    {
        Lower = Guard.RequireNotNull(lower);
        Upper = Guard.RequireNotNull(upper);
    }

    /// <summary>Lower interval endpoint.</summary>
    [JsonRequired]
    public TemporalIntervalBound Lower { get; init; }

    /// <summary>Upper interval endpoint.</summary>
    [JsonRequired]
    public TemporalIntervalBound Upper { get; init; }

    /// <summary>Creates the conventional half-open interval <c>[lower, upper)</c>.</summary>
    /// <param name="lower">Expression producing the inclusive lower endpoint.</param>
    /// <param name="upper">Expression producing the exclusive upper endpoint.</param>
    /// <param name="upperNullBehavior">Explicit interpretation of a present null upper endpoint.</param>
    /// <returns>A half-open temporal interval.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="lower"/> or <paramref name="upper"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="upperNullBehavior"/> is unsupported.
    /// </exception>
    public static TemporalInterval HalfOpen(
        Expr lower,
        Expr upper,
        TemporalNullBoundBehavior upperNullBehavior = TemporalNullBoundBehavior.Invalid) =>
        new(
            new ExpressionTemporalIntervalBound(lower, TemporalBoundaryInclusion.Inclusive),
            new ExpressionTemporalIntervalBound(
                upper,
                TemporalBoundaryInclusion.Exclusive,
                upperNullBehavior));
}

/// <summary>
/// Base definition for the temporal membership condition of a temporal join.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = RelationQueryWireNames.TemporalMatchDiscriminator)]
[JsonDerivedType(typeof(TemporalPointInIntervalMatch), RelationQueryWireNames.TemporalPointInIntervalMatch)]
[JsonDerivedType(typeof(TemporalIntervalOverlapMatch), RelationQueryWireNames.TemporalIntervalOverlapMatch)]
public abstract record TemporalJoinMatch
{
    /// <summary>Initializes a temporal join-match definition.</summary>
    protected TemporalJoinMatch()
    {
    }
}

/// <summary>
/// Matches when a temporal point from the left input belongs to an interval from the right input
/// under its declared boundary semantics.
/// </summary>
public sealed record TemporalPointInIntervalMatch : TemporalJoinMatch
{
    /// <summary>Creates a point-in-interval temporal match.</summary>
    /// <param name="point">Expression producing the temporal point from the left input.</param>
    /// <param name="interval">Right-input interval tested for membership.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="point"/> or <paramref name="interval"/> is <see langword="null"/>.
    /// </exception>
    public TemporalPointInIntervalMatch(Expr point, TemporalInterval interval)
    {
        Point = Guard.RequireNotNull(point);
        Interval = Guard.RequireNotNull(interval);
    }

    /// <summary>Expression producing the temporal point from the left input environment.</summary>
    [JsonRequired]
    public Expr Point { get; init; }

    /// <summary>Right-input interval tested for membership.</summary>
    [JsonRequired]
    public TemporalInterval Interval { get; init; }
}

/// <summary>
/// Matches when two temporal intervals share at least one point under their declared boundary semantics.
/// </summary>
public sealed record TemporalIntervalOverlapMatch : TemporalJoinMatch
{
    /// <summary>Creates an interval-overlap temporal match.</summary>
    /// <param name="left">Interval evaluated against the left input.</param>
    /// <param name="right">Interval evaluated against the right input.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="left"/> or <paramref name="right"/> is <see langword="null"/>.
    /// </exception>
    public TemporalIntervalOverlapMatch(TemporalInterval left, TemporalInterval right)
    {
        Left = Guard.RequireNotNull(left);
        Right = Guard.RequireNotNull(right);
    }

    /// <summary>Interval evaluated against the left input.</summary>
    [JsonRequired]
    public TemporalInterval Left { get; init; }

    /// <summary>Interval evaluated against the right input.</summary>
    [JsonRequired]
    public TemporalInterval Right { get; init; }
}

/// <summary>
/// Joins two independently produced rowsets using an ordinary correlation predicate and an explicit
/// valid-time membership condition.
/// </summary>
/// <remarks>
/// This node evaluates <see cref="Correlation"/> over the combined pre-null-extension binding
/// environment. Match operands retain their declared side scopes: the point or left interval uses
/// the left input, and the right interval uses the right input. The node does not acquire historical
/// versions, consult an ambient clock, select a nearest predecessor, or silently choose one of
/// several matches.
/// </remarks>
public sealed record TemporalJoinQueryNode : LogicalQueryNode
{
    /// <summary>Creates a temporal join node.</summary>
    /// <param name="id">Stable node identifier.</param>
    /// <param name="left">Left input rowset.</param>
    /// <param name="right">Right input rowset.</param>
    /// <param name="kind">Join null-extension semantics.</param>
    /// <param name="correlation">Boolean predicate correlating the two input binding environments.</param>
    /// <param name="match">Explicit valid-time membership condition.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="correlation"/> or <paramref name="match"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is unsupported.</exception>
    public TemporalJoinQueryNode(
        QueryNodeId id,
        QueryNodeId left,
        QueryNodeId right,
        JoinKind kind,
        Expr correlation,
        TemporalJoinMatch match)
        : base(id)
    {
        Left = left;
        Right = right;
        Kind = kind;
        Correlation = Guard.RequireNotNull(correlation);
        Match = Guard.RequireNotNull(match);

        if (!Enum.IsDefined(Kind))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported temporal join kind.");
    }

    /// <summary>Left input rowset.</summary>
    public QueryNodeId Left { get; init; }

    /// <summary>Right input rowset.</summary>
    public QueryNodeId Right { get; init; }

    /// <summary>Join null-extension semantics.</summary>
    [JsonRequired]
    public JoinKind Kind { get; init; }

    /// <summary>Boolean predicate correlating the two input binding environments.</summary>
    [JsonRequired]
    public Expr Correlation { get; init; }

    /// <summary>Explicit valid-time membership condition.</summary>
    [JsonRequired]
    public TemporalJoinMatch Match { get; init; }

    /// <inheritdoc />
    public override ImmutableArray<QueryNodeId> Inputs => [Left, Right];
}
