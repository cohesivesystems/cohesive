using System.Collections.Immutable;
using Cohesive.Model.Serialization;
using Cohesive.Relations.IR;
using Cohesive.Relations.Model;
using Cohesive.Relations.Serialization;
using CanonicalRelationDefinition = Cohesive.Relations.IR.RelationDefinition;

namespace Cohesive.Relations.Compilation;

/// <summary>Origin of the effective output demand used by static compilation.</summary>
public enum RelationQueryCompilationDemandOrigin
{
    /// <summary>The request omitted demand and the compiler applied its all-declared-outputs convention.</summary>
    Convention = 0,

    /// <summary>The caller supplied the demand explicitly.</summary>
    Explicit = 1
}

/// <summary>
/// Exact persisted semantic inputs and output demand supplied to static compilation.
/// </summary>
public sealed class RelationQueryCompilationRequest
{
    /// <summary>Creates a static compilation request.</summary>
    /// <param name="definitionDocument">Exact persisted relation/query definition document to compile.</param>
    /// <param name="shapeDocuments">Exact persisted shape-graph snapshots available to compilation.</param>
    /// <param name="relationshipCatalogDocument">
    /// Exact persisted relationship catalog snapshot, or <see langword="null"/> when no catalog is supplied.
    /// </param>
    /// <param name="demand">
    /// Requested outputs, or <see langword="null"/> to use <see cref="RelationQueryCompilationDemand.AllDeclaredOutputs"/>.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="definitionDocument"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="shapeDocuments"/> contains a <see langword="null"/> entry.</exception>
    public RelationQueryCompilationRequest(
        RelationQueryDocument definitionDocument,
        ImmutableArray<ShapeGraphDocument> shapeDocuments = default,
        RelationshipCatalogDocument? relationshipCatalogDocument = null,
        RelationQueryCompilationDemand? demand = null)
    {
        DefinitionDocument = Guard.RequireNotNull(definitionDocument);
        var snapshots = shapeDocuments.IsDefault ? [] : shapeDocuments;
        if (snapshots.Any(static document => document is null))
            throw new ArgumentException("Shape documents cannot contain null entries.", nameof(shapeDocuments));

        ShapeDocuments =
        [
            .. snapshots
                .OrderBy(static document => document.Graph?.Id.Value, StringComparer.Ordinal)
                .ThenBy(static document => document.SchemaVersion, StringComparer.Ordinal)
        ];
        RelationshipCatalogDocument = relationshipCatalogDocument;
        DemandOrigin = demand is null
            ? RelationQueryCompilationDemandOrigin.Convention
            : RelationQueryCompilationDemandOrigin.Explicit;
        Demand = demand ?? RelationQueryCompilationDemand.AllDeclaredOutputs;
    }

    /// <summary>Exact persisted relation/query definition document to compile.</summary>
    public RelationQueryDocument DefinitionDocument { get; }

    /// <summary>
    /// Exact supplied shape-graph documents sorted by graph identity; duplicate identities remain for diagnostics.
    /// </summary>
    public ImmutableArray<ShapeGraphDocument> ShapeDocuments { get; }

    /// <summary>Exact relationship catalog document supplied to compilation, or <see langword="null"/>.</summary>
    public RelationshipCatalogDocument? RelationshipCatalogDocument { get; }

    /// <summary>Explicit or convention-derived output demand.</summary>
    public RelationQueryCompilationDemand Demand { get; }

    /// <summary>Whether <see cref="Demand"/> was explicitly supplied or selected by convention.</summary>
    public RelationQueryCompilationDemandOrigin DemandOrigin { get; }
}

/// <summary>
/// Kind of semantics-preserving logical-node bypass selected by static compilation.
/// </summary>
public enum RelationQueryLogicalBypassKind
{
    /// <summary>
    /// Bypass a left, optional relationship traversal whose result cardinality is proven to be at most one.
    /// </summary>
    OptionalAtMostOneLeftRelationshipTraversal = 0
}

