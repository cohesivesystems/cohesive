using System.Collections.Immutable;
using Cohesive.Model.Serialization;
using Cohesive.Relations.IR;
using Cohesive.Relations.Model;
using IRQueryDefinition = Cohesive.Relations.IR.QueryDefinition;
using IRRelationDefinition = Cohesive.Relations.IR.RelationDefinition;

namespace Cohesive.Relations.Authoring;

/// <summary>
/// Reusable programmatic construction substrate that lowers structural authoring operations into
/// the canonical relation/query IR.
/// </summary>
/// <remarks>
/// This type owns deterministic identities, typed-handle scope, canonical node construction, and
/// non-semantic authoring attribution. Higher-level built-in frontends, including C# expression
/// authoring, should lower through this core instead of constructing logical nodes or node tables
/// independently. Canonical validators remain authoritative for semantic correctness.
/// A core represents one mutable construction session and is not thread-safe. Each terminal result
/// snapshots the current canonical body and provenance; later authoring operations do not mutate a
/// previously returned result. Identity ordinals advance only after a declaration commits, so a
/// caller may diagnose a rejected declaration and retry it without perturbing later durable identities.
/// </remarks>
public sealed class RelationQueryAuthoringCore
{
    readonly List<LogicalQueryNode> nodes = [];
    readonly List<QueryParameterDefinition> parameters = [];
    readonly Dictionary<QueryNodeId, LogicalQueryNode> nodesById = [];
    readonly HashSet<ValueBindingId> bindingIds = [];
    readonly HashSet<QueryParameterId> parameterIds = [];
    readonly HashSet<QueryAssignmentId> assignmentIds = [];
    readonly HashSet<QueryResultId> resultIds = [];
    readonly Dictionary<string, int> nodeOrdinals = new(StringComparer.Ordinal);
    readonly Dictionary<string, int> resultOrdinals = new(StringComparer.Ordinal);
    readonly List<RelationQueryAuthoringIdentityDecision> identities = [];
    readonly List<RelationQueryAuthoringSourceDecision> sources = [];
    int parameterOrdinal;

    /// <summary>Creates an empty mutable structural authoring session.</summary>
    public RelationQueryAuthoringCore()
    {
    }

    /// <summary>Declares one invocation parameter in the canonical logical body.</summary>
    /// <param name="type">Portable semantic parameter type.</param>
    /// <param name="presence">Whether an invocation must provide the parameter.</param>
    /// <param name="defaultValue">Optional persisted fallback for an omitted optional parameter.</param>
    /// <param name="id">Optional explicit parameter identity.</param>
    /// <param name="source">Optional producer-source attribution.</param>
    /// <returns>A typed handle that can produce a canonical parameter expression.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="type"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="id"/> is default or repeated, or the requested default contradicts
    /// <paramref name="presence"/>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="presence"/> is unsupported.</exception>
    public RelationQueryParameterHandle Parameter(
        TypeRef type,
        FieldPresence presence = FieldPresence.Required,
        ObservationValue? defaultValue = null,
        QueryParameterId? id = null,
        RelationQueryAuthoringSource? source = null)
    {
        var selected = SelectParameterId(id, source);
        var definition = new QueryParameterDefinition(selected.Id, type, presence, defaultValue);
        EnsureAvailable(parameterIds, selected.Id, nameof(id), "query parameter");

        CommitParameterOrdinal(selected);
        parameterIds.Add(selected.Id);
        parameters.Add(definition);
        identities.Add(selected.Decision);
        Trace(RelationQueryAuthoringDecisionKind.Parameter, selected.Id.Value, source);
        return new(this, selected.Id);
    }

    /// <summary>Declares a semantic-shaped source without choosing its physical placement.</summary>
    /// <param name="shape">Graph-qualified semantic source shape.</param>
    /// <param name="nodeId">Optional explicit logical-node identity.</param>
    /// <param name="bindingId">Optional explicit source-binding identity.</param>
    /// <param name="source">Optional producer-source attribution.</param>
    /// <param name="bindingSource">Optional producer-source attribution specific to the introduced binding.</param>
    /// <returns>Typed handles for the source node and introduced binding.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="nodeId"/> or <paramref name="bindingId"/> is default or repeated.
    /// </exception>
    public RelationQueryBoundNodeHandle<SourceQueryNode> Source(
        QualifiedShapeId shape,
        QueryNodeId? nodeId = null,
        ValueBindingId? bindingId = null,
        RelationQueryAuthoringSource? source = null,
        RelationQueryAuthoringSource? bindingSource = null)
    {
        var selectedNode = SelectNodeId(RelationQueryWireNames.SourceNode, nodeId, source);
        var selectedBinding = SelectBindingId(selectedNode.Id, "source", bindingId, bindingSource ?? source);
        var node = new SourceQueryNode(selectedNode.Id, selectedBinding.Id, shape);
        AddNode(node, selectedNode, source, selectedBinding, bindingSource ?? source);
        return Bound(node, selectedBinding.Id);
    }

    /// <summary>Filters one input rowset using a canonical predicate expression.</summary>
    /// <typeparam name="TInput">Canonical type of the input node.</typeparam>
    /// <param name="input">Typed input-node handle.</param>
    /// <param name="predicate">Predicate evaluated for every input row.</param>
    /// <param name="nodeId">Optional explicit logical-node identity.</param>
    /// <param name="source">Optional producer-source attribution for the node and predicate.</param>
    /// <param name="predicateSource">Optional producer-source attribution specific to the predicate expression.</param>
    /// <returns>A typed filter-node handle.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="predicate"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="input"/> belongs to another core, or <paramref name="nodeId"/> is default or repeated.
    /// </exception>
    public RelationQueryNodeHandle<FilterQueryNode> Filter<TInput>(
        RelationQueryNodeHandle<TInput> input,
        Expr predicate,
        QueryNodeId? nodeId = null,
        RelationQueryAuthoringSource? source = null,
        RelationQueryAuthoringSource? predicateSource = null)
        where TInput : LogicalQueryNode
    {
        RequireNode(input, nameof(input));
        var selected = SelectNodeId(RelationQueryWireNames.FilterNode, nodeId, source);
        var node = new FilterQueryNode(selected.Id, input.Id, predicate);
        AddNode(node, selected, source);
        Trace(RelationQueryAuthoringDecisionKind.Expression, node.Id.Value, predicateSource ?? source, "predicate");
        return Handle(node);
    }

