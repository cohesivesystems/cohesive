using System.Collections.Immutable;
using Cohesive.Model.Serialization;
using Cohesive.Relations.IR;
using Cohesive.Relations.Model;
using Cohesive.Relations.Serialization;

namespace Cohesive.Relations.Compilation;

/// <summary>
/// Compiles canonical relation/query IR into a deterministic, target-independent requirement plan.
/// </summary>
/// <remarks>
/// Static compilation determines the exact semantic inputs, retained logical nodes, value lineage,
/// inverse dependencies, and provenance required by an output demand. It does not choose a backend,
/// physical placement, join algorithm, batching strategy, or runtime representation. Backend
/// compilers consume the resulting plan and either realize its semantics or emit capability
/// diagnostics.
/// </remarks>
public static class RelationQueryStaticCompiler
{
    /// <summary>Compiles one persisted relation or query and output demand.</summary>
    /// <param name="request">Exact semantic snapshots and output demand to compile.</param>
    /// <returns>
    /// A successful target-independent plan, or structured diagnostics and any expression analysis
    /// that could be completed when compilation fails.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// A supplied semantic document contains a value with no canonical JSON encoding.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// A supplied semantic document contains a runtime type unsupported by its canonical serializer.
    /// </exception>
    public static RelationQueryCompilationResult Compile(RelationQueryCompilationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        List<DocumentValidationDiagnostic> diagnostics = [];
        AddDiagnostics(diagnostics, RelationQueryDocumentSemanticValidator.Validate(request.DefinitionDocument));

        var shapeDocumentsValid = true;
        foreach (var shapeDocument in request.ShapeDocuments)
        {
            var prefix = $"/shapeGraphs/{Encode(shapeDocument.Graph?.Id.Value)}";
            var shapeValidation = ShapeGraphDocumentSemanticValidator.Validate(shapeDocument);
            shapeDocumentsValid &= shapeValidation.IsValid;
            AddDiagnostics(
                diagnostics,
                PrefixLocations(
                    shapeValidation,
                    prefix));
        }

        var duplicateGraphIds = request.ShapeDocuments
            .Where(static document => document.Graph is not null)
            .GroupBy(static document => document.Graph.Id)
            .Where(static group => group.Count() > 1)
            .OrderBy(static group => group.Key.Value, StringComparer.Ordinal)
            .ToArray();
        foreach (var duplicate in duplicateGraphIds)
        {
            diagnostics.Add(new(
                Code: RelationQueryCompilationDiagnosticCodes.ShapeGraphDuplicate,
                Severity: DiagnosticSeverity.Error,
                Message: $"Shape graph '{duplicate.Key.Value}' is supplied more than once.",
                Location: $"/shapeGraphs/{Encode(duplicate.Key.Value)}"
                )
            );
        }
        shapeDocumentsValid &= duplicateGraphIds.Length == 0;

        if (request.RelationshipCatalogDocument is { } catalogDocument)
        {
            var catalogValidation = RelationshipCatalogDocumentSemanticValidator.Validate(catalogDocument);
            AddDiagnostics(
                diagnostics,
                catalogValidation);
            if (catalogValidation.IsValid
                && shapeDocumentsValid
                && catalogDocument.Catalog is not null)
            {
                AddDiagnostics(
                    diagnostics,
                    RelationshipCatalogValidator.Validate(
                        catalogDocument.Catalog,
                        request.ShapeDocuments.Select(static document => document.Graph)));
            }
        }

        RelationQueryExpressionAnalysisResult? expressionAnalysis = null;
        if (!diagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            && request.DefinitionDocument.Definition is { } definition)
        {
            var shapeGraphs = request.ShapeDocuments.Select(static document => document.Graph);
            expressionAnalysis = request.RelationshipCatalogDocument is { } catalog
                ? RelationQueryExpressionAnalyzer.AnalyzeWithCatalog(definition, catalog, shapeGraphs)
                : RelationQueryExpressionAnalyzer.Analyze(definition, shapeGraphs);
            AddDiagnostics(diagnostics, expressionAnalysis.Validation);
        }

        var validation = NormalizeValidation(diagnostics);
        if (expressionAnalysis is null || !validation.IsValid)
        {
            return new(
                plan: null,
                expressionAnalysis,
                EnsureFailureDiagnostic(validation, expressionAnalysis));
        }

        RelationQueryRequirementGraphBuilder builder = new(
            request.DefinitionDocument.Definition,
            request.ShapeDocuments,
            request.RelationshipCatalogDocument,
            request.Demand,
            expressionAnalysis
            );
        var build = builder.Build();
        AddDiagnostics(diagnostics, build.Validation);
        validation = NormalizeValidation(diagnostics);
        if (!validation.IsValid || build.Graph is null)
        {
            return new(
                plan: null,
                expressionAnalysis,
                EnsureFailureDiagnostic(validation, expressionAnalysis));
        }

        var logicalPlan = CreateLogicalPlan(
            request.DefinitionDocument.Definition,
            build.RetainedNodes,
            build.Bypasses);
        var provenance = new RelationQueryCompilationProvenance(
            request.DefinitionDocument,
            request.ShapeDocuments,
            request.RelationshipCatalogDocument);
        var plan = new CompiledRelationQueryPlan(
            request.Demand,
            request.DemandOrigin,
            logicalPlan,
            build.Graph,
            expressionAnalysis,
            build.DemandedExpressionSites,
            build.DemandedAggregateAssignments,
            provenance);
        return new(plan, expressionAnalysis, validation);
    }

