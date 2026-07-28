using System.Collections.Immutable;
using System.Globalization;
using Cohesive.Model.Expressions;
using Cohesive.Model.Serialization;
using Cohesive.Relations.IR;
using Cohesive.Relations.Model;
using Cohesive.Relations.Serialization;
using IRRelationDefinition = Cohesive.Relations.IR.RelationDefinition;

namespace Cohesive.Relations.Compilation;

sealed record RelationQueryRequirementGraphBuildResult(
    RelationQueryRequirementGraph? Graph,
    ImmutableArray<QueryNodeId> RetainedNodes,
    ImmutableArray<RelationQueryLogicalBypass> Bypasses,
    ImmutableArray<RelationQueryExpressionSiteAnalysis> DemandedExpressionSites,
    ImmutableArray<RelationQueryAggregateAssignmentReference> DemandedAggregateAssignments,
    DocumentValidationResult Validation);

/// <summary>
/// Performs deterministic backward demand propagation over a validated relation/query DAG.
/// </summary>
sealed class RelationQueryRequirementGraphBuilder
{
    readonly RelationQueryDefinition definition;
    readonly RelationshipCatalogDocument? catalogDocument;
    readonly RelationQueryCompilationDemand demand;
    readonly RelationQueryExpressionAnalysisResult analysis;
    readonly ImmutableDictionary<QueryNodeId, LogicalQueryNode> nodes;
    readonly ImmutableDictionary<string, QueryParameterDefinition> parameters;
    readonly ImmutableDictionary<GraphId, ShapeGraph> shapeGraphs;
    readonly RelationQueryShapeResolver shapeResolver;
    readonly RelationQueryBindingFlowAnalysis bindingFlow;
    readonly Dictionary<SiteKey, RelationQueryExpressionSiteAnalysis> sites;
    readonly Dictionary<ExprSiteId, RelationQueryExpressionSiteAnalysis> demandedSites = [];
    readonly HashSet<RelationQueryAggregateAssignmentReference> demandedAggregateAssignments = [];
    readonly RequirementAccumulator requirements = new();
    readonly HashSet<QueryNodeId> retainedNodes = [];
    readonly Dictionary<QueryNodeId, TraversalResolution> bypassedTraversals = [];
    readonly List<DocumentValidationDiagnostic> diagnostics = [];
    readonly HashSet<FieldWalkKey> activeFieldWalks = [];
    readonly HashSet<RowWalkKey> activeRowWalks = [];

    public RelationQueryRequirementGraphBuilder(
        RelationQueryDefinition definition,
        ImmutableArray<ShapeGraphDocument> shapeDocuments,
        RelationshipCatalogDocument? catalogDocument,
        RelationQueryCompilationDemand demand,
        RelationQueryExpressionAnalysisResult analysis)
    {
        this.definition = Guard.RequireNotNull(definition);
        this.catalogDocument = catalogDocument;
        this.demand = Guard.RequireNotNull(demand);
        this.analysis = Guard.RequireNotNull(analysis);
        nodes = definition.Body.Nodes.ToImmutableDictionary(static node => node.Id);
        parameters = definition.Body.Parameters.ToImmutableDictionary(
            static parameter => parameter.Id.Value,
            StringComparer.Ordinal);
        shapeGraphs = shapeDocuments.ToImmutableDictionary(
            static document => document.Graph.Id,
            static document => document.Graph);
        shapeResolver = new([.. shapeGraphs.Values]);
        bindingFlow = RelationQueryBindingFlowAnalyzer.Analyze(
            definition,
            catalogDocument?.Catalog);
        sites = analysis.SiteAnalyses.ToDictionary(
            static site => SiteKey.From(site),
            static site => site);
    }

    public RelationQueryRequirementGraphBuildResult Build()
    {
        switch (definition)
        {
            case IRRelationDefinition relation:
                BuildRelation(relation);
                break;
            case QueryDefinition query:
                BuildQuery(query);
                break;
            default:
                AddError(
                    RelationQueryCompilationDiagnosticCodes.NodeUnsupported,
                    $"Definition type '{definition.GetType().Name}' is not supported by static compilation.",
                    "/definition");
                break;
        }

        var validation = NormalizeValidation(diagnostics);
        RelationQueryRequirementGraph? graph = null;
        if (validation.IsValid && requirements.HasEdges)
            graph = requirements.Build();
        else if (validation.IsValid)
        {
            AddError(
                RelationQueryCompilationDiagnosticCodes.PlanUnavailable,
                "The output demand produced no semantic input requirements.",
                "/definition");
            validation = NormalizeValidation(diagnostics);
        }

        return new(
            graph,
            [.. retainedNodes.OrderBy(static node => node.Value, StringComparer.Ordinal)],
            [
                .. bypassedTraversals.Values
                    .OrderBy(static item => item.Traversal.Id.Value, StringComparer.Ordinal)
                    .Select(static item => new RelationQueryLogicalBypass(
                        RelationQueryLogicalBypassKind.OptionalAtMostOneLeftRelationshipTraversal,
                        item.Traversal.Id,
                        item.Relationship,
                        item.Traversal.Direction,
                        item.Cardinality,
                        item.Traversal.From,
                        item.Traversal.Result))
            ],
            [.. demandedSites.Values.OrderBy(static site => site.Analysis.Site.Id.Value, StringComparer.Ordinal)],
            [
                .. demandedAggregateAssignments
                    .OrderBy(static assignment => assignment.Node.Value, StringComparer.Ordinal)
                    .ThenBy(static assignment => assignment.Assignment.Value, StringComparer.Ordinal)
            ],
            validation);
    }

    void BuildRelation(IRRelationDefinition relation)
    {
        if (demand.Kind == RelationQueryCompilationDemandKind.QueryResults)
        {
            AddError(
                RelationQueryCompilationDiagnosticCodes.DemandKindMismatch,
                "A query-result demand cannot compile a relation definition.",
                "/demand");
            return;
        }

        if (!TryGetSingleOutputBinding(
                relation.Output.Node,
                relation.Output.Shape,
                out var outputBinding))
        {
            return;
        }

        var strictAssignments = demand.Kind == RelationQueryCompilationDemandKind.RelationFields;
        var selectedFields = demand.Kind switch
        {
            RelationQueryCompilationDemandKind.AllDeclaredOutputs =>
                GetDeclaredFields(relation.Output.Shape),
            RelationQueryCompilationDemandKind.RelationFields =>
                ValidateSelectedFields(
                    demand.RelationFields,
                    relation.Output.Shape,
                    RelationQueryCompilationDiagnosticCodes.RelationFieldInvalid,
                    "/demand/relationFields"),
            _ => []
        };

        var rowOutput = CreateRelationOutput(relation, field: null);
        requirements.AddOutput(rowOutput);
        WalkRow(
            relation.Output.Node,
            RelationQueryRequirementEffect.Membership,
            rowOutput,
            QueryInputRequirement.Required,
            []);

        foreach (var field in selectedFields)
        {
            var output = CreateRelationOutput(relation, field);
            var resolved = WalkField(
                relation.Output.Node,
                outputBinding,
                field.Path,
                RelationQueryRequirementEffect.Value,
                output,
                QueryInputRequirement.Required,
                [],
                strictAssignments);
            if (resolved)
                requirements.AddOutput(output);
        }

        if (relation.Output.Key is not null
            && TryGetSite(RelationQueryExpressionSiteKind.RelationOutputKey, out var outputKey))
        {
            WalkSiteRequirements(
                relation.Output.Node,
                outputKey,
                RelationQueryRequirementEffect.Identity,
                rowOutput,
                QueryInputRequirement.Required,
                []);
        }

        foreach (var invariant in relation.Invariants
                     .Where(static invariant => invariant is not null)
                     .OrderBy(static invariant => invariant.Name, StringComparer.Ordinal))
        {
            if (!TryGetSite(
                    RelationQueryExpressionSiteKind.RelationInvariant,
                    out var invariantSite,
                    invariantName: invariant.Name))
            {
                continue;
            }

            WalkSiteRequirements(
                relation.Output.Node,
                invariantSite,
                RelationQueryRequirementEffect.Validation,
                rowOutput,
                QueryInputRequirement.Required,
                []);
        }
    }

    void BuildQuery(QueryDefinition query)
    {
        if (demand.Kind == RelationQueryCompilationDemandKind.RelationFields)
        {
            AddError(
                RelationQueryCompilationDiagnosticCodes.DemandKindMismatch,
                "A relation-field demand cannot compile a query definition.",
                "/demand");
            return;
        }

        var resultsById = query.Results.ToDictionary(static result => result.Id);
        ImmutableArray<(QueryResultDefinition Result, RelationQueryFieldSelectionKind Selection, ImmutableArray<RelationQueryFieldReference> Fields, bool Strict)> selectedResults;
        if (demand.Kind == RelationQueryCompilationDemandKind.AllDeclaredOutputs)
        {
            selectedResults =
            [
                .. query.Results.Select(static result => (
                    result,
                    RelationQueryFieldSelectionKind.AllFields,
                    ImmutableArray<RelationQueryFieldReference>.Empty,
                    false))
            ];
        }
        else
        {
            List<(QueryResultDefinition, RelationQueryFieldSelectionKind, ImmutableArray<RelationQueryFieldReference>, bool)> selected = [];
            foreach (var resultDemand in demand.QueryResults)
            {
                if (!resultsById.TryGetValue(resultDemand.Result, out var result))
                {
                    AddError(
                        RelationQueryCompilationDiagnosticCodes.QueryResultUnknown,
                        $"Query result '{resultDemand.Result.Value}' is not declared by query '{query.Id.Value}'.",
                        $"/demand/queryResults/{Encode(resultDemand.Result.Value)}");
                    continue;
                }

                selected.Add((result, resultDemand.Selection, resultDemand.Fields, true));
            }

            selectedResults = [.. selected];
        }

        foreach (var selected in selectedResults
                     .OrderBy(static item => item.Result.Id.Value, StringComparer.Ordinal))
        {
            if (!TryGetSingleOutputBinding(selected.Result.Input, expectedShape: null, out var outputBinding))
                continue;
            if (!bindingFlow.GetOutput(selected.Result.Input).TryGetValue(outputBinding, out var binding)
                || binding.Shape is not { } resultShape)
            {
                AddError(
                    RelationQueryCompilationDiagnosticCodes.QueryFieldInvalid,
                    $"Query result '{selected.Result.Id.Value}' does not resolve to a shaped binding.",
                    $"/definition/results/{Encode(selected.Result.Id.Value)}");
                continue;
            }

            var selectedFields = selected.Selection == RelationQueryFieldSelectionKind.AllFields
                ? GetDeclaredFields(resultShape)
                : ValidateSelectedFields(
                    selected.Fields,
                    resultShape,
                    RelationQueryCompilationDiagnosticCodes.QueryFieldInvalid,
                    $"/demand/queryResults/{Encode(selected.Result.Id.Value)}/fields");
            var rowOutput = CreateQueryOutput(query, selected.Result, resultShape, field: null);
            requirements.AddOutput(rowOutput);
            WalkRow(
                selected.Result.Input,
                RelationQueryRequirementEffect.Membership,
                rowOutput,
                QueryInputRequirement.Required,
                []);

            foreach (var field in selectedFields)
            {
                var output = CreateQueryOutput(query, selected.Result, resultShape, field);
                var resolved = WalkField(
                    selected.Result.Input,
                    outputBinding,
                    field.Path,
                    RelationQueryRequirementEffect.Value,
                    output,
                    QueryInputRequirement.Required,
                    [],
                    selected.Strict);
                if (resolved)
                    requirements.AddOutput(output);
            }
        }
    }