    /// <summary>Traverses a declared semantic relationship from one visible binding.</summary>
    /// <typeparam name="TInput">Canonical type of the input node.</typeparam>
    /// <param name="input">Typed input-node handle.</param>
    /// <param name="from">Visible binding from which traversal starts.</param>
    /// <param name="relationship">Declared semantic relationship identity.</param>
    /// <param name="direction">Traversal direction.</param>
    /// <param name="joinKind">Behavior when related values are absent.</param>
    /// <param name="requirement">Whether resolution of the related value is required.</param>
    /// <param name="nodeId">Optional explicit logical-node identity.</param>
    /// <param name="resultBindingId">Optional explicit related-value binding identity.</param>
    /// <param name="source">Optional producer-source attribution.</param>
    /// <param name="bindingSource">Optional producer-source attribution specific to the introduced binding.</param>
    /// <returns>Typed handles for the traversal node and related-value binding.</returns>
    /// <exception cref="ArgumentException">
    /// An input handle belongs to another core, or an explicit identity is default or repeated.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="direction"/>, <paramref name="joinKind"/>, or <paramref name="requirement"/> is unsupported.
    /// </exception>
    public RelationQueryBoundNodeHandle<TraverseRelationshipQueryNode> Traverse<TInput>(
        RelationQueryNodeHandle<TInput> input,
        RelationQueryBindingHandle from,
        RelationshipId relationship,
        RelationshipTraversalDirection direction = RelationshipTraversalDirection.Forward,
        JoinKind joinKind = JoinKind.Left,
        QueryInputRequirement requirement = QueryInputRequirement.Required,
        QueryNodeId? nodeId = null,
        ValueBindingId? resultBindingId = null,
        RelationQueryAuthoringSource? source = null,
        RelationQueryAuthoringSource? bindingSource = null)
        where TInput : LogicalQueryNode
    {
        RequireNode(input, nameof(input));
        RequireBinding(from, nameof(from));
        var selectedNode = SelectNodeId(RelationQueryWireNames.TraverseRelationshipNode, nodeId, source);
        var selectedBinding = SelectBindingId(selectedNode.Id, "result", resultBindingId, bindingSource ?? source);
        var node = new TraverseRelationshipQueryNode(
            selectedNode.Id,
            input.Id,
            from.Id,
            relationship,
            direction,
            selectedBinding.Id,
            joinKind,
            requirement);
        AddNode(node, selectedNode, source, selectedBinding, bindingSource ?? source);
        return Bound(node, selectedBinding.Id);
    }

    /// <summary>Joins independently produced rowsets using an explicit canonical predicate.</summary>
    /// <typeparam name="TLeft">Canonical type of the left input node.</typeparam>
    /// <typeparam name="TRight">Canonical type of the right input node.</typeparam>
    /// <param name="left">Typed left input-node handle.</param>
    /// <param name="right">Typed right input-node handle.</param>
    /// <param name="kind">Join semantics.</param>
    /// <param name="predicate">Predicate correlating both binding environments.</param>
    /// <param name="nodeId">Optional explicit logical-node identity.</param>
    /// <param name="source">Optional producer-source attribution for the node and predicate.</param>
    /// <param name="predicateSource">Optional producer-source attribution specific to the predicate expression.</param>
    /// <returns>A typed explicit-join node handle.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="predicate"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// An input handle belongs to another core, or <paramref name="nodeId"/> is default or repeated.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is unsupported.</exception>
    public RelationQueryNodeHandle<JoinQueryNode> Join<TLeft, TRight>(
        RelationQueryNodeHandle<TLeft> left,
        RelationQueryNodeHandle<TRight> right,
        JoinKind kind,
        Expr predicate,
        QueryNodeId? nodeId = null,
        RelationQueryAuthoringSource? source = null,
        RelationQueryAuthoringSource? predicateSource = null)
        where TLeft : LogicalQueryNode
        where TRight : LogicalQueryNode
    {
        RequireNode(left, nameof(left));
        RequireNode(right, nameof(right));
        var selected = SelectNodeId(RelationQueryWireNames.JoinNode, nodeId, source);
        var node = new JoinQueryNode(selected.Id, left.Id, right.Id, kind, predicate);
        AddNode(node, selected, source);
        Trace(RelationQueryAuthoringDecisionKind.Expression, node.Id.Value, predicateSource ?? source, "predicate");
        return Handle(node);
    }