/// <summary>
/// Exact semantic evidence for one transparent logical node bypass.
/// </summary>
public sealed record RelationQueryLogicalBypass
{
    internal RelationQueryLogicalBypass(
        RelationQueryLogicalBypassKind kind,
        QueryNodeId node,
        RelationshipDefinition relationship,
        RelationshipTraversalDirection direction,
        RelationshipTraversalCardinality cardinality,
        ValueBindingId from,
        ValueBindingId result)
    {
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported logical bypass kind.");
        if (string.IsNullOrWhiteSpace(node.Value))
            throw new ArgumentException("A logical bypass requires a canonical node identity.", nameof(node));
        Relationship = Guard.RequireNotNull(relationship);
        if (!Enum.IsDefined(direction))
            throw new ArgumentOutOfRangeException(nameof(direction), direction, "Unsupported traversal direction.");
        if (!Enum.IsDefined(cardinality))
            throw new ArgumentOutOfRangeException(nameof(cardinality), cardinality, "Unsupported traversal cardinality.");
        if (kind == RelationQueryLogicalBypassKind.OptionalAtMostOneLeftRelationshipTraversal
            && cardinality != RelationshipTraversalCardinality.AtMostOne)
        {
            throw new ArgumentException("The selected bypass requires at-most-one traversal cardinality.", nameof(cardinality));
        }
        if (string.IsNullOrWhiteSpace(from.Value))
            throw new ArgumentException("A traversal bypass requires a source binding.", nameof(from));
        if (string.IsNullOrWhiteSpace(result.Value))
            throw new ArgumentException("A traversal bypass requires a result binding.", nameof(result));

        Kind = kind;
        Node = node;
        Direction = direction;
        Cardinality = cardinality;
        From = from;
        Result = result;
    }

    /// <summary>Selected semantics-preserving bypass strategy.</summary>
    public RelationQueryLogicalBypassKind Kind { get; }

    /// <summary>Canonical logical node omitted by the bypass.</summary>
    public QueryNodeId Node { get; }

    /// <summary>Exact relationship definition used to prove the traversal semantics.</summary>
    public RelationshipDefinition Relationship { get; }

    /// <summary>Direction in which the omitted traversal follows <see cref="Relationship"/>.</summary>
    public RelationshipTraversalDirection Direction { get; }

    /// <summary>Proven maximum result cardinality of the omitted traversal.</summary>
    public RelationshipTraversalCardinality Cardinality { get; }

    /// <summary>Visible binding from which the omitted traversal starts.</summary>
    public ValueBindingId From { get; }

    /// <summary>Binding introduced only by the omitted traversal.</summary>
    public ValueBindingId Result { get; }
}

/// <summary>
/// One canonical input slot, its effective retained input, and explicit contiguous bypass evidence.
/// </summary>
public sealed record RelationQueryLogicalPlanInput
{
    internal RelationQueryLogicalPlanInput(
        QueryNodeId canonicalInput,
        QueryNodeId effectiveInput,
        ImmutableArray<RelationQueryLogicalBypass> bypasses = default)
    {
        if (string.IsNullOrWhiteSpace(canonicalInput.Value))
            throw new ArgumentException("A logical input slot requires a canonical input.", nameof(canonicalInput));
        if (string.IsNullOrWhiteSpace(effectiveInput.Value))
            throw new ArgumentException("A logical input slot requires an effective input.", nameof(effectiveInput));
        var normalized = bypasses.IsDefault ? [] : bypasses;
        if (normalized.Any(static bypass => bypass is null))
            throw new ArgumentException("Logical bypass evidence cannot contain null entries.", nameof(bypasses));
        if (normalized.GroupBy(static bypass => bypass.Node).Any(static group => group.Count() > 1))
            throw new ArgumentException("A logical input slot cannot bypass the same node more than once.", nameof(bypasses));
        if (normalized.IsDefaultOrEmpty && canonicalInput != effectiveInput)
            throw new ArgumentException("A rewired effective input requires explicit bypass evidence.", nameof(effectiveInput));
        if (!normalized.IsDefaultOrEmpty && normalized[0].Node != canonicalInput)
            throw new ArgumentException("The bypass chain must begin at the canonical input.", nameof(bypasses));

        CanonicalInput = canonicalInput;
        EffectiveInput = effectiveInput;
        Bypasses = normalized;
    }

