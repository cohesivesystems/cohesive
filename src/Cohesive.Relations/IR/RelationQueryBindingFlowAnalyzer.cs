using System.Collections.Immutable;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Model;

namespace Cohesive.Relations.IR;

/// <summary>
/// Computes the canonical value-binding flow shared by relation/query validation and expression analysis.
/// </summary>
static class RelationQueryBindingFlowAnalyzer
{
    /// <summary>
    /// Analyzes binding visibility, semantic shape, value type, and availability throughout a logical query graph.
    /// </summary>
    /// <param name="definition">Relation or query definition whose logical graph is analyzed.</param>
    /// <param name="relationshipCatalog">
    /// Optional relationship catalog used to resolve traversal result shapes. When omitted, traversal bindings
    /// remain visible with an unknown shape and no catalog-resolution diagnostics are emitted.
    /// </param>
    /// <returns>The deterministic binding-flow analysis.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public static RelationQueryBindingFlowAnalysis Analyze(
        RelationQueryDefinition definition,
        RelationshipCatalog? relationshipCatalog = null
        )
    {
        ArgumentNullException.ThrowIfNull(definition);

        if (definition.Body is null)
            return RelationQueryBindingFlowAnalysis.Empty;

        Context context = new(definition, relationshipCatalog);
        return context.Analyze();
    }

    sealed class Context(
        RelationQueryDefinition definition,
        RelationshipCatalog? relationshipCatalog)
    {
        readonly Dictionary<QueryNodeId, LogicalQueryNode> nodes = [];
        readonly Dictionary<QueryNodeId, RelationQueryBindingEnvironment> inputsByNode = [];
        readonly Dictionary<QueryNodeId, RelationQueryBindingEnvironment> outputsByNode = [];
        readonly HashSet<QueryNodeId> visiting = [];
        readonly HashSet<QueryNodeId> cycleReported = [];
        readonly List<DocumentValidationDiagnostic> structuralDiagnostics = [];
        readonly List<DocumentValidationDiagnostic> catalogDiagnostics = [];

        public RelationQueryBindingFlowAnalysis Analyze()
        {
            foreach (var group in (definition.Body.Nodes.IsDefault ? [] : definition.Body.Nodes)
                         .Where(static node => node is not null)
                         .GroupBy(static node => node.Id)
                         .OrderBy(static group => group.Key.Value, StringComparer.Ordinal))
            {
                var candidates = group.Take(2).ToArray();
                if (candidates.Length == 1)
                    nodes.Add(group.Key, candidates[0]);
            }

            foreach (var nodeId in nodes.Keys.OrderBy(static id => id.Value, StringComparer.Ordinal))
                _ = ResolveOutput(nodeId);

            return new(
                inputsByNode.ToImmutableDictionary(),
                outputsByNode.ToImmutableDictionary(),
                SortDiagnostics(structuralDiagnostics),
                SortDiagnostics(catalogDiagnostics));
        }

        RelationQueryBindingEnvironment ResolveOutput(QueryNodeId nodeId)
        {
            if (outputsByNode.TryGetValue(nodeId, out var cached))
                return cached;
            if (!nodes.TryGetValue(nodeId, out var node))
                return RelationQueryBindingEnvironment.Empty;

            if (!visiting.Add(nodeId))
            {
                if (cycleReported.Add(nodeId))
                {
                    AddStructural(
                        code: "relationQuery.node.cycle",
                        message: $"Logical query graph contains a cycle involving node '{nodeId.Value}'.",
                        location: NodeLocation(nodeId));
                }

                return RelationQueryBindingEnvironment.Empty;
            }

            var output = node switch
            {
                SourceQueryNode source => ResolveSource(source),
                FilterQueryNode filter => ResolvePreservingNode(filter.Id, filter.Input),
                TraverseRelationshipQueryNode traversal => ResolveTraversal(traversal),
                JoinQueryNode join => ResolveJoin(join),
                TemporalJoinQueryNode temporalJoin => ResolveTemporalJoin(temporalJoin),
                ExpandCollectionQueryNode expansion => ResolveExpansion(expansion),
                ProjectQueryNode project => ResolveProject(project),
                DistinctQueryNode distinct => ResolvePreservingNode(distinct.Id, distinct.Input),
                AggregateQueryNode aggregate => ResolveAggregate(aggregate),
                OrderQueryNode order => ResolvePreservingNode(order.Id, order.Input),
                PageQueryNode page => ResolvePreservingNode(page.Id, page.Input),
                _ => RelationQueryBindingEnvironment.Empty
            };

            visiting.Remove(nodeId);
            outputsByNode[nodeId] = output;
            return output;
        }

        RelationQueryBindingEnvironment ResolveSource(SourceQueryNode source)
        {
            inputsByNode[source.Id] = RelationQueryBindingEnvironment.Empty;
            return RelationQueryBindingEnvironment.Create(
                source.Binding,
                new(
                    Shape: source.Shape,
                    Type: null,
                    Availability: RelationQueryBindingAvailability.AlwaysPresent));
        }

        RelationQueryBindingEnvironment ResolvePreservingNode(QueryNodeId nodeId, QueryNodeId input)
        {
            var environment = ResolveOutput(input);
            inputsByNode[nodeId] = environment;
            return environment;
        }

        RelationQueryBindingEnvironment ResolveTraversal(TraverseRelationshipQueryNode traversal)
        {
            var input = ResolveOutput(traversal.Input);
            inputsByNode[traversal.Id] = input;
            var output = input.ToBuilder();

            if (!input.Contains(traversal.From))
            {
                AddStructural(
                    code: "relationQuery.traversal.sourceBindingMissing",
                    message: $"Relationship traversal '{traversal.Id.Value}' references binding '{traversal.From.Value}' that is not visible from its input.",
                    location: NodeLocation(traversal.Id));
            }

            if (input.Contains(traversal.Result))
            {
                AddStructural(
                    code: "relationQuery.traversal.resultBindingDuplicate",
                    message: $"Relationship traversal '{traversal.Id.Value}' redeclares visible binding '{traversal.Result.Value}'.",
                    location: NodeLocation(traversal.Id));
            }

            var availability = traversal.JoinKind == JoinKind.Left
                               && traversal.Requirement == QueryInputRequirement.Optional
                ? RelationQueryBindingAvailability.MayBeAbsent
                : RelationQueryBindingAvailability.AlwaysPresent;

            if (relationshipCatalog is null)
            {
                output.TryAdd(traversal.Result, new(null, null, availability));
                return output.ToEnvironment();
            }

            if (!relationshipCatalog.TryGetRelationship(traversal.Relationship, out var relationship))
            {
                AddCatalog(
                    code: "relationQuery.traversal.relationshipUnknown",
                    message: $"Relationship traversal '{traversal.Id.Value}' references unknown relationship '{traversal.Relationship.Value}'.",
                    location: $"{NodeLocation(traversal.Id)}/relationship");
                output.TryAdd(traversal.Result, new(null, null, availability));
                return output.ToEnvironment();
            }

            if (!Enum.IsDefined(traversal.Direction))
            {
                output.TryAdd(traversal.Result, new(null, null, availability));
                return output.ToEnvironment();
            }

            var expectedSource = traversal.Direction == RelationshipTraversalDirection.Forward
                ? relationship.SourceShape
                : relationship.TargetShape;
            var expectedResult = traversal.Direction == RelationshipTraversalDirection.Forward
                ? relationship.TargetShape
                : relationship.SourceShape;

            if (input.TryGetValue(traversal.From, out var actualSource))
            {
                if (actualSource.Shape is null)
                {
                    AddCatalog(
                        code: "relationQuery.traversal.sourceShapeUnknown",
                        message: $"Relationship traversal '{traversal.Id.Value}' starts from binding '{traversal.From.Value}' whose shape is not known.",
                        location: $"{NodeLocation(traversal.Id)}/from");
                }
                else if (actualSource.Shape.Value != expectedSource)
                {
                    AddCatalog(
                        code: "relationQuery.traversal.sourceShapeMismatch",
                        message: $"Relationship traversal '{traversal.Id.Value}' starts from shape '{actualSource.Shape.Value}', but {traversal.Direction} traversal of '{relationship.Id.Value}' requires '{expectedSource}'.",
                        location: $"{NodeLocation(traversal.Id)}/from");
                }
            }

            if (input.TryGetValue(traversal.Result, out var existingResult)
                && existingResult.Shape is { } existingShape
                && existingShape != expectedResult)
            {
                AddCatalog(
                    code: "relationQuery.traversal.resultShapeConflict",
                    message: $"Relationship traversal '{traversal.Id.Value}' would bind shape '{expectedResult}' to existing binding '{traversal.Result.Value}' with shape '{existingShape}'.",
                    location: $"{NodeLocation(traversal.Id)}/result");
            }
            else
            {
                output[traversal.Result] = new(expectedResult, null, availability);
            }

            return output.ToEnvironment();
        }

        RelationQueryBindingEnvironment ResolveJoin(JoinQueryNode join) =>
            ResolveJoin(
                join.Id,
                join.Left,
                join.Right,
                join.Kind,
                diagnosticPrefix: "relationQuery.join",
                displayName: "Join");

        RelationQueryBindingEnvironment ResolveTemporalJoin(TemporalJoinQueryNode join) =>
            ResolveJoin(
                join.Id,
                join.Left,
                join.Right,
                join.Kind,
                diagnosticPrefix: "relationQuery.temporalJoin",
                displayName: "Temporal join");

        RelationQueryBindingEnvironment ResolveJoin(
            QueryNodeId id,
            QueryNodeId leftInput,
            QueryNodeId rightInput,
            JoinKind kind,
            string diagnosticPrefix,
            string displayName)
        {
            var left = ResolveOutput(leftInput);
            var right = ResolveOutput(rightInput);
            var predicateInput = left.ToBuilder();
            foreach (var (binding, analysis) in right.Bindings)
            {
                if (!predicateInput.TryAdd(binding, analysis))
                {
                    AddStructural(
                        code: $"{diagnosticPrefix}.bindingCollision",
                        message: $"{displayName} '{id.Value}' receives binding '{binding.Value}' from both inputs.",
                        location: NodeLocation(id));
                }
            }

            // Join predicates are evaluated before outer-join null extension. Downstream expressions
            // consume the separately derived output environment below.
            inputsByNode[id] = predicateInput.ToEnvironment();

            var output = left.ToBuilder();
            var nullableRight = right.ToBuilder();
            if (kind is JoinKind.Right or JoinKind.Full)
                MarkMayBeAbsent(output, left.Bindings.Keys);
            if (kind is JoinKind.Left or JoinKind.Full)
                MarkMayBeAbsent(nullableRight, right.Bindings.Keys);

            foreach (var (binding, analysis) in nullableRight)
                output.TryAdd(binding, analysis);

            return output.ToEnvironment();
        }

        RelationQueryBindingEnvironment ResolveExpansion(ExpandCollectionQueryNode expansion)
        {
            var input = ResolveOutput(expansion.Input);
            inputsByNode[expansion.Id] = input;
            var output = input.ToBuilder();
            if (!output.TryAdd(
                    expansion.ItemBinding,
                    new(
                        Shape: expansion.ItemShape,
                        Type: expansion.ItemType,
                        Availability: RelationQueryBindingAvailability.AlwaysPresent)))
            {
                AddStructural(
                    code: "relationQuery.expandCollection.itemBindingDuplicate",
                    message: $"Collection-expansion node '{expansion.Id.Value}' redeclares visible binding '{expansion.ItemBinding.Value}'.",
                    location: NodeLocation(expansion.Id));
            }

            return output.ToEnvironment();
        }

        RelationQueryBindingEnvironment ResolveProject(ProjectQueryNode project)
        {
            inputsByNode[project.Id] = ResolveOutput(project.Input);
            return RelationQueryBindingEnvironment.Create(
                project.ResultBinding,
                new(
                    Shape: project.ResultShape,
                    Type: null,
                    Availability: RelationQueryBindingAvailability.AlwaysPresent));
        }

        RelationQueryBindingEnvironment ResolveAggregate(AggregateQueryNode aggregate)
        {
            inputsByNode[aggregate.Id] = ResolveOutput(aggregate.Input);
            return RelationQueryBindingEnvironment.Create(
                aggregate.ResultBinding,
                new(
                    Shape: aggregate.ResultShape,
                    Type: null,
                    Availability: RelationQueryBindingAvailability.AlwaysPresent));
        }

        static void MarkMayBeAbsent(
            IDictionary<ValueBindingId, RelationQueryBindingAnalysis> bindings,
            IEnumerable<ValueBindingId> affected)
        {
            foreach (var binding in affected)
            {
                if (bindings.TryGetValue(binding, out var analysis))
                {
                    bindings[binding] = analysis with
                    {
                        Availability = RelationQueryBindingAvailability.MayBeAbsent
                    };
                }
            }
        }

        void AddStructural(string code, string message, string location) =>
            structuralDiagnostics.Add(new(code, DiagnosticSeverity.Error, message, location));

        void AddCatalog(string code, string message, string location) =>
            catalogDiagnostics.Add(new(code, DiagnosticSeverity.Error, message, location));

        static ImmutableArray<DocumentValidationDiagnostic> SortDiagnostics(
            IEnumerable<DocumentValidationDiagnostic> diagnostics) =>
        [
            .. diagnostics
                .OrderBy(static diagnostic => diagnostic.Location, StringComparer.Ordinal)
                .ThenBy(static diagnostic => diagnostic.Code, StringComparer.Ordinal)
                .ThenBy(static diagnostic => diagnostic.Message, StringComparer.Ordinal)
        ];

        static string NodeLocation(QueryNodeId nodeId) => $"/definition/body/nodes/{nodeId.Value}";
    }
}

