using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Cohesive.Model.Serialization;
using Cohesive.Model.Expressions;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.IR;
using Cohesive.Relations.Model;

namespace Cohesive.Relations.Realization;

/// <summary>Portable demanded output referenced by a realization requirement.</summary>
public sealed record RelationQueryRealizationOutputReference
{
    /// <summary>Creates a realization output reference.</summary>
    /// <param name="id">Stable compiled demanded-output identity.</param>
    /// <param name="kind">Whether the output belongs to a relation or named query result.</param>
    /// <param name="node">Logical node producing the output.</param>
    /// <param name="shape">Graph-qualified output shape.</param>
    /// <param name="relation">Relation identity for a relation output; otherwise <see langword="null"/>.</param>
    /// <param name="queryResult">Named result identity for a query output; otherwise <see langword="null"/>.</param>
    /// <param name="field">Demanded output field, or <see langword="null"/> for the complete terminal.</param>
    /// <exception cref="ArgumentException">
    /// An identity, node, or shape is invalid; relation and query-result identity do not agree with
    /// <paramref name="kind"/>; or <paramref name="field"/> does not belong to <paramref name="shape"/>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is unsupported.</exception>
    [JsonConstructor]
    public RelationQueryRealizationOutputReference(
        RelationQueryOutputId id,
        RelationQueryOutputReferenceKind kind,
        QueryNodeId node,
        QualifiedShapeId shape,
        RelationId? relation = null,
        QueryResultId? queryResult = null,
        RelationQueryFieldReference? field = null)
    {
        if (string.IsNullOrWhiteSpace(id.Value))
            throw new ArgumentException("A realization output requires a stable identity.", nameof(id));
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported output-reference kind.");
        if (string.IsNullOrWhiteSpace(node.Value))
            throw new ArgumentException("A realization output requires a logical node.", nameof(node));
        if (string.IsNullOrWhiteSpace(shape.GraphId.Value) || string.IsNullOrWhiteSpace(shape.ShapeId.Value))
            throw new ArgumentException("A realization output requires a graph-qualified shape.", nameof(shape));
        if (kind == RelationQueryOutputReferenceKind.Relation
            && (relation is not { } relationId
                || string.IsNullOrWhiteSpace(relationId.Value)
                || queryResult is not null))
        {
            throw new ArgumentException("A relation output requires only a relation identity.", nameof(relation));
        }
        if (kind == RelationQueryOutputReferenceKind.QueryResult
            && (queryResult is not { } resultId
                || string.IsNullOrWhiteSpace(resultId.Value)
                || relation is not null))
        {
            throw new ArgumentException("A query-result output requires only a query-result identity.", nameof(queryResult));
        }
        if (field is { } outputField
            && (outputField.Shape != shape || outputField.Path.Segments.IsDefaultOrEmpty))
        {
            throw new ArgumentException("An output field must be a valid path on the output shape.", nameof(field));
        }

        Id = id;
        Kind = kind;
        Node = node;
        Shape = shape;
        Relation = relation;
        QueryResult = queryResult;
        Field = field;
    }

    /// <summary>Stable compiled demanded-output identity.</summary>
    public RelationQueryOutputId Id { get; }

    /// <summary>Whether the output belongs to a relation or named query result.</summary>
    public RelationQueryOutputReferenceKind Kind { get; }

    /// <summary>Logical node producing the output.</summary>
    public QueryNodeId Node { get; }

    /// <summary>Graph-qualified output shape.</summary>
    public QualifiedShapeId Shape { get; }

    /// <summary>Relation identity for a relation output; otherwise <see langword="null"/>.</summary>
    public RelationId? Relation { get; }

    /// <summary>Named result identity for a query output; otherwise <see langword="null"/>.</summary>
    public QueryResultId? QueryResult { get; }

    /// <summary>Demanded output field, or <see langword="null"/> for the complete terminal.</summary>
    public RelationQueryFieldReference? Field { get; }
}