    /// <summary>Joins two rowsets using correlation and explicit valid-time membership semantics.</summary>
    /// <typeparam name="TLeft">Canonical type of the left input node.</typeparam>
    /// <typeparam name="TRight">Canonical type of the right input node.</typeparam>
    /// <param name="left">Typed left input-node handle.</param>
    /// <param name="right">Typed right input-node handle.</param>
    /// <param name="kind">Join null-extension semantics.</param>
    /// <param name="correlation">Predicate correlating both binding environments.</param>
    /// <param name="match">Explicit temporal-membership condition.</param>
    /// <param name="nodeId">Optional explicit logical-node identity.</param>
    /// <param name="source">Optional producer-source attribution for the node and temporal expressions.</param>
    /// <param name="correlationSource">Optional producer-source attribution specific to the correlation expression.</param>
    /// <param name="matchSource">Optional producer-source attribution specific to the temporal-match expressions.</param>
    /// <returns>A typed temporal-join node handle.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="correlation"/> or <paramref name="match"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// An input handle belongs to another core, or <paramref name="nodeId"/> is default or repeated.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is unsupported.</exception>
    public RelationQueryNodeHandle<TemporalJoinQueryNode> TemporalJoin<TLeft, TRight>(
        RelationQueryNodeHandle<TLeft> left,
        RelationQueryNodeHandle<TRight> right,
        JoinKind kind,
        Expr correlation,
        TemporalJoinMatch match,
        QueryNodeId? nodeId = null,
        RelationQueryAuthoringSource? source = null,
        RelationQueryAuthoringSource? correlationSource = null,
        RelationQueryAuthoringSource? matchSource = null)
        where TLeft : LogicalQueryNode
        where TRight : LogicalQueryNode
    {
        RequireNode(left, nameof(left));
        RequireNode(right, nameof(right));
        var selected = SelectNodeId(RelationQueryWireNames.TemporalJoinNode, nodeId, source);
        var node = new TemporalJoinQueryNode(selected.Id, left.Id, right.Id, kind, correlation, match);
        AddNode(node, selected, source);
        Trace(RelationQueryAuthoringDecisionKind.Expression, node.Id.Value, correlationSource ?? source, "correlation");
        Trace(RelationQueryAuthoringDecisionKind.Expression, node.Id.Value, matchSource ?? source, "match");
        return Handle(node);
    }

    /// <summary>Expands a collection expression into one row per item.</summary>
    /// <typeparam name="TInput">Canonical type of the input node.</typeparam>
    /// <param name="input">Typed input-node handle.</param>
    /// <param name="collection">Canonical collection expression.</param>
    /// <param name="itemType">Portable semantic type of each collection item.</param>
    /// <param name="nodeId">Optional explicit logical-node identity.</param>
    /// <param name="itemBindingId">Optional explicit item-binding identity.</param>
    /// <param name="source">Optional producer-source attribution for the node and collection expression.</param>
    /// <param name="bindingSource">Optional producer-source attribution specific to the introduced item binding.</param>
    /// <param name="collectionSource">Optional producer-source attribution specific to the collection expression.</param>
    /// <returns>Typed handles for the expansion node and item binding.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="collection"/> or <paramref name="itemType"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="input"/> belongs to another core, or an explicit identity is default or repeated.
    /// </exception>
    public RelationQueryBoundNodeHandle<ExpandCollectionQueryNode> Expand<TInput>(
        RelationQueryNodeHandle<TInput> input,
        Expr collection,
        TypeRef itemType,
        QueryNodeId? nodeId = null,
        ValueBindingId? itemBindingId = null,
        RelationQueryAuthoringSource? source = null,
        RelationQueryAuthoringSource? bindingSource = null,
        RelationQueryAuthoringSource? collectionSource = null)
        where TInput : LogicalQueryNode
    {
        RequireNode(input, nameof(input));
        var selectedNode = SelectNodeId(RelationQueryWireNames.ExpandCollectionNode, nodeId, source);
        var selectedBinding = SelectBindingId(selectedNode.Id, "item", itemBindingId, bindingSource ?? source);
        var node = new ExpandCollectionQueryNode(
            selectedNode.Id,
            input.Id,
            collection,
            selectedBinding.Id,
            itemType);
        AddNode(node, selectedNode, source, selectedBinding, bindingSource ?? source);
        Trace(RelationQueryAuthoringDecisionKind.Expression, node.Id.Value, collectionSource ?? source, "collection");
        return Bound(node, selectedBinding.Id);
    }

    /// <summary>Projects an input binding environment into a semantic output shape.</summary>
    /// <typeparam name="TInput">Canonical type of the input node.</typeparam>
    /// <param name="input">Typed input-node handle.</param>
    /// <param name="resultShape">Graph-qualified semantic output shape.</param>
    /// <param name="assignments">Structural field-assignment inputs.</param>
    /// <param name="nodeId">Optional explicit logical-node identity.</param>
    /// <param name="resultBindingId">Optional explicit projected-value binding identity.</param>
    /// <param name="source">Optional producer-source attribution for the projection node.</param>
    /// <param name="bindingSource">Optional producer-source attribution specific to the projected-value binding.</param>
    /// <returns>Typed handles for the projection node and projected-value binding.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="input"/> belongs to another core; <paramref name="assignments"/> is default,
    /// empty, or contains a <see langword="null"/> entry; or an explicit identity is default or repeated.
    /// </exception>
    public RelationQueryBoundNodeHandle<ProjectQueryNode> Project<TInput>(
        RelationQueryNodeHandle<TInput> input,
        QualifiedShapeId resultShape,
        ImmutableArray<RelationQueryProjectionAssignment> assignments,
        QueryNodeId? nodeId = null,
        ValueBindingId? resultBindingId = null,
        RelationQueryAuthoringSource? source = null,
        RelationQueryAuthoringSource? bindingSource = null)
        where TInput : LogicalQueryNode
    {
        RequireNode(input, nameof(input));
        RequireEntries(assignments, nameof(assignments), "Projection assignments");

        var selectedNode = SelectNodeId(RelationQueryWireNames.ProjectNode, nodeId, source);
        var selectedBinding = SelectBindingId(selectedNode.Id, "result", resultBindingId, bindingSource ?? source);
        var selectedAssignments = assignments
            .Select((assignment, index) =>
            {
                var assignmentSource = assignment.AssignmentSource ?? source;
                var selected = SelectAssignmentId(
                    selectedNode.Id,
                    "projection",
                    index + 1,
                    assignment.Id,
                    assignmentSource);
                return new AuthoredProjectionAssignment(
                    new ProjectionAssignment(selected.Id, assignment.Target, assignment.Value),
                    selected,
                    assignmentSource,
                    assignment.ValueSource ?? assignmentSource);
            })
            .ToImmutableArray();

        EnsureAssignmentsAvailable(selectedAssignments.Select(static assignment => assignment.Identity));
        var node = new ProjectQueryNode(
            selectedNode.Id,
            input.Id,
            selectedBinding.Id,
            resultShape,
            [.. selectedAssignments.Select(static assignment => assignment.Assignment)]);
        AddNode(node, selectedNode, source, selectedBinding, bindingSource ?? source);
        foreach (var assignment in selectedAssignments)
        {
            AddAssignment(assignment.Identity, assignment.AssignmentSource);
            Trace(
                RelationQueryAuthoringDecisionKind.Expression,
                assignment.Assignment.Id.Value,
                assignment.ValueSource,
                "value");
        }

        return Bound(node, selectedBinding.Id);
    }