    /// <summary>Canonical input referenced by the retained node in persisted IR.</summary>
    public QueryNodeId CanonicalInput { get; }

    /// <summary>Retained node that supplies this input after applying <see cref="Bypasses"/>.</summary>
    public QueryNodeId EffectiveInput { get; }

    /// <summary>Ordered omitted nodes from the canonical input toward the effective retained input.</summary>
    public ImmutableArray<RelationQueryLogicalBypass> Bypasses { get; }
}

/// <summary>
/// One retained canonical node and its effective input slots after semantics-preserving pruning.
/// </summary>
public sealed record RelationQueryLogicalPlanNode
{
    internal RelationQueryLogicalPlanNode(
        QueryNodeId node,
        ImmutableArray<RelationQueryLogicalPlanInput> inputs)
    {
        if (string.IsNullOrWhiteSpace(node.Value))
            throw new ArgumentException("A logical-plan node requires a canonical node identity.", nameof(node));
        var normalized = inputs.IsDefault ? [] : inputs;
        if (normalized.Any(static input => input is null))
            throw new ArgumentException("Logical-plan inputs cannot contain null entries.", nameof(inputs));

        Node = node;
        Inputs = normalized;
        EffectiveInputs = [.. normalized.Select(static input => input.EffectiveInput)];
        if (EffectiveInputs.Contains(node))
            throw new ArgumentException("A logical-plan node cannot consume itself.", nameof(inputs));
    }

    /// <summary>Retained canonical logical node identity.</summary>
    public QueryNodeId Node { get; }

    /// <summary>Canonical input slots and their explicit rewiring decisions, in canonical input order.</summary>
    public ImmutableArray<RelationQueryLogicalPlanInput> Inputs { get; }

    /// <summary>Effective retained inputs projected from <see cref="Inputs"/>.</summary>
    public ImmutableArray<QueryNodeId> EffectiveInputs { get; }
}

/// <summary>
/// Target-independent logical plan whose effective topology explicitly represents semantics-preserving pruning.
/// </summary>
public sealed class RelationQueryLogicalPlan
{
    internal RelationQueryLogicalPlan(ImmutableArray<RelationQueryLogicalPlanNode> nodes)
    {
        var normalized = nodes.IsDefault ? [] : nodes;
        if (normalized.IsDefaultOrEmpty)
            throw new ArgumentException("A logical plan requires at least one retained node.", nameof(nodes));
        if (normalized.Any(static node => node is null))
            throw new ArgumentException("Logical-plan nodes cannot contain null entries.", nameof(nodes));
        if (normalized.GroupBy(static node => node.Node).Any(static group => group.Count() > 1))
            throw new ArgumentException("A logical plan cannot repeat a canonical node identity.", nameof(nodes));

        Nodes = [.. normalized.OrderBy(static node => node.Node.Value, StringComparer.Ordinal)];
        RetainedNodes = [.. Nodes.Select(static node => node.Node)];
        var retained = RetainedNodes.ToHashSet();
        if (Nodes.SelectMany(static node => node.EffectiveInputs).Any(input => !retained.Contains(input)))
            throw new ArgumentException("Every effective input must identify a retained logical-plan node.", nameof(nodes));
        EvaluationOrder = CreateEvaluationOrder(Nodes);
    }

    /// <summary>Retained canonical nodes and effective inputs sorted by canonical node identity.</summary>
    public ImmutableArray<RelationQueryLogicalPlanNode> Nodes { get; }