/// <summary>Semantic kind of one portable realization-requirement trace step.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RelationQueryRealizationTraceStepKind
{
    /// <summary>A logical node crossed while propagating a requirement.</summary>
    Structural = 0,

    /// <summary>A typed expression site that consumes a requirement.</summary>
    ExpressionSite = 1,

    /// <summary>An aggregate assignment operation that consumes a requirement.</summary>
    AggregateOperation = 2,

    /// <summary>A demanded relation or query terminal that requires the semantic capability.</summary>
    Terminal = 3
}

/// <summary>One typed step from a demanded output toward a required semantic capability.</summary>
public sealed record RelationQueryRealizationTraceStep
{
    /// <summary>Creates a realization trace step.</summary>
    /// <param name="kind">Typed trace-step role.</param>
    /// <param name="node">Logical node anchoring the step.</param>
    /// <param name="siteKind">Expression-site kind for an expression step; otherwise <see langword="null"/>.</param>
    /// <param name="expressionSite">Stable expression-site identity for an expression step.</param>
    /// <param name="assignment">Projection, grouping, or aggregate assignment identity when applicable.</param>
    /// <param name="ordinal">Stable ordered-site ordinal when applicable.</param>
    /// <param name="invariantName">Stable invariant name for a relation-invariant site.</param>
    /// <exception cref="ArgumentException">
    /// The node is default; an optional identity is empty; an expression step omits expression-site data; or a
    /// non-expression step supplies expression-site data inconsistent with <paramref name="kind"/>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="kind"/> or <paramref name="siteKind"/> is unsupported, or <paramref name="ordinal"/> is negative.
    /// </exception>
    [JsonConstructor]
    public RelationQueryRealizationTraceStep(
        RelationQueryRealizationTraceStepKind kind,
        QueryNodeId node,
        RelationQueryExpressionSiteKind? siteKind = null,
        ExprSiteId? expressionSite = null,
        QueryAssignmentId? assignment = null,
        int? ordinal = null,
        string? invariantName = null)
    {
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported realization trace-step kind.");
        if (string.IsNullOrWhiteSpace(node.Value))
            throw new ArgumentException("A realization trace step requires a logical node.", nameof(node));
        if (siteKind is { } expressionKind && !Enum.IsDefined(expressionKind))
            throw new ArgumentOutOfRangeException(nameof(siteKind), siteKind, "Unsupported expression-site kind.");
        if (expressionSite is { } site && string.IsNullOrWhiteSpace(site.Value))
            throw new ArgumentException("An expression-site identity cannot be empty.", nameof(expressionSite));
        if (assignment is { } assignmentId && string.IsNullOrWhiteSpace(assignmentId.Value))
            throw new ArgumentException("An assignment identity cannot be empty.", nameof(assignment));
        if (ordinal is < 0)
            throw new ArgumentOutOfRangeException(nameof(ordinal), ordinal, "A realization trace ordinal cannot be negative.");
        if (invariantName is not null && string.IsNullOrWhiteSpace(invariantName))
            throw new ArgumentException("An invariant name cannot be empty.", nameof(invariantName));

        if (kind == RelationQueryRealizationTraceStepKind.ExpressionSite)
        {
            if (siteKind is null || expressionSite is null)
                throw new ArgumentException("An expression trace step requires a site kind and identity.", nameof(expressionSite));
        }
        else if (siteKind is not null || expressionSite is not null || ordinal is not null || invariantName is not null)
        {
            throw new ArgumentException("Only an expression trace step can carry expression-site data.", nameof(kind));
        }

        if (kind == RelationQueryRealizationTraceStepKind.AggregateOperation && assignment is null)
            throw new ArgumentException("An aggregate-operation trace step requires an assignment.", nameof(assignment));
        if (kind is RelationQueryRealizationTraceStepKind.Structural or RelationQueryRealizationTraceStepKind.Terminal
            && assignment is not null)
        {
            throw new ArgumentException("A structural or terminal trace step cannot carry an assignment.", nameof(assignment));
        }

        Kind = kind;
        Node = node;
        SiteKind = siteKind;
        ExpressionSite = expressionSite;
        Assignment = assignment;
        Ordinal = ordinal;
        InvariantName = invariantName;
    }

