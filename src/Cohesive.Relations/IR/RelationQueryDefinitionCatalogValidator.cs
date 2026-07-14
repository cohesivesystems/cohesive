using System.Collections.Immutable;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Model;
using Cohesive.Relations.Serialization;

namespace Cohesive.Relations.IR;

public static partial class RelationQueryDefinitionValidator
{
    /// <summary>
    /// Validates against a persisted catalog snapshot and retains that exact document and fingerprint
    /// for downstream compiler provenance.
    /// </summary>
    /// <param name="definition">Canonical relation or query definition to validate.</param>
    /// <param name="catalogDocument">Exact persisted relationship catalog snapshot.</param>
    /// <returns>A catalog-bound validation result retaining the consumed snapshot.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="definition"/> or <paramref name="catalogDocument"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The catalog contains a value with no canonical relationship catalog JSON encoding.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// The catalog contains a runtime type unsupported by canonical relationship catalog serialization.
    /// </exception>
    public static RelationQueryCatalogValidationResult ValidateWithCatalog(
        RelationQueryDefinition definition,
        RelationshipCatalogDocument catalogDocument)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(catalogDocument);

        var documentValidation = RelationshipCatalogDocumentSemanticValidator.Validate(catalogDocument);
        if (catalogDocument.Catalog is null)
        {
            return new(
                catalogDocument,
                DocumentValidationResult.Combine(documentValidation, Validate(definition)),
                bindingShapes: []);
        }