    /// <summary>Removes duplicate rows, optionally using explicit key expressions.</summary>
    /// <typeparam name="TInput">Canonical type of the input node.</typeparam>
    /// <param name="input">Typed input-node handle.</param>
    /// <param name="keys">Optional structural distinct-key inputs; an empty collection means whole-row distinctness.</param>
    /// <param name="nodeId">Optional explicit logical-node identity.</param>
    /// <param name="source">Optional producer-source attribution for the distinct node.</param>
    /// <returns>A typed distinct-node handle.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="input"/> belongs to another core, <paramref name="keys"/> contains a
    /// <see langword="null"/> entry, or <paramref name="nodeId"/> is default or repeated.
    /// </exception>
    public RelationQueryNodeHandle<DistinctQueryNode> Distinct<TInput>(
        RelationQueryNodeHandle<TInput> input,
        ImmutableArray<RelationQueryExpressionInput> keys = default,
        QueryNodeId? nodeId = null,
        RelationQueryAuthoringSource? source = null)
        where TInput : LogicalQueryNode
    {
        RequireNode(input, nameof(input));
        keys = NormalizeEntries(keys, nameof(keys), "Distinct keys");
        var selected = SelectNodeId(RelationQueryWireNames.DistinctNode, nodeId, source);
        var node = new DistinctQueryNode(selected.Id, input.Id, [.. keys.Select(static key => key.Value)]);
        AddNode(node, selected, source);
        for (var index = 0; index < keys.Length; index++)
        {
            Trace(
                RelationQueryAuthoringDecisionKind.Expression,
                node.Id.Value,
                keys[index].Source ?? source,
                $"keys/{index}");
        }

        return Handle(node);
    }

    /// <summary>Groups an input rowset and projects aggregate values into a semantic shape.</summary>
    /// <typeparam name="TInput">Canonical type of the input node.</typeparam>
    /// <param name="input">Typed input-node handle.</param>
    /// <param name="resultShape">Graph-qualified aggregate output shape.</param>
    /// <param name="groupings">Optional grouping-assignment inputs.</param>
    /// <param name="aggregates">Aggregate-assignment inputs.</param>
    /// <param name="nodeId">Optional explicit logical-node identity.</param>
    /// <param name="resultBindingId">Optional explicit aggregate-value binding identity.</param>
    /// <param name="source">Optional producer-source attribution for the aggregate node.</param>
    /// <param name="bindingSource">Optional producer-source attribution specific to the aggregate-value binding.</param>
    /// <returns>Typed handles for the aggregate node and aggregate-value binding.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="input"/> belongs to another core; an assignment collection contains a
    /// <see langword="null"/> entry; <paramref name="aggregates"/> is default or empty; or an
    /// explicit identity is default or repeated.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">An aggregate operation is unsupported.</exception>
    public RelationQueryBoundNodeHandle<AggregateQueryNode> Aggregate<TInput>(
        RelationQueryNodeHandle<TInput> input,
        QualifiedShapeId resultShape,
        ImmutableArray<RelationQueryGroupingAssignment> groupings = default,
        ImmutableArray<RelationQueryAggregateAssignment> aggregates = default,
        QueryNodeId? nodeId = null,
        ValueBindingId? resultBindingId = null,
        RelationQueryAuthoringSource? source = null,
        RelationQueryAuthoringSource? bindingSource = null)
        where TInput : LogicalQueryNode
    {
        RequireNode(input, nameof(input));
        groupings = NormalizeEntries(groupings, nameof(groupings), "Aggregate groupings");
        RequireEntries(aggregates, nameof(aggregates), "Aggregate assignments");

        var selectedNode = SelectNodeId(RelationQueryWireNames.AggregateNode, nodeId, source);
        var selectedBinding = SelectBindingId(selectedNode.Id, "result", resultBindingId, bindingSource ?? source);
        var selectedGroupings = groupings
            .Select((grouping, index) =>
            {
                var assignmentSource = grouping.AssignmentSource ?? source;
                var selected = SelectAssignmentId(
                    selectedNode.Id,
                    "grouping",
                    index + 1,
                    grouping.Id,
                    assignmentSource);
                return new AuthoredGroupingAssignment(
                    new QueryGrouping(selected.Id, grouping.Target, grouping.Key),
                    selected,
                    assignmentSource,
                    grouping.KeySource ?? assignmentSource);
            })
            .ToImmutableArray();
        var selectedAggregates = aggregates
            .Select((aggregate, index) =>
            {
                var assignmentSource = aggregate.AssignmentSource ?? source;
                var selected = SelectAssignmentId(
                    selectedNode.Id,
                    "aggregate",
                    index + 1,
                    aggregate.Id,
                    assignmentSource);
                return new AuthoredAggregateAssignment(
                    new QueryAggregateAssignment(
                        selected.Id,
                        aggregate.Target,
                        aggregate.Operation,
                        aggregate.Value,
                        aggregate.Filter),
                    selected,
                    assignmentSource,
                    aggregate.ValueSource ?? assignmentSource,
                    aggregate.FilterSource ?? assignmentSource);
            })
            .ToImmutableArray();

        EnsureAssignmentsAvailable(
            selectedGroupings.Select(static grouping => grouping.Identity)
                .Concat(selectedAggregates.Select(static aggregate => aggregate.Identity)));
        var node = new AggregateQueryNode(
            selectedNode.Id,
            input.Id,
            selectedBinding.Id,
            resultShape,
            [.. selectedGroupings.Select(static grouping => grouping.Grouping)],
            [.. selectedAggregates.Select(static aggregate => aggregate.Aggregate)]);
        AddNode(node, selectedNode, source, selectedBinding, bindingSource ?? source);

        foreach (var grouping in selectedGroupings)
        {
            AddAssignment(grouping.Identity, grouping.AssignmentSource);
            Trace(
                RelationQueryAuthoringDecisionKind.Expression,
                grouping.Grouping.Id.Value,
                grouping.KeySource,
                "key");
        }
        foreach (var aggregate in selectedAggregates)
        {
            AddAssignment(aggregate.Identity, aggregate.AssignmentSource);
            if (aggregate.Aggregate.Value is not null)
            {
                Trace(
                    RelationQueryAuthoringDecisionKind.Expression,
                    aggregate.Aggregate.Id.Value,
                    aggregate.ValueSource,
                    "value");
            }
            if (aggregate.Aggregate.Filter is not null)
            {
                Trace(
                    RelationQueryAuthoringDecisionKind.Expression,
                    aggregate.Aggregate.Id.Value,
                    aggregate.FilterSource,
                    "filter");
            }
        }

        return Bound(node, selectedBinding.Id);
    }