    /// <summary>Typed trace-step role.</summary>
    public RelationQueryRealizationTraceStepKind Kind { get; }

    /// <summary>Logical node anchoring the step.</summary>
    public QueryNodeId Node { get; }

    /// <summary>Expression-site kind for an expression step; otherwise <see langword="null"/>.</summary>
    public RelationQueryExpressionSiteKind? SiteKind { get; }

    /// <summary>Stable expression-site identity for an expression step.</summary>
    public ExprSiteId? ExpressionSite { get; }

    /// <summary>Projection, grouping, or aggregate assignment identity when applicable.</summary>
    public QueryAssignmentId? Assignment { get; }

    /// <summary>Stable ordered-site ordinal when applicable.</summary>
    public int? Ordinal { get; }

    /// <summary>Stable invariant name for a relation-invariant site.</summary>
    public string? InvariantName { get; }
}

/// <summary>Ordered provenance chain from a demanded output to one realization requirement.</summary>
public sealed record RelationQueryRealizationTrace
{
    /// <summary>Creates a realization requirement trace.</summary>
    /// <param name="steps">Non-empty downstream-to-upstream trace steps.</param>
    /// <exception cref="ArgumentException"><paramref name="steps"/> is empty or contains a <see langword="null"/> entry.</exception>
    [JsonConstructor]
    public RelationQueryRealizationTrace(ImmutableArray<RelationQueryRealizationTraceStep> steps)
    {
        var normalized = steps.IsDefault ? [] : steps;
        if (normalized.IsDefaultOrEmpty)
            throw new ArgumentException("A realization trace requires at least one step.", nameof(steps));
        if (normalized.Any(static step => step is null))
            throw new ArgumentException("A realization trace cannot contain null steps.", nameof(steps));
        Steps = normalized;
    }

    /// <summary>Downstream-to-upstream trace steps in semantic propagation order.</summary>
    public ImmutableArray<RelationQueryRealizationTraceStep> Steps { get; }
}

/// <summary>One demanded-output use of a realization requirement.</summary>
public sealed record RelationQueryRealizationRequirementUse
{
    /// <summary>Creates a realization requirement use.</summary>
    /// <param name="output">Demanded output affected by the requirement.</param>
    /// <param name="effect">Semantic effect through which the requirement affects the output.</param>
    /// <param name="requirement">Whether the semantic input represented by the use is required or optional.</param>
    /// <param name="traces">One or more exact provenance traces.</param>
    /// <exception cref="ArgumentNullException"><paramref name="output"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="traces"/> is empty, contains null, or repeats a trace.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="effect"/> or <paramref name="requirement"/> is unsupported.
    /// </exception>
    [JsonConstructor]
    public RelationQueryRealizationRequirementUse(
        RelationQueryRealizationOutputReference output,
        RelationQueryRequirementEffect effect,
        QueryInputRequirement requirement,
        ImmutableArray<RelationQueryRealizationTrace> traces)
    {
        Output = Guard.RequireNotNull(output);
        if (!Enum.IsDefined(effect))
            throw new ArgumentOutOfRangeException(nameof(effect), effect, "Unsupported requirement effect.");
        if (!Enum.IsDefined(requirement))
            throw new ArgumentOutOfRangeException(nameof(requirement), requirement, "Unsupported input requirement.");
        Effect = effect;
        Requirement = requirement;
        Traces = NormalizeTraces(traces);
    }

    /// <summary>Demanded output affected by the requirement.</summary>
    public RelationQueryRealizationOutputReference Output { get; }

    /// <summary>Semantic effect through which the requirement affects the output.</summary>
    public RelationQueryRequirementEffect Effect { get; }

    /// <summary>Whether the semantic input represented by the use is required or optional.</summary>
    public QueryInputRequirement Requirement { get; }

