using System.Collections.Immutable;
using Cohesive.Model;
using Cohesive.Relations.IR;
using Cohesive.Relations.Model;

namespace Cohesive.Relations.Compilation;

readonly record struct RelationQueryAggregateAssignmentReference(
    QueryNodeId Node,
    QueryAssignmentId Assignment);

/// <summary>One demanded projection assignment and its exact analyzed value-expression site.</summary>
public sealed record RelationQueryProjectionExecutionAssignment
{
    internal RelationQueryProjectionExecutionAssignment(
        ProjectionAssignment definition,
        RelationQueryExpressionSiteAnalysis valueSite)
    {
        Definition = Guard.RequireNotNull(definition);
        ValueSite = Guard.RequireNotNull(valueSite);
        if (valueSite.Kind != RelationQueryExpressionSiteKind.ProjectionAssignmentValue
            || valueSite.Assignment != definition.Id)
        {
            throw new ArgumentException(
                "A projection execution assignment requires its canonical value-expression site.",
                nameof(valueSite));
        }
    }

    /// <summary>Exact canonical projection assignment retained by output demand.</summary>
    public ProjectionAssignment Definition { get; }

    /// <summary>Analyzed expression site for <see cref="ProjectionAssignment.Value"/>.</summary>
    public RelationQueryExpressionSiteAnalysis ValueSite { get; }
}

/// <summary>One demanded aggregate grouping and its exact analyzed key-expression site.</summary>
public sealed record RelationQueryAggregateGroupingExecution
{
    internal RelationQueryAggregateGroupingExecution(
        QueryGrouping definition,
        RelationQueryExpressionSiteAnalysis keySite)
    {
        Definition = Guard.RequireNotNull(definition);
        KeySite = Guard.RequireNotNull(keySite);
        if (keySite.Kind != RelationQueryExpressionSiteKind.AggregateGroupingKey
            || keySite.Assignment != definition.Id)
        {
            throw new ArgumentException(
                "An aggregate grouping execution requires its canonical key-expression site.",
                nameof(keySite));
        }
    }

    /// <summary>Exact canonical grouping retained by output demand.</summary>
    public QueryGrouping Definition { get; }

    /// <summary>Analyzed expression site for <see cref="QueryGrouping.Key"/>.</summary>
    public RelationQueryExpressionSiteAnalysis KeySite { get; }
}

/// <summary>
/// One demanded aggregate assignment together with the exact analyzed sites it evaluates.
/// </summary>
public sealed record RelationQueryAggregateAssignmentExecution
{
    internal RelationQueryAggregateAssignmentExecution(
        QueryAggregateAssignment definition,
        RelationQueryExpressionSiteAnalysis? valueSite,
        RelationQueryExpressionSiteAnalysis? filterSite)
    {
        Definition = Guard.RequireNotNull(definition);
        ValidateSite(
            valueSite,
            RelationQueryExpressionSiteKind.AggregateAssignmentValue,
            definition.Id,
            nameof(valueSite));
        ValidateSite(
            filterSite,
            RelationQueryExpressionSiteKind.AggregateAssignmentFilter,
            definition.Id,
            nameof(filterSite));
        if ((definition.Value is null) != (valueSite is null))
            throw new ArgumentException("Aggregate value-site presence must match the canonical assignment.", nameof(valueSite));
        if ((definition.Filter is null) != (filterSite is null))
            throw new ArgumentException("Aggregate filter-site presence must match the canonical assignment.", nameof(filterSite));

        ValueSite = valueSite;
        FilterSite = filterSite;
    }

    /// <summary>Exact canonical aggregate assignment retained by output demand.</summary>
    public QueryAggregateAssignment Definition { get; }

    /// <summary>Analyzed aggregate value-expression site, or <see langword="null"/> for value-less count.</summary>
    public RelationQueryExpressionSiteAnalysis? ValueSite { get; }

    /// <summary>Analyzed aggregate filter-expression site, or <see langword="null"/> when no filter is declared.</summary>
    public RelationQueryExpressionSiteAnalysis? FilterSite { get; }

    static void ValidateSite(
        RelationQueryExpressionSiteAnalysis? site,
        RelationQueryExpressionSiteKind expectedKind,
        QueryAssignmentId expectedAssignment,
        string parameterName)
    {
        if (site is not null && (site.Kind != expectedKind || site.Assignment != expectedAssignment))
        {
            throw new ArgumentException(
                "An aggregate execution site must match the canonical aggregate assignment.",
                parameterName);
        }
    }
}

/// <summary>One demanded relation invariant and its exact analyzed predicate site.</summary>
public sealed record RelationQueryInvariantExecution
{
    internal RelationQueryInvariantExecution(
        InvariantDefinition definition,
        RelationQueryExpressionSiteAnalysis predicateSite)
    {
        Definition = Guard.RequireNotNull(definition);
        PredicateSite = Guard.RequireNotNull(predicateSite);
        if (predicateSite.Kind != RelationQueryExpressionSiteKind.RelationInvariant
            || !string.Equals(predicateSite.InvariantName, definition.Name, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A relation invariant execution requires its canonical predicate site.",
                nameof(predicateSite));
        }
    }