    ImmutableArray<RelationQueryFieldReference> ValidateSelectedFields(
        ImmutableArray<RelationQueryFieldReference> selected,
        QualifiedShapeId expectedShape,
        string diagnosticCode,
        string location)
    {
        List<RelationQueryFieldReference> valid = [];
        for (var index = 0; index < selected.Length; index++)
        {
            var field = selected[index];
            if (field.Shape != expectedShape || !TryResolveField(field.Shape, field.Path, out _))
            {
                AddError(
                    diagnosticCode,
                    $"Demanded field '{field}' is not declared by output shape '{expectedShape}'.",
                    $"{location}/{index}");
                continue;
            }

            valid.Add(field);
        }

        return RelationQueryContractOrdering.NormalizeFields(valid);
    }

    ImmutableArray<RelationQueryFieldReference> GetDeclaredFields(QualifiedShapeId shapeId)
    {
        if (!TryGetShape(shapeId, out var shape))
        {
            AddError(
                RelationQueryCompilationDiagnosticCodes.QueryFieldInvalid,
                $"Output shape '{shapeId}' is unavailable to static compilation.",
                $"/shapeGraphs/{Encode(shapeId.GraphId.Value)}/shapes/{Encode(shapeId.ShapeId.Value)}");
            return [];
        }

        return
        [
            .. shape.Fields
                .OrderBy(static field => field.Name.Value, StringComparer.Ordinal)
                .Select(field => new RelationQueryFieldReference(
                    shapeId,
                    FieldPath.FromField(field.Name.Value)))
        ];
    }

    bool TryGetSingleOutputBinding(
        QueryNodeId node,
        QualifiedShapeId? expectedShape,
        out ValueBindingId binding)
    {
        var output = bindingFlow.GetOutput(node).Bindings
            .Where(item => expectedShape is null || item.Value.Shape == expectedShape)
            .OrderBy(static item => item.Key.Value, StringComparer.Ordinal)
            .Take(2)
            .ToArray();
        if (output.Length == 1)
        {
            binding = output[0].Key;
            return true;
        }

        AddError(
            RelationQueryCompilationDiagnosticCodes.BindingRouteUnavailable,
            expectedShape is null
                ? $"Output node '{node.Value}' must expose exactly one result binding."
                : $"Output node '{node.Value}' must expose exactly one binding for shape '{expectedShape}'.",
            $"/definition/body/nodes/{Encode(node.Value)}");
        binding = default;
        return false;
    }

    bool WalkField(
        QueryNodeId nodeId,
        ValueBindingId binding,
        FieldPath path,
        RelationQueryRequirementEffect effect,
        RelationQueryOutputReference output,
        QueryInputRequirement requirement,
        ImmutableArray<RelationQueryRequirementTraceStep> trace,
        bool strictAssignment)
    {
        var walkKey = new FieldWalkKey(nodeId, binding, path, effect, output.Id);
        if (!activeFieldWalks.Add(walkKey))
        {
            AddError(
                RelationQueryCompilationDiagnosticCodes.BindingRouteUnavailable,
                $"Field demand for binding '{binding.Value}' cycles at node '{nodeId.Value}'.",
                NodeLocation(nodeId));
            return false;
        }

        try
        {
            if (!nodes.TryGetValue(nodeId, out var node))
            {
                AddError(
                    RelationQueryCompilationDiagnosticCodes.BindingRouteUnavailable,
                    $"Field demand references unknown node '{nodeId.Value}'.",
                    NodeLocation(nodeId));
                return false;
            }

            return node switch
            {
                SourceQueryNode source => WalkSourceField(
                    source,
                    binding,
                    path,
                    effect,
                    output,
                    trace),
                FilterQueryNode filter => WalkPreservedField(
                    filter.Id,
                    filter.Input,
                    binding,
                    path,
                    effect,
                    output,
                    requirement,
                    trace,
                    strictAssignment),
                TraverseRelationshipQueryNode traversal => WalkTraversalField(
                    traversal,
                    binding,
                    path,
                    effect,
                    output,
                    requirement,
                    trace,
                    strictAssignment),
                JoinQueryNode join => WalkJoinField(
                    join,
                    binding,
                    path,
                    effect,
                    output,
                    requirement,
                    trace,
                    strictAssignment),
                TemporalJoinQueryNode temporalJoin => WalkTemporalJoinField(
                    temporalJoin,
                    binding,
                    path,
                    effect,
                    output,
                    requirement,
                    trace,
                    strictAssignment),
                ExpandCollectionQueryNode expansion => WalkExpansionField(
                    expansion,
                    binding,
                    path,
                    effect,
                    output,
                    requirement,
                    trace,
                    strictAssignment),
                ProjectQueryNode project => WalkProjectionField(
                    project,
                    binding,
                    path,
                    effect,
                    output,
                    requirement,
                    trace,
                    strictAssignment),
                DistinctQueryNode distinct => WalkPreservedField(
                    distinct.Id,
                    distinct.Input,
                    binding,
                    path,
                    effect,
                    output,
                    requirement,
                    trace,
                    strictAssignment),
                AggregateQueryNode aggregate => WalkAggregateField(
                    aggregate,
                    binding,
                    path,
                    effect,
                    output,
                    requirement,
                    trace,
                    strictAssignment),
                OrderQueryNode order => WalkPreservedField(
                    order.Id,
                    order.Input,
                    binding,
                    path,
                    effect,
                    output,
                    requirement,
                    trace,
                    strictAssignment),
                PageQueryNode page => WalkPreservedField(
                    page.Id,
                    page.Input,
                    binding,
                    path,
                    effect,
                    output,
                    requirement,
                    trace,
                    strictAssignment),
                _ => UnsupportedFieldNode(node, binding)
            };
        }
        finally
        {
            activeFieldWalks.Remove(walkKey);
        }
    }

    bool WalkSourceField(
        SourceQueryNode source,
        ValueBindingId binding,
        FieldPath path,
        RelationQueryRequirementEffect effect,
        RelationQueryOutputReference output,
        ImmutableArray<RelationQueryRequirementTraceStep> trace)
    {
        if (binding != source.Binding)
        {
            AddBindingRouteError(source.Id, binding, path);
            return false;
        }

        Retain(source.Id);
        var field = new RelationQueryFieldReference(source.Shape, path);
        var input = new RelationQueryFieldInput(
            CreateFieldInputId(source.Id, binding, field),
            source.Id,
            binding,
            field,
            GetValueContract(field));
        requirements.Add(
            input,
            output,
            effect,
            QueryInputRequirement.Required,
            CreateTrace(AppendStructural(trace, source.Id)));
        return true;
    }

    bool WalkPreservedField(
        QueryNodeId node,
        QueryNodeId input,
        ValueBindingId binding,
        FieldPath path,
        RelationQueryRequirementEffect effect,
        RelationQueryOutputReference output,
        QueryInputRequirement requirement,
        ImmutableArray<RelationQueryRequirementTraceStep> trace,
        bool strictAssignment)
    {
        Retain(node);
        return WalkField(
            input,
            binding,
            path,
            effect,
            output,
            requirement,
            AppendStructural(trace, node),
            strictAssignment);
    }

    bool WalkTraversalField(
        TraverseRelationshipQueryNode traversal,
        ValueBindingId binding,
        FieldPath path,
        RelationQueryRequirementEffect effect,
        RelationQueryOutputReference output,
        QueryInputRequirement requirement,
        ImmutableArray<RelationQueryRequirementTraceStep> trace,
        bool strictAssignment)
    {
        if (binding != traversal.Result)
        {
            return WalkField(
                traversal.Input,
                binding,
                path,
                effect,
                output,
                requirement,
                trace,
                strictAssignment);
        }

        if (!TryResolveTraversal(traversal, out var relationship, out var cardinality))
            return false;

        Retain(traversal.Id);
        var traversalTrace = AppendStructural(trace, traversal.Id);
        var relationshipInput = CreateRelationshipInput(traversal, relationship, cardinality);
        requirements.Add(
            relationshipInput,
            output,
            RelationQueryRequirementEffect.Acquisition,
            traversal.Requirement,
            CreateTrace(traversalTrace));

        var resultShape = traversal.Direction == RelationshipTraversalDirection.Forward
            ? relationship.TargetShape
            : relationship.SourceShape;
        var field = new RelationQueryFieldReference(resultShape, path);
        var fieldInput = new RelationQueryFieldInput(
            CreateFieldInputId(traversal.Id, traversal.Result, field),
            traversal.Id,
            traversal.Result,
            field,
            GetValueContract(field));
        requirements.Add(
            fieldInput,
            output,
            effect,
            traversal.Requirement,
            CreateTrace(traversalTrace));
        AddTraversalCorrelation(
            traversal,
            relationship,
            cardinality,
            output,
            traversalTrace);
        return true;
    }