    /// <summary>Retained canonical node identities (from <see cref="Nodes"/>).</summary>
    public ImmutableArray<QueryNodeId> RetainedNodes { get; }

    /// <summary>Dependency-first deterministic node evaluation order derived from <see cref="Nodes"/>.</summary>
    public ImmutableArray<QueryNodeId> EvaluationOrder { get; }

    static ImmutableArray<QueryNodeId> CreateEvaluationOrder(ImmutableArray<RelationQueryLogicalPlanNode> nodes)
    {
        var remainingInputs = nodes.ToDictionary(static node => node.Node, static node => node.EffectiveInputs.Length);
        Dictionary<QueryNodeId, List<QueryNodeId>> consumers = [];
        foreach (var node in nodes)
        {
            foreach (var input in node.EffectiveInputs)
            {
                if (!consumers.TryGetValue(input, out var downstream))
                {
                    downstream = [];
                    consumers.Add(input, downstream);
                }
                downstream.Add(node.Node);
            }
        }

        SortedSet<QueryNodeId> ready = new(
            remainingInputs.Where(static pair => pair.Value == 0).Select(static pair => pair.Key),
            QueryNodeIdComparer.Instance
            );
        List<QueryNodeId> order = new(nodes.Length);
        while (ready.Count != 0)
        {
            var next = ready.Min;
            ready.Remove(next);
            order.Add(next);
            if (!consumers.TryGetValue(next, out var downstream))
                continue;
            foreach (var consumer in downstream.Order(QueryNodeIdComparer.Instance))
            {
                remainingInputs[consumer]--;
                if (remainingInputs[consumer] == 0)
                    ready.Add(consumer);
            }
        }

        if (order.Count != nodes.Length)
            throw new ArgumentException("The effective logical-plan topology contains a cycle.", nameof(nodes));
        return [.. order];
    }

    sealed class QueryNodeIdComparer : IComparer<QueryNodeId>
    {
        public static QueryNodeIdComparer Instance { get; } = new();

        public int Compare(QueryNodeId left, QueryNodeId right) =>
            StringComparer.Ordinal.Compare(left.Value, right.Value);
    }
}

/// <summary>
/// Exact semantic snapshots and fingerprints consumed by static compilation.
/// </summary>
public sealed class RelationQueryCompilationProvenance
{
    /// <summary>Current target-independent static compiler profile identifier.</summary>
    public const string CurrentCompilerProfile = "relation-query-static/v1";

    internal RelationQueryCompilationProvenance(
        RelationQueryDocument definitionDocument,
        ImmutableArray<ShapeGraphDocument> shapeDocuments,
        RelationshipCatalogDocument? relationshipCatalogDocument,
        string compilerProfile = CurrentCompilerProfile)
    {
        DefinitionDocument = Guard.RequireNotNull(definitionDocument);
        CompilerProfile = Guard.RequireNotNullOrWhiteSpace(compilerProfile);
        var snapshots = shapeDocuments.IsDefault ? [] : shapeDocuments;
        if (snapshots.Any(static document => document is null))
            throw new ArgumentException("Compilation provenance cannot contain null shape documents.", nameof(shapeDocuments));
        ShapeDocuments =
        [
            .. snapshots
                .OrderBy(static document => document.Graph.Id.Value, StringComparer.Ordinal)
                .ThenBy(static document => document.SchemaVersion, StringComparer.Ordinal)
        ];
        RelationshipCatalogDocument = relationshipCatalogDocument;
    }

    /// <summary>Stable compiler profile that produced the plan.</summary>
    public string CompilerProfile { get; }

    /// <summary>Exact persisted relation/query definition document consumed by compilation.</summary>
    public RelationQueryDocument DefinitionDocument { get; }

    /// <summary>Semantic fingerprint declared by <see cref="DefinitionDocument"/>.</summary>
    public RelationQueryDefinitionFingerprint DefinitionFingerprint => DefinitionDocument.DefinitionFingerprint;

