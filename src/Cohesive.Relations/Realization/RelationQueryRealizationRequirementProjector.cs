using System.Collections.Immutable;
using System.Globalization;
using Cohesive.Model.Expressions;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.IR;
using Cohesive.Relations.Model;

namespace Cohesive.Relations.Realization;

/// <summary>
/// Projects the exact target capabilities demanded by a successful static relation/query compilation.
/// </summary>
/// <remarks>
/// Projection consumes only the compiler-produced execution slice and input contract. It never
/// re-discovers demand by walking the complete canonical definition, so safely pruned nodes,
/// assignments, expression sites, and terminals cannot reappear as realization requirements.
/// </remarks>
public static class RelationQueryRealizationRequirementProjector
{
    static readonly ImmutableArray<RelationQueryGuaranteeCapabilityKind> SemanticBaselineGuarantees =
    [
        RelationQueryGuaranteeCapabilityKind.MissingNullDistinction,
        RelationQueryGuaranteeCapabilityKind.AbsenceAvailabilityFailureDistinction,
        RelationQueryGuaranteeCapabilityKind.DeterministicResult,
        RelationQueryGuaranteeCapabilityKind.EvidenceCompleteness,
        RelationQueryGuaranteeCapabilityKind.InconclusiveEvidence
    ];

    static readonly ImmutableArray<RelationQueryGuaranteeCapabilityKind> StrictBaselineGuarantees =
    [
        .. SemanticBaselineGuarantees,
        RelationQueryGuaranteeCapabilityKind.OccurrenceProvenance
    ];

    /// <summary>Projects deterministic, demand-scoped realization requirements from a compiled plan.</summary>
    /// <param name="plan">Successful target-independent relation/query plan.</param>
    /// <returns>Requirements sorted by stable requirement identity.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="plan"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// The compiled execution slice and input contract contain inconsistent provenance or duplicate
    /// requirement identities with conflicting definitions.
    /// </exception>
    /// <remarks>
    /// This compatibility overload requires <see cref="RelationQueryResultObservability.ExactContributors"/>.
    /// </remarks>
    public static ImmutableArray<RelationQueryRealizationRequirement> Project(
        CompiledRelationQueryPlan plan)
        => Project(plan, RelationQueryResultObservability.ExactContributors);