    bool WalkJoinField(
        JoinQueryNode join,
        ValueBindingId binding,
        FieldPath path,
        RelationQueryRequirementEffect effect,
        RelationQueryOutputReference output,
        QueryInputRequirement requirement,
        ImmutableArray<RelationQueryRequirementTraceStep> trace,
        bool strictAssignment)
        => WalkBinaryJoinField(
            join.Id,
            join.Left,
            join.Right,
            binding,
            path,
            effect,
            output,
            requirement,
            trace,
            strictAssignment);

    bool WalkTemporalJoinField(
        TemporalJoinQueryNode join,
        ValueBindingId binding,
        FieldPath path,
        RelationQueryRequirementEffect effect,
        RelationQueryOutputReference output,
        QueryInputRequirement requirement,
        ImmutableArray<RelationQueryRequirementTraceStep> trace,
        bool strictAssignment)
        => WalkBinaryJoinField(
            join.Id,
            join.Left,
            join.Right,
            binding,
            path,
            effect,
            output,
            requirement,
            trace,
            strictAssignment);

    bool WalkBinaryJoinField(
        QueryNodeId join,
        QueryNodeId left,
        QueryNodeId right,
        ValueBindingId binding,
        FieldPath path,
        RelationQueryRequirementEffect effect,
        RelationQueryOutputReference output,
        QueryInputRequirement requirement,
        ImmutableArray<RelationQueryRequirementTraceStep> trace,
        bool strictAssignment)
    {
        Retain(join);
        var leftContains = bindingFlow.GetOutput(left).Contains(binding);
        var rightContains = bindingFlow.GetOutput(right).Contains(binding);
        if (leftContains == rightContains)
        {
            AddBindingRouteError(join, binding, path);
            return false;
        }

        return WalkField(
            leftContains ? left : right,
            binding,
            path,
            effect,
            output,
            requirement,
            AppendStructural(trace, join),
            strictAssignment);
    }

    bool WalkExpansionField(
        ExpandCollectionQueryNode expansion,
        ValueBindingId binding,
        FieldPath path,
        RelationQueryRequirementEffect effect,
        RelationQueryOutputReference output,
        QueryInputRequirement requirement,
        ImmutableArray<RelationQueryRequirementTraceStep> trace,
        bool strictAssignment)
    {
        Retain(expansion.Id);
        if (binding != expansion.ItemBinding)
        {
            return WalkField(
                expansion.Input,
                binding,
                path,
                effect,
                output,
                requirement,
                AppendStructural(trace, expansion.Id),
                strictAssignment);
        }

        if (!TryGetSite(
                RelationQueryExpressionSiteKind.ExpandCollection,
                out var collectionSite,
                node: expansion.Id))
        {
            return false;
        }

        var siteTrace = AppendSite(trace, expansion.Id, collectionSite);
        if (expansion.Collection is FieldExpr collectionField
            && TryResolveSiteFieldBinding(collectionSite, collectionField, out var sourceBinding)
            && TryResolveComposedSiteFieldPath(
                collectionSite,
                sourceBinding,
                collectionField.Path,
                [FieldPathSegment.Element(), .. path.Segments],
                out var sourcePath))
        {
            WalkSiteNonFieldRequirements(
                collectionSite,
                effect,
                output,
                requirement,
                siteTrace);
            return WalkField(
                expansion.Input,
                sourceBinding,
                sourcePath,
                effect,
                output,
                requirement,
                siteTrace,
                strictAssignment);
        }

        AddWarning(
            RelationQueryCompilationDiagnosticCodes.ExpandedItemUnavailable,
            $"Expanded item field '{path}' cannot be mapped exactly to collection node '{expansion.Id.Value}'; the complete collection expression is retained.",
            NodeLocation(expansion.Id));
        WalkSiteRequirements(
            expansion.Input,
            collectionSite,
            effect,
            output,
            requirement,
            trace);
        return true;
    }

    bool WalkProjectionField(
        ProjectQueryNode project,
        ValueBindingId binding,
        FieldPath path,
        RelationQueryRequirementEffect effect,
        RelationQueryOutputReference output,
        QueryInputRequirement requirement,
        ImmutableArray<RelationQueryRequirementTraceStep> trace,
        bool strictAssignment)
    {
        if (binding != project.ResultBinding)
        {
            AddBindingRouteError(project.Id, binding, path);
            return false;
        }

        var assignments = project.Assignments
            .Where(assignment => assignment.Target.Overlaps(path))
            .OrderBy(static assignment => RelationQueryContractOrdering.FieldPathKey(assignment.Target), StringComparer.Ordinal)
            .ThenBy(static assignment => assignment.Id.Value, StringComparer.Ordinal)
            .ToArray();
        if (assignments.Length == 0)
        {
            if (!strictAssignment && IsOptionalField(project.ResultShape, path))
                return false;

            AddError(
                RelationQueryCompilationDiagnosticCodes.OutputFieldUnassigned,
                $"Projection '{project.Id.Value}' does not assign demanded field '{path}' in shape '{project.ResultShape}'.",
                $"{NodeLocation(project.Id)}/assignments");
            return false;
        }

        if (HasAmbiguousOverlap(assignments, path))
        {
            AddError(
                RelationQueryCompilationDiagnosticCodes.OutputFieldAmbiguous,
                $"Projection '{project.Id.Value}' has overlapping assignments for demanded field '{path}'.",
                $"{NodeLocation(project.Id)}/assignments");
            return false;
        }

        var missingCoverage = FindMissingAssignmentCoverage(
            project.ResultShape,
            path,
            assignments.Select(static assignment => assignment.Target),
            strictAssignment);
        if (!missingCoverage.IsDefaultOrEmpty)
        {
            AddError(
                RelationQueryCompilationDiagnosticCodes.OutputFieldUnassigned,
                $"Projection '{project.Id.Value}' does not completely assign demanded field '{path}'; missing required path(s): {FormatPaths(missingCoverage)}.",
                $"{NodeLocation(project.Id)}/assignments");
            return false;
        }

        Retain(project.Id);
        var resolved = false;
        foreach (var assignment in assignments)
        {
            if (!TryGetSite(
                    RelationQueryExpressionSiteKind.ProjectionAssignmentValue,
                    out var site,
                    node: project.Id,
                    assignment: assignment.Id))
            {
                continue;
            }

            var suffix = assignment.Target.IsPrefixOf(path)
                ? path.Segments[assignment.Target.Segments.Length..]
                : ImmutableArray<FieldPathSegment>.Empty;
            if (!suffix.IsDefaultOrEmpty
                && assignment.Value is FieldExpr directField
                && TryResolveSiteFieldBinding(site, directField, out var sourceBinding)
                && TryResolveComposedSiteFieldPath(
                    site,
                    sourceBinding,
                    directField.Path,
                    suffix,
                    out var sourcePath))
            {
                var siteTrace = AppendSite(trace, project.Id, site);
                WalkSiteNonFieldRequirements(
                    site,
                    effect,
                    output,
                    requirement,
                    siteTrace);
                resolved |= WalkField(
                    project.Input,
                    sourceBinding,
                    sourcePath,
                    effect,
                    output,
                    requirement,
                    siteTrace,
                    strictAssignment);
                continue;
            }

            if (!suffix.IsDefaultOrEmpty)
            {
                AddWarning(
                    RelationQueryCompilationDiagnosticCodes.FieldSelectionConservative,
                    $"Nested demand '{path}' cannot be mapped exactly through projection assignment '{assignment.Id.Value}'; all expression inputs are retained.",
                    $"{NodeLocation(project.Id)}/assignments/{Encode(assignment.Id.Value)}");
            }

            WalkSiteRequirements(
                project.Input,
                site,
                effect,
                output,
                requirement,
                trace);
            resolved = true;
        }

        return resolved;
    }