    /// <summary>Exact shape-graph documents consumed by compilation, sorted by graph identity.</summary>
    public ImmutableArray<ShapeGraphDocument> ShapeDocuments { get; }

    /// <summary>Exact relationship catalog document consumed by compilation, or <see langword="null"/>.</summary>
    public RelationshipCatalogDocument? RelationshipCatalogDocument { get; }

    /// <summary>Catalog fingerprint declared by <see cref="RelationshipCatalogDocument"/>, or <see langword="null"/>.</summary>
    public RelationshipCatalogFingerprint? RelationshipCatalogFingerprint =>
        RelationshipCatalogDocument?.CatalogFingerprint;
}

/// <summary>
/// Successful target-independent static compilation of one relation or query demand.
/// </summary>
public sealed class CompiledRelationQueryPlan
{
    internal CompiledRelationQueryPlan(
        RelationQueryCompilationDemand demand,
        RelationQueryCompilationDemandOrigin demandOrigin,
        RelationQueryLogicalPlan logicalPlan,
        RelationQueryRequirementGraph requirementGraph,
        RelationQueryExpressionAnalysisResult expressionAnalysis,
        ImmutableArray<RelationQueryExpressionSiteAnalysis> demandedExpressionSites,
        ImmutableArray<RelationQueryAggregateAssignmentReference> demandedAggregateAssignments,
        RelationQueryCompilationProvenance provenance)
    {
        Demand = Guard.RequireNotNull(demand);
        if (!Enum.IsDefined(demandOrigin))
            throw new ArgumentOutOfRangeException(nameof(demandOrigin), demandOrigin, "Unsupported compilation-demand origin.");
        DemandOrigin = demandOrigin;
        LogicalPlan = Guard.RequireNotNull(logicalPlan);
        RequirementGraph = Guard.RequireNotNull(requirementGraph);
        Provenance = Guard.RequireNotNull(provenance);
        ValidateLogicalPlan(Definition, logicalPlan, provenance);
        ValidateRetainedNodeReferences(logicalPlan, requirementGraph);
        ValidateDemand(Definition, demand, requirementGraph);
        ExecutionSlice = new(
            Definition,
            logicalPlan,
            requirementGraph,
            Guard.RequireNotNull(expressionAnalysis),
            demandedExpressionSites,
            demandedAggregateAssignments);
        InputContract = new(requirementGraph);
        Lineage = new(requirementGraph);
        DependencyManifest = new(requirementGraph);
    }

    /// <summary>Canonical relation/query definition interpreted by this plan.</summary>
    public RelationQueryDefinition Definition => Provenance.DefinitionDocument.Definition;

    /// <summary>Effective output demand compiled into the plan.</summary>
    public RelationQueryCompilationDemand Demand { get; }

    /// <summary>Whether <see cref="Demand"/> was explicitly supplied or selected by convention.</summary>
    public RelationQueryCompilationDemandOrigin DemandOrigin { get; }

    /// <summary>Retained canonical logical nodes and their evaluation order.</summary>
    public RelationQueryLogicalPlan LogicalPlan { get; }

    /// <summary>Canonical input-to-output requirement graph.</summary>
    public RelationQueryRequirementGraph RequirementGraph { get; }

    /// <summary>
    /// Explicit demand-scoped nodes, assignments, expression sites, binding metadata, and result terminals.
    /// </summary>
    public RelationQueryExecutionSlice ExecutionSlice { get; }

    /// <summary>Acquisition contract projected from <see cref="RequirementGraph"/>.</summary>
    public RelationQueryInputContract InputContract { get; }

    /// <summary>Output-oriented static lineage projected from <see cref="RequirementGraph"/>.</summary>
    public RelationQueryLineage Lineage { get; }

    /// <summary>Input-oriented inverse dependency manifest projected from <see cref="RequirementGraph"/>.</summary>
    public RelationQueryDependencyManifest DependencyManifest { get; }