    /// <summary>Exact provenance traces in deterministic order.</summary>
    public ImmutableArray<RelationQueryRealizationTrace> Traces { get; }

    internal static ImmutableArray<RelationQueryRealizationTrace> NormalizeTraces(
        ImmutableArray<RelationQueryRealizationTrace> traces)
    {
        var normalized = traces.IsDefault ? [] : traces;
        if (normalized.IsDefaultOrEmpty)
            throw new ArgumentException("A realization requirement use requires at least one trace.", nameof(traces));
        if (normalized.Any(static trace => trace is null))
            throw new ArgumentException("Realization traces cannot contain null entries.", nameof(traces));
        var keyed = normalized.Select(trace => (Trace: trace, Key: TraceKey(trace))).ToArray();
        if (keyed.GroupBy(static item => item.Key, StringComparer.Ordinal).Any(static group => group.Count() > 1))
            throw new ArgumentException("Realization traces cannot be duplicated.", nameof(traces));
        return [.. keyed.OrderBy(static item => item.Key, StringComparer.Ordinal).Select(static item => item.Trace)];
    }

    internal static string TraceKey(RelationQueryRealizationTrace trace) =>
        RelationQueryRealizationOrdering.SequenceKey(trace.Steps.Select(static step =>
            RelationQueryRealizationOrdering.SequenceKey(
            [
                ((int)step.Kind).ToString("D4", System.Globalization.CultureInfo.InvariantCulture),
                step.Node.Value,
                step.SiteKind is { } siteKind
                    ? ((int)siteKind).ToString("D4", System.Globalization.CultureInfo.InvariantCulture)
                    : null,
                step.ExpressionSite?.Value,
                step.Assignment?.Value,
                step.Ordinal?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                step.InvariantName
            ])));
}

/// <summary>Typed semantic location from which a realization requirement was projected.</summary>
public sealed record RelationQueryRealizationRequirementOrigin
{
    /// <summary>Creates a requirement origin.</summary>
    /// <param name="input">Compiled input identity associated with the requirement, or <see langword="null"/>.</param>
    /// <param name="node">Logical node associated with the requirement, or <see langword="null"/>.</param>
    /// <param name="semanticSite">Stable expression, assignment, terminal, or operator site.</param>
    /// <param name="expressionPath">Path to the exact expression-tree node requiring the capability.</param>
    /// <param name="fieldPath">Typed structural field path requiring the capability.</param>
    /// <param name="binding">Named value binding against which the requirement is evaluated.</param>
    /// <exception cref="ArgumentException">
    /// Every locator is absent; an identity or string locator is empty; or <paramref name="fieldPath"/> is invalid.
    /// </exception>
    [JsonConstructor]
    public RelationQueryRealizationRequirementOrigin(
        RelationQueryInputId? input = null,
        QueryNodeId? node = null,
        string? semanticSite = null,
        string? expressionPath = null,
        FieldPath? fieldPath = null,
        ValueBindingId? binding = null)
    {
        if (input is { } inputId && string.IsNullOrWhiteSpace(inputId.Value))
            throw new ArgumentException("A requirement-origin input identity cannot be empty.", nameof(input));
        if (node is { } nodeId && string.IsNullOrWhiteSpace(nodeId.Value))
            throw new ArgumentException("A requirement-origin node identity cannot be empty.", nameof(node));
        if (semanticSite is not null && string.IsNullOrWhiteSpace(semanticSite))
            throw new ArgumentException("A requirement-origin semantic site cannot be empty.", nameof(semanticSite));
        if (expressionPath is not null && string.IsNullOrWhiteSpace(expressionPath))
            throw new ArgumentException("A requirement-origin expression path cannot be empty.", nameof(expressionPath));
        if (fieldPath is { } path && path.Segments.IsDefaultOrEmpty)
            throw new ArgumentException("A requirement-origin field path cannot be empty.", nameof(fieldPath));
        if (binding is { } bindingId && string.IsNullOrWhiteSpace(bindingId.Value))
            throw new ArgumentException("A requirement-origin binding identity cannot be empty.", nameof(binding));
        if (input is null
            && node is null
            && semanticSite is null
            && expressionPath is null
            && fieldPath is null
            && binding is null)
        {
            throw new ArgumentException("A requirement origin requires at least one typed locator.", nameof(semanticSite));
        }

        Input = input;
        Node = node;
        SemanticSite = semanticSite;
        ExpressionPath = expressionPath;
        FieldPath = fieldPath;
        Binding = binding;
    }

