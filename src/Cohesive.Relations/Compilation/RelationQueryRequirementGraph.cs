using System.Collections.Immutable;
using Cohesive.Model.Expressions;
using Cohesive.Model.Serialization;
using Cohesive.Relations.IR;
using Cohesive.Relations.Model;

namespace Cohesive.Relations.Compilation;

/// <summary>
/// Stable identity for one semantic input in a compiled requirement graph.
/// </summary>
[System.Text.Json.Serialization.JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct RelationQueryInputId
{
    /// <summary>Creates an input identifier.</summary>
    /// <param name="value">Stable non-empty identifier.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white space.</exception>
    public RelationQueryInputId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw stable identifier.</summary>
    public string Value { get; }

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>
/// Stable identity for one demanded output in a compiled requirement graph.
/// </summary>
[System.Text.Json.Serialization.JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct RelationQueryOutputId
{
    /// <summary>Creates an output identifier.</summary>
    /// <param name="value">Stable non-empty identifier.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white space.</exception>
    public RelationQueryOutputId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw stable identifier.</summary>
    public string Value { get; }

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>
/// Semantic effect through which an input may influence a compiled output.
/// </summary>
public enum RelationQueryRequirementEffect
{
    /// <summary>The input contributes to an emitted field value.</summary>
    Value = 0,

    /// <summary>The input contributes stable observation or output identity.</summary>
    Identity = 1,

    /// <summary>The input affects whether a row belongs to the result.</summary>
    Membership = 2,

    /// <summary>The input correlates values across independently produced rowsets.</summary>
    Correlation = 3,

    /// <summary>The input is required to acquire another semantic value.</summary>
    Acquisition = 4,

    /// <summary>The input affects the number of emitted rows.</summary>
    Cardinality = 5,

    /// <summary>The input affects result ordering.</summary>
    Ordering = 6,

    /// <summary>The input determines an aggregation group.</summary>
    Grouping = 7,

    /// <summary>The input contributes to an aggregate result.</summary>
    Aggregation = 8,

    /// <summary>The input affects page boundaries or page membership.</summary>
    Pagination = 9,

    /// <summary>The input is required to validate a declared semantic invariant.</summary>
    Validation = 10,

    /// <summary>The input is a capability required to evaluate demanded semantics.</summary>
    Evaluation = 11
}

/// <summary>
/// Base type for a semantic input required by a compiled relation or query.
/// </summary>
public abstract record RelationQueryRequirementInput
{
    /// <summary>Creates a requirement input with a stable identity.</summary>
    /// <param name="id">Stable input identity within the compiled requirement graph.</param>
    /// <exception cref="ArgumentException"><paramref name="id"/> is default.</exception>
    private protected RelationQueryRequirementInput(RelationQueryInputId id)
    {
        if (string.IsNullOrWhiteSpace(id.Value))
            throw new ArgumentException("A requirement input requires a non-empty identifier.", nameof(id));
        Id = id;
    }

    /// <summary>Stable input identity within the compiled requirement graph.</summary>
    public RelationQueryInputId Id { get; }
}

/// <summary>
/// A field value acquired from a shaped binding.
/// </summary>
public sealed record RelationQueryFieldInput : RelationQueryRequirementInput
{
    internal RelationQueryFieldInput(
        RelationQueryInputId id,
        QueryNodeId producer,
        ValueBindingId binding,
        RelationQueryFieldReference field,
        ExprValueContract? valueContract = null)
        : base(id)
    {
        if (string.IsNullOrWhiteSpace(producer.Value))
            throw new ArgumentException("A field input requires a producer node.", nameof(producer));
        if (string.IsNullOrWhiteSpace(binding.Value))
            throw new ArgumentException("A field input requires a binding.", nameof(binding));
        if (!RelationQueryContractOrdering.IsValid(field))
            throw new ArgumentException("A field input requires a valid field reference.", nameof(field));

        Producer = producer;
        Binding = binding;
        Field = field;
        ValueContract = valueContract;
    }

    /// <summary>Node that introduces the shaped binding containing the field.</summary>
    public QueryNodeId Producer { get; }

    /// <summary>Binding against which the field path is evaluated.</summary>
    public ValueBindingId Binding { get; }

    /// <summary>Graph-qualified field required from the binding.</summary>
    public RelationQueryFieldReference Field { get; }

    /// <summary>Resolved semantic value contract, or <see langword="null"/> when unresolved.</summary>
    public ExprValueContract? ValueContract { get; }
}

/// <summary>
/// Stable observation identity required from a shaped binding.
/// </summary>
public sealed record RelationQueryObservationIdentityInput : RelationQueryRequirementInput
{
    internal RelationQueryObservationIdentityInput(
        RelationQueryInputId id,
        QueryNodeId producer,
        ValueBindingId binding,
        QualifiedShapeId shape)
        : base(id)
    {
        RequireNode(producer, nameof(producer));
        RequireBinding(binding, nameof(binding));
        RequireShape(shape, nameof(shape));
        Producer = producer;
        Binding = binding;
        Shape = shape;
    }

    /// <summary>Node that introduces the identity-bearing binding.</summary>
    public QueryNodeId Producer { get; }

    /// <summary>Identity-bearing binding.</summary>
    public ValueBindingId Binding { get; }

    /// <summary>Shape whose observation identity is required.</summary>
    public QualifiedShapeId Shape { get; }

    static void RequireNode(QueryNodeId node, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(node.Value))
            throw new ArgumentException("An identity input requires a producer node.", parameterName);
    }

    static void RequireBinding(ValueBindingId binding, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(binding.Value))
            throw new ArgumentException("An identity input requires a binding.", parameterName);
    }

    static void RequireShape(QualifiedShapeId shape, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(shape.GraphId.Value) || string.IsNullOrWhiteSpace(shape.ShapeId.Value))
            throw new ArgumentException("An identity input requires a graph-qualified shape.", parameterName);
    }
}

/// <summary>
/// Existence or enumeration of a complete semantic source set.
/// </summary>
public sealed record RelationQuerySourceSetInput : RelationQueryRequirementInput
{
    internal RelationQuerySourceSetInput(
        RelationQueryInputId id,
        QueryNodeId source,
        ValueBindingId binding,
        QualifiedShapeId shape,
        RelationQuerySourceInputRole role,
        QueryInputRequirement requirement)
        : base(id)
    {
        if (string.IsNullOrWhiteSpace(source.Value))
            throw new ArgumentException("A source-set input requires a source node.", nameof(source));
        if (string.IsNullOrWhiteSpace(binding.Value))
            throw new ArgumentException("A source-set input requires a binding.", nameof(binding));
        if (string.IsNullOrWhiteSpace(shape.GraphId.Value) || string.IsNullOrWhiteSpace(shape.ShapeId.Value))
            throw new ArgumentException("A source-set input requires a graph-qualified shape.", nameof(shape));
        if (!Enum.IsDefined(role))
            throw new ArgumentOutOfRangeException(nameof(role), role, "Unsupported source-input role.");
        if (!Enum.IsDefined(requirement))
            throw new ArgumentOutOfRangeException(nameof(requirement), requirement, "Unsupported input requirement.");

        Source = source;
        Binding = binding;
        Shape = shape;
        Role = role;
        Requirement = requirement;
    }

    /// <summary>Source node whose set is required.</summary>
    public QueryNodeId Source { get; }

    /// <summary>Binding introduced by the source.</summary>
    public ValueBindingId Binding { get; }

    /// <summary>Shape of values in the source set.</summary>
    public QualifiedShapeId Shape { get; }

    /// <summary>Whether the source supplies relation roots or independently acquired values.</summary>
    public RelationQuerySourceInputRole Role { get; }

    /// <summary>Whether source-set acquisition is required or optional.</summary>
    public QueryInputRequirement Requirement { get; }
}

/// <summary>
/// Declared semantic relationship required by a traversal.
/// </summary>
public sealed record RelationQueryRelationshipInput : RelationQueryRequirementInput
{
    internal RelationQueryRelationshipInput(
        RelationQueryInputId id,
        QueryNodeId traversal,
        RelationshipDefinition definition,
        RelationshipTraversalDirection direction,
        ValueBindingId from,
        QualifiedShapeId fromShape,
        ValueBindingId result,
        QualifiedShapeId resultShape,
        JoinKind joinKind,
        QueryInputRequirement requirement,
        RelationshipTraversalCardinality cardinality)
        : base(id)
    {
        if (string.IsNullOrWhiteSpace(traversal.Value))
            throw new ArgumentException("A relationship input requires a traversal node.", nameof(traversal));
        Definition = Guard.RequireNotNull(definition);
        if (!Enum.IsDefined(direction))
            throw new ArgumentOutOfRangeException(nameof(direction), direction, "Unsupported traversal direction.");
        if (string.IsNullOrWhiteSpace(from.Value))
            throw new ArgumentException("A relationship input requires a source binding.", nameof(from));
        if (string.IsNullOrWhiteSpace(result.Value))
            throw new ArgumentException("A relationship input requires a result binding.", nameof(result));
        if (!Enum.IsDefined(joinKind) || joinKind is JoinKind.Right or JoinKind.Full)
            throw new ArgumentOutOfRangeException(nameof(joinKind), joinKind, "A relationship traversal supports inner or left joins.");
        if (!Enum.IsDefined(requirement))
            throw new ArgumentOutOfRangeException(nameof(requirement), requirement, "Unsupported input requirement.");
        if (!Enum.IsDefined(cardinality))
            throw new ArgumentOutOfRangeException(nameof(cardinality), cardinality, "Unsupported traversal cardinality.");

        var expectedFromShape = direction == RelationshipTraversalDirection.Forward
            ? definition.SourceShape
            : definition.TargetShape;
        var expectedResultShape = direction == RelationshipTraversalDirection.Forward
            ? definition.TargetShape
            : definition.SourceShape;
        if (fromShape != expectedFromShape)
            throw new ArgumentException("The traversal source shape does not match the relationship endpoint.", nameof(fromShape));
        if (resultShape != expectedResultShape)
            throw new ArgumentException("The traversal result shape does not match the relationship endpoint.", nameof(resultShape));
        if (direction == RelationshipTraversalDirection.Inverse && cardinality != definition.InverseCardinality)
            throw new ArgumentException("Inverse traversal cardinality must match the relationship definition.", nameof(cardinality));

        Traversal = traversal;
        Direction = direction;
        From = from;
        FromShape = fromShape;
        Result = result;
        ResultShape = resultShape;
        JoinKind = joinKind;
        Requirement = requirement;
        Cardinality = cardinality;
    }

    /// <summary>Traversal node that consumes the relationship.</summary>
    public QueryNodeId Traversal { get; }

    /// <summary>Stable relationship identifier.</summary>
    public RelationshipId Relationship => Definition.Id;

    /// <summary>Exact canonical relationship definition consumed from the catalog snapshot.</summary>
    public RelationshipDefinition Definition { get; }

    /// <summary>Direction in which the relationship is traversed.</summary>
    public RelationshipTraversalDirection Direction { get; }

    /// <summary>Visible binding from which traversal starts.</summary>
    public ValueBindingId From { get; }

    /// <summary>Shape at the traversal source endpoint.</summary>
    public QualifiedShapeId FromShape { get; }

    /// <summary>Binding introduced for related values.</summary>
    public ValueBindingId Result { get; }

    /// <summary>Shape at the traversal result endpoint.</summary>
    public QualifiedShapeId ResultShape { get; }

    /// <summary>Join semantics applied when related values are absent.</summary>
    public JoinKind JoinKind { get; }

    /// <summary>Whether related-value resolution is required or optional.</summary>
    public QueryInputRequirement Requirement { get; }

    /// <summary>Maximum number of results yielded per traversal source.</summary>
    public RelationshipTraversalCardinality Cardinality { get; }
}

/// <summary>
/// Invocation parameter required by a demanded expression site.
/// </summary>
public sealed record RelationQueryParameterInput : RelationQueryRequirementInput
{
    internal RelationQueryParameterInput(RelationQueryInputId id, QueryParameterDefinition definition)
        : base(id)
    {
        Definition = Guard.RequireNotNull(definition);
    }

    /// <summary>Required query parameter.</summary>
    public QueryParameterId Parameter => Definition.Id;

    /// <summary>Canonical query-parameter declaration.</summary>
    public QueryParameterDefinition Definition { get; }
}

/// <summary>
/// Expression operation or ambient capability required by demanded semantics.
/// </summary>
public sealed record RelationQueryCapabilityInput : RelationQueryRequirementInput
{
    internal RelationQueryCapabilityInput(
        RelationQueryInputId id,
        ExprCapabilityRequirement capability)
        : base(id)
    {
        if (string.IsNullOrWhiteSpace(capability.Capability.Value)
            || !Enum.IsDefined(capability.Kind))
        {
            throw new ArgumentException("A capability input requires a valid capability requirement.", nameof(capability));
        }

        Capability = capability;
    }

    /// <summary>Required expression capability and its requirement kind.</summary>
    public ExprCapabilityRequirement Capability { get; }
}

/// <summary>
/// Kind of declared result represented by a requirement-graph output.
/// </summary>
public enum RelationQueryOutputReferenceKind
{
    /// <summary>The output belongs to the compiled relation.</summary>
    Relation = 0,

    /// <summary>The output belongs to a named query result.</summary>
    QueryResult = 1
}

/// <summary>
/// Demanded relation output or named query result, optionally narrowed to one field.
/// </summary>
public sealed record RelationQueryOutputReference
{
    internal RelationQueryOutputReference(
        RelationQueryOutputId id,
        RelationQueryOutputReferenceKind kind,
        QueryNodeId node,
        QualifiedShapeId shape,
        RelationId? relation = null,
        QueryResultId? queryResult = null,
        RelationQueryFieldReference? field = null)
    {
        if (string.IsNullOrWhiteSpace(id.Value))
            throw new ArgumentException("An output reference requires a stable identifier.", nameof(id));
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported output-reference kind.");
        if (string.IsNullOrWhiteSpace(node.Value))
            throw new ArgumentException("An output reference requires a producing node.", nameof(node));
        if (string.IsNullOrWhiteSpace(shape.GraphId.Value) || string.IsNullOrWhiteSpace(shape.ShapeId.Value))
            throw new ArgumentException("An output reference requires a graph-qualified shape.", nameof(shape));
        if (kind == RelationQueryOutputReferenceKind.Relation
            && (relation is null || string.IsNullOrWhiteSpace(relation.Value) || queryResult is not null))
        {
            throw new ArgumentException("A relation output requires only a relation identifier.", nameof(relation));
        }
        if (kind == RelationQueryOutputReferenceKind.QueryResult
            && (queryResult is not { } result || string.IsNullOrWhiteSpace(result.Value) || relation is not null))
        {
            throw new ArgumentException("A query-result output requires only a query-result identifier.", nameof(queryResult));
        }
        if (field is { } selectedField && selectedField.Shape != shape)
            throw new ArgumentException("An output field must belong to the output shape.", nameof(field));

        Id = id;
        Kind = kind;
        Node = node;
        Shape = shape;
        Relation = relation;
        QueryResult = queryResult;
        Field = field;
    }

    /// <summary>Stable demanded-output identity.</summary>
    public RelationQueryOutputId Id { get; }

    /// <summary>Whether this is a relation output or named query result.</summary>
    public RelationQueryOutputReferenceKind Kind { get; }

    /// <summary>Logical node producing the output.</summary>
    public QueryNodeId Node { get; }

    /// <summary>Shape emitted by the output.</summary>
    public QualifiedShapeId Shape { get; }

    /// <summary>Relation identity for a relation output; otherwise <see langword="null"/>.</summary>
    public RelationId? Relation { get; }

    /// <summary>Named query-result identity for a query result; otherwise <see langword="null"/>.</summary>
    public QueryResultId? QueryResult { get; }

    /// <summary>Demanded output field, or <see langword="null"/> when the edge affects the complete output.</summary>
    public RelationQueryFieldReference? Field { get; }
}

/// <summary>Semantic kind of one requirement-provenance step.</summary>
public enum RelationQueryRequirementTraceStepKind
{
    /// <summary>A logical node crossed while propagating a requirement upstream.</summary>
    Structural = 0,

    /// <summary>A canonical expression site that consumes the requirement.</summary>
    ExpressionSite = 1,

    /// <summary>An aggregate assignment whose operator requires a capability.</summary>
    AggregateOperation = 2
}

/// <summary>One typed step in a requirement-provenance chain.</summary>
public readonly record struct RelationQueryRequirementTraceStep
{
    internal RelationQueryRequirementTraceStep(QueryNodeId node)
        : this(
            RelationQueryRequirementTraceStepKind.Structural,
            node,
            siteKind: null,
            expressionSite: null,
            assignment: null,
            ordinal: null,
            invariantName: null)
    {
    }

    internal RelationQueryRequirementTraceStep(
        QueryNodeId node,
        RelationQueryExpressionSiteKind siteKind,
        ExprSiteId expressionSite,
        QueryAssignmentId? assignment = null,
        int? ordinal = null,
        string? invariantName = null)
        : this(
            RelationQueryRequirementTraceStepKind.ExpressionSite,
            node,
            siteKind,
            expressionSite,
            assignment,
            ordinal,
            invariantName)
    {
    }

    internal static RelationQueryRequirementTraceStep ForAggregateOperation(
        QueryNodeId node,
        QueryAssignmentId assignment) =>
        new(
            RelationQueryRequirementTraceStepKind.AggregateOperation,
            node,
            siteKind: null,
            expressionSite: null,
            assignment,
            ordinal: null,
            invariantName: null);

    RelationQueryRequirementTraceStep(
        RelationQueryRequirementTraceStepKind kind,
        QueryNodeId node,
        RelationQueryExpressionSiteKind? siteKind,
        ExprSiteId? expressionSite,
        QueryAssignmentId? assignment,
        int? ordinal,
        string? invariantName)
    {
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported requirement trace-step kind.");
        if (string.IsNullOrWhiteSpace(node.Value))
            throw new ArgumentException("A requirement trace step requires a logical node.", nameof(node));
        if (siteKind is { } expressionKind && !Enum.IsDefined(expressionKind))
            throw new ArgumentOutOfRangeException(nameof(siteKind), siteKind, "Unsupported expression-site kind.");
        if (expressionSite is { } site && string.IsNullOrWhiteSpace(site.Value))
            throw new ArgumentException("A requirement trace expression site cannot be empty.", nameof(expressionSite));
        if (assignment is { } assigned && string.IsNullOrWhiteSpace(assigned.Value))
            throw new ArgumentException("A requirement trace assignment cannot be empty.", nameof(assignment));
        if (ordinal is < 0)
            throw new ArgumentOutOfRangeException(nameof(ordinal), ordinal, "A requirement trace ordinal cannot be negative.");
        if (invariantName is not null && string.IsNullOrWhiteSpace(invariantName))
            throw new ArgumentException("A requirement trace invariant name cannot be empty.", nameof(invariantName));

        if (kind == RelationQueryRequirementTraceStepKind.Structural)
        {
            if (siteKind is not null
                || expressionSite is not null
                || assignment is not null
                || ordinal is not null
                || invariantName is not null)
                throw new ArgumentException("A structural trace step cannot declare a semantic-site origin.", nameof(kind));
        }
        else if (kind == RelationQueryRequirementTraceStepKind.ExpressionSite)
        {
            if (siteKind is null)
                throw new ArgumentException("An expression trace step requires an expression-site kind.", nameof(siteKind));
            if (expressionSite is null)
                throw new ArgumentException("An expression requirement trace step requires an expression-site identity.", nameof(expressionSite));

            var assignmentSite = siteKind is RelationQueryExpressionSiteKind.ProjectionAssignmentValue
                or RelationQueryExpressionSiteKind.AggregateGroupingKey
                or RelationQueryExpressionSiteKind.AggregateAssignmentValue
                or RelationQueryExpressionSiteKind.AggregateAssignmentFilter;
            var indexedSite = siteKind is RelationQueryExpressionSiteKind.DistinctKey
                or RelationQueryExpressionSiteKind.OrderKey
                or RelationQueryExpressionSiteKind.KeysetBoundary
                or RelationQueryExpressionSiteKind.TemporalJoinIntervalLowerBound
                or RelationQueryExpressionSiteKind.TemporalJoinIntervalUpperBound;
            var invariantSite = siteKind == RelationQueryExpressionSiteKind.RelationInvariant;

            if (assignmentSite != (assignment is not null))
                throw new ArgumentException("The assignment origin does not match the expression-site kind.", nameof(assignment));
            if (indexedSite != (ordinal is not null))
                throw new ArgumentException("The ordinal origin does not match the expression-site kind.", nameof(ordinal));
            if (invariantSite != (invariantName is not null))
                throw new ArgumentException("The invariant origin does not match the expression-site kind.", nameof(invariantName));
        }
        else
        {
            if (siteKind is not null || expressionSite is not null || ordinal is not null || invariantName is not null)
                throw new ArgumentException("An aggregate-operation trace step cannot declare expression-site origin values.", nameof(kind));
            if (assignment is null)
                throw new ArgumentException("An aggregate-operation trace step requires an assignment.", nameof(assignment));
        }

        Kind = kind;
        Node = node;
        SiteKind = siteKind;
        ExpressionSite = expressionSite;
        Assignment = assignment;
        Ordinal = ordinal;
        InvariantName = invariantName;
    }

    /// <summary>Semantic kind of provenance step.</summary>
    public RelationQueryRequirementTraceStepKind Kind { get; }

    /// <summary>
    /// Logical node whose binding environment anchors this step, including the output node for relation-level sites.
    /// </summary>
    public QueryNodeId Node { get; }

    /// <summary>Typed expression-site role, or <see langword="null"/> for a structural propagation step.</summary>
    public RelationQueryExpressionSiteKind? SiteKind { get; }

    /// <summary>Expression site consumed at this step, or <see langword="null"/> for a structural step.</summary>
    public ExprSiteId? ExpressionSite { get; }

    /// <summary>Projection, grouping, or aggregate-operation assignment crossed by this step, when applicable.</summary>
    public QueryAssignmentId? Assignment { get; }

    /// <summary>Stable ordinal for an ordered expression site, when applicable.</summary>
    public int? Ordinal { get; }

    /// <summary>Stable relation invariant name, or <see langword="null"/> for another site kind.</summary>
    public string? InvariantName { get; }
}

/// <summary>
/// Ordered provenance chain from a downstream demand to the semantic input that satisfies it.
/// </summary>
public sealed record RelationQueryRequirementTrace
{
    internal RelationQueryRequirementTrace(ImmutableArray<RelationQueryRequirementTraceStep> steps)
    {
        Steps = steps.IsDefault ? [] : steps;
        if (Steps.IsDefaultOrEmpty)
            throw new ArgumentException("A requirement trace requires at least one step.", nameof(steps));
    }

    /// <summary>
    /// Ordered steps beginning at the downstream consuming site and proceeding toward the upstream input.
    /// </summary>
    public ImmutableArray<RelationQueryRequirementTraceStep> Steps { get; }
}

/// <summary>
/// One required-input edge and its effect on a demanded output.
/// </summary>
public sealed record RelationQueryRequirementEdge
{
    internal RelationQueryRequirementEdge(
        RelationQueryRequirementInput input,
        RelationQueryOutputReference output,
        RelationQueryRequirementEffect effect,
        QueryInputRequirement requirement,
        ImmutableArray<RelationQueryRequirementTrace> traces)
    {
        Input = Guard.RequireNotNull(input);
        Output = Guard.RequireNotNull(output);
        if (!Enum.IsDefined(effect))
            throw new ArgumentOutOfRangeException(nameof(effect), effect, "Unsupported requirement effect.");
        if (!Enum.IsDefined(requirement))
            throw new ArgumentOutOfRangeException(nameof(requirement), requirement, "Unsupported input requirement.");
        Effect = effect;
        Requirement = requirement;
        Traces = RelationQueryRequirementOrdering.NormalizeTraces(traces);
        if (Traces.IsDefaultOrEmpty)
            throw new ArgumentException("A requirement edge requires at least one provenance trace.", nameof(traces));
    }

    /// <summary>Semantic input required by the edge.</summary>
    public RelationQueryRequirementInput Input { get; }

    /// <summary>Demanded output affected by the input.</summary>
    public RelationQueryOutputReference Output { get; }

    /// <summary>Semantic effect through which the input affects the output.</summary>
    public RelationQueryRequirementEffect Effect { get; }

    /// <summary>Whether acquisition of the input is required or optional.</summary>
    public QueryInputRequirement Requirement { get; }

    /// <summary>Distinct, deterministically ordered propagation traces.</summary>
    public ImmutableArray<RelationQueryRequirementTrace> Traces { get; }
}

/// <summary>
/// Immutable canonical graph from semantic inputs to demanded outputs.
/// </summary>
public sealed class RelationQueryRequirementGraph
{
    internal RelationQueryRequirementGraph(
        ImmutableArray<RelationQueryRequirementInput> inputs,
        ImmutableArray<RelationQueryOutputReference> outputs,
        ImmutableArray<RelationQueryRequirementEdge> edges)
    {
        Inputs = NormalizeInputs(inputs);
        Outputs = NormalizeOutputs(outputs);
        if (Inputs.IsDefaultOrEmpty)
            throw new ArgumentException("A requirement graph requires at least one semantic input.", nameof(inputs));
        if (Outputs.IsDefaultOrEmpty)
            throw new ArgumentException("A requirement graph requires at least one demanded output.", nameof(outputs));
        var inputsById = Inputs.ToDictionary(static input => input.Id);
        var outputsById = Outputs.ToDictionary(static output => output.Id);

        var normalizedEdges = edges.IsDefault ? [] : edges;
        if (normalizedEdges.Any(static edge => edge is null))
            throw new ArgumentException("Requirement edges cannot contain null entries.", nameof(edges));
        foreach (var edge in normalizedEdges)
        {
            if (!inputsById.ContainsKey(edge.Input.Id))
                throw new ArgumentException($"Requirement edge references unknown input '{edge.Input.Id.Value}'.", nameof(edges));
            if (!outputsById.ContainsKey(edge.Output.Id))
                throw new ArgumentException($"Requirement edge references unknown output '{edge.Output.Id.Value}'.", nameof(edges));
            if (!Equals(inputsById[edge.Input.Id], edge.Input))
                throw new ArgumentException($"Requirement edge carries a conflicting definition for input '{edge.Input.Id.Value}'.", nameof(edges));
            if (!Equals(outputsById[edge.Output.Id], edge.Output))
                throw new ArgumentException($"Requirement edge carries a conflicting definition for output '{edge.Output.Id.Value}'.", nameof(edges));
        }

        Edges =
        [
            .. normalizedEdges
                .GroupBy(static edge => (
                    Input: edge.Input.Id,
                    Output: edge.Output.Id,
                    edge.Effect))
                .Select(group => new RelationQueryRequirementEdge(
                    inputsById[group.Key.Input],
                    outputsById[group.Key.Output],
                    group.Key.Effect,
                    group.Any(static edge => edge.Requirement == QueryInputRequirement.Required)
                        ? QueryInputRequirement.Required
                        : QueryInputRequirement.Optional,
                    [.. group.SelectMany(static edge => edge.Traces)]))
                .OrderBy(static edge => edge.Input.Id.Value, StringComparer.Ordinal)
                .ThenBy(static edge => edge.Output.Id.Value, StringComparer.Ordinal)
                .ThenBy(static edge => (int)edge.Effect)
                .ThenBy(static edge => (int)edge.Requirement)
        ];

        var referencedInputs = Edges.Select(static edge => edge.Input.Id).ToHashSet();
        if (Inputs.Any(input => !referencedInputs.Contains(input.Id)))
            throw new ArgumentException("A requirement graph cannot contain orphan inputs.", nameof(inputs));

        foreach (var input in Inputs)
        {
            var requirements = Edges
                .Where(edge => edge.Input.Id == input.Id)
                .Select(static edge => edge.Requirement);
            var strongest = requirements.Contains(QueryInputRequirement.Required)
                ? QueryInputRequirement.Required
                : QueryInputRequirement.Optional;
            if (input is RelationQuerySourceSetInput source && source.Requirement != strongest)
            {
                throw new ArgumentException(
                    $"Source-set input '{input.Id.Value}' requiredness conflicts with its graph edges.",
                    nameof(edges));
            }
            if (input is RelationQueryRelationshipInput relationship && relationship.Requirement != strongest)
            {
                throw new ArgumentException(
                    $"Relationship input '{input.Id.Value}' requiredness conflicts with its graph edges.",
                    nameof(edges));
            }
        }
    }

    /// <summary>Semantic inputs sorted by stable input identity.</summary>
    public ImmutableArray<RelationQueryRequirementInput> Inputs { get; }

    /// <summary>
    /// Demanded outputs sorted by stable output identity. A constant-derived output may have no incoming edge
    /// because producing its value requires no semantic runtime input.
    /// </summary>
    public ImmutableArray<RelationQueryOutputReference> Outputs { get; }

    /// <summary>Requirement edges sorted by input, output, effect, and requiredness.</summary>
    public ImmutableArray<RelationQueryRequirementEdge> Edges { get; }

    static ImmutableArray<RelationQueryRequirementInput> NormalizeInputs(
        ImmutableArray<RelationQueryRequirementInput> inputs)
    {
        var normalized = inputs.IsDefault ? [] : inputs;
        if (normalized.Any(static input => input is null))
            throw new ArgumentException("Requirement inputs cannot contain null entries.", nameof(inputs));

        foreach (var group in normalized.GroupBy(static input => input.Id))
        {
            var first = group.First();
            if (group.Skip(1).Any(input => !Equals(input, first)))
                throw new ArgumentException($"Requirement input id '{group.Key.Value}' has conflicting definitions.", nameof(inputs));
        }

        return
        [
            .. normalized.GroupBy(static input => input.Id)
                .Select(static group => group.First())
                .OrderBy(static input => input.Id.Value, StringComparer.Ordinal)
        ];
    }

    static ImmutableArray<RelationQueryOutputReference> NormalizeOutputs(
        ImmutableArray<RelationQueryOutputReference> outputs)
    {
        var normalized = outputs.IsDefault ? [] : outputs;
        if (normalized.Any(static output => output is null))
            throw new ArgumentException("Requirement outputs cannot contain null entries.", nameof(outputs));

        foreach (var group in normalized.GroupBy(static output => output.Id))
        {
            var first = group.First();
            if (group.Skip(1).Any(output => !Equals(output, first)))
                throw new ArgumentException($"Requirement output id '{group.Key.Value}' has conflicting definitions.", nameof(outputs));
        }

        return
        [
            .. normalized.GroupBy(static output => output.Id)
                .Select(static group => group.First())
                .OrderBy(static output => output.Id.Value, StringComparer.Ordinal)
        ];
    }
}

static class RelationQueryRequirementOrdering
{
    public static ImmutableArray<RelationQueryRequirementTrace> NormalizeTraces(ImmutableArray<RelationQueryRequirementTrace> traces)
    {
        var normalized = traces.IsDefault ? [] : traces;
        if (normalized.Any(static trace => trace is null))
            throw new ArgumentException("Requirement traces cannot contain null entries.", nameof(traces));

        return
        [
            .. normalized.GroupBy(TraceKey, StringComparer.Ordinal)
                .Select(static group => group.First())
                .OrderBy(TraceKey, StringComparer.Ordinal)
        ];
    }

    public static string TraceKey(RelationQueryRequirementTrace trace) =>
        string.Concat(trace.Steps.Select(static step =>
            Encode(step.Node.Value)
            + Encode(((int)step.Kind).ToString(System.Globalization.CultureInfo.InvariantCulture))
            + Encode(step.SiteKind is null
                ? null
                : ((int)step.SiteKind.Value).ToString(System.Globalization.CultureInfo.InvariantCulture))
            + Encode(step.ExpressionSite?.Value)
            + Encode(step.Assignment?.Value)
            + Encode(step.Ordinal?.ToString(System.Globalization.CultureInfo.InvariantCulture))
            + Encode(step.InvariantName)));

    static string Encode(string? value) => value is null
        ? "-1:"
        : $"{value.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)}:{value}";
}