    /// <summary>Exact canonical invariant retained by relation execution.</summary>
    public InvariantDefinition Definition { get; }

    /// <summary>Analyzed predicate site for <see cref="InvariantDefinition.Expression"/>.</summary>
    public RelationQueryExpressionSiteAnalysis PredicateSite { get; }
}

/// <summary>
/// One prepared temporal interval bound and its demanded expression site when finite.
/// </summary>
public sealed record RelationQueryTemporalBoundExecution
{
    internal RelationQueryTemporalBoundExecution(
        TemporalIntervalBound definition,
        int intervalOrdinal,
        RelationQueryExpressionSiteKind siteKind,
        RelationQueryExpressionSiteAnalysis? valueSite)
    {
        Definition = Guard.RequireNotNull(definition);
        if (intervalOrdinal < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(intervalOrdinal),
                intervalOrdinal,
                "A temporal interval ordinal cannot be negative.");
        }
        if (siteKind is not RelationQueryExpressionSiteKind.TemporalJoinIntervalLowerBound
            and not RelationQueryExpressionSiteKind.TemporalJoinIntervalUpperBound)
        {
            throw new ArgumentOutOfRangeException(
                nameof(siteKind),
                siteKind,
                "A temporal bound requires a lower- or upper-bound expression-site kind.");
        }

        var requiresSite = definition is ExpressionTemporalIntervalBound;
        if (requiresSite != (valueSite is not null))
        {
            throw new ArgumentException(
                "Expression-backed temporal bounds require one demanded value site; unbounded bounds require none.",
                nameof(valueSite));
        }
        if (valueSite is not null
            && (valueSite.Kind != siteKind || valueSite.Ordinal != intervalOrdinal))
        {
            throw new ArgumentException(
                "A temporal bound value site must match its canonical endpoint kind and interval ordinal.",
                nameof(valueSite));
        }

        IntervalOrdinal = intervalOrdinal;
        ValueSite = valueSite;
    }

    /// <summary>Exact canonical temporal bound.</summary>
    public TemporalIntervalBound Definition { get; }

    /// <summary>Zero-based interval position within the temporal match.</summary>
    public int IntervalOrdinal { get; }

    /// <summary>
    /// Analyzed endpoint expression site, or <see langword="null"/> when the bound is structurally unbounded.
    /// </summary>
    public RelationQueryExpressionSiteAnalysis? ValueSite { get; }

    /// <summary>Whether this endpoint is structurally unbounded.</summary>
    public bool IsStructurallyUnbounded => Definition is UnboundedTemporalIntervalBound;
}

/// <summary>
/// One prepared temporal interval with lower and upper endpoint execution metadata.
/// </summary>
public sealed record RelationQueryTemporalIntervalExecution
{
    internal RelationQueryTemporalIntervalExecution(
        TemporalInterval definition,
        int ordinal,
        RelationQueryTemporalBoundExecution lower,
        RelationQueryTemporalBoundExecution upper)
    {
        Definition = Guard.RequireNotNull(definition);
        if (ordinal < 0)
            throw new ArgumentOutOfRangeException(nameof(ordinal), ordinal, "A temporal interval ordinal cannot be negative.");
        Lower = Guard.RequireNotNull(lower);
        Upper = Guard.RequireNotNull(upper);
        if (lower.IntervalOrdinal != ordinal || upper.IntervalOrdinal != ordinal)
            throw new ArgumentException("Prepared temporal bounds must belong to their interval ordinal.", nameof(ordinal));

        Ordinal = ordinal;
    }

    /// <summary>Exact canonical temporal interval.</summary>
    public TemporalInterval Definition { get; }

    /// <summary>Zero-based interval position within the temporal match.</summary>
    public int Ordinal { get; }

    /// <summary>Prepared lower endpoint.</summary>
    public RelationQueryTemporalBoundExecution Lower { get; }

    /// <summary>Prepared upper endpoint.</summary>
    public RelationQueryTemporalBoundExecution Upper { get; }
}