    /// <summary>Compiled input identity associated with the requirement, or <see langword="null"/>.</summary>
    public RelationQueryInputId? Input { get; }

    /// <summary>Logical node associated with the requirement, or <see langword="null"/>.</summary>
    public QueryNodeId? Node { get; }

    /// <summary>Stable expression, assignment, terminal, or operator site.</summary>
    public string? SemanticSite { get; }

    /// <summary>Path to the exact expression-tree node requiring the capability.</summary>
    public string? ExpressionPath { get; }

    /// <summary>Typed structural field path requiring the capability.</summary>
    public FieldPath? FieldPath { get; }

    /// <summary>Named value binding against which the requirement is evaluated, or <see langword="null"/>.</summary>
    public ValueBindingId? Binding { get; }
}

/// <summary>Kind of immutable demand-scoped plan fact available to validate an operating boundary.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RelationQueryRealizationStaticFactKind
{
    /// <summary>Number of effective segments in a structural field path.</summary>
    FieldPathDepth = 0,

    /// <summary>Depth of the exact canonical expression-tree site.</summary>
    ExpressionDepth = 1,

    /// <summary>Maximum row count requested by one paging node.</summary>
    PageSize = 2
}

/// <summary>One portable immutable plan fact used to validate a constrained realization.</summary>
public sealed record RelationQueryRealizationStaticFact
{
    /// <summary>Creates a demand-scoped static plan fact.</summary>
    /// <param name="kind">Semantic measurement represented by the fact.</param>
    /// <param name="value">Non-negative measured value.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="kind"/> is unsupported, or <paramref name="value"/> is negative.
    /// </exception>
    [JsonConstructor]
    public RelationQueryRealizationStaticFact(
        RelationQueryRealizationStaticFactKind kind,
        long value)
    {
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported realization static-fact kind.");
        if (value < 0)
            throw new ArgumentOutOfRangeException(nameof(value), value, "A realization static fact cannot be negative.");

        Kind = kind;
        Value = value;
    }

    /// <summary>Semantic measurement represented by the fact.</summary>
    public RelationQueryRealizationStaticFactKind Kind { get; }

    /// <summary>
    /// Non-negative measured value. Portable JSON encodes the value as a canonical decimal string.
    /// </summary>
    [JsonConverter(typeof(StringEncodedInt64JsonConverter))]
    public long Value { get; }
}