        var analysis = AnalyzeWithCatalog(definition, catalogDocument.Catalog);
        return new(
            catalogDocument,
            DocumentValidationResult.Combine(documentValidation, analysis.Validation),
            analysis.BindingShapes);
    }

    static CatalogValidationOutcome AnalyzeWithCatalog(
        RelationQueryDefinition definition,
        RelationshipCatalog relationshipCatalog)
    {
        var structural = Validate(definition);
        if (definition.Body is null)
        {
            return new(
                structural,
                BindingShapes: []);
        }

        CatalogValidationContext context = new(definition, relationshipCatalog);
        context.Validate();
        return new(
            DocumentValidationResult.Combine(
                structural,
                DocumentValidationResult.FromDiagnostics(context.Diagnostics)),
            context.GetBindingShapes());
    }

    readonly record struct CatalogValidationOutcome(
        DocumentValidationResult Validation,
        ImmutableArray<RelationQueryBindingShape> BindingShapes);

    sealed class CatalogValidationContext(
        RelationQueryDefinition definition,
        RelationshipCatalog relationshipCatalog)
    {
        readonly Dictionary<QueryNodeId, LogicalQueryNode> nodes = [];
        readonly Dictionary<QueryNodeId, Dictionary<ValueBindingId, BindingAnalysis>> bindingsByNode = [];
        readonly HashSet<QueryNodeId> visiting = [];

        public List<DocumentValidationDiagnostic> Diagnostics { get; } = [];

        public void Validate()
        {
            foreach (var node in definition.Body.Nodes.IsDefault ? [] : definition.Body.Nodes)
                nodes.TryAdd(node.Id, node);

            foreach (var node in definition.Body.Nodes.IsDefault ? [] : definition.Body.Nodes)
                _ = ResolveBindings(node.Id);
        }

        public ImmutableArray<RelationQueryBindingShape> GetBindingShapes() =>
        [
            .. bindingsByNode
                .OrderBy(static entry => entry.Key.Value, StringComparer.Ordinal)
                .SelectMany(static entry => entry.Value
                    .OrderBy(static binding => binding.Key.Value, StringComparer.Ordinal)
                    .Select(binding => new RelationQueryBindingShape(
                        entry.Key,
                        binding.Key,
                        binding.Value.Shape,
                        binding.Value.Availability)))
        ];

        Dictionary<ValueBindingId, BindingAnalysis> ResolveBindings(QueryNodeId nodeId)
        {
            if (bindingsByNode.TryGetValue(nodeId, out var cached))
                return cached;
            if (!nodes.TryGetValue(nodeId, out var node) || !visiting.Add(nodeId))
                return [];

            Dictionary<ValueBindingId, BindingAnalysis> bindings = node switch
            {
                SourceQueryNode source => new()
                {
                    [source.Binding] = new(
                        source.Shape,
                        RelationQueryBindingAvailability.AlwaysPresent)
                },
                FilterQueryNode filter => Copy(filter.Input),
                TraverseRelationshipQueryNode traversal => ResolveTraversal(traversal),
                JoinQueryNode join => ResolveJoin(join),
                ExpandCollectionQueryNode expansion => ResolveExpansion(expansion),
                ProjectQueryNode project => new()
                {
                    [project.ResultBinding] = new(
                        project.ResultShape,
                        RelationQueryBindingAvailability.AlwaysPresent)
                },
                DistinctQueryNode distinct => Copy(distinct.Input),
                AggregateQueryNode aggregate => new()
                {
                    [aggregate.ResultBinding] = new(
                        aggregate.ResultShape,
                        RelationQueryBindingAvailability.AlwaysPresent)
                },
                OrderQueryNode order => Copy(order.Input),
                PageQueryNode page => Copy(page.Input),
                _ => []
            };

            visiting.Remove(nodeId);
            bindingsByNode[nodeId] = bindings;
            return bindings;
        }

        Dictionary<ValueBindingId, BindingAnalysis> ResolveTraversal(
            TraverseRelationshipQueryNode traversal)
        {
            var bindings = Copy(traversal.Input);
            var availability = traversal.JoinKind == JoinKind.Left
                               && traversal.Requirement == QueryInputRequirement.Optional
                ? RelationQueryBindingAvailability.MayBeAbsent
                : RelationQueryBindingAvailability.AlwaysPresent;
            if (!relationshipCatalog.TryGetRelationship(traversal.Relationship, out var relationship))
            {
                Add(
                    "relationQuery.traversal.relationshipUnknown",
                    $"Relationship traversal '{traversal.Id.Value}' references unknown relationship '{traversal.Relationship.Value}'.",
                    $"{NodeLocation(traversal.Id)}/relationship");
                bindings.TryAdd(traversal.Result, new(null, availability));
                return bindings;
            }

            if (!Enum.IsDefined(traversal.Direction))
            {
                bindings.TryAdd(traversal.Result, new(null, availability));
                return bindings;
            }

            var expectedSource = traversal.Direction == RelationshipTraversalDirection.Forward
                ? relationship.SourceShape
                : relationship.TargetShape;
            var expectedResult = traversal.Direction == RelationshipTraversalDirection.Forward
                ? relationship.TargetShape
                : relationship.SourceShape;

            if (bindings.TryGetValue(traversal.From, out var actualSource))
            {
                if (actualSource.Shape is null)
                {
                    Add(
                        "relationQuery.traversal.sourceShapeUnknown",
                        $"Relationship traversal '{traversal.Id.Value}' starts from binding '{traversal.From.Value}' whose shape is not known.",
                        $"{NodeLocation(traversal.Id)}/from");
                }
                else if (actualSource.Shape.Value != expectedSource)
                {
                    Add(
                        "relationQuery.traversal.sourceShapeMismatch",
                        $"Relationship traversal '{traversal.Id.Value}' starts from shape '{actualSource.Shape.Value}', but {traversal.Direction} traversal of '{relationship.Id.Value}' requires '{expectedSource}'.",
                        $"{NodeLocation(traversal.Id)}/from");
                }
            }

            if (bindings.TryGetValue(traversal.Result, out var existingResult)
                && existingResult.Shape is { } existingShape
                && existingShape != expectedResult)
            {
                Add(
                    "relationQuery.traversal.resultShapeConflict",
                    $"Relationship traversal '{traversal.Id.Value}' would bind shape '{expectedResult}' to existing binding '{traversal.Result.Value}' with shape '{existingShape}'.",
                    $"{NodeLocation(traversal.Id)}/result");
            }
            else
            {
                bindings[traversal.Result] = new(expectedResult, availability);
            }

            return bindings;
        }

        Dictionary<ValueBindingId, BindingAnalysis> ResolveJoin(JoinQueryNode join)
        {
            var left = Copy(join.Left);
            var right = Copy(join.Right);
            if (join.Kind is JoinKind.Right or JoinKind.Full)
                MarkMayBeAbsent(left);
            if (join.Kind is JoinKind.Left or JoinKind.Full)
                MarkMayBeAbsent(right);

            foreach (var (binding, analysis) in right)
                left.TryAdd(binding, analysis);
            return left;
        }

        Dictionary<ValueBindingId, BindingAnalysis> ResolveExpansion(
            ExpandCollectionQueryNode expansion)
        {
            var bindings = Copy(expansion.Input);
            bindings.TryAdd(
                expansion.ItemBinding,
                new(null, RelationQueryBindingAvailability.AlwaysPresent));
            return bindings;
        }

        Dictionary<ValueBindingId, BindingAnalysis> Copy(QueryNodeId input) =>
            new(ResolveBindings(input));

        static void MarkMayBeAbsent(Dictionary<ValueBindingId, BindingAnalysis> bindings)
        {
            foreach (var (binding, analysis) in bindings.ToArray())
                bindings[binding] = analysis with
                {
                    Availability = RelationQueryBindingAvailability.MayBeAbsent
                };
        }

        void Add(string code, string message, string location) => Diagnostics.Add(new(
            Code: code,
            Severity: DiagnosticSeverity.Error,
            Message: message,
            Location: location));

        static string NodeLocation(QueryNodeId nodeId) => $"/definition/body/nodes/{nodeId.Value}";

        readonly record struct BindingAnalysis(
            QualifiedShapeId? Shape,
            RelationQueryBindingAvailability Availability);
    }
}