    /// <summary>Exact semantic snapshots and compiler profile that produced this plan.</summary>
    public RelationQueryCompilationProvenance Provenance { get; }

    static void ValidateLogicalPlan(
        RelationQueryDefinition definition,
        RelationQueryLogicalPlan logicalPlan,
        RelationQueryCompilationProvenance provenance)
    {
        var nodes = definition.Body.Nodes.ToDictionary(static node => node.Id);
        var retained = logicalPlan.RetainedNodes.ToHashSet();
        var unknown = retained.Where(node => !nodes.ContainsKey(node)).ToArray();
        if (unknown.Length != 0)
            throw new ArgumentException("The logical plan retains nodes absent from the canonical definition.", nameof(logicalPlan));

        foreach (var plannedNode in logicalPlan.Nodes)
        {
            var canonicalInputs = nodes[plannedNode.Node].Inputs;
            if (plannedNode.Inputs.Length != canonicalInputs.Length)
            {
                throw new ArgumentException(
                    "A logical-plan node must retain one effective input for each canonical input position.",
                    nameof(logicalPlan));
            }

            for (var index = 0; index < canonicalInputs.Length; index++)
            {
                var canonicalInput = canonicalInputs[index];
                var input = plannedNode.Inputs[index];
                if (input.CanonicalInput != canonicalInput)
                {
                    throw new ArgumentException(
                        "Logical-plan input slots must preserve canonical input order and identity.",
                        nameof(logicalPlan));
                }

                var current = canonicalInput;
                foreach (var bypass in input.Bypasses)
                {
                    if (bypass.Node != current
                        || retained.Contains(bypass.Node)
                        || !nodes.TryGetValue(bypass.Node, out var bypassedNode)
                        || bypassedNode is not TraverseRelationshipQueryNode traversal)
                    {
                        throw new ArgumentException(
                            "Logical bypass evidence must follow omitted canonical traversal nodes contiguously.",
                            nameof(logicalPlan));
                    }

                    ValidateTraversalBypass(bypass, traversal, provenance);
                    current = traversal.Input;
                }

                if (current != input.EffectiveInput || !retained.Contains(input.EffectiveInput))
                {
                    throw new ArgumentException(
                        "A logical bypass chain must terminate at its declared retained effective input.",
                        nameof(logicalPlan));
                }
            }
        }
    }

    static void ValidateTraversalBypass(
        RelationQueryLogicalBypass bypass,
        TraverseRelationshipQueryNode traversal,
        RelationQueryCompilationProvenance provenance)
    {
        if (bypass.Kind != RelationQueryLogicalBypassKind.OptionalAtMostOneLeftRelationshipTraversal
            || traversal.JoinKind != JoinKind.Left
            || traversal.Requirement != QueryInputRequirement.Optional
            || traversal.Relationship != bypass.Relationship.Id
            || traversal.Direction != bypass.Direction
            || traversal.From != bypass.From
            || traversal.Result != bypass.Result)
        {
            throw new ArgumentException(
                "Logical bypass evidence does not match an optional left relationship traversal.",
                nameof(bypass));
        }

        var catalog = provenance.RelationshipCatalogDocument?.Catalog;
        if (catalog is null
            || !catalog.TryGetRelationship(bypass.Relationship.Id, out var catalogRelationship)
            || !Equals(catalogRelationship, bypass.Relationship))
        {
            throw new ArgumentException(
                "Logical traversal bypass evidence must match the exact retained catalog snapshot.",
                nameof(bypass));
        }

        var provenCardinality = bypass.Direction == RelationshipTraversalDirection.Inverse
            ? bypass.Relationship.InverseCardinality
            : ResolveForwardCardinality(bypass.Relationship, provenance.ShapeDocuments);
        if (provenCardinality != RelationshipTraversalCardinality.AtMostOne
            || bypass.Cardinality != provenCardinality)
        {
            throw new ArgumentException(
                "Logical traversal bypass requires exact at-most-one cardinality evidence.",
                nameof(bypass));
        }
    }