/// <summary>
/// Demand-scoped temporal join semantics prepared for interpretation without re-scanning canonical IR.
/// </summary>
public sealed record RelationQueryTemporalJoinExecution
{
    internal RelationQueryTemporalJoinExecution(
        TemporalJoinQueryNode definition,
        ImmutableArray<RelationQueryExpressionSiteAnalysis> sites)
    {
        Definition = Guard.RequireNotNull(definition);
        var normalized = sites.IsDefault ? [] : sites;
        CorrelationSite = normalized.Single(static site =>
            site.Kind == RelationQueryExpressionSiteKind.TemporalJoinCorrelation);

        switch (definition.Match)
        {
            case TemporalPointInIntervalMatch pointInInterval:
                PointSite = normalized.Single(static site =>
                    site.Kind == RelationQueryExpressionSiteKind.TemporalJoinPoint);
                Intervals =
                [
                    CreateInterval(pointInInterval.Interval, ordinal: 0, normalized)
                ];
                break;
            case TemporalIntervalOverlapMatch overlap:
                if (normalized.Any(static site =>
                        site.Kind == RelationQueryExpressionSiteKind.TemporalJoinPoint))
                {
                    throw new ArgumentException(
                        "An interval-overlap execution cannot contain a point-expression site.",
                        nameof(sites));
                }
                PointSite = null;
                Intervals =
                [
                    CreateInterval(overlap.Left, ordinal: 0, normalized),
                    CreateInterval(overlap.Right, ordinal: 1, normalized)
                ];
                break;
            default:
                throw new ArgumentException("Unsupported temporal join match type.", nameof(definition));
        }

        var temporalSites = normalized.Where(static site => site.Kind is
            RelationQueryExpressionSiteKind.TemporalJoinPoint
            or RelationQueryExpressionSiteKind.TemporalJoinIntervalLowerBound
            or RelationQueryExpressionSiteKind.TemporalJoinIntervalUpperBound);
        var domains = temporalSites
            .Select(static site => site.Analysis.KnownResult?.GetEffectiveType())
            .Select(static type => type is ScalarTypeRef
                {
                    Kind: ScalarTypeKind.Date or ScalarTypeKind.DateTime or ScalarTypeKind.Instant
                } temporal
                    ? temporal.Kind
                    : (ScalarTypeKind?)null)
            .ToArray();
        if (domains.Any(static domain => domain is null))
        {
            throw new ArgumentException(
                "Every demanded temporal join operand must have one exact temporal scalar domain.",
                nameof(sites));
        }

        var distinctDomains = domains
            .Select(static domain => domain!.Value)
            .Distinct()
            .ToArray();
        if (distinctDomains.Length > 1)
        {
            throw new ArgumentException(
                "Temporal join operands cannot mix temporal scalar domains.",
                nameof(sites));
        }
        Domain = distinctDomains.Length == 0 ? null : distinctDomains[0];
    }

    /// <summary>Exact canonical temporal join node.</summary>
    public TemporalJoinQueryNode Definition { get; }

    /// <summary>Analyzed Boolean correlation-expression site.</summary>
    public RelationQueryExpressionSiteAnalysis CorrelationSite { get; }

    /// <summary>
    /// Analyzed left-input point-expression site for point containment, or <see langword="null"/> for overlap.
    /// </summary>
    public RelationQueryExpressionSiteAnalysis? PointSite { get; }

    /// <summary>
    /// Prepared intervals in semantic operand order: one right interval for point containment, or left then right for overlap.
    /// </summary>
    public ImmutableArray<RelationQueryTemporalIntervalExecution> Intervals { get; }

    /// <summary>
    /// Exact temporal scalar domain shared by expression-backed operands, or <see langword="null"/>
    /// only for interval overlap when both intervals are entirely structurally unbounded.
    /// </summary>
    public ScalarTypeKind? Domain { get; }

    static RelationQueryTemporalIntervalExecution CreateInterval(
        TemporalInterval definition,
        int ordinal,
        ImmutableArray<RelationQueryExpressionSiteAnalysis> sites) =>
        new(
            definition,
            ordinal,
            CreateBound(
                definition.Lower,
                ordinal,
                RelationQueryExpressionSiteKind.TemporalJoinIntervalLowerBound,
                sites),
            CreateBound(
                definition.Upper,
                ordinal,
                RelationQueryExpressionSiteKind.TemporalJoinIntervalUpperBound,
                sites));

    static RelationQueryTemporalBoundExecution CreateBound(
        TemporalIntervalBound definition,
        int ordinal,
        RelationQueryExpressionSiteKind siteKind,
        ImmutableArray<RelationQueryExpressionSiteAnalysis> sites) =>
        new(
            definition,
            ordinal,
            siteKind,
            definition is ExpressionTemporalIntervalBound
                ? sites.Single(site => site.Kind == siteKind && site.Ordinal == ordinal)
                : null);
}