    bool WalkAggregateField(
        AggregateQueryNode aggregate,
        ValueBindingId binding,
        FieldPath path,
        RelationQueryRequirementEffect incomingEffect,
        RelationQueryOutputReference output,
        QueryInputRequirement requirement,
        ImmutableArray<RelationQueryRequirementTraceStep> trace,
        bool strictAssignment)
    {
        if (binding != aggregate.ResultBinding)
        {
            AddBindingRouteError(aggregate.Id, binding, path);
            return false;
        }

        var groupingMatches = aggregate.Groupings
            .Where(grouping => grouping.Target.Overlaps(path))
            .OrderBy(static grouping => RelationQueryContractOrdering.FieldPathKey(grouping.Target), StringComparer.Ordinal)
            .ThenBy(static grouping => grouping.Id.Value, StringComparer.Ordinal)
            .ToArray();
        var aggregateMatches = aggregate.Aggregates
            .Where(assignment => assignment.Target.Overlaps(path))
            .OrderBy(static assignment => RelationQueryContractOrdering.FieldPathKey(assignment.Target), StringComparer.Ordinal)
            .ThenBy(static assignment => assignment.Id.Value, StringComparer.Ordinal)
            .ToArray();
        if (groupingMatches.Length + aggregateMatches.Length == 0)
        {
            if (!strictAssignment && IsOptionalField(aggregate.ResultShape, path))
                return false;

            AddError(
                RelationQueryCompilationDiagnosticCodes.OutputFieldUnassigned,
                $"Aggregate node '{aggregate.Id.Value}' does not assign demanded field '{path}' in shape '{aggregate.ResultShape}'.",
                $"{NodeLocation(aggregate.Id)}/aggregates");
            return false;
        }

        if (HasAmbiguousAggregateOverlap(groupingMatches, aggregateMatches, path))
        {
            AddError(
                RelationQueryCompilationDiagnosticCodes.OutputFieldAmbiguous,
                $"Aggregate node '{aggregate.Id.Value}' has overlapping assignments for demanded field '{path}'.",
                NodeLocation(aggregate.Id));
            return false;
        }

        var missingCoverage = FindMissingAssignmentCoverage(
            aggregate.ResultShape,
            path,
            groupingMatches.Select(static grouping => grouping.Target)
                .Concat(aggregateMatches.Select(static assignment => assignment.Target)),
            strictAssignment);
        if (!missingCoverage.IsDefaultOrEmpty)
        {
            AddError(
                RelationQueryCompilationDiagnosticCodes.OutputFieldUnassigned,
                $"Aggregate node '{aggregate.Id.Value}' does not completely assign demanded field '{path}'; missing required path(s): {FormatPaths(missingCoverage)}.",
                $"{NodeLocation(aggregate.Id)}/aggregates");
            return false;
        }

        Retain(aggregate.Id);
        var resolved = false;
        if (groupingMatches.Length != 0)
        {
            WalkAggregateContributionContext(
                aggregate,
                RelationQueryRequirementEffect.Grouping,
                incomingEffect,
                output,
                requirement,
                trace);
        }
        if (aggregateMatches.Length != 0)
        {
            WalkAggregateContributionContext(
                aggregate,
                RelationQueryRequirementEffect.Aggregation,
                incomingEffect,
                output,
                requirement,
                trace);
        }

        foreach (var grouping in groupingMatches)
        {
            if (!TryGetSite(
                    RelationQueryExpressionSiteKind.AggregateGroupingKey,
                    out var site,
                    node: aggregate.Id,
                    assignment: grouping.Id))
            {
                continue;
            }

            WalkSiteRequirements(
                aggregate.Input,
                site,
                incomingEffect == RelationQueryRequirementEffect.Value
                    ? RelationQueryRequirementEffect.Value
                    : incomingEffect,
                output,
                requirement,
                trace);
            resolved = true;
        }

        foreach (var assignment in aggregateMatches)
        {
            if (assignment.Value is not null)
            {
                if (TryGetSite(
                        RelationQueryExpressionSiteKind.AggregateAssignmentValue,
                        out var valueSite,
                        node: aggregate.Id,
                        assignment: assignment.Id))
                {
                    WalkSiteRequirements(
                        aggregate.Input,
                        valueSite,
                        incomingEffect == RelationQueryRequirementEffect.Value
                            ? RelationQueryRequirementEffect.Aggregation
                            : incomingEffect,
                        output,
                        requirement,
                        trace);
                }
            }
            if (assignment.Filter is not null
                && TryGetSite(
                    RelationQueryExpressionSiteKind.AggregateAssignmentFilter,
                    out var filterSite,
                    node: aggregate.Id,
                    assignment: assignment.Id))
            {
                WalkSiteRequirements(
                    aggregate.Input,
                    filterSite,
                    incomingEffect == RelationQueryRequirementEffect.Value
                        ? RelationQueryRequirementEffect.Membership
                        : incomingEffect,
                    output,
                    requirement,
                    trace);
            }

            AddAggregateCapability(
                aggregate.Id,
                assignment.Id,
                assignment.Operation,
                output,
                trace);
            resolved = true;
        }

        return resolved;
    }

    void WalkAggregateContributionContext(
        AggregateQueryNode aggregate,
        RelationQueryRequirementEffect localRowEffect,
        RelationQueryRequirementEffect incomingEffect,
        RelationQueryOutputReference output,
        QueryInputRequirement requirement,
        ImmutableArray<RelationQueryRequirementTraceStep> trace)
    {
        var groupingEffect = incomingEffect == RelationQueryRequirementEffect.Value
            ? RelationQueryRequirementEffect.Grouping
            : incomingEffect;
        foreach (var grouping in aggregate.Groupings
                     .OrderBy(static grouping => grouping.Id.Value, StringComparer.Ordinal))
        {
            if (TryGetSite(
                    RelationQueryExpressionSiteKind.AggregateGroupingKey,
                    out var groupingSite,
                    node: aggregate.Id,
                    assignment: grouping.Id))
            {
                WalkSiteRequirements(
                    aggregate.Input,
                    groupingSite,
                    groupingEffect,
                    output,
                    requirement,
                    trace);
            }
        }

        WalkRow(
            aggregate.Input,
            incomingEffect == RelationQueryRequirementEffect.Value
                ? localRowEffect
                : incomingEffect,
            output,
            requirement,
            AppendStructural(trace, aggregate.Id));
    }

    bool UnsupportedFieldNode(LogicalQueryNode node, ValueBindingId binding)
    {
        AddError(
            RelationQueryCompilationDiagnosticCodes.NodeUnsupported,
            $"Logical node '{node.Id.Value}' of type '{node.GetType().Name}' cannot route binding '{binding.Value}'.",
            NodeLocation(node.Id));
        return false;
    }

    void WalkRow(
        QueryNodeId nodeId,
        RelationQueryRequirementEffect effect,
        RelationQueryOutputReference output,
        QueryInputRequirement requirement,
        ImmutableArray<RelationQueryRequirementTraceStep> trace)
    {
        var walkKey = new RowWalkKey(nodeId, effect, output.Id);
        if (!activeRowWalks.Add(walkKey))
        {
            AddError(
                RelationQueryCompilationDiagnosticCodes.BindingRouteUnavailable,
                $"Row demand cycles at node '{nodeId.Value}'.",
                NodeLocation(nodeId));
            return;
        }

        try
        {
            if (!nodes.TryGetValue(nodeId, out var node))
            {
                AddError(
                    RelationQueryCompilationDiagnosticCodes.BindingRouteUnavailable,
                    $"Row demand references unknown node '{nodeId.Value}'.",
                    NodeLocation(nodeId));
                return;
            }

            switch (node)
            {
                case SourceQueryNode source:
                    WalkSourceRow(source, effect, output, trace);
                    break;
                case FilterQueryNode filter:
                    Retain(filter.Id);
                    if (TryGetSite(
                            RelationQueryExpressionSiteKind.FilterPredicate,
                            out var filterSite,
                            node: filter.Id))
                    {
                        WalkSiteRequirements(
                            filter.Input,
                            filterSite,
                            RelationQueryRequirementEffect.Membership,
                            output,
                            QueryInputRequirement.Required,
                            trace);
                    }
                    WalkRow(
                        filter.Input,
                        effect,
                        output,
                        requirement,
                        AppendStructural(trace, filter.Id));
                    break;
                case TraverseRelationshipQueryNode traversal:
                    WalkTraversalRow(traversal, effect, output, requirement, trace);
                    break;
                case JoinQueryNode join:
                    WalkJoinRow(join, effect, output, requirement, trace);
                    break;
                case TemporalJoinQueryNode temporalJoin:
                    WalkTemporalJoinRow(temporalJoin, effect, output, requirement, trace);
                    break;
                case ExpandCollectionQueryNode expansion:
                    Retain(expansion.Id);
                    if (TryGetSite(
                            RelationQueryExpressionSiteKind.ExpandCollection,
                            out var expansionSite,
                            node: expansion.Id))
                    {
                        WalkSiteRequirements(
                            expansion.Input,
                            expansionSite,
                            RelationQueryRequirementEffect.Cardinality,
                            output,
                            QueryInputRequirement.Required,
                            trace);
                        WalkSiteRequirements(
                            expansion.Input,
                            expansionSite,
                            RelationQueryRequirementEffect.Membership,
                            output,
                            QueryInputRequirement.Required,
                            trace);
                    }
                    var expansionTrace = AppendStructural(trace, expansion.Id);
                    WalkRow(expansion.Input, effect, output, requirement, expansionTrace);
                    if (effect != RelationQueryRequirementEffect.Membership)
                    {
                        WalkRow(
                            expansion.Input,
                            RelationQueryRequirementEffect.Membership,
                            output,
                            requirement,
                            expansionTrace);
                    }
                    if (effect != RelationQueryRequirementEffect.Cardinality)
                    {
                        WalkRow(
                            expansion.Input,
                            RelationQueryRequirementEffect.Cardinality,
                            output,
                            requirement,
                            expansionTrace);
                    }
                    break;
                case ProjectQueryNode project:
                    Retain(project.Id);
                    WalkRow(
                        project.Input,
                        effect,
                        output,
                        requirement,
                        AppendStructural(trace, project.Id));
                    break;
                case DistinctQueryNode distinct:
                    WalkDistinctRow(distinct, effect, output, requirement, trace);
                    break;
                case AggregateQueryNode aggregate:
                    WalkAggregateRow(aggregate, output, requirement, trace);
                    break;
                case OrderQueryNode order:
                    Retain(order.Id);
                    for (var index = 0; index < order.Orderings.Length; index++)
                    {
                        if (TryGetSite(
                                RelationQueryExpressionSiteKind.OrderKey,
                                out var orderSite,
                                node: order.Id,
                                ordinal: index))
                        {
                            WalkSiteRequirements(
                                order.Input,
                                orderSite,
                                RelationQueryRequirementEffect.Ordering,
                                output,
                                QueryInputRequirement.Required,
                                trace);
                        }
                    }
                    WalkRow(
                        order.Input,
                        effect,
                        output,
                        requirement,
                        AppendStructural(trace, order.Id));
                    break;
                case PageQueryNode page:
                    Retain(page.Id);
                    if (page.Page is KeysetPageDefinition keyset)
                    {
                        for (var index = 0; index < keyset.After.Length; index++)
                        {
                            if (TryGetSite(
                                    RelationQueryExpressionSiteKind.KeysetBoundary,
                                    out var boundarySite,
                                    node: page.Id,
                                    ordinal: index))
                            {
                                WalkSiteRequirements(
                                    page.Input,
                                    boundarySite,
                                    RelationQueryRequirementEffect.Pagination,
                                    output,
                                    QueryInputRequirement.Required,
                                    trace);
                            }
                        }
                    }
                    var pageTrace = AppendStructural(trace, page.Id);
                    WalkRow(page.Input, effect, output, requirement, pageTrace);
                    if (effect != RelationQueryRequirementEffect.Pagination)
                    {
                        WalkRow(
                            page.Input,
                            RelationQueryRequirementEffect.Pagination,
                            output,
                            requirement,
                            pageTrace);
                    }
                    break;
                default:
                    AddError(
                        RelationQueryCompilationDiagnosticCodes.NodeUnsupported,
                        $"Logical node '{node.Id.Value}' of type '{node.GetType().Name}' cannot satisfy a row demand.",
                        NodeLocation(node.Id));
                    break;
            }
        }
        finally
        {
            activeRowWalks.Remove(walkKey);
        }
    }