/// <summary>One exact demand-scoped semantic capability a target must realize.</summary>
public sealed record RelationQueryRealizationRequirement
{
    /// <summary>Creates a realization requirement.</summary>
    /// <param name="id">Stable demand-scoped requirement identity.</param>
    /// <param name="capability">Exact semantic capability required by the compiled plan.</param>
    /// <param name="origin">Typed originating site, or <see langword="null"/> for a plan-wide requirement.</param>
    /// <param name="uses">
    /// Demanded-output uses of the requirement. The collection may be empty for constant-only, global, or terminal requirements.
    /// </param>
    /// <param name="requiredGuarantees">
    /// Guarantees that the selected strategy for this exact requirement must preserve.
    /// </param>
    /// <param name="staticFacts">
    /// Immutable demand-scoped plan measurements available for operating-boundary validation.
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="id"/> is default; <paramref name="uses"/> contains null, repeats an output/effect pair, or
    /// contains conflicting definitions for one output identity; or <paramref name="staticFacts"/> contains null
    /// entries or repeats a fact kind.
    /// </exception>
    /// <exception cref="ArgumentNullException"><paramref name="capability"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="requiredGuarantees"/> contains an unsupported guarantee.
    /// </exception>
    [JsonConstructor]
    public RelationQueryRealizationRequirement(
        RelationQueryRealizationRequirementId id,
        RelationQueryCapability capability,
        RelationQueryRealizationRequirementOrigin? origin = null,
        ImmutableArray<RelationQueryRealizationRequirementUse> uses = default,
        ImmutableArray<RelationQueryGuaranteeCapabilityKind> requiredGuarantees = default,
        ImmutableArray<RelationQueryRealizationStaticFact> staticFacts = default)
    {
        if (string.IsNullOrWhiteSpace(id.Value))
            throw new ArgumentException("A realization requirement requires a stable identity.", nameof(id));
        Id = id;
        Capability = Guard.RequireNotNull(capability);
        Origin = origin;
        Uses = NormalizeUses(uses);
        RequiredGuarantees = RelationQueryRealizationOrdering.NormalizeGuarantees(
            requiredGuarantees,
            nameof(requiredGuarantees));
        StaticFacts = NormalizeStaticFacts(staticFacts);
    }

    /// <summary>Stable demand-scoped requirement identity.</summary>
    public RelationQueryRealizationRequirementId Id { get; }

    /// <summary>Exact semantic capability required by the compiled plan.</summary>
    public RelationQueryCapability Capability { get; }

    /// <summary>Typed originating site, or <see langword="null"/> for a plan-wide requirement.</summary>
    public RelationQueryRealizationRequirementOrigin? Origin { get; }

    /// <summary>Demanded-output uses of the requirement in deterministic order.</summary>
    public ImmutableArray<RelationQueryRealizationRequirementUse> Uses { get; }

    /// <summary>Guarantees that the selected strategy for this exact requirement must preserve.</summary>
    public ImmutableArray<RelationQueryGuaranteeCapabilityKind> RequiredGuarantees { get; }

    /// <summary>Immutable demand-scoped plan measurements in deterministic kind order.</summary>
    public ImmutableArray<RelationQueryRealizationStaticFact> StaticFacts { get; }

    static ImmutableArray<RelationQueryRealizationStaticFact> NormalizeStaticFacts(
        ImmutableArray<RelationQueryRealizationStaticFact> staticFacts)
    {
        var normalized = staticFacts.IsDefault ? [] : staticFacts;
        if (normalized.Any(static fact => fact is null))
            throw new ArgumentException("Realization static facts cannot contain null entries.", nameof(staticFacts));
        if (normalized.GroupBy(static fact => fact.Kind).Any(static group => group.Count() > 1))
            throw new ArgumentException("Realization static facts cannot repeat a fact kind.", nameof(staticFacts));
        return [.. normalized.OrderBy(static fact => (int)fact.Kind)];
    }

    static ImmutableArray<RelationQueryRealizationRequirementUse> NormalizeUses(
        ImmutableArray<RelationQueryRealizationRequirementUse> uses)
    {
        var normalized = uses.IsDefault ? [] : uses;
        if (normalized.Any(static use => use is null))
            throw new ArgumentException("Realization requirement uses cannot contain null entries.", nameof(uses));
        foreach (var group in normalized.GroupBy(static use => use.Output.Id))
        {
            var expected = group.First().Output;
            if (group.Skip(1).Any(use => !Equals(use.Output, expected)))
                throw new ArgumentException($"Output '{group.Key.Value}' has conflicting realization references.", nameof(uses));
        }
        if (normalized.GroupBy(static use => (use.Output.Id, use.Effect)).Any(static group => group.Count() > 1))
            throw new ArgumentException("Realization requirement uses cannot repeat an output/effect pair.", nameof(uses));
        return
        [
            .. normalized
                .OrderBy(static use => use.Output.Id.Value, StringComparer.Ordinal)
                .ThenBy(static use => (int)use.Effect)
        ];
    }
}