/// <summary>
/// One retained logical node together with the demand-scoped semantic material needed to execute it.
/// </summary>
public sealed record RelationQueryExecutionNode
{
    internal RelationQueryExecutionNode(
        RelationQueryLogicalPlanNode logicalPlan,
        LogicalQueryNode canonicalNode,
        ImmutableArray<RelationQueryBindingShape> outputBindings,
        ImmutableArray<RelationQueryExpressionSiteAnalysis> expressionSites,
        ImmutableArray<RelationQueryProjectionExecutionAssignment> projectionAssignments,
        ImmutableArray<RelationQueryAggregateGroupingExecution> aggregateGroupings,
        ImmutableArray<RelationQueryAggregateAssignmentExecution> aggregateAssignments)
    {
        LogicalPlan = Guard.RequireNotNull(logicalPlan);
        CanonicalNode = Guard.RequireNotNull(canonicalNode);
        if (logicalPlan.Node != canonicalNode.Id)
            throw new ArgumentException("The logical-plan and canonical node identities must match.", nameof(canonicalNode));

        OutputBindings = NormalizeBindings(outputBindings, canonicalNode.Id);
        ExpressionSites = NormalizeSites(expressionSites, canonicalNode.Id);
        ProjectionAssignments = NormalizeProjectionAssignments(projectionAssignments);
        AggregateGroupings = NormalizeAggregateGroupings(aggregateGroupings);
        AggregateAssignments = NormalizeAggregateAssignments(aggregateAssignments);
        TemporalJoin = canonicalNode is TemporalJoinQueryNode temporalJoin
            ? new(temporalJoin, ExpressionSites)
            : null;
        DistinctKeys = SelectIndexedSites(RelationQueryExpressionSiteKind.DistinctKey);
        RepresentativeKeys = SelectIndexedSites(RelationQueryExpressionSiteKind.RepresentativeKey);
        OrderKeys = SelectIndexedSites(RelationQueryExpressionSiteKind.OrderKey);
        KeysetBoundaries = SelectIndexedSites(RelationQueryExpressionSiteKind.KeysetBoundary);
    }

    /// <summary>Stable canonical node identity.</summary>
    public QueryNodeId Id => CanonicalNode.Id;

    /// <summary>Effective input topology and bypass evidence for this retained node.</summary>
    public RelationQueryLogicalPlanNode LogicalPlan { get; }

    /// <summary>Exact canonical node from the persisted definition.</summary>
    public LogicalQueryNode CanonicalNode { get; }

    /// <summary>
    /// Bindings visible at this node's output, sorted by binding identity with exact shape and availability metadata.
    /// </summary>
    public ImmutableArray<RelationQueryBindingShape> OutputBindings { get; }

    /// <summary>Demanded analyzed expression sites evaluated by this node, sorted by stable site identity.</summary>
    public ImmutableArray<RelationQueryExpressionSiteAnalysis> ExpressionSites { get; }

    /// <summary>Demanded projection assignments sorted by stable assignment identity.</summary>
    public ImmutableArray<RelationQueryProjectionExecutionAssignment> ProjectionAssignments { get; }

    /// <summary>Demanded aggregate groupings sorted by stable assignment identity.</summary>
    public ImmutableArray<RelationQueryAggregateGroupingExecution> AggregateGroupings { get; }

    /// <summary>Demanded aggregate assignments sorted by stable assignment identity.</summary>
    public ImmutableArray<RelationQueryAggregateAssignmentExecution> AggregateAssignments { get; }

    /// <summary>Prepared temporal semantics when this is a temporal join node; otherwise <see langword="null"/>.</summary>
    public RelationQueryTemporalJoinExecution? TemporalJoin { get; }

    /// <summary>Demanded distinct-key sites sorted by their canonical ordinal.</summary>
    public ImmutableArray<RelationQueryExpressionSiteAnalysis> DistinctKeys { get; }

    /// <summary>Indexed partition expressions for ordered representative selection.</summary>
    public ImmutableArray<RelationQueryExpressionSiteAnalysis> RepresentativeKeys { get; }

    /// <summary>Demanded ordering-key sites sorted by their canonical ordinal.</summary>
    public ImmutableArray<RelationQueryExpressionSiteAnalysis> OrderKeys { get; }

    /// <summary>Demanded keyset-boundary sites sorted by their canonical ordinal.</summary>
    public ImmutableArray<RelationQueryExpressionSiteAnalysis> KeysetBoundaries { get; }

    ImmutableArray<RelationQueryExpressionSiteAnalysis> SelectIndexedSites(
        RelationQueryExpressionSiteKind kind) =>
    [
        .. ExpressionSites.Where(site => site.Kind == kind)
            .OrderBy(static site => site.Ordinal)
            .ThenBy(static site => site.Analysis.Site.Id.Value, StringComparer.Ordinal)
    ];

    static ImmutableArray<RelationQueryBindingShape> NormalizeBindings(
        ImmutableArray<RelationQueryBindingShape> bindings,
        QueryNodeId node)
    {
        var normalized = bindings.IsDefault ? [] : bindings;
        if (normalized.Any(binding => binding.Node != node))
            throw new ArgumentException("Execution-node output bindings must belong to the node.", nameof(bindings));
        if (normalized.GroupBy(static binding => binding.Binding).Any(static group => group.Count() > 1))
            throw new ArgumentException("Execution-node output bindings cannot repeat a binding identity.", nameof(bindings));
        return [.. normalized.OrderBy(static binding => binding.Binding.Value, StringComparer.Ordinal)];
    }