    static RelationshipTraversalCardinality ResolveForwardCardinality(
        RelationshipDefinition relationship,
        ImmutableArray<ShapeGraphDocument> shapeDocuments)
    {
        var graphs = shapeDocuments
            .Where(document => document.Graph.Id == relationship.SourceShape.GraphId)
            .Take(2)
            .ToArray();
        if (graphs.Length != 1
            || !graphs[0].Graph.TryGetShape(relationship.SourceShape, out var shape)
            || relationship.SourceReference.Segments is not [{ Kind: SegmentKind.Field, Segment: { } fieldName }])
        {
            throw new ArgumentException(
                "Forward traversal cardinality cannot be proven from the retained shape snapshot.",
                nameof(shapeDocuments));
        }

        var field = shape.Fields.SingleOrDefault(candidate =>
            string.Equals(candidate.Name.Value, fieldName, StringComparison.Ordinal));
        if (field is null)
        {
            throw new ArgumentException(
                "The retained source shape does not contain the relationship reference field.",
                nameof(shapeDocuments));
        }
        return relationship.GetForwardCardinality(field);
    }

    static void ValidateDemand(
        RelationQueryDefinition definition,
        RelationQueryCompilationDemand demand,
        RelationQueryRequirementGraph requirements)
    {
        switch (definition)
        {
            case CanonicalRelationDefinition relation:
                if (demand.Kind == RelationQueryCompilationDemandKind.QueryResults)
                    throw new ArgumentException("A relation cannot be compiled with query-result demand.", nameof(demand));
                if (requirements.Outputs.Any(output =>
                        output.Kind != RelationQueryOutputReferenceKind.Relation
                        || output.Relation != relation.Id
                        || output.Node != relation.Output.Node
                        || output.Shape != relation.Output.Shape))
                {
                    throw new ArgumentException("Requirement outputs do not match the canonical relation output.", nameof(requirements));
                }

                if (demand.Kind == RelationQueryCompilationDemandKind.RelationFields)
                {
                    var actual = requirements.Outputs
                        .Where(static output => output.Field is not null)
                        .Select(static output => output.Field!.Value)
                        .ToHashSet();
                    if (!actual.SetEquals(demand.RelationFields))
                        throw new ArgumentException("Requirement outputs do not match demanded relation fields.", nameof(requirements));
                }
                break;

            case QueryDefinition query:
                if (demand.Kind == RelationQueryCompilationDemandKind.RelationFields)
                    throw new ArgumentException("A query cannot be compiled with relation-field demand.", nameof(demand));

                var results = query.Results.ToDictionary(static result => result.Id);
                foreach (var output in requirements.Outputs)
                {
                    if (output.Kind != RelationQueryOutputReferenceKind.QueryResult
                        || output.QueryResult is not { } resultId
                        || !results.TryGetValue(resultId, out var result)
                        || output.Node != result.Input)
                    {
                        throw new ArgumentException("A requirement output does not match a canonical query result.", nameof(requirements));
                    }
                }

                var demandedResults = demand.Kind == RelationQueryCompilationDemandKind.AllDeclaredOutputs
                    ? query.Results.Select(static result => result.Id).ToHashSet()
                    : demand.QueryResults.Select(static result => result.Result).ToHashSet();
                var actualResults = requirements.Outputs
                    .Select(static output => output.QueryResult!.Value)
                    .ToHashSet();
                if (!actualResults.SetEquals(demandedResults))
                    throw new ArgumentException("Requirement outputs do not match demanded query results.", nameof(requirements));

                if (demand.Kind == RelationQueryCompilationDemandKind.QueryResults)
                {
                    foreach (var selected in demand.QueryResults.Where(static result =>
                                 result.Selection == RelationQueryFieldSelectionKind.SelectedFields))
                    {
                        var actual = requirements.Outputs
                            .Where(output => output.QueryResult == selected.Result && output.Field is not null)
                            .Select(static output => output.Field!.Value)
                            .ToHashSet();
                        if (!actual.SetEquals(selected.Fields))
                            throw new ArgumentException("Requirement outputs do not match demanded query-result fields.", nameof(requirements));
                    }
                }
                break;
        }
    }