/// <summary>
/// Result of canonical relation/query binding-flow analysis.
/// </summary>
internal sealed class RelationQueryBindingFlowAnalysis
{
    /// <summary>An empty binding-flow result.</summary>
    public static RelationQueryBindingFlowAnalysis Empty { get; } = new([], [], [], []);

    /// <summary>Creates a binding-flow result.</summary>
    /// <param name="inputsByNode">Expression-evaluation environment at each logical node.</param>
    /// <param name="outputsByNode">Output environment at each logical node.</param>
    /// <param name="structuralDiagnostics">Binding-topology diagnostics independent of a relationship catalog.</param>
    /// <param name="catalogDiagnostics">Diagnostics produced while resolving relationship shapes.</param>
    public RelationQueryBindingFlowAnalysis(
        ImmutableDictionary<QueryNodeId, RelationQueryBindingEnvironment> inputsByNode,
        ImmutableDictionary<QueryNodeId, RelationQueryBindingEnvironment> outputsByNode,
        ImmutableArray<DocumentValidationDiagnostic> structuralDiagnostics,
        ImmutableArray<DocumentValidationDiagnostic> catalogDiagnostics)
    {
        InputsByNode = inputsByNode;
        OutputsByNode = outputsByNode;
        StructuralDiagnostics = structuralDiagnostics.IsDefault ? [] : structuralDiagnostics;
        CatalogDiagnostics = catalogDiagnostics.IsDefault ? [] : catalogDiagnostics;
        BindingShapes =
        [
            .. OutputsByNode
                .OrderBy(static entry => entry.Key.Value, StringComparer.Ordinal)
                .SelectMany(static entry => entry.Value.Bindings
                    .OrderBy(static binding => binding.Key.Value, StringComparer.Ordinal)
                    .Select(binding => new RelationQueryBindingShape(
                        entry.Key,
                        binding.Key,
                        binding.Value.Shape,
                        binding.Value.Availability)))
        ];
    }