    static ImmutableArray<RelationQueryExpressionSiteAnalysis> NormalizeSites(
        ImmutableArray<RelationQueryExpressionSiteAnalysis> sites,
        QueryNodeId node)
    {
        var normalized = sites.IsDefault ? [] : sites;
        if (normalized.Any(static site => site is null))
            throw new ArgumentException("Execution-node expression sites cannot contain null entries.", nameof(sites));
        if (normalized.Any(site => site.Node != node))
            throw new ArgumentException("Execution-node expression sites must belong to the node.", nameof(sites));
        if (normalized.GroupBy(static site => site.Analysis.Site.Id).Any(static group => group.Count() > 1))
            throw new ArgumentException("Execution-node expression sites cannot repeat a stable site identity.", nameof(sites));
        return [.. normalized.OrderBy(static site => site.Analysis.Site.Id.Value, StringComparer.Ordinal)];
    }

    static ImmutableArray<RelationQueryProjectionExecutionAssignment> NormalizeProjectionAssignments(
        ImmutableArray<RelationQueryProjectionExecutionAssignment> assignments)
    {
        var normalized = assignments.IsDefault ? [] : assignments;
        if (normalized.Any(static assignment => assignment is null))
            throw new ArgumentException("Projection execution assignments cannot contain null entries.", nameof(assignments));
        if (normalized.GroupBy(static assignment => assignment.Definition.Id).Any(static group => group.Count() > 1))
            throw new ArgumentException("Projection execution assignments cannot repeat an identity.", nameof(assignments));
        return [.. normalized.OrderBy(static assignment => assignment.Definition.Id.Value, StringComparer.Ordinal)];
    }

    static ImmutableArray<RelationQueryAggregateGroupingExecution> NormalizeAggregateGroupings(
        ImmutableArray<RelationQueryAggregateGroupingExecution> groupings)
    {
        var normalized = groupings.IsDefault ? [] : groupings;
        if (normalized.Any(static grouping => grouping is null))
            throw new ArgumentException("Aggregate execution groupings cannot contain null entries.", nameof(groupings));
        if (normalized.GroupBy(static grouping => grouping.Definition.Id).Any(static group => group.Count() > 1))
            throw new ArgumentException("Aggregate execution groupings cannot repeat an identity.", nameof(groupings));
        return [.. normalized.OrderBy(static grouping => grouping.Definition.Id.Value, StringComparer.Ordinal)];
    }

    static ImmutableArray<RelationQueryAggregateAssignmentExecution> NormalizeAggregateAssignments(
        ImmutableArray<RelationQueryAggregateAssignmentExecution> assignments)
    {
        var normalized = assignments.IsDefault ? [] : assignments;
        if (normalized.Any(static assignment => assignment is null))
            throw new ArgumentException("Aggregate execution assignments cannot contain null entries.", nameof(assignments));
        if (normalized.GroupBy(static assignment => assignment.Definition.Id).Any(static group => group.Count() > 1))
            throw new ArgumentException("Aggregate execution assignments cannot repeat an identity.", nameof(assignments));
        return [.. normalized.OrderBy(static assignment => assignment.Definition.Id.Value, StringComparer.Ordinal)];
    }
}

/// <summary>Demand-scoped terminal metadata for a canonical relation output.</summary>
public sealed record RelationQueryRelationExecutionOutput
{
    internal RelationQueryRelationExecutionOutput(
        RelationId relation,
        ValueBindingId rootBinding,
        RelationOutputDefinition definition,
        ValueBindingId binding,
        ImmutableArray<RelationQueryOutputReference> outputs,
        RelationQueryExpressionSiteAnalysis? keySite,
        ImmutableArray<RelationQueryInvariantExecution> invariants)
    {
        Relation = Guard.RequireNotNull(relation);
        if (string.IsNullOrWhiteSpace(rootBinding.Value))
            throw new ArgumentException("A relation execution output requires a root binding.", nameof(rootBinding));
        Definition = Guard.RequireNotNull(definition);
        if (string.IsNullOrWhiteSpace(binding.Value))
            throw new ArgumentException("A relation execution output requires a result binding.", nameof(binding));
        RootBinding = rootBinding;
        Binding = binding;
        Outputs = NormalizeOutputs(outputs, RelationQueryOutputReferenceKind.Relation);
        if (Outputs.Any(output => output.Relation != relation
            || output.Node != definition.Node
            || output.Shape != definition.Shape))
        {
            throw new ArgumentException("Relation execution outputs must match the canonical relation terminal.", nameof(outputs));
        }
        Fields = RelationQueryContractOrdering.NormalizeFields(
            Outputs.Where(static output => output.Field is not null)
                .Select(static output => output.Field!.Value));
        if (keySite is not null && keySite.Kind != RelationQueryExpressionSiteKind.RelationOutputKey)
            throw new ArgumentException("A relation output key requires a relation-output-key site.", nameof(keySite));
        if ((definition.Key is null) != (keySite is null))
            throw new ArgumentException("Relation output-key site presence must match the canonical terminal.", nameof(keySite));
        KeySite = keySite;
        Invariants = NormalizeInvariants(invariants);
    }