/// <summary>
/// Whether a visible binding is guaranteed to have a value for every row emitted by a logical node.
/// </summary>
public enum RelationQueryBindingAvailability
{
    /// <summary>Every emitted row contains the binding.</summary>
    AlwaysPresent = 0,

    /// <summary>An emitted row may preserve the binding in an absent state.</summary>
    MayBeAbsent = 1
}

/// <summary>
/// Shape resolved for one visible value binding at the output of a logical query node.
/// </summary>
/// <param name="Node">Logical node whose output binding environment was analyzed.</param>
/// <param name="Binding">Value binding visible at the node output.</param>
/// <param name="Shape">
/// Graph-qualified semantic shape, or <see langword="null"/> when the binding is visible but its
/// shape cannot be established statically.
/// </param>
/// <param name="Availability">Whether the binding may be absent on an emitted row.</param>
public readonly record struct RelationQueryBindingShape(
    QueryNodeId Node,
    ValueBindingId Binding,
    QualifiedShapeId? Shape,
    RelationQueryBindingAvailability Availability);

/// <summary>
/// Catalog-aware relation/query validation result retaining the exact catalog snapshot consumed.
/// </summary>
public sealed class RelationQueryCatalogValidationResult
{
    /// <summary>Creates a catalog-bound validation result.</summary>
    /// <param name="catalogDocument">Exact catalog document consumed by validation.</param>
    /// <param name="validation">Combined document, catalog, and relation/query diagnostics.</param>
    /// <param name="bindingShapes">Shape analysis for bindings visible at each logical node output.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="catalogDocument"/> or <paramref name="validation"/> is <see langword="null"/>.
    /// </exception>
    internal RelationQueryCatalogValidationResult(
        RelationshipCatalogDocument catalogDocument,
        DocumentValidationResult validation,
        ImmutableArray<RelationQueryBindingShape> bindingShapes = default)
    {
        CatalogDocument = Guard.RequireNotNull(catalogDocument);
        Validation = Guard.RequireNotNull(validation);
        BindingShapes = bindingShapes.IsDefault
            ? []
            :
            [
                .. bindingShapes
                    .OrderBy(static binding => binding.Node.Value, StringComparer.Ordinal)
                    .ThenBy(static binding => binding.Binding.Value, StringComparer.Ordinal)
            ];
    }

    /// <summary>Exact versioned catalog snapshot consumed by validation.</summary>
    public RelationshipCatalogDocument CatalogDocument { get; }

    /// <summary>Catalog content fingerprint consumed by validation, or <see langword="null"/> when absent.</summary>
    public RelationshipCatalogFingerprint? CatalogFingerprint => CatalogDocument.CatalogFingerprint;

    /// <summary>Combined document, catalog, and relation/query validation result.</summary>
    public DocumentValidationResult Validation { get; }

    /// <summary>
    /// Deterministically ordered shape analysis for bindings visible at each logical node output.
    /// </summary>
    public ImmutableArray<RelationQueryBindingShape> BindingShapes { get; }

    /// <summary>Structured validation diagnostics.</summary>
    public IReadOnlyList<DocumentValidationDiagnostic> Diagnostics => Validation.Diagnostics;

    /// <summary>Whether the catalog document and relation/query definition are valid together.</summary>
    public bool IsValid => Validation.IsValid;
}