    /// <summary>Expression-evaluation environments keyed by logical node.</summary>
    public ImmutableDictionary<QueryNodeId, RelationQueryBindingEnvironment> InputsByNode { get; }

    /// <summary>Output environments keyed by logical node.</summary>
    public ImmutableDictionary<QueryNodeId, RelationQueryBindingEnvironment> OutputsByNode { get; }

    /// <summary>Binding-topology diagnostics independent of relationship resolution.</summary>
    public ImmutableArray<DocumentValidationDiagnostic> StructuralDiagnostics { get; }

    /// <summary>Relationship-catalog resolution diagnostics.</summary>
    public ImmutableArray<DocumentValidationDiagnostic> CatalogDiagnostics { get; }

    /// <summary>Public-compatible flattened shape and availability view of every node output.</summary>
    public ImmutableArray<RelationQueryBindingShape> BindingShapes { get; }

    /// <summary>Gets the expression-evaluation environment for a logical node.</summary>
    /// <param name="node">Logical node identifier.</param>
    /// <returns>The node's evaluation environment, or an empty environment when the node is unknown.</returns>
    public RelationQueryBindingEnvironment GetInput(QueryNodeId node) =>
        InputsByNode.GetValueOrDefault(node, RelationQueryBindingEnvironment.Empty);