    /// <summary>Stable canonical relation identity.</summary>
    public RelationId Relation { get; }

    /// <summary>Binding whose values define rooted relation execution.</summary>
    public ValueBindingId RootBinding { get; }

    /// <summary>Exact canonical relation output definition.</summary>
    public RelationOutputDefinition Definition { get; }

    /// <summary>Binding containing each emitted output value.</summary>
    public ValueBindingId Binding { get; }

    /// <summary>Demanded row and field outputs sorted by stable output identity.</summary>
    public ImmutableArray<RelationQueryOutputReference> Outputs { get; }

    /// <summary>Demanded output fields sorted by graph, shape, and field path.</summary>
    public ImmutableArray<RelationQueryFieldReference> Fields { get; }

    /// <summary>Analyzed stable-output-key site, or <see langword="null"/> when no key is declared.</summary>
    public RelationQueryExpressionSiteAnalysis? KeySite { get; }

    /// <summary>Demanded relation invariants sorted by invariant name.</summary>
    public ImmutableArray<RelationQueryInvariantExecution> Invariants { get; }

    static ImmutableArray<RelationQueryInvariantExecution> NormalizeInvariants(
        ImmutableArray<RelationQueryInvariantExecution> invariants)
    {
        var normalized = invariants.IsDefault ? [] : invariants;
        if (normalized.Any(static invariant => invariant is null))
            throw new ArgumentException("Relation execution invariants cannot contain null entries.", nameof(invariants));
        if (normalized.GroupBy(static invariant => invariant.Definition.Name, StringComparer.Ordinal)
            .Any(static group => group.Count() > 1))
        {
            throw new ArgumentException("Relation execution invariants cannot repeat a name.", nameof(invariants));
        }
        return [.. normalized.OrderBy(static invariant => invariant.Definition.Name, StringComparer.Ordinal)];
    }

    internal static ImmutableArray<RelationQueryOutputReference> NormalizeOutputs(
        ImmutableArray<RelationQueryOutputReference> outputs,
        RelationQueryOutputReferenceKind kind)
    {
        var normalized = outputs.IsDefault ? [] : outputs;
        if (normalized.IsDefaultOrEmpty)
            throw new ArgumentException("An execution terminal requires at least one demanded output.", nameof(outputs));
        if (normalized.Any(static output => output is null))
            throw new ArgumentException("Execution outputs cannot contain null entries.", nameof(outputs));
        if (normalized.Any(output => output.Kind != kind))
            throw new ArgumentException("Execution outputs must have the expected terminal kind.", nameof(outputs));
        if (normalized.GroupBy(static output => output.Id).Any(static group => group.Count() > 1))
            throw new ArgumentException("Execution outputs cannot repeat a stable output identity.", nameof(outputs));
        return [.. normalized.OrderBy(static output => output.Id.Value, StringComparer.Ordinal)];
    }
}

/// <summary>Demand-scoped terminal metadata for one named canonical query result.</summary>
public sealed record RelationQueryResultExecutionBranch
{
    internal RelationQueryResultExecutionBranch(
        QueryResultDefinition definition,
        ValueBindingId binding,
        QualifiedShapeId shape,
        ImmutableArray<RelationQueryOutputReference> outputs)
    {
        Definition = Guard.RequireNotNull(definition);
        if (string.IsNullOrWhiteSpace(binding.Value))
            throw new ArgumentException("A query-result execution branch requires a result binding.", nameof(binding));
        Binding = binding;
        Shape = shape;
        Outputs = RelationQueryRelationExecutionOutput.NormalizeOutputs(
            outputs,
            RelationQueryOutputReferenceKind.QueryResult);
        if (Outputs.Any(output => output.QueryResult != definition.Id
            || output.Node != definition.Input
            || output.Shape != shape))
        {
            throw new ArgumentException("Query-result execution outputs must match the canonical result branch.", nameof(outputs));
        }
        Fields = RelationQueryContractOrdering.NormalizeFields(
            Outputs.Where(static output => output.Field is not null)
                .Select(static output => output.Field!.Value));
    }

    /// <summary>Stable named result identity.</summary>
    public QueryResultId Id => Definition.Id;

    /// <summary>Exact canonical named result definition.</summary>
    public QueryResultDefinition Definition { get; }

    /// <summary>Binding containing each emitted result value.</summary>
    public ValueBindingId Binding { get; }

    /// <summary>Exact semantic shape emitted by this result branch.</summary>
    public QualifiedShapeId Shape { get; }

    /// <summary>Demanded row and field outputs sorted by stable output identity.</summary>
    public ImmutableArray<RelationQueryOutputReference> Outputs { get; }

    /// <summary>Demanded result fields sorted by graph, shape, and field path.</summary>
    public ImmutableArray<RelationQueryFieldReference> Fields { get; }
}