    void WalkSourceRow(
        SourceQueryNode source,
        RelationQueryRequirementEffect effect,
        RelationQueryOutputReference output,
        ImmutableArray<RelationQueryRequirementTraceStep> trace)
    {
        Retain(source.Id);
        var role = definition is IRRelationDefinition relation && relation.RootBinding == source.Binding
            ? RelationQuerySourceInputRole.RelationRoot
            : RelationQuerySourceInputRole.Source;
        var input = new RelationQuerySourceSetInput(
            CreateSourceSetInputId(source.Id),
            source.Id,
            source.Binding,
            source.Shape,
            role,
            QueryInputRequirement.Required);
        requirements.Add(
            input,
            output,
            effect,
            QueryInputRequirement.Required,
            CreateTrace(AppendStructural(trace, source.Id)));
    }

    void WalkTraversalRow(
        TraverseRelationshipQueryNode traversal,
        RelationQueryRequirementEffect incomingEffect,
        RelationQueryOutputReference output,
        QueryInputRequirement requirement,
        ImmutableArray<RelationQueryRequirementTraceStep> trace)
    {
        if (!TryResolveTraversal(traversal, out var relationship, out var cardinality))
            return;

        var safelyTransparent = traversal.JoinKind == JoinKind.Left
            && traversal.Requirement == QueryInputRequirement.Optional
            && cardinality == RelationshipTraversalCardinality.AtMostOne;
        if (safelyTransparent)
        {
            Bypass(traversal, relationship, cardinality);
            WalkRow(
                traversal.Input,
                incomingEffect,
                output,
                requirement,
                trace);
            return;
        }

        Retain(traversal.Id);
        var traversalTrace = AppendStructural(trace, traversal.Id);
        var relationshipInput = CreateRelationshipInput(traversal, relationship, cardinality);
        requirements.Add(
            relationshipInput,
            output,
            RelationQueryRequirementEffect.Acquisition,
            traversal.Requirement,
            CreateTrace(traversalTrace));
        if (traversal.JoinKind == JoinKind.Inner)
        {
            requirements.Add(
                relationshipInput,
                output,
                RelationQueryRequirementEffect.Membership,
                traversal.Requirement,
                CreateTrace(traversalTrace));
        }
        if (cardinality == RelationshipTraversalCardinality.Many)
        {
            requirements.Add(
                relationshipInput,
                output,
                RelationQueryRequirementEffect.Cardinality,
                traversal.Requirement,
                CreateTrace(traversalTrace));
        }
        AddTraversalCorrelation(
            traversal,
            relationship,
            cardinality,
            output,
            traversalTrace);
        WalkRow(
            traversal.Input,
            incomingEffect,
            output,
            requirement,
            traversalTrace);
        if (traversal.JoinKind == JoinKind.Inner
            && incomingEffect != RelationQueryRequirementEffect.Membership)
        {
            WalkRow(
                traversal.Input,
                RelationQueryRequirementEffect.Membership,
                output,
                requirement,
                traversalTrace);
        }
        if (cardinality == RelationshipTraversalCardinality.Many
            && incomingEffect != RelationQueryRequirementEffect.Cardinality)
        {
            WalkRow(
                traversal.Input,
                RelationQueryRequirementEffect.Cardinality,
                output,
                requirement,
                traversalTrace);
        }
    }

    void WalkJoinRow(
        JoinQueryNode join,
        RelationQueryRequirementEffect effect,
        RelationQueryOutputReference output,
        QueryInputRequirement requirement,
        ImmutableArray<RelationQueryRequirementTraceStep> trace)
    {
        Retain(join.Id);
        if (TryGetSite(
                RelationQueryExpressionSiteKind.JoinPredicate,
                out var joinSite,
                node: join.Id))
        {
            WalkSiteRequirements(
                join.Id,
                joinSite,
                RelationQueryRequirementEffect.Correlation,
                output,
                QueryInputRequirement.Required,
                trace);
            WalkSiteRequirements(
                join.Id,
                joinSite,
                RelationQueryRequirementEffect.Membership,
                output,
                QueryInputRequirement.Required,
                trace);
            WalkSiteRequirements(
                join.Id,
                joinSite,
                RelationQueryRequirementEffect.Cardinality,
                output,
                QueryInputRequirement.Required,
                trace);
        }

        var joinTrace = AppendStructural(trace, join.Id);
        WalkJoinInputRow(join.Left, effect, output, requirement, joinTrace);
        WalkJoinInputRow(join.Right, effect, output, requirement, joinTrace);
    }

    void WalkTemporalJoinRow(
        TemporalJoinQueryNode join,
        RelationQueryRequirementEffect effect,
        RelationQueryOutputReference output,
        QueryInputRequirement requirement,
        ImmutableArray<RelationQueryRequirementTraceStep> trace)
    {
        Retain(join.Id);
        if (TryGetSite(
                RelationQueryExpressionSiteKind.TemporalJoinCorrelation,
                out var correlationSite,
                node: join.Id))
        {
            WalkTemporalSiteEffects(join.Id, correlationSite, includeValidation: false);
        }

        foreach (var site in GetTemporalMatchSites(join.Id))
        {
            var routeFrom = site.Kind == RelationQueryExpressionSiteKind.TemporalJoinPoint
                || (site.Ordinal == 0 && join.Match is TemporalIntervalOverlapMatch)
                ? join.Left
                : join.Right;
            WalkTemporalSiteEffects(routeFrom, site, includeValidation: true);
        }

        var joinTrace = AppendStructural(trace, join.Id);
        WalkJoinInputRow(join.Left, effect, output, requirement, joinTrace);
        WalkJoinInputRow(join.Right, effect, output, requirement, joinTrace);

        void WalkTemporalSiteEffects(
            QueryNodeId routeFrom,
            RelationQueryExpressionSiteAnalysis site,
            bool includeValidation)
        {
            WalkSiteRequirements(
                routeFrom,
                site,
                RelationQueryRequirementEffect.Correlation,
                output,
                QueryInputRequirement.Required,
                trace);
            WalkSiteRequirements(
                routeFrom,
                site,
                RelationQueryRequirementEffect.Membership,
                output,
                QueryInputRequirement.Required,
                trace);
            WalkSiteRequirements(
                routeFrom,
                site,
                RelationQueryRequirementEffect.Cardinality,
                output,
                QueryInputRequirement.Required,
                trace);
            if (includeValidation)
            {
                WalkSiteRequirements(
                    routeFrom,
                    site,
                    RelationQueryRequirementEffect.Validation,
                    output,
                    QueryInputRequirement.Required,
                    trace);
            }
        }
    }

    IEnumerable<RelationQueryExpressionSiteAnalysis> GetTemporalMatchSites(QueryNodeId node)
    {
        var matching = sites.Values
            .Where(site => site.Node == node
                && site.Kind is RelationQueryExpressionSiteKind.TemporalJoinPoint
                    or RelationQueryExpressionSiteKind.TemporalJoinIntervalLowerBound
                    or RelationQueryExpressionSiteKind.TemporalJoinIntervalUpperBound)
            .OrderBy(static site => site.Analysis.Site.Id.Value, StringComparer.Ordinal)
            .ToArray();
        foreach (var site in matching)
            demandedSites[site.Analysis.Site.Id] = site;
        return matching;
    }

    void WalkJoinInputRow(
        QueryNodeId input,
        RelationQueryRequirementEffect incomingEffect,
        RelationQueryOutputReference output,
        QueryInputRequirement requirement,
        ImmutableArray<RelationQueryRequirementTraceStep> trace)
    {
        WalkRow(input, incomingEffect, output, requirement, trace);
        if (incomingEffect != RelationQueryRequirementEffect.Membership)
        {
            WalkRow(
                input,
                RelationQueryRequirementEffect.Membership,
                output,
                requirement,
                trace);
        }
        if (incomingEffect != RelationQueryRequirementEffect.Cardinality)
        {
            WalkRow(
                input,
                RelationQueryRequirementEffect.Cardinality,
                output,
                requirement,
                trace);
        }
    }

    void WalkDistinctRow(
        DistinctQueryNode distinct,
        RelationQueryRequirementEffect incomingEffect,
        RelationQueryOutputReference output,
        QueryInputRequirement requirement,
        ImmutableArray<RelationQueryRequirementTraceStep> trace)
    {
        Retain(distinct.Id);
        if (!distinct.Keys.IsDefaultOrEmpty)
        {
            for (var index = 0; index < distinct.Keys.Length; index++)
            {
                if (TryGetSite(
                        RelationQueryExpressionSiteKind.DistinctKey,
                        out var keySite,
                        node: distinct.Id,
                        ordinal: index))
                {
                    WalkSiteRequirements(
                        distinct.Input,
                        keySite,
                        RelationQueryRequirementEffect.Cardinality,
                        output,
                        QueryInputRequirement.Required,
                        trace);
                    WalkSiteRequirements(
                        distinct.Input,
                        keySite,
                        RelationQueryRequirementEffect.Membership,
                        output,
                        QueryInputRequirement.Required,
                        trace);
                }
            }
        }
        else
        {
            foreach (var (binding, bindingAnalysis) in bindingFlow.GetInput(distinct.Id).Bindings
                         .OrderBy(static item => item.Key.Value, StringComparer.Ordinal))
            {
                if (bindingAnalysis.Shape is not { } shape)
                {
                    AddError(
                        RelationQueryCompilationDiagnosticCodes.CompleteValueUnavailable,
                        $"Distinct node '{distinct.Id.Value}' requires the complete value of unshaped binding '{binding.Value}'.",
                        NodeLocation(distinct.Id));
                    continue;
                }

                foreach (var field in GetDeclaredFields(shape))
                {
                    WalkField(
                        distinct.Input,
                        binding,
                        field.Path,
                        RelationQueryRequirementEffect.Cardinality,
                        output,
                        QueryInputRequirement.Required,
                        AppendStructural(trace, distinct.Id),
                        strictAssignment: false);
                    WalkField(
                        distinct.Input,
                        binding,
                        field.Path,
                        RelationQueryRequirementEffect.Membership,
                        output,
                        QueryInputRequirement.Required,
                        AppendStructural(trace, distinct.Id),
                        strictAssignment: false);
                }
            }
        }

        var distinctTrace = AppendStructural(trace, distinct.Id);
        WalkRow(distinct.Input, incomingEffect, output, requirement, distinctTrace);
        if (incomingEffect != RelationQueryRequirementEffect.Cardinality)
        {
            WalkRow(
                distinct.Input,
                RelationQueryRequirementEffect.Cardinality,
                output,
                requirement,
                distinctTrace);
        }
        if (incomingEffect != RelationQueryRequirementEffect.Membership)
        {
            WalkRow(
                distinct.Input,
                RelationQueryRequirementEffect.Membership,
                output,
                requirement,
                distinctTrace);
        }
    }