    /// <summary>Gets the output binding environment for a logical node.</summary>
    /// <param name="node">Logical node identifier.</param>
    /// <returns>The node's output environment, or an empty environment when the node is unknown.</returns>
    public RelationQueryBindingEnvironment GetOutput(QueryNodeId node) =>
        OutputsByNode.GetValueOrDefault(node, RelationQueryBindingEnvironment.Empty);
}

/// <summary>
/// Immutable binding environment used while analyzing relation/query expressions.
/// </summary>
sealed class RelationQueryBindingEnvironment
{
    /// <summary>An empty binding environment.</summary>
    public static RelationQueryBindingEnvironment Empty { get; } = new([]);

    /// <summary>Creates a binding environment.</summary>
    /// <param name="bindings">Bindings visible in the environment.</param>
    public RelationQueryBindingEnvironment(ImmutableDictionary<ValueBindingId, RelationQueryBindingAnalysis> bindings)
    {
        Bindings = bindings;
    }

    /// <summary>Bindings visible in the environment.</summary>
    public ImmutableDictionary<ValueBindingId, RelationQueryBindingAnalysis> Bindings { get; }

    /// <summary>Number of visible bindings.</summary>
    public int Count => Bindings.Count;

    /// <summary>Tests whether a binding is visible.</summary>
    /// <param name="binding">Binding identifier.</param>
    /// <returns><see langword="true"/> when the binding is visible; otherwise <see langword="false"/>.</returns>
    public bool Contains(ValueBindingId binding) => Bindings.ContainsKey(binding);