    /// <summary>Orders an input rowset using one or more deterministic key expressions.</summary>
    /// <typeparam name="TInput">Canonical type of the input node.</typeparam>
    /// <param name="input">Typed input-node handle.</param>
    /// <param name="orderings">Ordered structural key inputs, from primary key to final tie-breaker.</param>
    /// <param name="nodeId">Optional explicit logical-node identity.</param>
    /// <param name="source">Optional producer-source attribution for the order node.</param>
    /// <returns>A typed order-node handle.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="input"/> belongs to another core; <paramref name="orderings"/> is default,
    /// empty, or contains a <see langword="null"/> entry; or <paramref name="nodeId"/> is default or repeated.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">An ordering direction or null placement is unsupported.</exception>
    public RelationQueryNodeHandle<OrderQueryNode> Order<TInput>(
        RelationQueryNodeHandle<TInput> input,
        ImmutableArray<RelationQueryOrderingInput> orderings,
        QueryNodeId? nodeId = null,
        RelationQueryAuthoringSource? source = null)
        where TInput : LogicalQueryNode
    {
        RequireNode(input, nameof(input));
        RequireEntries(orderings, nameof(orderings), "Orderings");
        var selected = SelectNodeId(RelationQueryWireNames.OrderNode, nodeId, source);
        var node = new OrderQueryNode(
            selected.Id,
            input.Id,
            [.. orderings.Select(static ordering => new QueryOrdering(
                ordering.Key,
                ordering.Direction,
                ordering.NullPlacement))]);
        AddNode(node, selected, source);
        for (var index = 0; index < orderings.Length; index++)
        {
            Trace(
                RelationQueryAuthoringDecisionKind.Expression,
                node.Id.Value,
                orderings[index].KeySource ?? source,
                $"orderings/{index}/key");
        }

        return Handle(node);
    }

    /// <summary>Applies a canonical page request to an input rowset.</summary>
    /// <typeparam name="TInput">Canonical type of the input node.</typeparam>
    /// <param name="input">Typed input-node handle.</param>
    /// <param name="page">Offset or keyset page semantics.</param>
    /// <param name="nodeId">Optional explicit logical-node identity.</param>
    /// <param name="source">Optional producer-source attribution for the page node and continuation expressions.</param>
    /// <returns>A typed page-node handle.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="page"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="input"/> belongs to another core, or <paramref name="nodeId"/> is default or repeated.
    /// </exception>
    public RelationQueryNodeHandle<PageQueryNode> Page<TInput>(
        RelationQueryNodeHandle<TInput> input,
        QueryPageDefinition page,
        QueryNodeId? nodeId = null,
        RelationQueryAuthoringSource? source = null)
        where TInput : LogicalQueryNode
    {
        RequireNode(input, nameof(input));
        var selected = SelectNodeId(RelationQueryWireNames.PageNode, nodeId, source);
        var node = new PageQueryNode(selected.Id, input.Id, page);
        AddNode(node, selected, source);
        if (page is KeysetPageDefinition keyset)
        {
            for (var index = 0; index < keyset.After.Length; index++)
            {
                Trace(
                    RelationQueryAuthoringDecisionKind.Expression,
                    node.Id.Value,
                    source,
                    $"page/after/{index}");
            }
        }

        return Handle(node);
    }

    /// <summary>Declares a named rows result over one logical branch.</summary>
    /// <typeparam name="TInput">Canonical type of the branch input node.</typeparam>
    /// <param name="input">Typed input-node handle.</param>
    /// <param name="id">Optional explicit named-result identity.</param>
    /// <param name="source">Optional producer-source attribution.</param>
    /// <returns>A typed rows-result handle.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="input"/> belongs to another core, or <paramref name="id"/> is default or repeated.
    /// </exception>
    public RelationQueryResultHandle<RowsQueryResultDefinition> Rows<TInput>(
        RelationQueryNodeHandle<TInput> input,
        QueryResultId? id = null,
        RelationQueryAuthoringSource? source = null)
        where TInput : LogicalQueryNode
    {
        RequireNode(input, nameof(input));
        var selected = SelectResultId(RelationQueryWireNames.RowsResult, id, source);
        EnsureAvailable(resultIds, selected.Id, nameof(id), "query result");
        var result = new RowsQueryResultDefinition(selected.Id, input.Id);
        CommitOrdinal(resultOrdinals, selected);
        resultIds.Add(selected.Id);
        identities.Add(selected.Decision);
        Trace(RelationQueryAuthoringDecisionKind.Result, selected.Id.Value, source);
        return new(this, result);
    }