    void WalkAggregateRow(
        AggregateQueryNode aggregate,
        RelationQueryOutputReference output,
        QueryInputRequirement requirement,
        ImmutableArray<RelationQueryRequirementTraceStep> trace)
    {
        Retain(aggregate.Id);
        foreach (var grouping in aggregate.Groupings
                     .OrderBy(static grouping => grouping.Id.Value, StringComparer.Ordinal))
        {
            if (TryGetSite(
                    RelationQueryExpressionSiteKind.AggregateGroupingKey,
                    out var groupingSite,
                    node: aggregate.Id,
                    assignment: grouping.Id))
            {
                WalkSiteRequirements(
                    aggregate.Input,
                    groupingSite,
                    RelationQueryRequirementEffect.Grouping,
                    output,
                    QueryInputRequirement.Required,
                    trace);
            }
        }

        WalkRow(
            aggregate.Input,
            aggregate.Groupings.IsDefaultOrEmpty
                ? RelationQueryRequirementEffect.Aggregation
                : RelationQueryRequirementEffect.Grouping,
            output,
            requirement,
            AppendStructural(trace, aggregate.Id));
    }

    void WalkSiteRequirements(
        QueryNodeId routeFrom,
        RelationQueryExpressionSiteAnalysis site,
        RelationQueryRequirementEffect effect,
        RelationQueryOutputReference output,
        QueryInputRequirement requirement,
        ImmutableArray<RelationQueryRequirementTraceStep> trace)
    {
        var siteOwner = site.Node ?? output.Node;
        var siteTrace = AppendSite(trace, siteOwner, site);
        foreach (var field in site.Analysis.Requirements.Fields)
        {
            if (field.Root == ExprFieldRootKind.CurrentItem)
                continue;

            if (field.Root != ExprFieldRootKind.Binding || field.Binding is not { } binding)
            {
                AddError(
                    RelationQueryCompilationDiagnosticCodes.BindingRouteUnavailable,
                    $"Expression site '{site.Analysis.Site.Id.Value}' has field '{field.Path}' without a routable binding.",
                    site.Analysis.Site.DiagnosticLocation);
                continue;
            }

            WalkField(
                routeFrom,
                binding,
                field.Path,
                effect,
                output,
                requirement,
                siteTrace,
                strictAssignment: false);
        }

        WalkSiteNonFieldRequirements(site, effect, output, requirement, siteTrace);
    }

    void WalkSiteNonFieldRequirements(
        RelationQueryExpressionSiteAnalysis site,
        RelationQueryRequirementEffect effect,
        RelationQueryOutputReference output,
        QueryInputRequirement requirement,
        ImmutableArray<RelationQueryRequirementTraceStep> siteTrace)
    {
        foreach (var parameterName in site.Analysis.Requirements.Parameters)
        {
            if (!parameters.TryGetValue(parameterName, out var parameter))
            {
                AddError(
                    RelationQueryCompilationDiagnosticCodes.BindingRouteUnavailable,
                    $"Expression site '{site.Analysis.Site.Id.Value}' references undeclared parameter '{parameterName}'.",
                    site.Analysis.Site.DiagnosticLocation);
                continue;
            }

            var input = new RelationQueryParameterInput(
                CreateParameterInputId(parameter.Id),
                parameter);
            requirements.Add(
                input,
                output,
                effect,
                parameter.Presence == FieldPresence.Required
                    ? QueryInputRequirement.Required
                    : QueryInputRequirement.Optional,
                CreateTrace(siteTrace));
        }

        foreach (var capability in site.Analysis.Requirements.Capabilities)
        {
            var input = new RelationQueryCapabilityInput(
                CreateCapabilityInputId(capability),
                capability);
            requirements.Add(
                input,
                output,
                RelationQueryRequirementEffect.Evaluation,
                requirement,
                CreateTrace(siteTrace));
        }

        var fieldBindings = site.Analysis.Requirements.Fields
            .Where(static field => field.Binding is not null)
            .Select(static field => field.Binding!.Value)
            .ToHashSet();
        foreach (var binding in site.Analysis.Requirements.Bindings.Where(binding => !fieldBindings.Contains(binding)))
        {
            if (!TryFindBindingShape(site, binding, out var shape)
                || FindBindingProducer(site.Node ?? output.Node, binding) is not { } producer)
            {
                AddError(
                    RelationQueryCompilationDiagnosticCodes.BindingRouteUnavailable,
                    $"Expression site '{site.Analysis.Site.Id.Value}' requires unresolvable binding '{binding.Value}'.",
                    site.Analysis.Site.DiagnosticLocation);
                continue;
            }

            var identity = new RelationQueryObservationIdentityInput(
                CreateIdentityInputId(producer, binding, shape),
                producer,
                binding,
                shape);
            requirements.Add(
                identity,
                output,
                effect,
                requirement,
                CreateTrace(siteTrace));
        }
    }

    void AddAggregateCapability(
        QueryNodeId aggregateNode,
        QueryAssignmentId assignment,
        AggregateOperator operation,
        RelationQueryOutputReference output,
        ImmutableArray<RelationQueryRequirementTraceStep> trace)
    {
        demandedAggregateAssignments.Add(new(aggregateNode, assignment));
        var capability = new ExprCapabilityRequirement(
            ExprCapabilities.ForAggregate(operation),
            ExprCapabilityRequirementKind.Operation);
        var input = new RelationQueryCapabilityInput(
            CreateCapabilityInputId(capability),
            capability);
        requirements.Add(
            input,
            output,
            RelationQueryRequirementEffect.Evaluation,
            QueryInputRequirement.Required,
            CreateTrace(AppendAggregateOperation(trace, aggregateNode, assignment)));
    }

    void AddTraversalCorrelation(
        TraverseRelationshipQueryNode traversal,
        RelationshipDefinition relationship,
        RelationshipTraversalCardinality cardinality,
        RelationQueryOutputReference output,
        ImmutableArray<RelationQueryRequirementTraceStep> trace)
    {
        _ = cardinality;
        if (traversal.Direction == RelationshipTraversalDirection.Forward)
        {
            WalkField(
                traversal.Input,
                traversal.From,
                relationship.SourceReference,
                RelationQueryRequirementEffect.Correlation,
                output,
                QueryInputRequirement.Required,
                trace,
                strictAssignment: true);

            var targetIdentity = new RelationQueryObservationIdentityInput(
                CreateIdentityInputId(traversal.Id, traversal.Result, relationship.TargetShape),
                traversal.Id,
                traversal.Result,
                relationship.TargetShape);
            requirements.Add(
                targetIdentity,
                output,
                RelationQueryRequirementEffect.Correlation,
                traversal.Requirement,
                CreateTrace(trace));
            return;
        }

        if (FindBindingProducer(traversal.Input, traversal.From) is { } fromProducer)
        {
            var sourceIdentity = new RelationQueryObservationIdentityInput(
                CreateIdentityInputId(fromProducer, traversal.From, relationship.TargetShape),
                fromProducer,
                traversal.From,
                relationship.TargetShape);
            requirements.Add(
                sourceIdentity,
                output,
                RelationQueryRequirementEffect.Correlation,
                QueryInputRequirement.Required,
                CreateTrace(trace));
        }
        else
        {
            AddError(
                RelationQueryCompilationDiagnosticCodes.BindingRouteUnavailable,
                $"Inverse traversal '{traversal.Id.Value}' cannot locate the producer of binding '{traversal.From.Value}'.",
                NodeLocation(traversal.Id));
        }

        var sourceReference = new RelationQueryFieldReference(
            relationship.SourceShape,
            relationship.SourceReference);
        var referenceInput = new RelationQueryFieldInput(
            CreateFieldInputId(traversal.Id, traversal.Result, sourceReference),
            traversal.Id,
            traversal.Result,
            sourceReference,
            GetValueContract(sourceReference));
        requirements.Add(
            referenceInput,
            output,
            RelationQueryRequirementEffect.Correlation,
            traversal.Requirement,
            CreateTrace(trace));
    }

    bool TryResolveTraversal(
        TraverseRelationshipQueryNode traversal,
        out RelationshipDefinition relationship,
        out RelationshipTraversalCardinality cardinality)
    {
        relationship = null!;
        cardinality = default;
        if (catalogDocument is null
            || !catalogDocument.Catalog.TryGetRelationship(traversal.Relationship, out var resolvedRelationship))
        {
            AddError(
                RelationQueryCompilationDiagnosticCodes.RelationshipUnavailable,
                $"Traversal '{traversal.Id.Value}' requires relationship '{traversal.Relationship.Value}' from an exact catalog snapshot.",
                $"{NodeLocation(traversal.Id)}/relationship");
            return false;
        }

        relationship = resolvedRelationship;

        if (traversal.Direction == RelationshipTraversalDirection.Inverse)
        {
            cardinality = relationship.InverseCardinality;
            return true;
        }

        if (!TryResolveField(relationship.SourceShape, relationship.SourceReference, out var sourceReference)
            || sourceReference is null)
        {
            AddError(
                RelationQueryCompilationDiagnosticCodes.RelationshipUnavailable,
                $"Relationship '{relationship.Id.Value}' source reference '{relationship.SourceReference}' cannot be resolved.",
                $"{NodeLocation(traversal.Id)}/relationship");
            return false;
        }

        cardinality = sourceReference.Cardinality == FieldCardinality.Many
            ? RelationshipTraversalCardinality.Many
            : RelationshipTraversalCardinality.AtMostOne;
        return true;
    }