    static RelationQueryLogicalPlan CreateLogicalPlan(
        RelationQueryDefinition definition,
        ImmutableArray<QueryNodeId> retainedNodes,
        ImmutableArray<RelationQueryLogicalBypass> bypasses)
    {
        var retained = retainedNodes.ToHashSet();
        var nodes = definition.Body.Nodes.ToDictionary(static node => node.Id);
        var bypassesByNode = bypasses.ToDictionary(static bypass => bypass.Node);
        List<RelationQueryLogicalPlanNode> planNodes = [];
        foreach (var nodeId in retained.OrderBy(static node => node.Value, StringComparer.Ordinal))
        {
            var canonicalNode = nodes[nodeId];
            List<RelationQueryLogicalPlanInput> inputs = [];
            foreach (var canonicalInput in canonicalNode.Inputs)
            {
                var effectiveInput = canonicalInput;
                List<RelationQueryLogicalBypass> chain = [];
                while (!retained.Contains(effectiveInput))
                {
                    if (!bypassesByNode.TryGetValue(effectiveInput, out var bypass)
                        || nodes[effectiveInput] is not TraverseRelationshipQueryNode traversal)
                    {
                        throw new InvalidOperationException(
                            $"Retained node '{nodeId.Value}' has non-retained input '{effectiveInput.Value}' without an explicit transparent bypass.");
                    }

                    chain.Add(bypass);
                    effectiveInput = traversal.Input;
                }

                inputs.Add(new(canonicalInput, effectiveInput, [.. chain]));
            }

            planNodes.Add(new(nodeId, [.. inputs]));
        }

        return new([.. planNodes]);
    }

    static DocumentValidationResult PrefixLocations(
        DocumentValidationResult validation,
        string prefix) =>
        DocumentValidationResult.FromDiagnostics(
            validation.Diagnostics.Select(diagnostic => diagnostic with
            {
                Location = diagnostic.Location is null
                    ? prefix
                    : $"{prefix}{diagnostic.Location}"
            }));

    static void AddDiagnostics(
        ICollection<DocumentValidationDiagnostic> target,
        DocumentValidationResult validation)
    {
        foreach (var diagnostic in validation.Diagnostics)
            target.Add(diagnostic);
    }