    /// <summary>Declares a named aggregation result over an aggregate-derived logical branch.</summary>
    /// <typeparam name="TInput">Canonical type of the branch input node.</typeparam>
    /// <param name="input">Typed aggregate-derived input-node handle.</param>
    /// <param name="id">Optional explicit named-result identity.</param>
    /// <param name="source">Optional producer-source attribution.</param>
    /// <returns>A typed aggregation-result handle.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="input"/> belongs to another core, or <paramref name="id"/> is default or repeated.
    /// </exception>
    public RelationQueryResultHandle<AggregationQueryResultDefinition> Aggregation<TInput>(
        RelationQueryNodeHandle<TInput> input,
        QueryResultId? id = null,
        RelationQueryAuthoringSource? source = null)
        where TInput : LogicalQueryNode
    {
        RequireNode(input, nameof(input));
        var selected = SelectResultId(RelationQueryWireNames.AggregationResult, id, source);
        EnsureAvailable(resultIds, selected.Id, nameof(id), "query result");
        var result = new AggregationQueryResultDefinition(selected.Id, input.Id);
        CommitOrdinal(resultOrdinals, selected);
        resultIds.Add(selected.Id);
        identities.Add(selected.Decision);
        Trace(RelationQueryAuthoringDecisionKind.Result, selected.Id.Value, source);
        return new(this, result);
    }

    /// <summary>Builds a canonical query definition and runs the authoritative canonical validator.</summary>
    /// <param name="id">Stable canonical query identity.</param>
    /// <param name="name">Human-readable canonical query name.</param>
    /// <param name="results">Named row and aggregation result handles.</param>
    /// <param name="source">Optional producer-source attribution for the query terminal.</param>
    /// <returns>The canonical query definition, validation, and non-semantic authoring provenance.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="results"/> is default, empty, contains a <see langword="null"/> entry, contains
    /// a handle from another core, or repeats a result identity.
    /// </exception>
    public RelationQueryAuthoringResult<IRQueryDefinition> BuildQuery(
        QueryId id,
        QueryName name,
        ImmutableArray<RelationQueryResultHandle> results,
        RelationQueryAuthoringSource? source = null)
    {
        RequireEntries(results, nameof(results), "Query results");
        foreach (var result in results)
        {
            if (!ReferenceEquals(result.Owner, this))
                throw new ArgumentException("A query result handle belongs to another authoring core.", nameof(results));
        }
        if (results.Select(static result => result.Id).Distinct().Count() != results.Length)
            throw new ArgumentException("Query result handles cannot repeat an identity.", nameof(results));

        IRQueryDefinition definition = new(
            id,
            name,
            CreateBody(),
            [.. results.Select(static result => result.Definition)]);
        var validation = RelationQueryDefinitionValidator.Validate(definition);
        HashSet<string> includedResults = results
            .Select(static result => result.Id.Value)
            .ToHashSet(StringComparer.Ordinal);
        return new(
            definition,
            validation,
            CreateManifest(
                includedResults,
                TerminalSource(RelationQueryWireNames.QueryDefinition, id.Value, source)));
    }

    /// <summary>Builds a canonical relation definition and runs the authoritative canonical validator.</summary>
    /// <typeparam name="TOutput">Canonical type of the relation output node.</typeparam>
    /// <param name="id">Stable canonical relation identity.</param>
    /// <param name="name">Human-readable canonical relation name.</param>
    /// <param name="root">Source binding whose values define rooted execution.</param>
    /// <param name="output">Typed logical node producing relation outputs.</param>
    /// <param name="outputShape">Graph-qualified semantic output shape.</param>
    /// <param name="mode">Output cardinality relative to relation roots.</param>
    /// <param name="key">Optional expression defining stable output identity.</param>
    /// <param name="invariants">Optional relation-output invariants.</param>
    /// <param name="source">Optional producer-source attribution for the relation terminal.</param>
    /// <param name="keySource">Optional producer-source attribution specific to the output-key expression.</param>
    /// <returns>The canonical relation definition, validation, and non-semantic authoring provenance.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="id"/> or <paramref name="name"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="root"/> or <paramref name="output"/> belongs to another core, or
    /// <paramref name="invariants"/> contains a <see langword="null"/> entry.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="mode"/> is unsupported.</exception>
    public RelationQueryAuthoringResult<IRRelationDefinition> BuildRelation<TOutput>(
        RelationId id,
        RelationName name,
        RelationQueryBindingHandle root,
        RelationQueryNodeHandle<TOutput> output,
        QualifiedShapeId outputShape,
        RelationOutputMode mode,
        Expr? key = null,
        ImmutableArray<InvariantDefinition> invariants = default,
        RelationQueryAuthoringSource? source = null,
        RelationQueryAuthoringSource? keySource = null)
        where TOutput : LogicalQueryNode
    {
        RequireBinding(root, nameof(root));
        RequireNode(output, nameof(output));
        invariants = NormalizeEntries(invariants, nameof(invariants), "Relation invariants");
        IRRelationDefinition definition = new(
            id,
            name,
            CreateBody(),
            root.Id,
            new RelationOutputDefinition(output.Id, outputShape, mode, key),
            invariants);
        var validation = RelationQueryDefinitionValidator.Validate(definition);
        List<RelationQueryAuthoringSourceDecision> terminalSources = [];
        if (TerminalSource(RelationQueryWireNames.RelationDefinition, id.Value, source) is { } terminal)
            terminalSources.Add(terminal);
        var effectiveKeySource = keySource ?? source;
        if (key is not null && effectiveKeySource is not null)
        {
            terminalSources.Add(new(
                RelationQueryAuthoringDecisionKind.Expression,
                id.Value,
                effectiveKeySource,
                "output/key"));
        }
        for (var index = 0; index < invariants.Length; index++)
        {
            if (source is not null)
            {
                terminalSources.Add(new(
                    RelationQueryAuthoringDecisionKind.Expression,
                    id.Value,
                    source,
                    $"invariants/{index}/expression"));
            }
        }

        return new(
            definition,
            validation,
            CreateManifest(new HashSet<string>(StringComparer.Ordinal), [.. terminalSources]));
    }