    RelationQueryRelationshipInput CreateRelationshipInput(
        TraverseRelationshipQueryNode traversal,
        RelationshipDefinition relationship,
        RelationshipTraversalCardinality cardinality)
    {
        var fromShape = traversal.Direction == RelationshipTraversalDirection.Forward
            ? relationship.SourceShape
            : relationship.TargetShape;
        var resultShape = traversal.Direction == RelationshipTraversalDirection.Forward
            ? relationship.TargetShape
            : relationship.SourceShape;
        return new(
            CreateRelationshipInputId(traversal.Id),
            traversal.Id,
            relationship,
            traversal.Direction,
            traversal.From,
            fromShape,
            traversal.Result,
            resultShape,
            traversal.JoinKind,
            traversal.Requirement,
            cardinality);
    }

    bool TryGetSite(
        RelationQueryExpressionSiteKind kind,
        out RelationQueryExpressionSiteAnalysis site,
        QueryNodeId? node = null,
        QueryAssignmentId? assignment = null,
        int? ordinal = null,
        string? invariantName = null)
    {
        if (sites.TryGetValue(new(kind, node, assignment, ordinal, invariantName), out site!))
        {
            demandedSites[site.Analysis.Site.Id] = site;
            return true;
        }

        var location = node is { } owner
            ? NodeLocation(owner)
            : kind == RelationQueryExpressionSiteKind.RelationInvariant
                ? $"/definition/invariants/{Encode(invariantName)}"
                : "/definition/output/key";
        AddError(
            RelationQueryCompilationDiagnosticCodes.ExpressionSiteUnavailable,
            $"Expression analysis did not report required site '{kind}' at '{location}'.",
            location);
        return false;
    }

    bool TryResolveSiteFieldBinding(
        RelationQueryExpressionSiteAnalysis site,
        FieldExpr field,
        out ValueBindingId binding)
    {
        if (field.Binding is { } explicitBinding)
        {
            binding = explicitBinding;
            return true;
        }

        var candidates = site.Analysis.Requirements.Fields
            .Where(requirement => requirement.Root == ExprFieldRootKind.Binding
                && requirement.Binding is not null
                && requirement.Path == field.Path)
            .Select(static requirement => requirement.Binding!.Value)
            .Distinct()
            .Take(2)
            .ToArray();
        if (candidates.Length == 1)
        {
            binding = candidates[0];
            return true;
        }

        binding = default;
        return false;
    }

    bool TryResolveComposedSiteFieldPath(
        RelationQueryExpressionSiteAnalysis site,
        ValueBindingId binding,
        FieldPath prefix,
        IEnumerable<FieldPathSegment> suffix,
        out FieldPath path)
    {
        path = AppendPath(prefix, suffix);
        return TryFindBindingShape(site, binding, out var shape)
            && TryResolveField(shape, path, out _);
    }

    ImmutableArray<FieldPath> FindMissingAssignmentCoverage(
        QualifiedShapeId shape,
        FieldPath demandedPath,
        IEnumerable<FieldPath> assignmentPaths,
        bool strictAssignment)
    {
        var assignments = assignmentPaths
            .Distinct()
            .OrderBy(RelationQueryContractOrdering.FieldPathKey, StringComparer.Ordinal)
            .ToImmutableArray();
        if (assignments.Any(assignment => assignment.IsPrefixOf(demandedPath)))
            return [];
        if (!shapeGraphs.TryGetValue(shape.GraphId, out var graph)
            || !TryResolveField(shape, demandedPath, out var contract)
            || contract?.GetEffectiveType() is not { } demandedType)
        {
            return [demandedPath];
        }

        List<FieldPath> missing = [];
        Visit(demandedPath, demandedType, required: true);
        return
        [
            .. missing.Distinct()
                .OrderBy(RelationQueryContractOrdering.FieldPathKey, StringComparer.Ordinal)
        ];

        void Visit(FieldPath currentPath, TypeRef currentType, bool required)
        {
            if (assignments.Any(assignment => assignment.IsPrefixOf(currentPath)))
                return;

            var hasDescendantAssignment = assignments.Any(assignment =>
                assignment.Segments.Length > currentPath.Segments.Length
                && currentPath.IsPrefixOf(assignment));
            if (!hasDescendantAssignment)
            {
                if (required)
                    missing.Add(currentPath);
                return;
            }

            if (currentType is ArrayTypeRef array)
            {
                Visit(
                    AppendPath(currentPath, [FieldPathSegment.Element()]),
                    array.ElementType,
                    required: true);
                return;
            }

            var fields = currentType switch
            {
                ObjectTypeRef objectType =>
                [
                    .. objectType.Fields.Select(static field => (
                        field.Name,
                        field.Cardinality == FieldCardinality.Many
                            ? new ArrayTypeRef(field.Type)
                            : field.Type,
                        field.Presence))
                ],
                NamedTypeRef named
                    when graph.TryGetType(named.TypeId, out var definition)
                         && definition is TypeDefinition.Structural structural =>
                [
                    .. structural.Fields.Select(static field => (
                        field.Name.Value,
                        field.Cardinality == FieldCardinality.Many
                            ? new ArrayTypeRef(field.Type)
                            : field.Type,
                        field.Presence))
                ],
                _ => ImmutableArray<(string Name, TypeRef Type, FieldPresence Presence)>.Empty
            };
            if (fields.IsDefaultOrEmpty)
            {
                missing.Add(currentPath);
                return;
            }

            foreach (var field in fields.OrderBy(static field => field.Name, StringComparer.Ordinal))
            {
                Visit(
                    AppendPath(currentPath, [FieldPathSegment.ForField(field.Name)]),
                    field.Type,
                    strictAssignment || field.Presence == FieldPresence.Required);
            }
        }
    }

    static string FormatPaths(IEnumerable<FieldPath> paths) =>
        string.Join(", ", paths.Select(static path => $"'{path}'"));

    bool TryFindBindingShape(
        RelationQueryExpressionSiteAnalysis site,
        ValueBindingId binding,
        out QualifiedShapeId shape)
    {
        RelationQueryBindingEnvironment environment;
        if (site.Kind is RelationQueryExpressionSiteKind.RelationOutputKey
            or RelationQueryExpressionSiteKind.RelationInvariant)
        {
            var outputNode = definition is IRRelationDefinition relation
                ? relation.Output.Node
                : default;
            environment = bindingFlow.GetOutput(outputNode);
        }
        else if (site.Node is { } node)
        {
            environment = bindingFlow.GetInput(node);
        }
        else
        {
            environment = RelationQueryBindingEnvironment.Empty;
        }

        if (environment.TryGetValue(binding, out var found) && found.Shape is { } foundShape)
        {
            shape = foundShape;
            return true;
        }

        shape = default;
        return false;
    }

    QueryNodeId? FindBindingProducer(QueryNodeId nodeId, ValueBindingId binding)
    {
        HashSet<QueryNodeId> visited = [];
        return Find(nodeId);

        QueryNodeId? Find(QueryNodeId current)
        {
            if (!visited.Add(current) || !nodes.TryGetValue(current, out var node))
                return null;

            return node switch
            {
                SourceQueryNode source when source.Binding == binding => source.Id,
                SourceQueryNode => null,
                TraverseRelationshipQueryNode traversal when traversal.Result == binding => traversal.Id,
                TraverseRelationshipQueryNode traversal => Find(traversal.Input),
                ExpandCollectionQueryNode expansion when expansion.ItemBinding == binding => expansion.Id,
                ExpandCollectionQueryNode expansion => Find(expansion.Input),
                ProjectQueryNode project when project.ResultBinding == binding => project.Id,
                ProjectQueryNode => null,
                AggregateQueryNode aggregate when aggregate.ResultBinding == binding => aggregate.Id,
                AggregateQueryNode => null,
                JoinQueryNode join => FindJoin(join),
                TemporalJoinQueryNode join => FindBinaryJoin(join.Left, join.Right),
                FilterQueryNode filter => Find(filter.Input),
                DistinctQueryNode distinct => Find(distinct.Input),
                OrderQueryNode order => Find(order.Input),
                PageQueryNode page => Find(page.Input),
                _ => null
            };
        }

        QueryNodeId? FindJoin(JoinQueryNode join)
            => FindBinaryJoin(join.Left, join.Right);

        QueryNodeId? FindBinaryJoin(QueryNodeId left, QueryNodeId right)
        {
            var leftContains = bindingFlow.GetOutput(left).Contains(binding);
            var rightContains = bindingFlow.GetOutput(right).Contains(binding);
            return leftContains == rightContains
                ? null
                : Find(leftContains ? left : right);
        }
    }

    bool TryGetShape(QualifiedShapeId id, out Shape shape)
    {
        if (shapeGraphs.TryGetValue(id.GraphId, out var graph)
            && graph.TryGetShape(id.ShapeId, out var found))
        {
            shape = found;
            return true;
        }

        shape = null!;
        return false;
    }

    bool TryResolveField(
        QualifiedShapeId shape,
        FieldPath path,
        out ValueContract? contract)
    {
        if (shapeResolver.TryGetTargetExpectation(shape, path, out var expectation)
            && expectation.Value is { } value)
        {
            contract = value;
            return true;
        }

        contract = null;
        return false;
    }

    ValueContract? GetValueContract(RelationQueryFieldReference field) =>
        TryResolveField(field.Shape, field.Path, out var contract) ? contract : null;