    /// <summary>
    /// Projects deterministic, demand-scoped realization requirements from a compiled plan under an explicit
    /// result-observability contract.
    /// </summary>
    /// <param name="plan">Successful target-independent relation/query plan.</param>
    /// <param name="observability">Runtime result observability required from the interpretation.</param>
    /// <returns>Requirements sorted by stable requirement identity.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="plan"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// The compiled execution slice and input contract contain inconsistent provenance or duplicate
    /// requirement identities with conflicting definitions.
    /// </exception>
    public static ImmutableArray<RelationQueryRealizationRequirement> Project(
        CompiledRelationQueryPlan plan,
        RelationQueryResultObservability observability)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return new Projector(plan.ExecutionSlice, plan.InputContract, observability).Project();
    }

    sealed class Projector
    {
        readonly RelationQueryExecutionSlice slice;
        readonly RelationQueryInputContract inputContract;
        readonly RelationQueryResultObservability observability;
        readonly ImmutableArray<InputUse> inputUses;
        readonly ImmutableArray<RelationQueryFieldInputContract> fieldInputs;
        readonly IReadOnlyDictionary<ExprCapabilityRequirement, RelationQueryCapabilityInputContract> capabilityInputs;
        readonly IReadOnlyDictionary<QueryNodeId, RelationQueryLogicalPlanNode> logicalNodes;
        readonly IReadOnlyDictionary<ExprSiteId, QueryNodeId> expressionSiteNodes;
        readonly IReadOnlyDictionary<string, RelationQueryExpressionSiteAnalysis> expressionSitesById;
        readonly Dictionary<RelationQueryRealizationRequirementId, RelationQueryRealizationRequirement> requirements = [];
        readonly HashSet<RelationQueryGuaranteeCapabilityKind> guarantees = [];

        public Projector(
            RelationQueryExecutionSlice slice,
            RelationQueryInputContract inputContract,
            RelationQueryResultObservability observability)
        {
            this.slice = Guard.RequireNotNull(slice);
            this.inputContract = Guard.RequireNotNull(inputContract);
            this.observability = observability;
            if (!ReferenceEquals(slice.Requirements, inputContract.Requirements))
            {
                throw new InvalidOperationException(
                    "The execution slice and input contract must belong to the same requirement graph.");
            }

            logicalNodes = slice.LogicalPlan.Nodes.ToDictionary(static node => node.Node);
            inputUses = CreateInputUses(inputContract);
            fieldInputs =
            [
                .. inputContract.Sources.SelectMany(static source => source.Fields),
                .. inputContract.Traversals.SelectMany(static traversal => traversal.Fields)
            ];
            capabilityInputs = inputContract.Capabilities.ToDictionary(static contract => contract.Input.Capability);
            expressionSiteNodes = CreateExpressionSiteNodes(slice, inputUses);
            expressionSitesById = slice.ExpressionSites.ToDictionary(
                static site => site.Analysis.Site.Id.Value,
                StringComparer.Ordinal);
        }

        public ImmutableArray<RelationQueryRealizationRequirement> Project()
        {
            AddBaselineGuarantees();
            foreach (var node in slice.Nodes)
                ProjectNode(node);

            ProjectExpressionCapabilities();
            ProjectInputFieldStructures();
            ProjectExpressionFieldStructures();
            ProjectExpressionBindingAvailability();
            ProjectTemporalCapabilities();
            ProjectTerminals();

            foreach (var guarantee in guarantees.OrderBy(static value => (int)value))
            {
                Add(new(
                    GuaranteeId(guarantee),
                    new GuaranteeRelationQueryCapability(guarantee),
                    new(semanticSite: $"plan/guarantee/{((int)guarantee).ToString(CultureInfo.InvariantCulture)}"),
                    DirectTerminalUses(
                        slice.Requirements.Outputs,
                        RelationQueryRequirementEffect.Evaluation)));
            }

            return
            [
                .. requirements.Values.OrderBy(static requirement => requirement.Id.Value, StringComparer.Ordinal)
            ];
        }

        void ProjectNode(RelationQueryExecutionNode execution)
        {
            switch (execution.CanonicalNode)
            {
                case SourceQueryNode source:
                    ProjectSource(source);
                    break;
                case FilterQueryNode filter:
                    AddLogical(
                        execution,
                        RelationQueryLogicalCapabilityKind.Filter,
                        RelationQueryRequirementEffect.Membership,
                        requiredGuarantees:
                        [
                            RelationQueryGuaranteeCapabilityKind.MissingNullDistinction,
                            RelationQueryGuaranteeCapabilityKind.AbsenceAvailabilityFailureDistinction
                        ]);
                    break;
                case TraverseRelationshipQueryNode traversal:
                    ProjectTraversal(execution, traversal);
                    break;
                case JoinQueryNode join:
                    ProjectJoin(execution, join);
                    break;
                case TemporalJoinQueryNode:
                    AddLogical(
                        execution,
                        RelationQueryLogicalCapabilityKind.TemporalJoin,
                        RelationQueryRequirementEffect.Correlation,
                        requiredGuarantees:
                        [
                            RelationQueryGuaranteeCapabilityKind.JoinMembership,
                            RelationQueryGuaranteeCapabilityKind.Cardinality,
                            .. TemporalOperationGuarantees(execution.Id)
                        ]);
                    AddGuarantee(RelationQueryGuaranteeCapabilityKind.JoinMembership);
                    AddGuarantee(RelationQueryGuaranteeCapabilityKind.Cardinality);
                    break;
                case ExpandCollectionQueryNode:
                    AddLogical(
                        execution,
                        RelationQueryLogicalCapabilityKind.ExpandCollection,
                        RelationQueryRequirementEffect.Cardinality,
                        requiredGuarantees: [RelationQueryGuaranteeCapabilityKind.Cardinality]);
                    AddGuarantee(RelationQueryGuaranteeCapabilityKind.Cardinality);
                    break;
                case ProjectQueryNode:
                    ProjectProjection(execution);
                    break;
                case DistinctQueryNode distinct:
                    ProjectDistinct(execution, distinct);
                    break;
                case AggregateQueryNode:
                    ProjectAggregate(execution);
                    break;
                case OrderQueryNode order:
                    ProjectOrder(execution, order);
                    break;
                case PageQueryNode page:
                    ProjectPage(execution, page);
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Demand-scoped logical node '{execution.Id.Value}' has unsupported type "
                        + $"'{execution.CanonicalNode.GetType().Name}'.");
            }
        }

        void ProjectSource(SourceQueryNode source)
        {
            var contract = inputContract.Sources.Single(candidate => candidate.Node == source.Id);
            AddLogical(
                source.Id,
                RelationQueryLogicalCapabilityKind.Source,
                new(contract.Input.Id, source.Id, NodeSite(source.Id, "source")),
                UsesForInput(contract.Input.Id),
                requiredGuarantees:
                [
                    RelationQueryGuaranteeCapabilityKind.MissingNullDistinction,
                    RelationQueryGuaranteeCapabilityKind.AbsenceAvailabilityFailureDistinction,
                    RelationQueryGuaranteeCapabilityKind.EvidenceCompleteness
                ]);
        }

        void ProjectTraversal(
            RelationQueryExecutionNode execution,
            TraverseRelationshipQueryNode traversal)
        {
            var contract = inputContract.Traversals.Single(candidate => candidate.Input.Traversal == traversal.Id);
            var origin = new RelationQueryRealizationRequirementOrigin(
                contract.Input.Id,
                traversal.Id,
                NodeSite(traversal.Id, "relationship-traversal"));
            var uses = UsesForInput(contract.Input.Id);

            AddLogical(
                traversal.Id,
                RelationQueryLogicalCapabilityKind.RelationshipTraversal,
                origin,
                uses,
                requiredGuarantees:
                [
                    RelationQueryGuaranteeCapabilityKind.JoinMembership,
                    RelationQueryGuaranteeCapabilityKind.Cardinality
                ]);
            AddLogical(
                traversal.Id,
                traversal.Direction == RelationshipTraversalDirection.Forward
                    ? RelationQueryLogicalCapabilityKind.ForwardRelationshipTraversal
                    : RelationQueryLogicalCapabilityKind.InverseRelationshipTraversal,
                origin,
                uses,
                requiredGuarantees: [RelationQueryGuaranteeCapabilityKind.RelationshipDirection]);
            AddLogical(
                traversal.Id,
                contract.Cardinality == RelationshipTraversalCardinality.AtMostOne
                    ? RelationQueryLogicalCapabilityKind.AtMostOneRelationshipTraversal
                    : RelationQueryLogicalCapabilityKind.ManyRelationshipTraversal,
                origin,
                uses,
                requiredGuarantees:
                [
                    RelationQueryGuaranteeCapabilityKind.Cardinality,
                    RelationQueryGuaranteeCapabilityKind.RelationshipMultiplicity
                ]);
            AddLogical(
                traversal.Id,
                traversal.Requirement == QueryInputRequirement.Required
                    ? RelationQueryLogicalCapabilityKind.RequiredRelationshipTraversal
                    : RelationQueryLogicalCapabilityKind.OptionalRelationshipTraversal,
                origin,
                uses,
                requiredGuarantees:
                [
                    RelationQueryGuaranteeCapabilityKind.Cardinality,
                    RelationQueryGuaranteeCapabilityKind.AbsenceAvailabilityFailureDistinction
                ]);
            AddLogical(
                traversal.Id,
                JoinCapability(traversal.JoinKind),
                origin,
                uses,
                requiredGuarantees:
                [
                    RelationQueryGuaranteeCapabilityKind.JoinMembership,
                    RelationQueryGuaranteeCapabilityKind.Cardinality
                ]);

            AddGuarantee(RelationQueryGuaranteeCapabilityKind.JoinMembership);
            AddGuarantee(RelationQueryGuaranteeCapabilityKind.Cardinality);
            AddGuarantee(RelationQueryGuaranteeCapabilityKind.RelationshipDirection);
            AddGuarantee(RelationQueryGuaranteeCapabilityKind.RelationshipMultiplicity);
        }

        void ProjectJoin(RelationQueryExecutionNode execution, JoinQueryNode join)
        {
            AddLogical(
                execution,
                RelationQueryLogicalCapabilityKind.Join,
                RelationQueryRequirementEffect.Correlation,
                requiredGuarantees:
                [
                    RelationQueryGuaranteeCapabilityKind.JoinMembership,
                    RelationQueryGuaranteeCapabilityKind.Cardinality
                ]);
            AddLogical(
                execution,
                JoinCapability(join.Kind),
                RelationQueryRequirementEffect.Membership,
                requiredGuarantees:
                [
                    RelationQueryGuaranteeCapabilityKind.JoinMembership,
                    RelationQueryGuaranteeCapabilityKind.Cardinality
                ]);
            AddGuarantee(RelationQueryGuaranteeCapabilityKind.JoinMembership);
            AddGuarantee(RelationQueryGuaranteeCapabilityKind.Cardinality);
        }

        void ProjectProjection(RelationQueryExecutionNode execution)
        {
            AddLogical(
                execution,
                RelationQueryLogicalCapabilityKind.Projection,
                RelationQueryRequirementEffect.Value,
                requiredGuarantees:
                [
                    RelationQueryGuaranteeCapabilityKind.MissingNullDistinction,
                    RelationQueryGuaranteeCapabilityKind.AbsenceAvailabilityFailureDistinction
                ]);
            foreach (var assignment in execution.ProjectionAssignments)
            {
                var site = assignment.ValueSite;
                AddLogical(
                    execution.Id,
                    RelationQueryLogicalCapabilityKind.ProjectionAssignment,
                    new(
                        node: execution.Id,
                        semanticSite: site.Analysis.Site.Id.Value,
                        fieldPath: assignment.Definition.Target),
                    UsesForSite(site, RelationQueryRequirementEffect.Value),
                    qualifier: $"assignment/{Encode(assignment.Definition.Id.Value)}",
                    requiredGuarantees:
                    [
                        RelationQueryGuaranteeCapabilityKind.MissingNullDistinction,
                        RelationQueryGuaranteeCapabilityKind.AbsenceAvailabilityFailureDistinction
                    ]);
                AddStructural(
                    RelationQueryStructuralCapabilityRole.ProjectionTarget,
                    assignment.Definition.Target,
                    input: null,
                    execution.Id,
                    site.Analysis.Site.Id.Value,
                    UsesForSite(site, RelationQueryRequirementEffect.Value));
            }
        }

        void ProjectDistinct(RelationQueryExecutionNode execution, DistinctQueryNode distinct)
        {
            AddLogical(
                execution,
                distinct.Keys.IsDefaultOrEmpty
                    ? RelationQueryLogicalCapabilityKind.DistinctRows
                    : RelationQueryLogicalCapabilityKind.DistinctKeys,
                RelationQueryRequirementEffect.Cardinality,
                requiredGuarantees:
                [
                    RelationQueryGuaranteeCapabilityKind.DuplicateHandling,
                    RelationQueryGuaranteeCapabilityKind.Cardinality,
                    RelationQueryGuaranteeCapabilityKind.MissingNullDistinction
                ]);
            if (distinct.Keys.IsDefaultOrEmpty)
            {
                AddStructural(
                    RelationQueryStructuralCapabilityRole.CompleteValue,
                    path: null,
                    input: null,
                    execution.Id,
                    NodeSite(execution.Id, "distinct-complete-value"),
                    UsesForNode(execution.Id, RelationQueryRequirementEffect.Cardinality));
            }
            AddGuarantee(RelationQueryGuaranteeCapabilityKind.DuplicateHandling);
            AddGuarantee(RelationQueryGuaranteeCapabilityKind.Cardinality);
        }

        void ProjectAggregate(RelationQueryExecutionNode execution)
        {
            var aggregationGuarantees = execution.AggregateGroupings.IsDefaultOrEmpty
                ? ImmutableArray.Create(
                    RelationQueryGuaranteeCapabilityKind.Aggregation,
                    RelationQueryGuaranteeCapabilityKind.Cardinality)
                : ImmutableArray.Create(
                    RelationQueryGuaranteeCapabilityKind.Grouping,
                    RelationQueryGuaranteeCapabilityKind.Aggregation,
                    RelationQueryGuaranteeCapabilityKind.Cardinality);
            AddLogical(
                execution,
                RelationQueryLogicalCapabilityKind.Aggregation,
                RelationQueryRequirementEffect.Aggregation,
                requiredGuarantees: aggregationGuarantees);
            foreach (var grouping in execution.AggregateGroupings)
            {
                AddLogical(
                    execution.Id,
                    RelationQueryLogicalCapabilityKind.AggregateGrouping,
                    new(
                        node: execution.Id,
                        semanticSite: grouping.KeySite.Analysis.Site.Id.Value,
                        fieldPath: grouping.Definition.Target),
                    UsesForSite(grouping.KeySite, RelationQueryRequirementEffect.Grouping),
                    qualifier: $"grouping/{Encode(grouping.Definition.Id.Value)}",
                    requiredGuarantees:
                    [
                        RelationQueryGuaranteeCapabilityKind.Grouping,
                        RelationQueryGuaranteeCapabilityKind.MissingNullDistinction
                    ]);
                AddStructural(
                    RelationQueryStructuralCapabilityRole.GroupingTarget,
                    grouping.Definition.Target,
                    input: null,
                    execution.Id,
                    grouping.KeySite.Analysis.Site.Id.Value,
                    UsesForSite(grouping.KeySite, RelationQueryRequirementEffect.Grouping));
            }

            foreach (var assignment in execution.AggregateAssignments)
            {
                var operationCapability = new ExprCapabilityRequirement(
                    ExprCapabilities.ForAggregate(assignment.Definition.Operation),
                    ExprCapabilityRequirementKind.Operation);
                capabilityInputs.TryGetValue(operationCapability, out var operationInput);
                var operationSite = AggregateOperationSite(execution.Id, assignment.Definition.Id);
                var origin = new RelationQueryRealizationRequirementOrigin(
                    operationInput?.Input.Id,
                    execution.Id,
                    operationSite,
                    fieldPath: assignment.Definition.Target);
                var uses = UsesForAggregateOperation(
                    execution.Id,
                    assignment.Definition.Id,
                    operationInput?.Input.Id);
                AddLogical(
                    execution.Id,
                    AggregateCapability(assignment.Definition.Operation),
                    origin,
                    uses,
                    qualifier: $"aggregate/{Encode(assignment.Definition.Id.Value)}",
                    requiredGuarantees:
                    [
                        RelationQueryGuaranteeCapabilityKind.Aggregation,
                        RelationQueryGuaranteeCapabilityKind.Cardinality,
                        RelationQueryGuaranteeCapabilityKind.MissingNullDistinction
                    ]);
                AddStructural(
                    RelationQueryStructuralCapabilityRole.AggregateTarget,
                    assignment.Definition.Target,
                    input: null,
                    execution.Id,
                    operationSite,
                    uses);

                if (assignment.FilterSite is { } filter)
                {
                    AddLogical(
                        execution.Id,
                        RelationQueryLogicalCapabilityKind.AggregateFilter,
                        new(
                            node: execution.Id,
                            semanticSite: filter.Analysis.Site.Id.Value),
                        UsesForSite(filter, RelationQueryRequirementEffect.Membership),
                        qualifier: $"aggregate-filter/{Encode(assignment.Definition.Id.Value)}",
                        requiredGuarantees:
                        [
                            RelationQueryGuaranteeCapabilityKind.Aggregation,
                            RelationQueryGuaranteeCapabilityKind.MissingNullDistinction,
                            RelationQueryGuaranteeCapabilityKind.AbsenceAvailabilityFailureDistinction
                        ]);
                }
            }

            if (!execution.AggregateGroupings.IsDefaultOrEmpty)
                AddGuarantee(RelationQueryGuaranteeCapabilityKind.Grouping);
            AddGuarantee(RelationQueryGuaranteeCapabilityKind.Aggregation);
            AddGuarantee(RelationQueryGuaranteeCapabilityKind.Cardinality);
        }

        void ProjectOrder(RelationQueryExecutionNode execution, OrderQueryNode order)
        {
            AddLogical(
                execution,
                RelationQueryLogicalCapabilityKind.Ordering,
                RelationQueryRequirementEffect.Ordering,
                requiredGuarantees:
                [
                    RelationQueryGuaranteeCapabilityKind.Ordering,
                    RelationQueryGuaranteeCapabilityKind.NullPlacement
                ]);
            for (var index = 0; index < order.Orderings.Length; index++)
            {
                var ordering = order.Orderings[index];
                var site = execution.OrderKeys.Single(candidate => candidate.Ordinal == index);
                var origin = new RelationQueryRealizationRequirementOrigin(
                    node: execution.Id,
                    semanticSite: site.Analysis.Site.Id.Value);
                var uses = UsesForSite(site, RelationQueryRequirementEffect.Ordering);
                AddLogical(
                    execution.Id,
                    ordering.Direction == QuerySortDirection.Ascending
                        ? RelationQueryLogicalCapabilityKind.AscendingOrdering
                        : RelationQueryLogicalCapabilityKind.DescendingOrdering,
                    origin,
                    uses,
                    qualifier: $"ordering/{index.ToString(CultureInfo.InvariantCulture)}/direction",
                    requiredGuarantees: [RelationQueryGuaranteeCapabilityKind.Ordering]);
                AddLogical(
                    execution.Id,
                    ordering.NullPlacement == QueryNullPlacement.First
                        ? RelationQueryLogicalCapabilityKind.NullsFirst
                        : RelationQueryLogicalCapabilityKind.NullsLast,
                    origin,
                    uses,
                    qualifier: $"ordering/{index.ToString(CultureInfo.InvariantCulture)}/null-placement",
                    requiredGuarantees:
                    [
                        RelationQueryGuaranteeCapabilityKind.NullPlacement,
                        RelationQueryGuaranteeCapabilityKind.MissingNullDistinction
                    ]);
            }
            AddLogical(
                execution.Id,
                RelationQueryLogicalCapabilityKind.StableTieOrdering,
                new(node: execution.Id, semanticSite: NodeSite(execution.Id, "stable-ties")),
                UsesForNode(execution.Id, RelationQueryRequirementEffect.Ordering),
                requiredGuarantees: [RelationQueryGuaranteeCapabilityKind.DeterministicResult]);

            AddGuarantee(RelationQueryGuaranteeCapabilityKind.Ordering);
            AddGuarantee(RelationQueryGuaranteeCapabilityKind.NullPlacement);
        }

        void ProjectPage(RelationQueryExecutionNode execution, PageQueryNode page)
        {
            AddLogical(
                execution,
                page.Page switch
                {
                    OffsetPageDefinition => RelationQueryLogicalCapabilityKind.OffsetPaging,
                    KeysetPageDefinition => RelationQueryLogicalCapabilityKind.KeysetPaging,
                    _ => throw new InvalidOperationException(
                        $"Demand-scoped page node '{page.Id.Value}' has unsupported definition "
                        + $"'{page.Page.GetType().Name}'.")
                },
                RelationQueryRequirementEffect.Pagination,
                requiredGuarantees:
                [
                    RelationQueryGuaranteeCapabilityKind.StablePaging,
                    RelationQueryGuaranteeCapabilityKind.Cardinality,
                    RelationQueryGuaranteeCapabilityKind.DeterministicResult
                ],
                staticFacts:
                [
                    new(
                        RelationQueryRealizationStaticFactKind.PageSize,
                        page.Page.Limit)
                ]);
            AddGuarantee(RelationQueryGuaranteeCapabilityKind.StablePaging);
            AddGuarantee(RelationQueryGuaranteeCapabilityKind.Cardinality);
        }

        void ProjectExpressionCapabilities()
        {
            foreach (var site in slice.ExpressionSites)
            {
                var node = ResolveSiteNode(site);
                foreach (var use in site.Analysis.CapabilityUses)
                {
                    var isScopedCollectionExpression = IsScopedCollectionExpressionUse(site, use);
                    capabilityInputs.TryGetValue(use.Requirement, out var input);
                    var origin = new RelationQueryRealizationRequirementOrigin(
                        input?.Input.Id,
                        node,
                        site.Analysis.Site.Id.Value,
                        use.ExpressionPath);
                    Add(new(
                        ExpressionId(site, use),
                        new ExpressionRelationQueryCapability(
                            use.Requirement.Capability,
                            use.Requirement.Kind),
                        origin,
                        UsesForSite(site, EffectForSite(site.Kind)),
                        requiredGuarantees:
                        [
                            RelationQueryGuaranteeCapabilityKind.MissingNullDistinction,
                            RelationQueryGuaranteeCapabilityKind.AbsenceAvailabilityFailureDistinction,
                            .. isScopedCollectionExpression
                                ? [RelationQueryGuaranteeCapabilityKind.CollectionElementCorrelation]
                                : ImmutableArray<RelationQueryGuaranteeCapabilityKind>.Empty
                        ]));
                    if (isScopedCollectionExpression)
                    {
                        AddGuarantee(RelationQueryGuaranteeCapabilityKind.CollectionElementCorrelation);
                    }
                }
            }
        }

        void ProjectInputFieldStructures()
        {
            foreach (var field in fieldInputs)
            {
                AddStructural(
                    RelationQueryStructuralCapabilityRole.BindingRead,
                    field.Input.Field.Path,
                    field.Input.Id,
                    field.Input.Producer,
                    field.Input.Id.Value,
                    ConvertUses(field.Uses),
                    binding: field.Input.Binding);
                if (!RequiresOccurrenceProvenance())
                    continue;
                AddStructural(
                    RelationQueryStructuralCapabilityRole.OccurrenceEvidenceReconstruction,
                    field.Input.Field.Path,
                    field.Input.Id,
                    field.Input.Producer,
                    field.Input.Id.Value,
                    ConvertUses(field.Uses),
                    binding: field.Input.Binding);
            }
        }

        void ProjectExpressionFieldStructures()
        {
            foreach (var site in slice.ExpressionSites)
            {
                var node = ResolveSiteNode(site);
                foreach (var field in site.Analysis.Requirements.Fields)
                {
                    switch (field.Root)
                    {
                        case ExprFieldRootKind.Binding:
                            {
                                var input = ResolveFieldInput(site, field);
                                AddStructural(
                                    RelationQueryStructuralCapabilityRole.BindingRead,
                                    field.Path,
                                    input?.Input.Id,
                                    node,
                                    site.Analysis.Site.Id.Value,
                                    UsesForSite(site, EffectForSite(site.Kind)),
                                    binding: field.Binding);
                                break;
                            }
                        case ExprFieldRootKind.CurrentItem:
                            AddStructural(
                                RelationQueryStructuralCapabilityRole.CurrentItemRead,
                                field.Path,
                                input: null,
                                node,
                                site.Analysis.Site.Id.Value,
                                UsesForSite(site, EffectForSite(site.Kind)),
                                classifyCurrentItemPath: true,
                                additionalRequiredGuarantees:
                                [
                                    RelationQueryGuaranteeCapabilityKind.CollectionElementCorrelation
                                ]);
                            AddGuarantee(RelationQueryGuaranteeCapabilityKind.CollectionElementCorrelation);
                            break;
                        case ExprFieldRootKind.Unresolved:
                            throw new InvalidOperationException(
                                $"Demanded expression site '{site.Analysis.Site.Id.Value}' contains an unresolved field root.");
                        default:
                            throw new InvalidOperationException(
                                $"Demanded expression site '{site.Analysis.Site.Id.Value}' contains unsupported field root '{field.Root}'.");
                    }
                }
            }
        }

        void ProjectExpressionBindingAvailability()
        {
            foreach (var site in slice.ExpressionSites)
            {
                var node = ResolveSiteNode(site);
                var requiredBindings = site.Analysis.Requirements.Bindings
                    .Concat(site.Analysis.Requirements.Fields
                        .Select(static field => field.Binding)
                        .OfType<ValueBindingId>())
                    .Distinct()
                    .OrderBy(static binding => binding.Value, StringComparer.Ordinal);
                foreach (var binding in requiredBindings)
                {
                    if (!site.Analysis.Site.Scope.TryGetBinding(binding, out var scopedBinding))
                    {
                        throw new InvalidOperationException(
                            $"Demanded expression site '{site.Analysis.Site.Id.Value}' requires binding "
                            + $"'{binding.Value}', but the analyzed scope does not contain it.");
                    }

                    AddLogical(
                        node,
                        scopedBinding.Availability == ExprBindingAvailability.AlwaysPresent
                            ? RelationQueryLogicalCapabilityKind.AlwaysPresentBinding
                            : RelationQueryLogicalCapabilityKind.MayBeAbsentBinding,
                        new(
                            node: node,
                            semanticSite: site.Analysis.Site.Id.Value,
                            binding: binding),
                        UsesForSite(site, EffectForSite(site.Kind)),
                        qualifier: $"binding/{Encode(binding.Value)}/{Encode(site.Analysis.Site.Id.Value)}",
                        requiredGuarantees:
                        [
                            RelationQueryGuaranteeCapabilityKind.MissingNullDistinction,
                            RelationQueryGuaranteeCapabilityKind.AbsenceAvailabilityFailureDistinction
                        ]);
                }
            }
        }

        void ProjectTemporalCapabilities()
        {
            foreach (var temporal in inputContract.TemporalCapabilities)
            {
                var sites = SelectTemporalExpressionSites(temporal);
                var uses = UsesForTemporalCapability(temporal, sites);
                Add(new(
                    TemporalId(temporal),
                    new TemporalRelationQueryCapability(temporal.Capability),
                    new(
                        temporal.Id,
                        temporal.Node,
                        temporal.SemanticSite),
                    uses,
                    RequiredGuaranteesForTemporal(temporal),
                    ExpressionDepthFacts(sites)));
                AddTemporalGuarantees(temporal.Capability);
            }
        }

        void ProjectTerminals()
        {
            if (slice.RelationOutput is { } relation)
            {
                var terminalSite = $"relation/{Encode(relation.Relation.Value)}/output";
                var terminalUses = DirectTerminalUses(
                    relation.Outputs,
                    RelationQueryRequirementEffect.Cardinality);
                AddLogical(
                    relation.Definition.Node,
                    RelationOutputCapability(relation.Definition.Mode),
                    new(node: relation.Definition.Node, semanticSite: terminalSite),
                    terminalUses,
                    qualifier: $"relation-output/{(int)relation.Definition.Mode}",
                    requiredGuarantees:
                    [
                        RelationQueryGuaranteeCapabilityKind.OutputMode,
                        RelationQueryGuaranteeCapabilityKind.Cardinality
                    ]);
                AddGuarantee(RelationQueryGuaranteeCapabilityKind.OutputMode);

                if (relation.KeySite is { } keySite)
                {
                    AddLogical(
                        relation.Definition.Node,
                        RelationQueryLogicalCapabilityKind.RelationOutputIdentity,
                        new(
                            node: relation.Definition.Node,
                            semanticSite: keySite.Analysis.Site.Id.Value),
                        UsesForSite(keySite, RelationQueryRequirementEffect.Identity),
                        qualifier: "relation-output/identity",
                        requiredGuarantees:
                        [
                            RelationQueryGuaranteeCapabilityKind.OutputIdentity,
                            RelationQueryGuaranteeCapabilityKind.DeterministicResult
                        ]);
                    AddGuarantee(RelationQueryGuaranteeCapabilityKind.OutputIdentity);
                }

                foreach (var invariant in relation.Invariants)
                {
                    AddLogical(
                        relation.Definition.Node,
                        RelationQueryLogicalCapabilityKind.RelationInvariant,
                        new(
                            node: relation.Definition.Node,
                            semanticSite: invariant.PredicateSite.Analysis.Site.Id.Value),
                        UsesForSite(invariant.PredicateSite, RelationQueryRequirementEffect.Validation),
                        qualifier: $"invariant/{Encode(invariant.Definition.Name)}",
                        requiredGuarantees: [RelationQueryGuaranteeCapabilityKind.InvariantEnforcement]);
                    AddGuarantee(RelationQueryGuaranteeCapabilityKind.InvariantEnforcement);
                }

                ProjectTerminalSelections(relation.Outputs, relation.Definition.Node);
            }

            foreach (var result in slice.QueryResults)
            {
                AddLogical(
                    result.Definition.Input,
                    result.Definition is AggregationQueryResultDefinition
                        ? RelationQueryLogicalCapabilityKind.QueryAggregationResult
                        : RelationQueryLogicalCapabilityKind.QueryRowsResult,
                    new(
                        node: result.Definition.Input,
                        semanticSite: $"query-result/{Encode(result.Id.Value)}"),
                    DirectTerminalUses(result.Outputs, RelationQueryRequirementEffect.Value),
                    qualifier: $"query-result/{Encode(result.Id.Value)}");
                ProjectTerminalSelections(result.Outputs, result.Definition.Input);
            }
        }

        void ProjectTerminalSelections(
            ImmutableArray<RelationQueryOutputReference> outputs,
            QueryNodeId node)
        {
            foreach (var output in outputs)
            {
                var uses = DirectTerminalUses([output], RelationQueryRequirementEffect.Value);
                if (output.Field is { } field)
                {
                    AddStructural(
                        RelationQueryStructuralCapabilityRole.OutputSelection,
                        field.Path,
                        input: null,
                        node,
                        output.Id.Value,
                        uses,
                        qualifier: output.Id.Value);
                }
                else
                {
                    AddStructural(
                        RelationQueryStructuralCapabilityRole.CompleteValue,
                        path: null,
                        input: null,
                        node,
                        output.Id.Value,
                        uses,
                        qualifier: output.Id.Value);
                }
            }
        }

        void AddLogical(
            RelationQueryExecutionNode execution,
            RelationQueryLogicalCapabilityKind capability,
            RelationQueryRequirementEffect fallbackEffect,
            ImmutableArray<RelationQueryGuaranteeCapabilityKind> requiredGuarantees = default,
            ImmutableArray<RelationQueryRealizationStaticFact> staticFacts = default) =>
            AddLogical(
                execution.Id,
                capability,
                new(node: execution.Id, semanticSite: NodeSite(execution.Id, capability.ToString())),
                UsesForNode(execution.Id, fallbackEffect),
                requiredGuarantees: requiredGuarantees,
                staticFacts: MergeStaticFacts(
                    staticFacts,
                    ExpressionDepthFacts(execution.ExpressionSites)));

        void AddLogical(
            QueryNodeId node,
            RelationQueryLogicalCapabilityKind capability,
            RelationQueryRealizationRequirementOrigin origin,
            ImmutableArray<RelationQueryRealizationRequirementUse> uses,
            string? qualifier = null,
            ImmutableArray<RelationQueryGuaranteeCapabilityKind> requiredGuarantees = default,
            ImmutableArray<RelationQueryRealizationStaticFact> staticFacts = default) =>
            Add(new(
                LogicalId(node, capability, qualifier),
                new LogicalRelationQueryCapability(capability),
                origin,
                uses,
                requiredGuarantees,
                staticFacts));

        void AddStructural(
            RelationQueryStructuralCapabilityRole role,
            FieldPath? path,
            RelationQueryInputId? input,
            QueryNodeId node,
            string semanticSite,
            ImmutableArray<RelationQueryRealizationRequirementUse> uses,
            bool classifyCurrentItemPath = false,
            string? qualifier = null,
            ValueBindingId? binding = null,
            ImmutableArray<RelationQueryGuaranteeCapabilityKind> additionalRequiredGuarantees = default)
        {
            var pathKind = ClassifyPath(path, classifyCurrentItemPath);
            var additionalGuarantees = additionalRequiredGuarantees.IsDefault
                ? []
                : additionalRequiredGuarantees;
            Add(new(
                StructuralId(role, pathKind, input, binding, node, semanticSite, path, qualifier),
                new StructuralRelationQueryCapability(role, pathKind),
                new(input, node, semanticSite, fieldPath: path, binding: binding),
                uses,
                requiredGuarantees:
                [
                    RelationQueryGuaranteeCapabilityKind.MissingNullDistinction,
                    RelationQueryGuaranteeCapabilityKind.AbsenceAvailabilityFailureDistinction,
                    .. additionalGuarantees
                ],
                staticFacts:
                [
                    new(
                        RelationQueryRealizationStaticFactKind.FieldPathDepth,
                        FieldPathDepth(path, classifyCurrentItemPath))
                ]));
        }

        void Add(RelationQueryRealizationRequirement requirement)
        {
            requirement = AddInferredExpressionDepth(requirement);
            if (requirement.Capability is not GuaranteeRelationQueryCapability)
            {
                requirement = new(
                    requirement.Id,
                    requirement.Capability,
                    requirement.Origin,
                    requirement.Uses,
                    [.. RequiredBaselineGuarantees(), .. requirement.RequiredGuarantees],
                    requirement.StaticFacts);
            }
            if (requirements.TryGetValue(requirement.Id, out var existing))
            {
                if (!Equals(existing.Capability, requirement.Capability)
                    || !Equals(existing.Origin, requirement.Origin))
                {
                    throw new InvalidOperationException(
                        $"Realization requirement identity '{requirement.Id.Value}' has conflicting projections.");
                }
                requirements[requirement.Id] = new(
                    existing.Id,
                    existing.Capability,
                    existing.Origin,
                    NormalizeUses(existing.Uses.Concat(requirement.Uses).Select(static use => new UseProjection(
                        use.Output,
                        use.Effect,
                        use.Requirement,
                        use.Traces))),
                    [.. existing.RequiredGuarantees, .. requirement.RequiredGuarantees],
                    MergeStaticFacts(existing.StaticFacts, requirement.StaticFacts));
                return;
            }
            requirements.Add(requirement.Id, requirement);
        }

        RelationQueryRealizationRequirement AddInferredExpressionDepth(
            RelationQueryRealizationRequirement requirement)
        {
            if (requirement.Origin?.SemanticSite is not { } semanticSite
                || !expressionSitesById.TryGetValue(semanticSite, out var site))
            {
                return requirement;
            }

            return new(
                requirement.Id,
                requirement.Capability,
                requirement.Origin,
                requirement.Uses,
                requirement.RequiredGuarantees,
                MergeStaticFacts(requirement.StaticFacts, ExpressionDepthFacts([site])));
        }

        static ImmutableArray<RelationQueryRealizationStaticFact> ExpressionDepthFacts(
            IEnumerable<RelationQueryExpressionSiteAnalysis> sites)
        {
            var depths = sites.Select(static site => ExpressionDepth(site.Analysis.Site.Expression)).ToArray();
            return depths.Length == 0
                ? []
                :
                [
                    new(
                        RelationQueryRealizationStaticFactKind.ExpressionDepth,
                        depths.Max())
                ];
        }

        static long ExpressionDepth(Expr expression) => expression switch
        {
            UnaryExpr unary => 1 + ExpressionDepth(unary.Operand),
            BinaryExpr binary => 1 + Math.Max(
                ExpressionDepth(binary.Left),
                ExpressionDepth(binary.Right)),
            ConditionalExpr conditional => 1 + new[]
            {
                ExpressionDepth(conditional.Test),
                ExpressionDepth(conditional.IfTrue),
                ExpressionDepth(conditional.IfFalse)
            }.Max(),
            CallExpr call when !call.Arguments.IsDefaultOrEmpty =>
                1 + call.Arguments.Max(ExpressionDepth),
            AggregateExpr aggregate => 1 + new[]
            {
                ExpressionDepth(aggregate.Source),
                aggregate.GroupBy.IsDefaultOrEmpty ? 0 : aggregate.GroupBy.Max(ExpressionDepth)
            }.Max(),
            _ => 1
        };

        static long FieldPathDepth(FieldPath? path, bool excludeCurrentItemRoot)
        {
            if (path is null)
                return 0;
            var segments = path.Value.Segments;
            return excludeCurrentItemRoot
                   && segments[0] is { Kind: SegmentKind.Field, Segment: ExprFieldRoots.CurrentItem }
                ? segments.Length - 1
                : segments.Length;
        }

        static ImmutableArray<RelationQueryRealizationStaticFact> MergeStaticFacts(
            ImmutableArray<RelationQueryRealizationStaticFact> first,
            ImmutableArray<RelationQueryRealizationStaticFact> second)
        {
            var normalizedFirst = first.IsDefault ? [] : first;
            var normalizedSecond = second.IsDefault ? [] : second;
            var groups = normalizedFirst.Concat(normalizedSecond)
                .GroupBy(static fact => fact.Kind)
                .OrderBy(static group => (int)group.Key)
                .ToArray();
            foreach (var group in groups)
            {
                if (group.Select(static fact => fact.Value).Distinct().Skip(1).Any())
                {
                    throw new InvalidOperationException(
                        $"Realization requirement has conflicting '{group.Key}' static facts.");
                }
            }
            return [.. groups.Select(static group => group.First())];
        }

        void AddBaselineGuarantees()
        {
            foreach (var guarantee in RequiredBaselineGuarantees())
                AddGuarantee(guarantee);
        }

        ImmutableArray<RelationQueryGuaranteeCapabilityKind> RequiredBaselineGuarantees() =>
            RequiresOccurrenceProvenance()
                ? StrictBaselineGuarantees
                : SemanticBaselineGuarantees;

        bool RequiresOccurrenceProvenance() =>
            observability.OccurrenceProvenance == RelationQueryOccurrenceProvenanceMode.ExactContributors
            || slice.RelationOutput is { Definition.Mode: not RelationOutputMode.Set };

        void AddGuarantee(RelationQueryGuaranteeCapabilityKind guarantee) => guarantees.Add(guarantee);

        void AddTemporalGuarantees(RelationQueryTemporalExecutionCapability capability)
        {
            switch (capability)
            {
                case RelationQueryTemporalExecutionCapability.DateDomain:
                case RelationQueryTemporalExecutionCapability.DateTimeDomain:
                case RelationQueryTemporalExecutionCapability.InstantDomain:
                    AddGuarantee(RelationQueryGuaranteeCapabilityKind.TemporalDomain);
                    break;
                case RelationQueryTemporalExecutionCapability.InclusiveBoundary:
                case RelationQueryTemporalExecutionCapability.ExclusiveBoundary:
                    AddGuarantee(RelationQueryGuaranteeCapabilityKind.TemporalBoundary);
                    break;
                case RelationQueryTemporalExecutionCapability.UnboundedBoundary:
                case RelationQueryTemporalExecutionCapability.NullAsUnbounded:
                    AddGuarantee(RelationQueryGuaranteeCapabilityKind.UnboundedTemporalBoundary);
                    break;
                case RelationQueryTemporalExecutionCapability.PreserveAllMatches:
                    AddGuarantee(RelationQueryGuaranteeCapabilityKind.Cardinality);
                    break;
                case RelationQueryTemporalExecutionCapability.InnerJoin:
                case RelationQueryTemporalExecutionCapability.LeftOuterJoin:
                case RelationQueryTemporalExecutionCapability.RightOuterJoin:
                case RelationQueryTemporalExecutionCapability.FullOuterJoin:
                    AddGuarantee(RelationQueryGuaranteeCapabilityKind.JoinMembership);
                    AddGuarantee(RelationQueryGuaranteeCapabilityKind.Cardinality);
                    break;
                case RelationQueryTemporalExecutionCapability.InconclusiveEvidence:
                    AddGuarantee(RelationQueryGuaranteeCapabilityKind.InconclusiveEvidence);
                    break;
            }
        }

        RelationQueryFieldInputContract? ResolveFieldInput(
            RelationQueryExpressionSiteAnalysis site,
            ExprFieldRequirement field)
        {
            if (field.Binding is not { } binding)
                return null;
            var matches = fieldInputs.Where(candidate =>
                    candidate.Input.Binding == binding
                    && candidate.Input.Field.Path == field.Path
                    && candidate.Uses.Any(use => use.Traces.Any(trace => TraceContainsSite(
                        trace,
                        site.Analysis.Site.Id))))
                .ToArray();
            return matches.Length switch
            {
                0 => null,
                1 => matches[0],
                _ => throw new InvalidOperationException(
                    $"Expression site '{site.Analysis.Site.Id.Value}' maps field '{binding.Value}.{field.Path}' "
                    + "to more than one compiled field input.")
            };
        }

        QueryNodeId ResolveSiteNode(RelationQueryExpressionSiteAnalysis site)
        {
            if (site.Node is { } node)
                return node;
            if (expressionSiteNodes.TryGetValue(site.Analysis.Site.Id, out var tracedNode))
                return tracedNode;
            if (slice.RelationOutput is { } relation)
                return relation.Definition.Node;
            throw new InvalidOperationException(
                $"Expression site '{site.Analysis.Site.Id.Value}' has no logical-node attribution.");
        }

        ImmutableArray<RelationQueryRealizationRequirementUse> UsesForInput(RelationQueryInputId input) =>
            ConvertUses(inputUses.Where(candidate => candidate.Input == input).Select(static candidate => candidate.Use));

        ImmutableArray<RelationQueryRealizationRequirementUse> UsesForNode(
            QueryNodeId node,
            RelationQueryRequirementEffect fallbackEffect)
        {
            var exact = ConvertUses(
                inputUses.Select(static candidate => candidate.Use),
                trace => trace.Steps.Any(step => step.Node == node));
            return exact.IsDefaultOrEmpty
                ? SyntheticUses(node, fallbackEffect)
                : exact;
        }

        ImmutableArray<RelationQueryRealizationRequirementUse> UsesForSite(
            RelationQueryExpressionSiteAnalysis site,
            RelationQueryRequirementEffect fallbackEffect)
        {
            var exact = ConvertUses(
                inputUses.Select(static candidate => candidate.Use),
                trace => TraceContainsSite(trace, site.Analysis.Site.Id));
            return exact.IsDefaultOrEmpty
                ? SyntheticSiteUses(site, fallbackEffect)
                : exact;
        }

        ImmutableArray<RelationQueryRealizationRequirementUse> UsesForAggregateOperation(
            QueryNodeId node,
            QueryAssignmentId assignment,
            RelationQueryInputId? capabilityInput)
        {
            var exact = ConvertUses(
                inputUses
                    .Where(candidate => capabilityInput is null || candidate.Input == capabilityInput)
                    .Select(static candidate => candidate.Use),
                trace => trace.Steps.Any(step =>
                    step.Kind == RelationQueryRequirementTraceStepKind.AggregateOperation
                    && step.Node == node
                    && step.Assignment == assignment));
            return exact.IsDefaultOrEmpty
                ? SyntheticAggregateUses(node, assignment)
                : exact;
        }

        ImmutableArray<RelationQueryRealizationRequirementUse> UsesForTemporalCapability(
            RelationQueryTemporalCapabilityInputContract temporal,
            ImmutableArray<RelationQueryExpressionSiteAnalysis> sites)
        {
            var effect = EffectForTemporal(temporal.Capability);
            if (!sites.IsDefaultOrEmpty)
            {
                return NormalizeUses(sites.SelectMany(site =>
                    UsesForSite(site, effect).Select(use => new UseProjection(
                        use.Output,
                        use.Effect,
                        use.Requirement,
                        use.Traces))));
            }

            return UsesForNode(temporal.Node, effect);
        }

        ImmutableArray<RelationQueryGuaranteeCapabilityKind> RequiredGuaranteesForTemporal(
            RelationQueryTemporalCapabilityInputContract temporal)
        {
            return temporal.Capability switch
            {
                RelationQueryTemporalExecutionCapability.PointInInterval
                    or RelationQueryTemporalExecutionCapability.IntervalOverlap
                    or RelationQueryTemporalExecutionCapability.ValidateIntervals =>
                    TemporalOperationGuarantees(temporal.Node),
                RelationQueryTemporalExecutionCapability.DateDomain
                    or RelationQueryTemporalExecutionCapability.DateTimeDomain
                    or RelationQueryTemporalExecutionCapability.InstantDomain =>
                    [RelationQueryGuaranteeCapabilityKind.TemporalDomain],
                RelationQueryTemporalExecutionCapability.InclusiveBoundary
                    or RelationQueryTemporalExecutionCapability.ExclusiveBoundary =>
                    [RelationQueryGuaranteeCapabilityKind.TemporalBoundary],
                RelationQueryTemporalExecutionCapability.UnboundedBoundary =>
                    [RelationQueryGuaranteeCapabilityKind.UnboundedTemporalBoundary],
                RelationQueryTemporalExecutionCapability.NullAsUnbounded =>
                [
                    RelationQueryGuaranteeCapabilityKind.MissingNullDistinction,
                    RelationQueryGuaranteeCapabilityKind.UnboundedTemporalBoundary
                ],
                RelationQueryTemporalExecutionCapability.PreserveAllMatches =>
                    [RelationQueryGuaranteeCapabilityKind.Cardinality],
                RelationQueryTemporalExecutionCapability.InnerJoin
                    or RelationQueryTemporalExecutionCapability.LeftOuterJoin
                    or RelationQueryTemporalExecutionCapability.RightOuterJoin
                    or RelationQueryTemporalExecutionCapability.FullOuterJoin =>
                [
                    RelationQueryGuaranteeCapabilityKind.JoinMembership,
                    RelationQueryGuaranteeCapabilityKind.Cardinality
                ],
                RelationQueryTemporalExecutionCapability.InconclusiveEvidence =>
                [
                    RelationQueryGuaranteeCapabilityKind.EvidenceCompleteness,
                    RelationQueryGuaranteeCapabilityKind.InconclusiveEvidence
                ],
                _ => throw new InvalidOperationException(
                    $"Unsupported demanded temporal capability '{temporal.Capability}'.")
            };
        }

        ImmutableArray<RelationQueryGuaranteeCapabilityKind> TemporalOperationGuarantees(QueryNodeId node)
        {
            HashSet<RelationQueryGuaranteeCapabilityKind> required = [];
            foreach (var capability in inputContract.TemporalCapabilities
                         .Where(candidate => candidate.Node == node)
                         .Select(static candidate => candidate.Capability))
            {
                switch (capability)
                {
                    case RelationQueryTemporalExecutionCapability.DateDomain:
                    case RelationQueryTemporalExecutionCapability.DateTimeDomain:
                    case RelationQueryTemporalExecutionCapability.InstantDomain:
                        required.Add(RelationQueryGuaranteeCapabilityKind.TemporalDomain);
                        break;
                    case RelationQueryTemporalExecutionCapability.InclusiveBoundary:
                    case RelationQueryTemporalExecutionCapability.ExclusiveBoundary:
                        required.Add(RelationQueryGuaranteeCapabilityKind.TemporalBoundary);
                        break;
                    case RelationQueryTemporalExecutionCapability.UnboundedBoundary:
                    case RelationQueryTemporalExecutionCapability.NullAsUnbounded:
                        required.Add(RelationQueryGuaranteeCapabilityKind.UnboundedTemporalBoundary);
                        break;
                    case RelationQueryTemporalExecutionCapability.InconclusiveEvidence:
                        required.Add(RelationQueryGuaranteeCapabilityKind.EvidenceCompleteness);
                        required.Add(RelationQueryGuaranteeCapabilityKind.InconclusiveEvidence);
                        break;
                }
            }
            return [.. required.OrderBy(static guarantee => (int)guarantee)];
        }

        ImmutableArray<RelationQueryExpressionSiteAnalysis> SelectTemporalExpressionSites(
            RelationQueryTemporalCapabilityInputContract temporal)
        {
            var execution = slice.Nodes.Single(node => node.Id == temporal.Node);
            var temporalJoin = execution.TemporalJoin
                ?? throw new InvalidOperationException(
                    $"Temporal capability '{temporal.Id.Value}' does not reference a prepared temporal join.");
            var boundSites = temporalJoin.Intervals
                .SelectMany(static interval => new[] { interval.Lower.ValueSite, interval.Upper.ValueSite })
                .OfType<RelationQueryExpressionSiteAnalysis>()
                .ToImmutableArray();
            var operandSites = temporalJoin.PointSite is { } point
                ? ImmutableArray.Create(point).AddRange(boundSites)
                : boundSites;
            IEnumerable<RelationQueryExpressionSiteAnalysis> selected = temporal.Capability switch
            {
                RelationQueryTemporalExecutionCapability.PointInInterval
                    or RelationQueryTemporalExecutionCapability.IntervalOverlap
                    or RelationQueryTemporalExecutionCapability.PreserveAllMatches
                    or RelationQueryTemporalExecutionCapability.DateDomain
                    or RelationQueryTemporalExecutionCapability.DateTimeDomain
                    or RelationQueryTemporalExecutionCapability.InstantDomain => operandSites,
                RelationQueryTemporalExecutionCapability.ValidateIntervals => boundSites,
                RelationQueryTemporalExecutionCapability.InconclusiveEvidence =>
                    [temporalJoin.CorrelationSite, .. operandSites],
                RelationQueryTemporalExecutionCapability.InnerJoin
                    or RelationQueryTemporalExecutionCapability.LeftOuterJoin
                    or RelationQueryTemporalExecutionCapability.RightOuterJoin
                    or RelationQueryTemporalExecutionCapability.FullOuterJoin => [temporalJoin.CorrelationSite],
                RelationQueryTemporalExecutionCapability.InclusiveBoundary
                    or RelationQueryTemporalExecutionCapability.ExclusiveBoundary
                    or RelationQueryTemporalExecutionCapability.NullAsUnbounded =>
                    execution.ExpressionSites.Where(site => string.Equals(
                        site.Analysis.Site.Id.Value,
                        temporal.SemanticSite,
                        StringComparison.Ordinal)),
                RelationQueryTemporalExecutionCapability.UnboundedBoundary => [],
                _ => throw new InvalidOperationException(
                    $"Unsupported demanded temporal capability '{temporal.Capability}'.")
            };
            return
            [
                .. selected.DistinctBy(static site => site.Analysis.Site.Id)
                    .OrderBy(static site => site.Analysis.Site.Id.Value, StringComparer.Ordinal)
            ];
        }

        ImmutableArray<RelationQueryRealizationRequirementUse> ConvertUses(
            IEnumerable<RelationQueryRequirementUse> uses,
            Func<RelationQueryRequirementTrace, bool>? tracePredicate = null)
        {
            var candidates = uses.Select(use => new
            {
                Use = use,
                Traces = use.Traces
                        .Where(trace => tracePredicate is null || tracePredicate(trace))
                        .ToArray()
            })
                .Where(static candidate => candidate.Traces.Length != 0)
                .ToArray();
            return NormalizeUses(candidates.Select(candidate => new UseProjection(
                ToOutput(candidate.Use.Output),
                candidate.Use.Effect,
                candidate.Use.Requirement,
                [.. candidate.Traces.Select(trace => ConvertTrace(candidate.Use.Output, trace))])));
        }

        ImmutableArray<RelationQueryRealizationRequirementUse> SyntheticUses(
            QueryNodeId node,
            RelationQueryRequirementEffect effect) =>
            NormalizeUses(slice.Requirements.Outputs.Select(output =>
            {
                var path = FindPath(output.Node, node);
                return path is null
                    ? null
                    : new UseProjection(
                        ToOutput(output),
                        effect,
                        QueryInputRequirement.Required,
                        [CreateStructuralTrace(output, path.Value)]);
            }).OfType<UseProjection>());

        ImmutableArray<RelationQueryRealizationRequirementUse> SyntheticSiteUses(
            RelationQueryExpressionSiteAnalysis site,
            RelationQueryRequirementEffect effect)
        {
            var node = ResolveSiteNode(site);
            return NormalizeUses(slice.Requirements.Outputs.Select(output =>
            {
                var path = FindPath(output.Node, node);
                if (path is null)
                    return null;
                var trace = CreateStructuralTrace(output, path.Value);
                return new UseProjection(
                    ToOutput(output),
                    effect,
                    QueryInputRequirement.Required,
                    [Append(trace, ExpressionStep(node, site))]);
            }).OfType<UseProjection>());
        }

        ImmutableArray<RelationQueryRealizationRequirementUse> SyntheticAggregateUses(
            QueryNodeId node,
            QueryAssignmentId assignment) =>
            NormalizeUses(slice.Requirements.Outputs.Select(output =>
            {
                var path = FindPath(output.Node, node);
                if (path is null)
                    return null;
                var trace = CreateStructuralTrace(output, path.Value);
                return new UseProjection(
                    ToOutput(output),
                    RelationQueryRequirementEffect.Aggregation,
                    QueryInputRequirement.Required,
                    [Append(trace, new(
                        RelationQueryRealizationTraceStepKind.AggregateOperation,
                        node,
                        assignment: assignment))]);
            }).OfType<UseProjection>());

        ImmutableArray<RelationQueryRealizationRequirementUse> DirectTerminalUses(
            IEnumerable<RelationQueryOutputReference> outputs,
            RelationQueryRequirementEffect effect) =>
            NormalizeUses(outputs.Select(output => new UseProjection(
                ToOutput(output),
                effect,
                QueryInputRequirement.Required,
                [new([new(RelationQueryRealizationTraceStepKind.Terminal, output.Node)])])));

        static ImmutableArray<RelationQueryRealizationRequirementUse> NormalizeUses(
            IEnumerable<UseProjection> projections)
        {
            return
            [
                .. projections
                    .GroupBy(static projection => (projection.Output.Id, projection.Effect))
                    .Select(group =>
                    {
                        var output = group.First().Output;
                        if (group.Skip(1).Any(candidate => !Equals(candidate.Output, output)))
                        {
                            throw new InvalidOperationException(
                                $"Output '{output.Id.Value}' has conflicting realization references.");
                        }
                        var traces = group.SelectMany(static candidate => candidate.Traces)
                            .GroupBy(RelationQueryRealizationRequirementUse.TraceKey, StringComparer.Ordinal)
                            .Select(static traceGroup => traceGroup.First())
                            .ToImmutableArray();
                        return new RelationQueryRealizationRequirementUse(
                            output,
                            group.Key.Effect,
                            group.Any(static candidate => candidate.Requirement == QueryInputRequirement.Required)
                                ? QueryInputRequirement.Required
                                : QueryInputRequirement.Optional,
                            traces);
                    })
                    .OrderBy(static use => use.Output.Id.Value, StringComparer.Ordinal)
                    .ThenBy(static use => (int)use.Effect)
            ];
        }

        RelationQueryRealizationTrace ConvertTrace(
            RelationQueryOutputReference output,
            RelationQueryRequirementTrace trace)
        {
            List<RelationQueryRealizationTraceStep> steps =
            [
                new(RelationQueryRealizationTraceStepKind.Terminal, output.Node)
            ];
            steps.AddRange(trace.Steps.Select(static step =>
            {
                return step.Kind switch
                {
                    RelationQueryRequirementTraceStepKind.Structural =>
                        new RelationQueryRealizationTraceStep(
                            RelationQueryRealizationTraceStepKind.Structural,
                            step.Node),
                    RelationQueryRequirementTraceStepKind.ExpressionSite =>
                        new RelationQueryRealizationTraceStep(
                            RelationQueryRealizationTraceStepKind.ExpressionSite,
                            step.Node,
                            step.SiteKind,
                            step.ExpressionSite,
                            step.Assignment,
                            step.Ordinal,
                            step.InvariantName),
                    RelationQueryRequirementTraceStepKind.AggregateOperation =>
                        new RelationQueryRealizationTraceStep(
                            RelationQueryRealizationTraceStepKind.AggregateOperation,
                            step.Node,
                            assignment: step.Assignment),
                    _ => throw new InvalidOperationException(
                        $"Unsupported compiled requirement trace-step kind '{step.Kind}'.")
                };
            }));
            return new([.. steps]);
        }

        RelationQueryRealizationTrace CreateStructuralTrace(
            RelationQueryOutputReference output,
            ImmutableArray<QueryNodeId> path) =>
            new(
            [
                new(RelationQueryRealizationTraceStepKind.Terminal, output.Node),
                .. path.Select(static node => new RelationQueryRealizationTraceStep(
                    RelationQueryRealizationTraceStepKind.Structural,
                    node))
            ]);

        static RelationQueryRealizationTrace Append(
            RelationQueryRealizationTrace trace,
            RelationQueryRealizationTraceStep step) =>
            new([.. trace.Steps, step]);

        static RelationQueryRealizationTraceStep ExpressionStep(
            QueryNodeId node,
            RelationQueryExpressionSiteAnalysis site) =>
            new(
                RelationQueryRealizationTraceStepKind.ExpressionSite,
                node,
                site.Kind,
                site.Analysis.Site.Id,
                site.Assignment,
                site.Ordinal,
                site.InvariantName);

        ImmutableArray<QueryNodeId>? FindPath(QueryNodeId from, QueryNodeId target)
        {
            HashSet<QueryNodeId> active = [];
            return Find(from);

            ImmutableArray<QueryNodeId>? Find(QueryNodeId current)
            {
                if (!active.Add(current))
                    return null;
                try
                {
                    if (current == target)
                        return [current];
                    if (!logicalNodes.TryGetValue(current, out var logical))
                        return null;
                    foreach (var input in logical.EffectiveInputs.OrderBy(static value => value.Value, StringComparer.Ordinal))
                    {
                        var suffix = Find(input);
                        if (suffix is not null)
                            return [current, .. suffix.Value];
                    }
                    return null;
                }
                finally
                {
                    active.Remove(current);
                }
            }
        }

        static RelationQueryLogicalCapabilityKind JoinCapability(JoinKind kind) => kind switch
        {
            JoinKind.Inner => RelationQueryLogicalCapabilityKind.InnerJoin,
            JoinKind.Left => RelationQueryLogicalCapabilityKind.LeftOuterJoin,
            JoinKind.Right => RelationQueryLogicalCapabilityKind.RightOuterJoin,
            JoinKind.Full => RelationQueryLogicalCapabilityKind.FullOuterJoin,
            _ => throw new InvalidOperationException($"Unsupported demanded join kind '{kind}'.")
        };

        static RelationQueryLogicalCapabilityKind AggregateCapability(AggregateOperator operation) => operation switch
        {
            AggregateOperator.Count => RelationQueryLogicalCapabilityKind.CountAggregate,
            AggregateOperator.Sum => RelationQueryLogicalCapabilityKind.SumAggregate,
            AggregateOperator.Min => RelationQueryLogicalCapabilityKind.MinimumAggregate,
            AggregateOperator.Max => RelationQueryLogicalCapabilityKind.MaximumAggregate,
            AggregateOperator.Any => RelationQueryLogicalCapabilityKind.AnyAggregate,
            AggregateOperator.All => RelationQueryLogicalCapabilityKind.AllAggregate,
            _ => throw new InvalidOperationException($"Unsupported demanded aggregate operation '{operation}'.")
        };

        static RelationQueryLogicalCapabilityKind RelationOutputCapability(RelationOutputMode mode) => mode switch
        {
            RelationOutputMode.OnePerRoot => RelationQueryLogicalCapabilityKind.OnePerRootRelationOutput,
            RelationOutputMode.ZeroOrOnePerRoot => RelationQueryLogicalCapabilityKind.ZeroOrOnePerRootRelationOutput,
            RelationOutputMode.ManyPerRoot => RelationQueryLogicalCapabilityKind.ManyPerRootRelationOutput,
            RelationOutputMode.Set => RelationQueryLogicalCapabilityKind.SetRelationOutput,
            _ => throw new InvalidOperationException($"Unsupported demanded relation-output mode '{mode}'.")
        };

        static RelationQueryRequirementEffect EffectForSite(RelationQueryExpressionSiteKind kind) => kind switch
        {
            RelationQueryExpressionSiteKind.FilterPredicate => RelationQueryRequirementEffect.Membership,
            RelationQueryExpressionSiteKind.JoinPredicate => RelationQueryRequirementEffect.Correlation,
            RelationQueryExpressionSiteKind.TemporalJoinCorrelation => RelationQueryRequirementEffect.Correlation,
            RelationQueryExpressionSiteKind.TemporalJoinPoint => RelationQueryRequirementEffect.Correlation,
            RelationQueryExpressionSiteKind.TemporalJoinIntervalLowerBound => RelationQueryRequirementEffect.Correlation,
            RelationQueryExpressionSiteKind.TemporalJoinIntervalUpperBound => RelationQueryRequirementEffect.Correlation,
            RelationQueryExpressionSiteKind.ExpandCollection => RelationQueryRequirementEffect.Cardinality,
            RelationQueryExpressionSiteKind.ProjectionAssignmentValue => RelationQueryRequirementEffect.Value,
            RelationQueryExpressionSiteKind.DistinctKey => RelationQueryRequirementEffect.Cardinality,
            RelationQueryExpressionSiteKind.AggregateGroupingKey => RelationQueryRequirementEffect.Grouping,
            RelationQueryExpressionSiteKind.AggregateAssignmentValue => RelationQueryRequirementEffect.Aggregation,
            RelationQueryExpressionSiteKind.AggregateAssignmentFilter => RelationQueryRequirementEffect.Membership,
            RelationQueryExpressionSiteKind.OrderKey => RelationQueryRequirementEffect.Ordering,
            RelationQueryExpressionSiteKind.KeysetBoundary => RelationQueryRequirementEffect.Pagination,
            RelationQueryExpressionSiteKind.RelationOutputKey => RelationQueryRequirementEffect.Identity,
            RelationQueryExpressionSiteKind.RelationInvariant => RelationQueryRequirementEffect.Validation,
            _ => throw new InvalidOperationException($"Unsupported demanded expression-site kind '{kind}'.")
        };

        static RelationQueryRequirementEffect EffectForTemporal(
            RelationQueryTemporalExecutionCapability capability) => capability switch
            {
                RelationQueryTemporalExecutionCapability.PreserveAllMatches => RelationQueryRequirementEffect.Cardinality,
                RelationQueryTemporalExecutionCapability.ValidateIntervals => RelationQueryRequirementEffect.Validation,
                RelationQueryTemporalExecutionCapability.InnerJoin
                    or RelationQueryTemporalExecutionCapability.LeftOuterJoin
                    or RelationQueryTemporalExecutionCapability.RightOuterJoin
                    or RelationQueryTemporalExecutionCapability.FullOuterJoin
                    or RelationQueryTemporalExecutionCapability.InconclusiveEvidence => RelationQueryRequirementEffect.Membership,
                _ => RelationQueryRequirementEffect.Correlation
            };

        static RelationQueryStructuralPathKind ClassifyPath(
            FieldPath? path,
            bool classifyCurrentItemPath)
        {
            if (path is null)
                return RelationQueryStructuralPathKind.RootValue;
            var segments = path.Value.Segments;
            if (classifyCurrentItemPath
                && !segments.IsDefaultOrEmpty
                && segments[0] is { Kind: SegmentKind.Field, Segment: ExprFieldRoots.CurrentItem })
            {
                segments = [.. segments.Skip(1)];
                var hasNestedCollection = segments.Any(static segment => segment.Kind == SegmentKind.Element);
                var relativeFieldCount = segments.Count(static segment => segment.Kind == SegmentKind.Field);
                return hasNestedCollection || relativeFieldCount > 1
                    ? RelationQueryStructuralPathKind.NestedCollectionElement
                    : RelationQueryStructuralPathKind.CollectionElement;
            }
            if (segments.IsDefaultOrEmpty)
                return RelationQueryStructuralPathKind.RootValue;

            var containsElement = segments.Any(static segment => segment.Kind == SegmentKind.Element);
            var fieldCount = segments.Count(static segment => segment.Kind == SegmentKind.Field);
            if (containsElement)
            {
                return fieldCount > 1
                    ? RelationQueryStructuralPathKind.NestedCollectionElement
                    : RelationQueryStructuralPathKind.CollectionElement;
            }
            return fieldCount <= 1
                ? RelationQueryStructuralPathKind.TopLevelField
                : RelationQueryStructuralPathKind.NestedField;
        }

        static bool IsScopedCollectionExpressionUse(
            RelationQueryExpressionSiteAnalysis site,
            ExprCapabilityUse use)
        {
            if (use.Requirement.Kind != ExprCapabilityRequirementKind.Operation)
            {
                return false;
            }

            if (FindExpression(site.Analysis.Site.Expression, use.ExpressionPath) is not CallExpr call
                || use.Requirement.Capability != ExprCapabilities.ForFunction(call.Function)
                || !ExprSemanticsCatalog.Default.TryGetFunction(call.Function, out var definition))
            {
                return false;
            }

            return definition.ScopedArguments.Any(scoped =>
                scoped.ArgumentIndex < call.Arguments.Length);
        }

        static Expr? FindExpression(Expr root, string expressionPath)
        {
            if (string.Equals(expressionPath, "/", StringComparison.Ordinal))
                return root;
            if (string.IsNullOrWhiteSpace(expressionPath)
                || expressionPath[0] != '/')
            {
                return null;
            }

            var segments = expressionPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            Expr current = root;
            for (var index = 0; index < segments.Length;)
            {
                switch (current)
                {
                    case UnaryExpr unary when segments[index] == "operand":
                        current = unary.Operand;
                        index++;
                        break;
                    case BinaryExpr binary when segments[index] == "left":
                        current = binary.Left;
                        index++;
                        break;
                    case BinaryExpr binary when segments[index] == "right":
                        current = binary.Right;
                        index++;
                        break;
                    case ConditionalExpr conditional when segments[index] == "test":
                        current = conditional.Test;
                        index++;
                        break;
                    case ConditionalExpr conditional when segments[index] == "ifTrue":
                        current = conditional.IfTrue;
                        index++;
                        break;
                    case ConditionalExpr conditional when segments[index] == "ifFalse":
                        current = conditional.IfFalse;
                        index++;
                        break;
                    case CallExpr call
                        when segments[index] == "arguments"
                             && TryReadIndex(segments, index + 1, call.Arguments.Length, out var argumentIndex):
                        current = call.Arguments[argumentIndex];
                        index += 2;
                        break;
                    case AggregateExpr aggregate when segments[index] == "source":
                        current = aggregate.Source;
                        index++;
                        break;
                    case AggregateExpr aggregate
                        when segments[index] == "groupBy"
                             && TryReadIndex(segments, index + 1, aggregate.GroupBy.Length, out var groupIndex):
                        current = aggregate.GroupBy[groupIndex];
                        index += 2;
                        break;
                    default:
                        return null;
                }
            }

            return current;
        }

        static bool TryReadIndex(
            IReadOnlyList<string> segments,
            int segmentIndex,
            int count,
            out int value)
        {
            value = -1;
            return segmentIndex < segments.Count
                && int.TryParse(
                    segments[segmentIndex],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out value)
                && value >= 0
                && value < count;
        }

        static ImmutableArray<InputUse> CreateInputUses(RelationQueryInputContract contract)
        {
            List<InputUse> uses = [];
            foreach (var source in contract.Sources)
            {
                Add(source.Input.Id, source.Uses);
                foreach (var field in source.Fields)
                    Add(field.Input.Id, field.Uses);
            }
            foreach (var traversal in contract.Traversals)
            {
                Add(traversal.Input.Id, traversal.Uses);
                foreach (var field in traversal.Fields)
                    Add(field.Input.Id, field.Uses);
            }
            foreach (var identity in contract.Identities)
                Add(identity.Input.Id, identity.Uses);
            foreach (var parameter in contract.Parameters)
                Add(parameter.Input.Id, parameter.Uses);
            foreach (var capability in contract.Capabilities)
                Add(capability.Input.Id, capability.Uses);
            return
            [
                .. uses.OrderBy(static use => use.Input.Value, StringComparer.Ordinal)
                    .ThenBy(static use => use.Use.Output.Id.Value, StringComparer.Ordinal)
                    .ThenBy(static use => (int)use.Use.Effect)
            ];

            void Add(RelationQueryInputId input, ImmutableArray<RelationQueryRequirementUse> inputUses)
            {
                uses.AddRange(inputUses.Select(use => new InputUse(input, use)));
            }
        }

        static IReadOnlyDictionary<ExprSiteId, QueryNodeId> CreateExpressionSiteNodes(
            RelationQueryExecutionSlice slice,
            ImmutableArray<InputUse> uses)
        {
            Dictionary<ExprSiteId, QueryNodeId> result = [];
            foreach (var site in slice.ExpressionSites)
            {
                if (site.Node is { } node)
                {
                    result.Add(site.Analysis.Site.Id, node);
                    continue;
                }
                var nodes = uses.SelectMany(static use => use.Use.Traces)
                    .SelectMany(static trace => trace.Steps)
                    .Where(step => step.ExpressionSite == site.Analysis.Site.Id)
                    .Select(static step => step.Node)
                    .Distinct()
                    .ToArray();
                if (nodes.Length == 1)
                    result.Add(site.Analysis.Site.Id, nodes[0]);
            }
            return result;
        }

        static bool TraceContainsSite(RelationQueryRequirementTrace trace, ExprSiteId site) =>
            trace.Steps.Any(step => step.ExpressionSite == site);

        static RelationQueryRealizationOutputReference ToOutput(RelationQueryOutputReference output) =>
            new(
                output.Id,
                output.Kind,
                output.Node,
                output.Shape,
                output.Relation,
                output.QueryResult,
                output.Field);

        static RelationQueryRealizationRequirementId LogicalId(
            QueryNodeId node,
            RelationQueryLogicalCapabilityKind capability,
            string? qualifier = null) =>
            new(
                $"requirement/logical/{Encode(node.Value)}/{((int)capability).ToString(CultureInfo.InvariantCulture)}"
                + (qualifier is null ? string.Empty : $"/{qualifier}"));

        static RelationQueryRealizationRequirementId ExpressionId(
            RelationQueryExpressionSiteAnalysis site,
            ExprCapabilityUse use) =>
            new(
                $"requirement/expression/{Encode(site.Analysis.Site.Id.Value)}"
                + $"/{Encode(use.ExpressionPath)}"
                + $"/{((int)use.Requirement.Kind).ToString(CultureInfo.InvariantCulture)}"
                + $"/{Encode(use.Requirement.Capability.Value)}");

        static RelationQueryRealizationRequirementId TemporalId(
            RelationQueryTemporalCapabilityInputContract temporal) =>
            new($"requirement/temporal/{Encode(temporal.Id.Value)}");

        static RelationQueryRealizationRequirementId StructuralId(
            RelationQueryStructuralCapabilityRole role,
            RelationQueryStructuralPathKind pathKind,
            RelationQueryInputId? input,
            ValueBindingId? binding,
            QueryNodeId node,
            string semanticSite,
            FieldPath? path,
            string? qualifier) =>
            new(
                $"requirement/structural/{((int)role).ToString(CultureInfo.InvariantCulture)}"
                + $"/{((int)pathKind).ToString(CultureInfo.InvariantCulture)}"
                + $"/{Encode(input?.Value ?? "-")}"
                + $"/{Encode(binding?.Value ?? "-")}"
                + $"/{Encode(node.Value)}"
                + $"/{Encode(semanticSite)}"
                + $"/{EncodePath(path)}"
                + (qualifier is null ? string.Empty : $"/{Encode(qualifier)}"));

        static RelationQueryRealizationRequirementId GuaranteeId(
            RelationQueryGuaranteeCapabilityKind guarantee) =>
            new($"requirement/guarantee/{((int)guarantee).ToString(CultureInfo.InvariantCulture)}");

        static string AggregateOperationSite(QueryNodeId node, QueryAssignmentId assignment) =>
            $"{node.Value}/aggregate/{assignment.Value}/operation";

        static string NodeSite(QueryNodeId node, string operation) =>
            $"node/{Encode(node.Value)}/{Encode(operation)}";

        static string Encode(string value) => Uri.EscapeDataString(value);

        static string EncodePath(FieldPath? path)
        {
            if (path is null)
                return "root";
            return string.Join(
                "/",
                path.Value.Segments.Select(static segment =>
                    $"{((int)segment.Kind).ToString(CultureInfo.InvariantCulture)}:{Encode(segment.Segment ?? string.Empty)}"));
        }

        readonly record struct InputUse(
            RelationQueryInputId Input,
            RelationQueryRequirementUse Use);

        sealed record UseProjection(
            RelationQueryRealizationOutputReference Output,
            RelationQueryRequirementEffect Effect,
            QueryInputRequirement Requirement,
            ImmutableArray<RelationQueryRealizationTrace> Traces);
    }
}