    LogicalQueryDefinition CreateBody() => new([.. nodes], [.. parameters]);

    RelationQueryAuthoringManifest CreateManifest(
        IReadOnlySet<string> includedResults,
        params RelationQueryAuthoringSourceDecision?[] additionalSources) =>
        new(
            [
                .. identities.Where(decision =>
                    decision.Kind != RelationQueryAuthoringIdentityKind.Result
                    || includedResults.Contains(decision.Value))
            ],
            [
                .. sources.Where(decision =>
                    decision.Kind != RelationQueryAuthoringDecisionKind.Result
                    || includedResults.Contains(decision.Target)),
                .. additionalSources.Where(static decision => decision is not null)!
            ]);

    static RelationQueryAuthoringSourceDecision? TerminalSource(
        string role,
        string target,
        RelationQueryAuthoringSource? source) =>
        source is null
            ? null
            : new RelationQueryAuthoringSourceDecision(
                RelationQueryAuthoringDecisionKind.Terminal,
                target,
                source,
                role);

    void RequireNode<TNode>(RelationQueryNodeHandle<TNode> handle, string parameterName)
        where TNode : LogicalQueryNode
    {
        if (!ReferenceEquals(handle.Owner, this)
            || !nodesById.TryGetValue(handle.Id, out var node)
            || node is not TNode)
        {
            throw new ArgumentException("The logical node handle belongs to another authoring core.", parameterName);
        }
    }

    void RequireBinding(RelationQueryBindingHandle handle, string parameterName)
    {
        if (!ReferenceEquals(handle.Owner, this) || !bindingIds.Contains(handle.Id))
            throw new ArgumentException("The value-binding handle belongs to another authoring core.", parameterName);
    }

    RelationQueryNodeHandle<TNode> Handle<TNode>(TNode node) where TNode : LogicalQueryNode =>
        new(this, node.Id);

    RelationQueryBoundNodeHandle<TNode> Bound<TNode>(TNode node, ValueBindingId binding)
        where TNode : LogicalQueryNode =>
        new(Handle(node), new RelationQueryBindingHandle(this, binding));

    void AddNode(
        LogicalQueryNode node,
        SelectedIdentity<QueryNodeId> selectedNode,
        RelationQueryAuthoringSource? nodeSource,
        SelectedIdentity<ValueBindingId>? selectedBinding = null,
        RelationQueryAuthoringSource? bindingSource = null)
    {
        EnsureAvailable(nodesById.Keys, node.Id, nameof(node), "logical node");
        if (selectedBinding is { } binding)
            EnsureAvailable(bindingIds, binding.Id, nameof(selectedBinding), "value binding");

        CommitOrdinal(nodeOrdinals, selectedNode);
        nodes.Add(node);
        nodesById.Add(node.Id, node);
        identities.Add(selectedNode.Decision);
        Trace(RelationQueryAuthoringDecisionKind.Node, node.Id.Value, nodeSource);
        if (selectedBinding is { } effectiveBinding)
        {
            bindingIds.Add(effectiveBinding.Id);
            identities.Add(effectiveBinding.Decision);
            Trace(RelationQueryAuthoringDecisionKind.Binding, effectiveBinding.Id.Value, bindingSource);
        }
    }

    void AddAssignment(
        SelectedIdentity<QueryAssignmentId> selected,
        RelationQueryAuthoringSource? source)
    {
        assignmentIds.Add(selected.Id);
        identities.Add(selected.Decision);
        Trace(RelationQueryAuthoringDecisionKind.Assignment, selected.Id.Value, source);
    }

    void EnsureAssignmentsAvailable(IEnumerable<SelectedIdentity<QueryAssignmentId>> selections)
    {
        HashSet<QueryAssignmentId> pending = [];
        foreach (var selected in selections)
        {
            EnsureAvailable(assignmentIds, selected.Id, "id", "query assignment");
            if (!pending.Add(selected.Id))
                throw new ArgumentException($"Query assignment identity '{selected.Id.Value}' is repeated.", "id");
        }
    }

    SelectedIdentity<QueryNodeId> SelectNodeId(
        string kind,
        QueryNodeId? explicitId,
        RelationQueryAuthoringSource? source)
    {
        var ordinal = PeekOrdinal(nodeOrdinals, kind);
        return explicitId is { } id
            ? Explicit(
                id,
                RelationQueryAuthoringIdentityKind.Node,
                id.Value,
                source,
                nameof(explicitId),
                kind,
                ordinal)
            : Convention(
                RelationQueryAuthoringIdentityConvention.CreateNodeId(kind, ordinal),
                RelationQueryAuthoringIdentityKind.Node,
                source,
                kind,
                ordinal);
    }

    SelectedIdentity<ValueBindingId> SelectBindingId(
        QueryNodeId node,
        string role,
        ValueBindingId? explicitId,
        RelationQueryAuthoringSource? source) =>
        explicitId is { } id
            ? Explicit(id, RelationQueryAuthoringIdentityKind.Binding, id.Value, source, nameof(explicitId))
            : Convention(
                RelationQueryAuthoringIdentityConvention.CreateBindingId(node, role),
                RelationQueryAuthoringIdentityKind.Binding,
                source);

    SelectedIdentity<QueryParameterId> SelectParameterId(
        QueryParameterId? explicitId,
        RelationQueryAuthoringSource? source)
    {
        var ordinal = parameterOrdinal + 1;
        return explicitId is { } id
            ? Explicit(
                id,
                RelationQueryAuthoringIdentityKind.Parameter,
                id.Value,
                source,
                nameof(explicitId),
                "parameter",
                ordinal)
            : Convention(
                RelationQueryAuthoringIdentityConvention.CreateParameterId(ordinal),
                RelationQueryAuthoringIdentityKind.Parameter,
                source,
                "parameter",
                ordinal);
    }