    static void ValidateRetainedNodeReferences(
        RelationQueryLogicalPlan logicalPlan,
        RelationQueryRequirementGraph requirements)
    {
        var retained = logicalPlan.RetainedNodes.ToHashSet();
        var requiredRetained = requirements.Outputs.Select(static output => output.Node)
            .Concat(requirements.Inputs.SelectMany(static input => input switch
            {
                RelationQueryFieldInput field => [field.Producer],
                RelationQueryObservationIdentityInput identity => [identity.Producer],
                RelationQuerySourceSetInput source => [source.Source],
                RelationQueryRelationshipInput relationship => [relationship.Traversal],
                _ => ImmutableArray<QueryNodeId>.Empty
            }));
        var missing = requiredRetained.Distinct().Where(node => !retained.Contains(node)).ToArray();
        if (missing.Length != 0)
        {
            throw new ArgumentException(
                $"Requirement graph references {missing.Length} node(s) not retained by the logical plan.",
                nameof(requirements));
        }

        var attributableNodes = retained
            .Concat(logicalPlan.Nodes.SelectMany(static node =>
                node.Inputs.SelectMany(static input => input.Bypasses.Select(static bypass => bypass.Node))))
            .ToHashSet();
        var unattributedTraceNodes = requirements.Edges
            .SelectMany(static edge => edge.Traces)
            .SelectMany(static trace => trace.Steps)
            .Select(static step => step.Node)
            .Distinct()
            .Where(node => !attributableNodes.Contains(node))
            .ToArray();
        if (unattributedTraceNodes.Length != 0)
        {
            throw new ArgumentException(
                "Requirement traces reference nodes that are neither retained nor explicitly bypassed.",
                nameof(requirements));
        }
    }
}

/// <summary>
/// Structured result of attempting target-independent static compilation.
/// </summary>
public sealed class RelationQueryCompilationResult
{
    internal RelationQueryCompilationResult(
        CompiledRelationQueryPlan? plan,
        RelationQueryExpressionAnalysisResult? expressionAnalysis,
        DocumentValidationResult validation)
    {
        Plan = plan;
        ExpressionAnalysis = expressionAnalysis;
        Validation = Guard.RequireNotNull(validation);
        if (plan is not null && (expressionAnalysis is null || !validation.IsValid))
        {
            throw new ArgumentException(
                "A compiled plan requires expression analysis and validation without error diagnostics.",
                nameof(plan));
        }
        var hasCompletePlan = plan is not null && expressionAnalysis is not null;
        if (validation.IsValid != hasCompletePlan)
        {
            throw new ArgumentException(
                "Successful validation requires both a compiled plan and expression analysis; failures require an error diagnostic.",
                nameof(validation));
        }
    }

    /// <summary>Compiled plan, or <see langword="null"/> when compilation produced error diagnostics.</summary>
    public CompiledRelationQueryPlan? Plan { get; }

    /// <summary>
    /// Expression analysis consumed by compilation, or <see langword="null"/> when analysis could not be created.
    /// </summary>
    public RelationQueryExpressionAnalysisResult? ExpressionAnalysis { get; }

    /// <summary>Combined document, semantic, demand, and compilation validation.</summary>
    public DocumentValidationResult Validation { get; }

    /// <summary>Structured diagnostics emitted by compilation.</summary>
    public ImmutableArray<DocumentValidationDiagnostic> Diagnostics => Validation.Diagnostics;

    /// <summary>Whether compilation produced a complete plan without error diagnostics.</summary>
    public bool IsSuccessful => Plan is not null && ExpressionAnalysis is not null && Validation.IsValid;
}
