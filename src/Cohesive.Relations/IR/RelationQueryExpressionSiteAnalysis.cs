using Cohesive.Model.Expressions;

namespace Cohesive.Relations.IR;

/// <summary>
/// Semantic role of one expression site in a canonical relation or query definition.
/// </summary>
public enum RelationQueryExpressionSiteKind
{
    /// <summary>A predicate that determines whether an input row passes a filter node.</summary>
    FilterPredicate = 0,

    /// <summary>A predicate that correlates the inputs of an explicit join node.</summary>
    JoinPredicate = 1,

    /// <summary>A collection expression expanded into one logical row per item.</summary>
    ExpandCollection = 2,

    /// <summary>A projection assignment expression that produces an output field.</summary>
    ProjectionAssignmentValue = 3,

    /// <summary>An indexed key expression used to determine row distinctness.</summary>
    DistinctKey = 4,

    /// <summary>A grouping-key expression that produces an aggregate grouping field.</summary>
    AggregateGroupingKey = 5,

    /// <summary>A value expression consumed by an aggregate assignment.</summary>
    AggregateAssignmentValue = 6,

    /// <summary>A predicate scoped to one aggregate assignment.</summary>
    AggregateAssignmentFilter = 7,

    /// <summary>An indexed key expression used to order rows.</summary>
    OrderKey = 8,

    /// <summary>An indexed continuation-boundary expression used by keyset paging.</summary>
    KeysetBoundary = 9,

    /// <summary>An expression that defines stable identity for relation output values.</summary>
    RelationOutputKey = 10,

    /// <summary>An expression that validates one named relation invariant.</summary>
    RelationInvariant = 11,

    /// <summary>A Boolean predicate that correlates the inputs of a temporal join.</summary>
    TemporalJoinCorrelation = 12,

    /// <summary>The temporal point tested by a point-in-interval temporal join.</summary>
    TemporalJoinPoint = 13,

    /// <summary>An indexed lower-bound expression used by a temporal join interval.</summary>
    TemporalJoinIntervalLowerBound = 14,

    /// <summary>An indexed upper-bound expression used by a temporal join interval.</summary>
    TemporalJoinIntervalUpperBound = 15,

    /// <summary>An indexed partition key used by ordered representative selection.</summary>
    RepresentativeKey = 16
}

/// <summary>
/// Expression analysis together with the typed canonical relation/query site that produced it.
/// </summary>
/// <remarks>
/// Instances are produced by <see cref="RelationQueryExpressionAnalyzer"/>. The origin properties
/// form a closed contract determined by <see cref="Kind"/>: node sites declare <see cref="Node"/>,
/// assignment sites additionally declare <see cref="Assignment"/>, indexed sites additionally
/// declare <see cref="Ordinal"/>, and invariant sites declare <see cref="InvariantName"/>.
/// </remarks>
public sealed class RelationQueryExpressionSiteAnalysis
{
    /// <summary>Creates a typed relation/query expression-site analysis.</summary>
    /// <param name="kind">Semantic role of the expression site.</param>
    /// <param name="analysis">Shared expression analysis for the site.</param>
    /// <param name="node">Logical node containing the site, when the site belongs to a node.</param>
    /// <param name="assignment">Assignment containing the site, when the site belongs to an assignment.</param>
    /// <param name="ordinal">Zero-based position of an indexed expression site.</param>
    /// <param name="invariantName">Stable invariant name for a relation-invariant site.</param>
    /// <exception cref="ArgumentNullException"><paramref name="analysis"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// An origin identifier is empty, a required origin value is absent, or an origin value is
    /// supplied for a <paramref name="kind"/> that does not admit it.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="kind"/> is unsupported or <paramref name="ordinal"/> is negative.
    /// </exception>
    internal RelationQueryExpressionSiteAnalysis(
        RelationQueryExpressionSiteKind kind,
        ExprAnalysisResult analysis,
        QueryNodeId? node = null,
        QueryAssignmentId? assignment = null,
        int? ordinal = null,
        string? invariantName = null)
    {
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported relation/query expression-site kind.");
        if (ordinal is < 0)
            throw new ArgumentOutOfRangeException(nameof(ordinal), ordinal, "Expression-site ordinal must be non-negative.");

        Analysis = Guard.RequireNotNull(analysis);
        Kind = kind;

        var requiresNode = kind is not RelationQueryExpressionSiteKind.RelationOutputKey and not RelationQueryExpressionSiteKind.RelationInvariant;
        var requiresAssignment = kind is RelationQueryExpressionSiteKind.ProjectionAssignmentValue
            or RelationQueryExpressionSiteKind.AggregateGroupingKey
            or RelationQueryExpressionSiteKind.AggregateAssignmentValue
            or RelationQueryExpressionSiteKind.AggregateAssignmentFilter;
        var requiresOrdinal = kind is RelationQueryExpressionSiteKind.DistinctKey
            or RelationQueryExpressionSiteKind.RepresentativeKey
            or RelationQueryExpressionSiteKind.OrderKey
            or RelationQueryExpressionSiteKind.KeysetBoundary
            or RelationQueryExpressionSiteKind.TemporalJoinIntervalLowerBound
            or RelationQueryExpressionSiteKind.TemporalJoinIntervalUpperBound;
        var requiresInvariantName = kind == RelationQueryExpressionSiteKind.RelationInvariant;

        ValidateOptionalIdentifier(node?.Value, node is not null, requiresNode, nameof(node), "node");
        ValidateOptionalIdentifier(
            assignment?.Value,
            assignment is not null,
            requiresAssignment,
            nameof(assignment),
            "assignment");
        ValidateOptionalValue(ordinal, requiresOrdinal, nameof(ordinal), "ordinal");
        ValidateOptionalIdentifier(
            invariantName,
            invariantName is not null,
            requiresInvariantName,
            nameof(invariantName),
            "invariant name");

        Node = node;
        Assignment = assignment;
        Ordinal = ordinal;
        InvariantName = invariantName;
    }

    /// <summary>Semantic role of the expression site.</summary>
    public RelationQueryExpressionSiteKind Kind { get; }

    /// <summary>Shared expression analysis performed for the site.</summary>
    public ExprAnalysisResult Analysis { get; }

    /// <summary>Logical node containing the site, or <see langword="null"/> for relation-level sites.</summary>
    public QueryNodeId? Node { get; }

    /// <summary>Assignment containing the site, or <see langword="null"/> for non-assignment sites.</summary>
    public QueryAssignmentId? Assignment { get; }

    /// <summary>Zero-based position for an indexed site, or <see langword="null"/> for named sites.</summary>
    public int? Ordinal { get; }

    /// <summary>Invariant name for a relation-invariant site, or <see langword="null"/> otherwise.</summary>
    public string? InvariantName { get; }

    static void ValidateOptionalIdentifier(
        string? value,
        bool supplied,
        bool required,
        string parameterName,
        string displayName)
    {
        if (required && string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"Expression-site {displayName} is required.", parameterName);
        if (!required && supplied)
            throw new ArgumentException($"Expression-site {displayName} is not valid for this site kind.", parameterName);
    }

    static void ValidateOptionalValue<T>(
        T? value,
        bool required,
        string parameterName,
        string displayName)
        where T : struct
    {
        if (required && value is null)
            throw new ArgumentException($"Expression-site {displayName} is required.", parameterName);
        if (!required && value is not null)
            throw new ArgumentException($"Expression-site {displayName} is not valid for this site kind.", parameterName);
    }
}