/// <summary>
/// Explicit demand-scoped execution material projected once by static compilation.
/// </summary>
/// <remarks>
/// The slice references exact canonical nodes, assignments, expression analyses, terminal outputs,
/// and binding metadata. Interpreters can therefore execute the compiled demand without rediscovering
/// assignments, expression sites, result branches, or binding shapes by scanning the persisted IR.
/// </remarks>
public sealed class RelationQueryExecutionSlice
{
    internal RelationQueryExecutionSlice(
        RelationQueryDefinition definition,
        RelationQueryLogicalPlan logicalPlan,
        RelationQueryRequirementGraph requirements,
        RelationQueryExpressionAnalysisResult expressionAnalysis,
        ImmutableArray<RelationQueryExpressionSiteAnalysis> demandedSites,
        ImmutableArray<RelationQueryAggregateAssignmentReference> demandedAggregateAssignments)
    {
        Guard.RequireNotNull(definition);
        LogicalPlan = Guard.RequireNotNull(logicalPlan);
        Requirements = Guard.RequireNotNull(requirements);
        Guard.RequireNotNull(expressionAnalysis);

        var canonicalNodes = definition.Body.Nodes.ToDictionary(static node => node.Id);
        var plannedNodes = logicalPlan.Nodes.ToDictionary(static node => node.Node);
        var sites = NormalizeDemandedSites(demandedSites);
        var aggregateAssignments = demandedAggregateAssignments.IsDefault
            ? []
            : demandedAggregateAssignments.Distinct().ToImmutableArray();
        var bindingsByNode = expressionAnalysis.BindingShapes.ToLookup(static binding => binding.Node);
        Nodes =
        [
            .. logicalPlan.EvaluationOrder.Select(node => CreateNode(
                plannedNodes[node],
                canonicalNodes[node],
                [.. bindingsByNode[node]],
                [.. sites.Where(site => site.Node == node)],
                aggregateAssignments))
        ];
        ExpressionSites = sites;

        switch (definition)
        {
            case Cohesive.Relations.IR.RelationDefinition relation:
                RelationOutput = CreateRelationOutput(relation, requirements, expressionAnalysis, sites);
                QueryResults = [];
                break;
            case QueryDefinition query:
                RelationOutput = null;
                QueryResults = CreateQueryResults(query, requirements, expressionAnalysis);
                break;
            default:
                throw new ArgumentException("Unsupported relation/query definition type.", nameof(definition));
        }
    }

    /// <summary>Retained execution nodes in deterministic dependency-first evaluation order.</summary>
    public ImmutableArray<RelationQueryExecutionNode> Nodes { get; }

    /// <summary>All demanded expression sites sorted by stable expression-site identity.</summary>
    public ImmutableArray<RelationQueryExpressionSiteAnalysis> ExpressionSites { get; }

    /// <summary>Relation terminal metadata, or <see langword="null"/> for a query definition.</summary>
    public RelationQueryRelationExecutionOutput? RelationOutput { get; }

    /// <summary>Demanded named query-result branches sorted by stable result identity.</summary>
    public ImmutableArray<RelationQueryResultExecutionBranch> QueryResults { get; }

    /// <summary>Effective retained logical topology used by <see cref="Nodes"/>.</summary>
    public RelationQueryLogicalPlan LogicalPlan { get; }

    /// <summary>Canonical requirement graph from which terminal demand was projected.</summary>
    public RelationQueryRequirementGraph Requirements { get; }

    static RelationQueryExecutionNode CreateNode(
        RelationQueryLogicalPlanNode logicalPlan,
        LogicalQueryNode canonicalNode,
        ImmutableArray<RelationQueryBindingShape> outputBindings,
        ImmutableArray<RelationQueryExpressionSiteAnalysis> sites,
        ImmutableArray<RelationQueryAggregateAssignmentReference> demandedAggregateAssignments)
    {
        var sitesByAssignment = sites
            .Where(static site => site.Assignment is not null)
            .ToLookup(static site => site.Assignment!.Value);
        ImmutableArray<RelationQueryProjectionExecutionAssignment> projections = [];
        ImmutableArray<RelationQueryAggregateGroupingExecution> groupings = [];
        ImmutableArray<RelationQueryAggregateAssignmentExecution> aggregates = [];

        if (canonicalNode is ProjectQueryNode project)
        {
            projections =
            [
                .. project.Assignments
                    .Where(assignment => sitesByAssignment[assignment.Id]
                        .Any(static site => site.Kind == RelationQueryExpressionSiteKind.ProjectionAssignmentValue))
                    .Select(assignment => new RelationQueryProjectionExecutionAssignment(
                        assignment,
                        sitesByAssignment[assignment.Id].Single(static site =>
                            site.Kind == RelationQueryExpressionSiteKind.ProjectionAssignmentValue)))
            ];
        }
        else if (canonicalNode is AggregateQueryNode aggregate)
        {
            groupings =
            [
                .. aggregate.Groupings
                    .Where(grouping => sitesByAssignment[grouping.Id]
                        .Any(static site => site.Kind == RelationQueryExpressionSiteKind.AggregateGroupingKey))
                    .Select(grouping => new RelationQueryAggregateGroupingExecution(
                        grouping,
                        sitesByAssignment[grouping.Id].Single(static site =>
                            site.Kind == RelationQueryExpressionSiteKind.AggregateGroupingKey)))
            ];

            var demandedIds = demandedAggregateAssignments
                .Where(reference => reference.Node == aggregate.Id)
                .Select(static reference => reference.Assignment)
                .ToHashSet();
            aggregates =
            [
                .. aggregate.Aggregates
                    .Where(assignment => demandedIds.Contains(assignment.Id))
                    .Select(assignment => new RelationQueryAggregateAssignmentExecution(
                        assignment,
                        sitesByAssignment[assignment.Id].SingleOrDefault(static site =>
                            site.Kind == RelationQueryExpressionSiteKind.AggregateAssignmentValue),
                        sitesByAssignment[assignment.Id].SingleOrDefault(static site =>
                            site.Kind == RelationQueryExpressionSiteKind.AggregateAssignmentFilter)))
            ];
        }

        return new(
            logicalPlan,
            canonicalNode,
            outputBindings,
            sites,
            projections,
            groupings,
            aggregates);
    }