    static DocumentValidationResult NormalizeValidation(
        IEnumerable<DocumentValidationDiagnostic> diagnostics) =>
        DocumentValidationResult.FromDiagnostics(
            diagnostics
                .Distinct()
                .OrderBy(static diagnostic => diagnostic.Location, StringComparer.Ordinal)
                .ThenBy(static diagnostic => diagnostic.SchemaLocation, StringComparer.Ordinal)
                .ThenBy(static diagnostic => diagnostic.Code, StringComparer.Ordinal)
                .ThenBy(static diagnostic => (int)diagnostic.Severity)
                .ThenBy(static diagnostic => diagnostic.Message, StringComparer.Ordinal));

    static DocumentValidationResult EnsureFailureDiagnostic(
        DocumentValidationResult validation,
        RelationQueryExpressionAnalysisResult? expressionAnalysis)
    {
        if (!validation.IsValid)
            return validation;

        return NormalizeValidation(
        [
            .. validation.Diagnostics,
            new(
                Code: RelationQueryCompilationDiagnosticCodes.PlanUnavailable,
                Severity: DiagnosticSeverity.Error,
                Message: expressionAnalysis is null
                    ? "Static compilation could not create expression analysis."
                    : "Static compilation could not create a complete requirement plan.",
                Location: "/definition")
        ]);
    }

    static string Encode(string? value) =>
        Uri.EscapeDataString(string.IsNullOrWhiteSpace(value) ? "missing" : value);
}

/// <summary>Stable diagnostic codes emitted by target-independent relation/query compilation.</summary>
public static class RelationQueryCompilationDiagnosticCodes
{
    /// <summary>More than one supplied shape document declares the same graph identity.</summary>
    public const string ShapeGraphDuplicate = "relationQuery.compilation.shapeGraph.duplicate";

    /// <summary>The requested demand kind does not apply to the supplied definition kind.</summary>
    public const string DemandKindMismatch = "relationQuery.compilation.demand.kindMismatch";

    /// <summary>A requested relation field does not belong to the relation output shape.</summary>
    public const string RelationFieldInvalid = "relationQuery.compilation.demand.relationFieldInvalid";

    /// <summary>A requested query result is not declared by the query.</summary>
    public const string QueryResultUnknown = "relationQuery.compilation.demand.queryResultUnknown";

    /// <summary>A requested query-result field does not belong to the result shape.</summary>
    public const string QueryFieldInvalid = "relationQuery.compilation.demand.queryFieldInvalid";

    /// <summary>A demanded output field has no producing projection or aggregate assignment.</summary>
    public const string OutputFieldUnassigned = "relationQuery.compilation.outputField.unassigned";

    /// <summary>Overlapping assignments make a demanded output field ambiguous.</summary>
    public const string OutputFieldAmbiguous = "relationQuery.compilation.outputField.ambiguous";

    /// <summary>A demanded relationship traversal cannot be resolved without a catalog entry.</summary>
    public const string RelationshipUnavailable = "relationQuery.compilation.relationship.unavailable";

    /// <summary>A field demand could not be routed to the node that introduces its binding.</summary>
    public const string BindingRouteUnavailable = "relationQuery.compilation.binding.routeUnavailable";

    /// <summary>Precise nested-field pruning was unavailable and a conservative requirement was retained.</summary>
    public const string FieldSelectionConservative = "relationQuery.compilation.fieldSelection.conservative";

    /// <summary>A complete-row operation could not enumerate an unknown binding shape.</summary>
    public const string CompleteValueUnavailable = "relationQuery.compilation.completeValue.unavailable";

    /// <summary>An expanded item field could not be mapped precisely to its source collection.</summary>
    public const string ExpandedItemUnavailable = "relationQuery.compilation.expand.itemUnavailable";

    /// <summary>Expression analysis omitted a site required by static demand propagation.</summary>
    public const string ExpressionSiteUnavailable = "relationQuery.compilation.expressionSite.unavailable";

    /// <summary>Compilation encountered an unsupported logical node.</summary>
    public const string NodeUnsupported = "relationQuery.compilation.node.unsupported";

    /// <summary>Compilation could not produce a complete plan despite otherwise successful validation.</summary>
    public const string PlanUnavailable = "relationQuery.compilation.plan.unavailable";
}