    /// <summary>Looks up a visible binding.</summary>
    /// <param name="binding">Binding identifier.</param>
    /// <param name="analysis">Resolved binding analysis when present.</param>
    /// <returns><see langword="true"/> when the binding is visible; otherwise <see langword="false"/>.</returns>
    public bool TryGetValue(ValueBindingId binding, out RelationQueryBindingAnalysis analysis) =>
        Bindings.TryGetValue(binding, out analysis);

    /// <summary>Creates a mutable copy used to derive another environment.</summary>
    /// <returns>A mutable binding map initialized from this environment.</returns>
    public Dictionary<ValueBindingId, RelationQueryBindingAnalysis> ToBuilder() => new(Bindings);

    /// <summary>Creates a single-binding environment.</summary>
    /// <param name="binding">Visible binding identifier.</param>
    /// <param name="analysis">Binding analysis.</param>
    /// <returns>The single-binding environment.</returns>
    public static RelationQueryBindingEnvironment Create(
        ValueBindingId binding,
        RelationQueryBindingAnalysis analysis) =>
        new(ImmutableDictionary<ValueBindingId, RelationQueryBindingAnalysis>.Empty.Add(binding, analysis));
}

/// <summary>
/// Semantic information known for one binding at a point in relation/query flow.
/// </summary>
/// <param name="Shape">Graph-qualified shape, when the binding represents a shaped value.</param>
/// <param name="Type">Semantic value type, including expanded collection item types.</param>
/// <param name="Availability">Whether a row may preserve the binding in an absent state.</param>
readonly record struct RelationQueryBindingAnalysis(
    QualifiedShapeId? Shape,
    TypeRef? Type,
    RelationQueryBindingAvailability Availability);

static class RelationQueryBindingEnvironmentBuilderExtensions
{
    /// <summary>Materializes a mutable binding map as an immutable environment.</summary>
    /// <param name="bindings">Mutable binding map.</param>
    /// <returns>An immutable binding environment.</returns>
    public static RelationQueryBindingEnvironment ToEnvironment(this Dictionary<ValueBindingId, RelationQueryBindingAnalysis> bindings) =>
        new([..bindings]);
}