    bool IsOptionalField(QualifiedShapeId shape, FieldPath path) =>
        TryResolveField(shape, path, out var contract)
        && contract?.Presence == FieldPresence.Optional;

    void Retain(QueryNodeId node)
    {
        retainedNodes.Add(node);
        bypassedTraversals.Remove(node);
    }

    void Bypass(
        TraverseRelationshipQueryNode traversal,
        RelationshipDefinition relationship,
        RelationshipTraversalCardinality cardinality)
    {
        if (retainedNodes.Contains(traversal.Id))
            return;
        bypassedTraversals[traversal.Id] = new(traversal, relationship, cardinality);
    }

    static bool HasAmbiguousOverlap(
        IReadOnlyList<ProjectionAssignment> assignments,
        FieldPath demand)
    {
        for (var left = 0; left < assignments.Count; left++)
        {
            for (var right = left + 1; right < assignments.Count; right++)
            {
                if (assignments[left].Target.Overlaps(assignments[right].Target))
                    return true;
            }
        }

        return assignments.Count > 1
            && assignments.Count(assignment => assignment.Target.IsPrefixOf(demand)) > 1;
    }

    static bool HasAmbiguousAggregateOverlap(
        IReadOnlyList<QueryGrouping> groupings,
        IReadOnlyList<QueryAggregateAssignment> aggregates,
        FieldPath demand)
    {
        var paths = groupings.Select(static grouping => grouping.Target)
            .Concat(aggregates.Select(static aggregate => aggregate.Target))
            .ToArray();
        for (var left = 0; left < paths.Length; left++)
        {
            for (var right = left + 1; right < paths.Length; right++)
            {
                if (paths[left].Overlaps(paths[right]))
                    return true;
            }
        }

        return paths.Length > 1 && paths.Count(path => path.IsPrefixOf(demand)) > 1;
    }

    static FieldPath AppendPath(
        FieldPath prefix,
        IEnumerable<FieldPathSegment> suffix) =>
        new([.. prefix.Segments, .. suffix]);

    RelationQueryOutputReference CreateRelationOutput(
        IRRelationDefinition relation,
        RelationQueryFieldReference? field) =>
        new(
            CreateOutputId("relation", relation.Id.Value, field),
            RelationQueryOutputReferenceKind.Relation,
            relation.Output.Node,
            relation.Output.Shape,
            relation: relation.Id,
            field: field);

    static RelationQueryOutputReference CreateQueryOutput(
        QueryDefinition query,
        QueryResultDefinition result,
        QualifiedShapeId shape,
        RelationQueryFieldReference? field) =>
        new(
            CreateOutputId("query", $"{query.Id.Value}/{result.Id.Value}", field),
            RelationQueryOutputReferenceKind.QueryResult,
            result.Input,
            shape,
            queryResult: result.Id,
            field: field);

    static RelationQueryOutputId CreateOutputId(
        string kind,
        string owner,
        RelationQueryFieldReference? field) =>
        new($"output/{kind}/{Encode(owner)}/{(field is null ? "row" : FieldKey(field.Value))}");

    static RelationQueryInputId CreateFieldInputId(
        QueryNodeId producer,
        ValueBindingId binding,
        RelationQueryFieldReference field) =>
        new($"input/field/{Encode(producer.Value)}/{Encode(binding.Value)}/{FieldKey(field)}");

    static RelationQueryInputId CreateIdentityInputId(
        QueryNodeId producer,
        ValueBindingId binding,
        QualifiedShapeId shape) =>
        new($"input/identity/{Encode(producer.Value)}/{Encode(binding.Value)}/{ShapeKey(shape)}");

    static RelationQueryInputId CreateSourceSetInputId(QueryNodeId source) =>
        RelationQueryInputIds.ForSource(source);

    static RelationQueryInputId CreateRelationshipInputId(QueryNodeId traversal) =>
        new($"input/relationship/{Encode(traversal.Value)}");

    static RelationQueryInputId CreateParameterInputId(QueryParameterId parameter) =>
        RelationQueryInputIds.ForParameter(parameter);

    static RelationQueryInputId CreateCapabilityInputId(ExprCapabilityRequirement capability) =>
        new($"input/capability/{((int)capability.Kind).ToString(CultureInfo.InvariantCulture)}/{Encode(capability.Capability.Value)}");

    static string FieldKey(RelationQueryFieldReference field) =>
        $"{ShapeKey(field.Shape)}/{Encode(RelationQueryContractOrdering.FieldPathKey(field.Path))}";

    static string ShapeKey(QualifiedShapeId shape) =>
        $"{Encode(shape.GraphId.Value)}/{Encode(shape.ShapeId.Value)}";

    static ImmutableArray<RelationQueryRequirementTraceStep> AppendStructural(
        ImmutableArray<RelationQueryRequirementTraceStep> trace,
        QueryNodeId node) =>
        AppendStep(trace, new(node));

    static ImmutableArray<RelationQueryRequirementTraceStep> AppendSite(
        ImmutableArray<RelationQueryRequirementTraceStep> trace,
        QueryNodeId anchor,
        RelationQueryExpressionSiteAnalysis site) =>
        AppendStep(
            trace,
            new(
                anchor,
                site.Kind,
                site.Analysis.Site.Id,
                site.Assignment,
                site.Ordinal,
                site.InvariantName));

    static ImmutableArray<RelationQueryRequirementTraceStep> AppendAggregateOperation(
        ImmutableArray<RelationQueryRequirementTraceStep> trace,
        QueryNodeId node,
        QueryAssignmentId assignment) =>
        AppendStep(trace, RelationQueryRequirementTraceStep.ForAggregateOperation(node, assignment));

    static ImmutableArray<RelationQueryRequirementTraceStep> AppendStep(
        ImmutableArray<RelationQueryRequirementTraceStep> trace,
        RelationQueryRequirementTraceStep step)
    {
        var normalized = trace.IsDefault ? ImmutableArray<RelationQueryRequirementTraceStep>.Empty : trace;
        return normalized.Length != 0 && normalized[^1] == step
            ? normalized
            : [.. normalized, step];
    }

    static RelationQueryRequirementTrace CreateTrace(
        ImmutableArray<RelationQueryRequirementTraceStep> steps) =>
        new(steps);

    void AddBindingRouteError(
        QueryNodeId node,
        ValueBindingId binding,
        FieldPath path) =>
        AddError(
            RelationQueryCompilationDiagnosticCodes.BindingRouteUnavailable,
            $"Node '{node.Value}' cannot route field '{path}' for binding '{binding.Value}'.",
            NodeLocation(node));

    void AddError(string code, string message, string location) =>
        diagnostics.Add(new(code, DiagnosticSeverity.Error, message, location));

    void AddWarning(string code, string message, string location) =>
        diagnostics.Add(new(code, DiagnosticSeverity.Warning, message, location));

    static DocumentValidationResult NormalizeValidation(
        IEnumerable<DocumentValidationDiagnostic> values) =>
        DocumentValidationResult.FromDiagnostics(
            values
                .Distinct()
                .OrderBy(static diagnostic => diagnostic.Location, StringComparer.Ordinal)
                .ThenBy(static diagnostic => diagnostic.SchemaLocation, StringComparer.Ordinal)
                .ThenBy(static diagnostic => diagnostic.Code, StringComparer.Ordinal)
                .ThenBy(static diagnostic => (int)diagnostic.Severity)
                .ThenBy(static diagnostic => diagnostic.Message, StringComparer.Ordinal));

    static string NodeLocation(QueryNodeId node) =>
        $"/definition/body/nodes/{Encode(node.Value)}";

    static string Encode(string? value) =>
        Uri.EscapeDataString(string.IsNullOrWhiteSpace(value) ? "missing" : value);

    readonly record struct SiteKey(
        RelationQueryExpressionSiteKind Kind,
        QueryNodeId? Node,
        QueryAssignmentId? Assignment,
        int? Ordinal,
        string? InvariantName)
    {
        public static SiteKey From(RelationQueryExpressionSiteAnalysis site) =>
            new(site.Kind, site.Node, site.Assignment, site.Ordinal, site.InvariantName);
    }

    readonly record struct FieldWalkKey(
        QueryNodeId Node,
        ValueBindingId Binding,
        FieldPath Path,
        RelationQueryRequirementEffect Effect,
        RelationQueryOutputId Output);

    readonly record struct RowWalkKey(
        QueryNodeId Node,
        RelationQueryRequirementEffect Effect,
        RelationQueryOutputId Output);

    internal sealed record TraversalResolution(
        TraverseRelationshipQueryNode Traversal,
        RelationshipDefinition Relationship,
        RelationshipTraversalCardinality Cardinality);

    sealed class RequirementAccumulator
    {
        readonly Dictionary<RelationQueryInputId, RelationQueryRequirementInput> inputs = [];
        readonly Dictionary<RelationQueryOutputId, RelationQueryOutputReference> outputs = [];
        readonly List<RelationQueryRequirementEdge> edges = [];

        public bool HasEdges => edges.Count != 0;

        public void AddOutput(RelationQueryOutputReference output)
        {
            Guard.RequireNotNull(output);
            if (outputs.TryGetValue(output.Id, out var existingOutput) && existingOutput != output)
            {
                throw new InvalidOperationException(
                    $"Requirement output id '{output.Id.Value}' has conflicting compiler definitions.");
            }
            outputs[output.Id] = output;
        }

        public void Add(
            RelationQueryRequirementInput input,
            RelationQueryOutputReference output,
            RelationQueryRequirementEffect effect,
            QueryInputRequirement requirement,
            RelationQueryRequirementTrace trace)
        {
            if (inputs.TryGetValue(input.Id, out var existingInput) && existingInput != input)
            {
                throw new InvalidOperationException(
                    $"Requirement input id '{input.Id.Value}' has conflicting compiler definitions.");
            }
            inputs[input.Id] = input;
            AddOutput(output);
            edges.Add(new(input, output, effect, requirement, [trace]));
        }

        public RelationQueryRequirementGraph Build() =>
            new([.. inputs.Values], [.. outputs.Values], [.. edges]);
    }
}