    static RelationQueryRelationExecutionOutput CreateRelationOutput(
        Cohesive.Relations.IR.RelationDefinition relation,
        RelationQueryRequirementGraph requirements,
        RelationQueryExpressionAnalysisResult expressionAnalysis,
        ImmutableArray<RelationQueryExpressionSiteAnalysis> sites)
    {
        var outputs = requirements.Outputs
            .Where(static output => output.Kind == RelationQueryOutputReferenceKind.Relation)
            .ToImmutableArray();
        var binding = ResolveOutputBinding(
            relation.Output.Node,
            relation.Output.Shape,
            expressionAnalysis.BindingShapes);
        var keySite = sites.SingleOrDefault(static site =>
            site.Kind == RelationQueryExpressionSiteKind.RelationOutputKey);
        var invariantSites = sites
            .Where(static site => site.Kind == RelationQueryExpressionSiteKind.RelationInvariant)
            .ToDictionary(static site => site.InvariantName!, StringComparer.Ordinal);
        var invariants = relation.Invariants
            .Where(invariant => invariantSites.ContainsKey(invariant.Name))
            .Select(invariant => new RelationQueryInvariantExecution(invariant, invariantSites[invariant.Name]))
            .ToImmutableArray();
        return new(
            relation.Id,
            relation.RootBinding,
            relation.Output,
            binding,
            outputs,
            keySite,
            invariants);
    }

    static ImmutableArray<RelationQueryResultExecutionBranch> CreateQueryResults(
        QueryDefinition query,
        RelationQueryRequirementGraph requirements,
        RelationQueryExpressionAnalysisResult expressionAnalysis)
    {
        var outputsByResult = requirements.Outputs
            .Where(static output => output.Kind == RelationQueryOutputReferenceKind.QueryResult)
            .GroupBy(static output => output.QueryResult!.Value)
            .ToDictionary(static group => group.Key, static group => group.ToImmutableArray());
        return
        [
            .. query.Results
                .Where(result => outputsByResult.ContainsKey(result.Id))
                .Select(result =>
                {
                    var outputs = outputsByResult[result.Id];
                    var shape = outputs[0].Shape;
                    var binding = ResolveOutputBinding(result.Input, shape, expressionAnalysis.BindingShapes);
                    return new RelationQueryResultExecutionBranch(result, binding, shape, outputs);
                })
                .OrderBy(static result => result.Id.Value, StringComparer.Ordinal)
        ];
    }

    static ValueBindingId ResolveOutputBinding(
        QueryNodeId node,
        QualifiedShapeId shape,
        ImmutableArray<RelationQueryBindingShape> bindings)
    {
        var matches = bindings
            .Where(binding => binding.Node == node && binding.Shape == shape)
            .Select(static binding => binding.Binding)
            .Distinct()
            .Take(2)
            .ToArray();
        if (matches.Length != 1)
        {
            throw new ArgumentException(
                $"Execution terminal '{node.Value}' does not resolve to one binding of shape '{shape}'.",
                nameof(bindings));
        }
        return matches[0];
    }

    static ImmutableArray<RelationQueryExpressionSiteAnalysis> NormalizeDemandedSites(
        ImmutableArray<RelationQueryExpressionSiteAnalysis> sites)
    {
        var normalized = sites.IsDefault ? [] : sites;
        if (normalized.Any(static site => site is null))
            throw new ArgumentException("Demanded execution sites cannot contain null entries.", nameof(sites));
        if (normalized.GroupBy(static site => site.Analysis.Site.Id).Any(static group => group.Count() > 1))
            throw new ArgumentException("Demanded execution sites cannot repeat a stable site identity.", nameof(sites));
        return [.. normalized.OrderBy(static site => site.Analysis.Site.Id.Value, StringComparer.Ordinal)];
    }
}