    static SelectedIdentity<QueryAssignmentId> SelectAssignmentId(
        QueryNodeId node,
        string role,
        int ordinal,
        QueryAssignmentId? explicitId,
        RelationQueryAuthoringSource? source) =>
        explicitId is { } id
            ? Explicit(id, RelationQueryAuthoringIdentityKind.Assignment, id.Value, source, nameof(explicitId))
            : Convention(
                RelationQueryAuthoringIdentityConvention.CreateAssignmentId(node, role, ordinal),
                RelationQueryAuthoringIdentityKind.Assignment,
                source);

    SelectedIdentity<QueryResultId> SelectResultId(
        string kind,
        QueryResultId? explicitId,
        RelationQueryAuthoringSource? source)
    {
        var ordinal = PeekOrdinal(resultOrdinals, kind);
        return explicitId is { } id
            ? Explicit(
                id,
                RelationQueryAuthoringIdentityKind.Result,
                id.Value,
                source,
                nameof(explicitId),
                kind,
                ordinal)
            : Convention(
                RelationQueryAuthoringIdentityConvention.CreateResultId(kind, ordinal),
                RelationQueryAuthoringIdentityKind.Result,
                source,
                kind,
                ordinal);
    }

    static SelectedIdentity<TId> Explicit<TId>(
        TId id,
        RelationQueryAuthoringIdentityKind kind,
        string? value,
        RelationQueryAuthoringSource? source,
        string parameterName,
        string? ordinalScope = null,
        int ordinal = 0)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("An explicit authoring identity cannot be default.", parameterName);
        return new(
            id,
            new RelationQueryAuthoringIdentityDecision(
                kind,
                value,
                RelationQueryAuthoringIdentityOrigin.Explicit,
                source: source),
            ordinalScope,
            ordinal);
    }

    static SelectedIdentity<TId> Convention<TId>(
        TId id,
        RelationQueryAuthoringIdentityKind kind,
        RelationQueryAuthoringSource? source,
        string? ordinalScope = null,
        int ordinal = 0)
    {
        var value = id switch
        {
            QueryNodeId node => node.Value,
            ValueBindingId binding => binding.Value,
            QueryParameterId parameter => parameter.Value,
            QueryAssignmentId assignment => assignment.Value,
            QueryResultId result => result.Value,
            _ => throw new InvalidOperationException($"Unsupported identity type '{typeof(TId).Name}'.")
        };
        return new(
            id,
            new RelationQueryAuthoringIdentityDecision(
                kind,
                value,
                RelationQueryAuthoringIdentityOrigin.Convention,
                RelationQueryAuthoringIdentityConvention.Version,
                source),
            ordinalScope,
            ordinal);
    }

    static int PeekOrdinal(IReadOnlyDictionary<string, int> ordinals, string kind)
    {
        ordinals.TryGetValue(kind, out var ordinal);
        return ordinal + 1;
    }

    static void CommitOrdinal<TId>(
        IDictionary<string, int> ordinals,
        SelectedIdentity<TId> selected)
    {
        if (selected.OrdinalScope is null || selected.Ordinal <= 0)
            throw new InvalidOperationException("A sequenced authoring identity requires an ordinal reservation.");

        ordinals.TryGetValue(selected.OrdinalScope, out var current);
        if (selected.Ordinal != current + 1)
            throw new InvalidOperationException("The authoring identity ordinal reservation is stale.");
        ordinals[selected.OrdinalScope] = selected.Ordinal;
    }

    void CommitParameterOrdinal(SelectedIdentity<QueryParameterId> selected)
    {
        if (selected.OrdinalScope != "parameter" || selected.Ordinal != parameterOrdinal + 1)
            throw new InvalidOperationException("The parameter identity ordinal reservation is stale.");
        parameterOrdinal = selected.Ordinal;
    }

    static void EnsureAvailable<TId>(
        IEnumerable<TId> existing,
        TId id,
        string parameterName,
        string identityKind)
        where TId : notnull
    {
        if (existing.Contains(id))
            throw new ArgumentException($"The {identityKind} identity '{id}' is already declared.", parameterName);
    }

    void Trace(
        RelationQueryAuthoringDecisionKind kind,
        string target,
        RelationQueryAuthoringSource? source,
        string? role = null)
    {
        if (source is not null)
            sources.Add(new(kind, target, source, role));
    }

    static ImmutableArray<T> NormalizeEntries<T>(
        ImmutableArray<T> entries,
        string parameterName,
        string description)
        where T : class
    {
        if (entries.IsDefault)
            return [];
        if (entries.Any(static entry => entry is null))
            throw new ArgumentException($"{description} cannot contain null entries.", parameterName);
        return entries;
    }

    static void RequireEntries<T>(
        ImmutableArray<T> entries,
        string parameterName,
        string description)
        where T : class
    {
        if (entries.IsDefaultOrEmpty)
            throw new ArgumentException($"{description} require at least one entry.", parameterName);
        if (entries.Any(static entry => entry is null))
            throw new ArgumentException($"{description} cannot contain null entries.", parameterName);
    }

    readonly record struct SelectedIdentity<TId>(
        TId Id,
        RelationQueryAuthoringIdentityDecision Decision,
        string? OrdinalScope = null,
        int Ordinal = 0);

    sealed record AuthoredProjectionAssignment(
        ProjectionAssignment Assignment,
        SelectedIdentity<QueryAssignmentId> Identity,
        RelationQueryAuthoringSource? AssignmentSource,
        RelationQueryAuthoringSource? ValueSource);

    sealed record AuthoredGroupingAssignment(
        QueryGrouping Grouping,
        SelectedIdentity<QueryAssignmentId> Identity,
        RelationQueryAuthoringSource? AssignmentSource,
        RelationQueryAuthoringSource? KeySource);

    sealed record AuthoredAggregateAssignment(
        QueryAggregateAssignment Aggregate,
        SelectedIdentity<QueryAssignmentId> Identity,
        RelationQueryAuthoringSource? AssignmentSource,
        RelationQueryAuthoringSource? ValueSource,
        RelationQueryAuthoringSource? FilterSource);
}
