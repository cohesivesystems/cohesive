using System.Collections.Immutable;
using System.Globalization;
using Cohesive.Model.Expressions;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Serialization;

namespace Cohesive.Relations.IR;

/// <summary>
/// Analyzes every canonical expression site in a relation or query definition.
/// </summary>
public static class RelationQueryExpressionAnalyzer
{
    static readonly ExprCapabilityProfile RelationLanguageProfile = CreateRelationLanguageProfile();
    static readonly ExprExpectation NullableComparableExpectation = new(
        ExprResultCategory.Comparable,
        new ExprValueContract(
            presence: FieldPresence.Optional,
            nullability: FieldNullability.Nullable));
    static readonly ExprExpectation NullableComparableParameterExpectation = new(
        ExprResultCategory.Comparable,
        new ExprValueContract(
            presence: FieldPresence.Optional,
            nullability: FieldNullability.Nullable),
        ExprDependencyKind.Parameter);
    static readonly ExprExpectation TemporalExpectation = new(ExprResultCategory.Temporal);
    static readonly ExprExpectation NullableTemporalExpectation = new(
        ExprResultCategory.Temporal,
        new ExprValueContract(nullability: FieldNullability.Nullable));
    static readonly ImmutableArray<ExprCapabilityId> RelationAmbientCapabilities =
    [
        ExprCapabilities.EntityIdentity,
        ExprCapabilities.RootKey,
        ExprCapabilities.SourceSet
    ];

    /// <summary>
    /// Analyzes expression scopes, expectations, requirements, and diagnostics without resolving a relationship catalog.
    /// </summary>
    /// <param name="definition">Canonical relation or query definition to analyze.</param>
    /// <returns>Deterministic per-site analysis and combined relation/query validation.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public static RelationQueryExpressionAnalysisResult Analyze(RelationQueryDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var bindingFlow = RelationQueryBindingFlowAnalyzer.Analyze(definition);
        return AnalyzeWithBindingFlow(definition, bindingFlow);
    }

    /// <summary>
    /// Analyzes expression sites using exact shape snapshots to resolve binding and assignment contracts.
    /// </summary>
    /// <param name="definition">Canonical relation or query definition to analyze.</param>
    /// <param name="shapeGraphs">
    /// Shape-graph snapshots retained for provenance; semantically invalid snapshots are diagnosed and quarantined
    /// from shape and target-field resolution.
    /// </param>
    /// <returns>Deterministic per-site analysis enriched with resolved shape and target contracts.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="definition"/> or <paramref name="shapeGraphs"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="shapeGraphs"/> contains a <see langword="null"/> graph or more than one graph with the same id.
    /// </exception>
    public static RelationQueryExpressionAnalysisResult Analyze(
        RelationQueryDefinition definition,
        IEnumerable<ShapeGraph> shapeGraphs)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var snapshots = NormalizeShapeGraphs(shapeGraphs);
        var bindingFlow = RelationQueryBindingFlowAnalyzer.Analyze(definition);
        return AnalyzeWithBindingFlow(
            definition,
            bindingFlow,
            shapeGraphs: snapshots,
            requireResolvedTemporalDomains: true);
    }

    /// <summary>
    /// Analyzes expression sites against an exact persisted relationship-catalog snapshot.
    /// </summary>
    /// <param name="definition">Canonical relation or query definition to analyze.</param>
    /// <param name="catalogDocument">Exact relationship-catalog snapshot used to resolve traversal bindings.</param>
    /// <returns>Deterministic per-site analysis, catalog-bound binding shapes, and combined validation.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="definition"/> or <paramref name="catalogDocument"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The catalog contains a value with no canonical relationship-catalog JSON encoding.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// The catalog contains a runtime type unsupported by the canonical JSON serializer.
    /// </exception>
    public static RelationQueryExpressionAnalysisResult AnalyzeWithCatalog(
        RelationQueryDefinition definition,
        RelationshipCatalogDocument catalogDocument)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(catalogDocument);
        return AnalyzeWithCatalogCore(
            definition,
            catalogDocument,
            [],
            requireResolvedTemporalDomains: false);
    }

    /// <summary>
    /// Analyzes expression sites against exact relationship-catalog and shape-graph snapshots.
    /// </summary>
    /// <param name="definition">Canonical relation or query definition to analyze.</param>
    /// <param name="catalogDocument">Exact relationship-catalog snapshot used to resolve traversal bindings.</param>
    /// <param name="shapeGraphs">
    /// Shape-graph snapshots retained for provenance; semantically invalid snapshots are diagnosed and quarantined
    /// from shape and target-field resolution.
    /// </param>
    /// <returns>Deterministic catalog-bound analysis enriched with resolved shape and target contracts.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="definition"/>, <paramref name="catalogDocument"/>, or
    /// <paramref name="shapeGraphs"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="shapeGraphs"/> contains a <see langword="null"/> graph or more than one graph with the same id.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The catalog contains a value with no canonical relationship-catalog JSON encoding.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// The catalog contains a runtime type unsupported by the canonical JSON serializer.
    /// </exception>
    public static RelationQueryExpressionAnalysisResult AnalyzeWithCatalog(
        RelationQueryDefinition definition,
        RelationshipCatalogDocument catalogDocument,
        IEnumerable<ShapeGraph> shapeGraphs)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(catalogDocument);
        return AnalyzeWithCatalogCore(
            definition,
            catalogDocument,
            NormalizeShapeGraphs(shapeGraphs),
            requireResolvedTemporalDomains: true);
    }

    static RelationQueryExpressionAnalysisResult AnalyzeWithCatalogCore(
        RelationQueryDefinition definition,
        RelationshipCatalogDocument catalogDocument,
        ImmutableArray<ShapeGraph> shapeGraphs,
        bool requireResolvedTemporalDomains)
    {
        var catalogValidation = RelationshipCatalogDocumentSemanticValidator.Validate(catalogDocument);
        var bindingFlow = RelationQueryBindingFlowAnalyzer.Analyze(definition, catalogDocument.Catalog);
        return AnalyzeWithBindingFlow(
            definition,
            bindingFlow,
            catalogDocument,
            DocumentValidationResult.Combine(
                catalogValidation,
                DocumentValidationResult.FromDiagnostics(bindingFlow.CatalogDiagnostics)),
            shapeGraphs,
            requireResolvedTemporalDomains);
    }

    /// <summary>Analyzes sites using an existing canonical binding-flow result.</summary>
    /// <param name="definition">Definition being analyzed.</param>
    /// <param name="bindingFlow">Binding flow for the definition.</param>
    /// <param name="catalogDocument">Optional exact catalog snapshot consumed by the flow.</param>
    /// <param name="additionalValidation">Optional validation to combine with structure and expression diagnostics.</param>
    /// <param name="shapeGraphs">Exact shape snapshots used to resolve shapes and target fields.</param>
    /// <param name="requireResolvedTemporalDomains">
    /// Whether every temporal-join operand must resolve to one exact portable temporal domain.
    /// </param>
    /// <returns>The complete expression analysis result.</returns>
    internal static RelationQueryExpressionAnalysisResult AnalyzeWithBindingFlow(
        RelationQueryDefinition definition,
        RelationQueryBindingFlowAnalysis bindingFlow,
        RelationshipCatalogDocument? catalogDocument = null,
        DocumentValidationResult? additionalValidation = null,
        ImmutableArray<ShapeGraph> shapeGraphs = default,
        bool requireResolvedTemporalDomains = false)
    {
        var snapshots = shapeGraphs.IsDefault ? ImmutableArray<ShapeGraph>.Empty : shapeGraphs;
        var (usableSnapshots, shapeValidation) = ValidateShapeSnapshots(snapshots);
        RelationQueryShapeResolver shapeResolver = new(usableSnapshots);
        var bindingShapeValidation = ValidateBindingShapeReferences(
            bindingFlow.BindingShapes,
            shapeResolver);
        var structuralValidation = RelationQueryDefinitionValidator.ValidateStructureWithBindingFlow(
            definition,
            bindingFlow);
        var parameters = CreateParameters(definition);
        List<DocumentValidationDiagnostic> siteDiagnostics = [];
        var siteAnalyses = AnalyzeSites(definition, bindingFlow, parameters, shapeResolver, siteDiagnostics);
        siteDiagnostics.AddRange(ValidateAggregateOperandTargetTypes(
            definition,
            siteAnalyses,
            shapeResolver));
        siteDiagnostics.AddRange(ValidateTemporalJoinDomains(
            siteAnalyses,
            requireKnownDomains: requireResolvedTemporalDomains));
        siteDiagnostics.AddRange(ValidateStaticTemporalIntervals(definition, siteAnalyses));
        var projectedDiagnostics = siteAnalyses
            .SelectMany(static site => ProjectDiagnostics(site.Analysis))
            .Concat(siteDiagnostics);
        var expressionValidation = DocumentValidationResult.FromDiagnostics(projectedDiagnostics);
        var combinedValidation = additionalValidation is null
            ? DocumentValidationResult.Combine(
                shapeValidation,
                bindingShapeValidation,
                structuralValidation,
                expressionValidation)
            : DocumentValidationResult.Combine(
                additionalValidation,
                shapeValidation,
                bindingShapeValidation,
                structuralValidation,
                expressionValidation);
        var validation = SortValidation(combinedValidation);

        return new(
            catalogDocument,
            snapshots,
            siteAnalyses,
            bindingFlow.BindingShapes,
            ExprRequirements.Combine(siteAnalyses.Select(static site => site.Analysis.Requirements)),
            validation);
    }

    static ImmutableArray<RelationQueryExpressionSiteAnalysis> AnalyzeSites(
        RelationQueryDefinition definition,
        RelationQueryBindingFlowAnalysis bindingFlow,
        ImmutableArray<ExprScopeParameter> parameters,
        RelationQueryShapeResolver shapeResolver,
        ICollection<DocumentValidationDiagnostic> diagnostics)
    {
        if (definition.Body is null)
            return [];

        List<PendingExpressionSite> sites = [];
        var prefix = DefinitionSitePrefix(definition);
        var ambientCapabilities = definition is RelationDefinition
            ? RelationAmbientCapabilities
            : ImmutableArray<ExprCapabilityId>.Empty;
        foreach (var node in (definition.Body.Nodes.IsDefault ? [] : definition.Body.Nodes)
                     .Where(static node => node is not null)
                     .GroupBy(static node => node.Id)
                     .Where(static group => group.Count() == 1)
                     .Select(static group => group.Single())
                     .OrderBy(static node => node.Id.Value, StringComparer.Ordinal))
        {
            var inputScope = CreateScope(
                bindingFlow.GetInput(node.Id),
                parameters,
                shapeResolver,
                ambientCapabilities);
            var nodePrefix = $"{prefix}/node/{Encode(node.Id.Value)}";
            var nodeLocation = NodeLocation(node.Id);
            switch (node)
            {
                case FilterQueryNode filter when filter.Predicate is not null:
                    sites.Add(CreateSite(
                        $"{nodePrefix}/filter/predicate",
                        filter.Predicate,
                        inputScope,
                        ExprExpectation.Boolean,
                        $"{nodeLocation}/predicate",
                        RelationQueryExpressionSiteKind.FilterPredicate,
                        node: filter.Id));
                    break;

                case JoinQueryNode join when join.Predicate is not null:
                    sites.Add(CreateSite(
                        $"{nodePrefix}/join/predicate",
                        join.Predicate,
                        inputScope,
                        ExprExpectation.Boolean,
                        $"{nodeLocation}/predicate",
                        RelationQueryExpressionSiteKind.JoinPredicate,
                        node: join.Id));
                    break;

                case TemporalJoinQueryNode temporalJoin
                    when temporalJoin.Correlation is not null && temporalJoin.Match is not null:
                    sites.Add(CreateSite(
                        $"{nodePrefix}/temporalJoin/correlation",
                        temporalJoin.Correlation,
                        inputScope,
                        ExprExpectation.Boolean,
                        $"{nodeLocation}/correlation",
                        RelationQueryExpressionSiteKind.TemporalJoinCorrelation,
                        node: temporalJoin.Id));
                    AddTemporalJoinSites(
                        sites,
                        temporalJoin,
                        CreateScope(
                            bindingFlow.GetOutput(temporalJoin.Left),
                            parameters,
                            shapeResolver,
                            ambientCapabilities),
                        CreateScope(
                            bindingFlow.GetOutput(temporalJoin.Right),
                            parameters,
                            shapeResolver,
                            ambientCapabilities),
                        nodePrefix,
                        nodeLocation);
                    break;

                case ExpandCollectionQueryNode expansion when expansion.Collection is not null:
                    sites.Add(CreateSite(
                        $"{nodePrefix}/expand/collection",
                        expansion.Collection,
                        inputScope,
                        new(ExprResultCategory.Collection),
                        $"{nodeLocation}/collection",
                        RelationQueryExpressionSiteKind.ExpandCollection,
                        node: expansion.Id));
                    break;

                case ProjectQueryNode project:
                    foreach (var assignment in (project.Assignments.IsDefault ? [] : project.Assignments)
                                 .Where(static assignment => assignment is not null)
                                 .OrderBy(static assignment => assignment.Id.Value, StringComparer.Ordinal))
                    {
                        if (assignment.Value is null)
                            continue;
                        var assignmentLocation = $"{nodeLocation}/assignments/{assignment.Id.Value}";
                        sites.Add(CreateSite(
                            $"{nodePrefix}/project/assignment/{Encode(assignment.Id.Value)}/value",
                            assignment.Value,
                            inputScope,
                            ResolveTargetExpectation(
                                shapeResolver,
                                project.ResultShape,
                                assignment.Target,
                                $"{assignmentLocation}/target",
                                diagnostics),
                            $"{assignmentLocation}/value",
                            RelationQueryExpressionSiteKind.ProjectionAssignmentValue,
                            node: project.Id,
                            assignment: assignment.Id));
                    }
                    break;

                case DistinctQueryNode distinct:
                    var distinctKeys = distinct.Keys.IsDefault ? [] : distinct.Keys;
                    for (var index = 0; index < distinctKeys.Length; index++)
                    {
                        if (distinctKeys[index] is not { } key)
                            continue;
                        sites.Add(CreateSite(
                            $"{nodePrefix}/distinct/key/{index}",
                            key,
                            inputScope,
                            ExprExpectation.Any,
                            $"{nodeLocation}/keys/{index}",
                            RelationQueryExpressionSiteKind.DistinctKey,
                            node: distinct.Id,
                            ordinal: index));
                    }
                    break;

                case AggregateQueryNode aggregate:
                    AddAggregateSites(
                        sites,
                        aggregate,
                        inputScope,
                        nodePrefix,
                        nodeLocation,
                        shapeResolver,
                        diagnostics);
                    break;

                case OrderQueryNode order:
                    var orderings = order.Orderings.IsDefault ? [] : order.Orderings;
                    for (var index = 0; index < orderings.Length; index++)
                    {
                        if (orderings[index]?.Key is not { } key)
                            continue;
                        sites.Add(CreateSite(
                            $"{nodePrefix}/order/key/{index}",
                            key,
                            inputScope,
                            NullableComparableExpectation,
                            $"{nodeLocation}/orderings/{index}/key",
                            RelationQueryExpressionSiteKind.OrderKey,
                            node: order.Id,
                            ordinal: index));
                    }
                    break;

                case PageQueryNode { Page: KeysetPageDefinition keyset } page:
                    var boundaryScope = CreateScope(
                        RelationQueryBindingEnvironment.Empty,
                        parameters,
                        shapeResolver,
                        []);
                    var after = keyset.After.IsDefault ? [] : keyset.After;
                    for (var index = 0; index < after.Length; index++)
                    {
                        if (after[index] is not { } boundary)
                            continue;
                        sites.Add(CreateSite(
                            $"{nodePrefix}/page/keyset/after/{index}",
                            boundary,
                            boundaryScope,
                            NullableComparableParameterExpectation,
                            $"{nodeLocation}/page/after/{index}",
                            RelationQueryExpressionSiteKind.KeysetBoundary,
                            node: page.Id,
                            ordinal: index));
                    }
                    break;
            }
        }

        if (definition is RelationDefinition relation && relation.Output is not null)
        {
            var outputScope = CreateScope(
                bindingFlow.GetOutput(relation.Output.Node),
                parameters,
                shapeResolver,
                ambientCapabilities);
            if (relation.Output.Key is { } outputKey)
            {
                sites.Add(CreateSite(
                    $"{prefix}/output/key",
                    outputKey,
                    outputScope,
                    ExprExpectation.Any,
                    "/definition/output/key",
                    RelationQueryExpressionSiteKind.RelationOutputKey));
            }

            AddInvariantSites(sites, relation, outputScope, prefix);
        }

        List<RelationQueryExpressionSiteAnalysis> analyses = [];
        foreach (var group in sites
                     .GroupBy(static site => site.Site.Id)
                     .OrderBy(static group => group.Key.Value, StringComparer.Ordinal))
        {
            var candidates = group.ToArray();
            if (candidates.Length > 1)
            {
                diagnostics.Add(new(
                    Code: "relationQuery.expression.site.duplicate",
                    Severity: DiagnosticSeverity.Error,
                    Message: $"Relation/query expression site identity '{group.Key.Value}' is declared more than once.",
                    Location: candidates
                        .Select(static site => site.Site.DiagnosticLocation)
                        .Order(StringComparer.Ordinal)
                        .First()));
                continue;
            }

            var candidate = candidates[0];
            analyses.Add(new(
                candidate.Kind,
                ExprAnalyzer.Analyze(candidate.Site, ExprSemanticsCatalog.Default),
                candidate.Node,
                candidate.Assignment,
                candidate.Ordinal,
                candidate.InvariantName));
        }

        return [.. analyses];
    }

    static IEnumerable<DocumentValidationDiagnostic> ValidateTemporalJoinDomains(
        ImmutableArray<RelationQueryExpressionSiteAnalysis> sites,
        bool requireKnownDomains)
    {
        foreach (var group in sites
                     .Where(static site => site.Kind is
                         RelationQueryExpressionSiteKind.TemporalJoinPoint
                         or RelationQueryExpressionSiteKind.TemporalJoinIntervalLowerBound
                         or RelationQueryExpressionSiteKind.TemporalJoinIntervalUpperBound)
                     .GroupBy(static site => site.Node!.Value)
                     .OrderBy(static group => group.Key.Value, StringComparer.Ordinal))
        {
            List<(RelationQueryExpressionSiteAnalysis Site, ScalarTypeKind Kind)> known = [];
            foreach (var site in group.OrderBy(static item => item.Analysis.Site.Id.Value, StringComparer.Ordinal))
            {
                if (site.Analysis.KnownResult?.GetEffectiveType() is ScalarTypeRef
                    {
                        Kind: ScalarTypeKind.Date or ScalarTypeKind.DateTime or ScalarTypeKind.Instant
                    } temporal)
                {
                    known.Add((site, temporal.Kind));
                    continue;
                }

                if (requireKnownDomains
                    && site.Analysis.IsValid
                    && site.Analysis.ResultCategory is ExprResultCategory.Any or ExprResultCategory.Temporal)
                {
                    yield return new(
                        Code: "relationQuery.temporalJoin.domainUnknown",
                        Severity: DiagnosticSeverity.Error,
                        Message: "A temporal join operand must resolve to Date, DateTime, or Instant during static compilation.",
                        Location: site.Analysis.Site.DiagnosticLocation);
                }
            }

            var domains = known
                .Select(static item => item.Kind)
                .Distinct()
                .Order()
                .ToArray();
            if (domains.Length <= 1)
                continue;

            yield return new(
                Code: "relationQuery.temporalJoin.domainMismatch",
                Severity: DiagnosticSeverity.Error,
                Message: $"Temporal join operands must use one exact temporal domain, but found {string.Join(", ", domains)}.",
                Location: $"{NodeLocation(group.Key)}/match");
        }
    }

    static IEnumerable<DocumentValidationDiagnostic> ValidateStaticTemporalIntervals(
        RelationQueryDefinition definition,
        ImmutableArray<RelationQueryExpressionSiteAnalysis> sites)
    {
        if (definition.Body is null)
            yield break;

        var domains = sites
            .Where(static site => site.Node is not null
                && site.Kind is RelationQueryExpressionSiteKind.TemporalJoinPoint
                    or RelationQueryExpressionSiteKind.TemporalJoinIntervalLowerBound
                    or RelationQueryExpressionSiteKind.TemporalJoinIntervalUpperBound)
            .Where(static site => site.Analysis.KnownResult?.GetEffectiveType() is ScalarTypeRef
            {
                Kind: ScalarTypeKind.Date or ScalarTypeKind.DateTime or ScalarTypeKind.Instant
            })
            .GroupBy(static site => site.Node!.Value)
            .ToDictionary(
                static group => group.Key,
                static group => group
                    .Select(static site => ((ScalarTypeRef)site.Analysis.KnownResult!.GetEffectiveType()!).Kind)
                    .Distinct()
                    .ToArray());

        foreach (var join in (definition.Body.Nodes.IsDefault ? [] : definition.Body.Nodes)
                     .OfType<TemporalJoinQueryNode>()
                     .OrderBy(static node => node.Id.Value, StringComparer.Ordinal))
        {
            if (!domains.TryGetValue(join.Id, out var nodeDomains) || nodeDomains.Length != 1)
                continue;

            var domain = nodeDomains[0];
            IEnumerable<(TemporalInterval Interval, string Location)> intervals = join.Match switch
            {
                TemporalPointInIntervalMatch point =>
                [
                    (point.Interval, $"{NodeLocation(join.Id)}/match/interval")
                ],
                TemporalIntervalOverlapMatch overlap =>
                [
                    (overlap.Left, $"{NodeLocation(join.Id)}/match/left"),
                    (overlap.Right, $"{NodeLocation(join.Id)}/match/right")
                ],
                _ => []
            };
            foreach (var (interval, location) in intervals)
            {
                if (!TryGetStaticBoundValue(interval.Lower, out var lower)
                    || !TryGetStaticBoundValue(interval.Upper, out var upper)
                    || !RelationQueryTemporalValueSemantics.TryCompare(domain, lower, upper, out var comparison)
                    || comparison <= 0)
                {
                    continue;
                }

                yield return new(
                    Code: "relationQuery.temporalJoin.intervalInvalid",
                    Severity: DiagnosticSeverity.Error,
                    Message: "A temporal interval lower bound cannot follow its upper bound.",
                    Location: location);
            }
        }
    }

    static bool TryGetStaticBoundValue(
        TemporalIntervalBound bound,
        out ObservationValue value)
    {
        if (bound is ExpressionTemporalIntervalBound expression)
        {
            value = expression.Value switch
            {
                ConstantExpr constant => constant.Value,
                LiteralExpr literal => literal.Value,
                _ => ObservationValue.Undefined
            };
            return value.Kind is not ObservationValueKind.Undefined
                and not ObservationValueKind.Null;
        }

        value = ObservationValue.Undefined;
        return false;
    }

    static void AddAggregateSites(
        ICollection<PendingExpressionSite> sites,
        AggregateQueryNode aggregate,
        ExprScope scope,
        string nodePrefix,
        string nodeLocation,
        RelationQueryShapeResolver shapeResolver,
        ICollection<DocumentValidationDiagnostic> diagnostics)
    {
        foreach (var grouping in (aggregate.Groupings.IsDefault ? [] : aggregate.Groupings)
                     .Where(static grouping => grouping is not null)
                     .OrderBy(static grouping => grouping.Id.Value, StringComparer.Ordinal))
        {
            if (grouping.Key is null)
                continue;
            sites.Add(CreateSite(
                $"{nodePrefix}/aggregate/grouping/{Encode(grouping.Id.Value)}/key",
                grouping.Key,
                scope,
                ResolveTargetExpectation(
                    shapeResolver,
                    aggregate.ResultShape,
                    grouping.Target,
                    $"{nodeLocation}/groupings/{grouping.Id.Value}/target",
                    diagnostics),
                $"{nodeLocation}/groupings/{grouping.Id.Value}/key",
                RelationQueryExpressionSiteKind.AggregateGroupingKey,
                node: aggregate.Id,
                assignment: grouping.Id));
        }

        foreach (var assignment in (aggregate.Aggregates.IsDefault ? [] : aggregate.Aggregates)
                     .Where(static assignment => assignment is not null)
                     .OrderBy(static assignment => assignment.Id.Value, StringComparer.Ordinal))
        {
            var assignmentPrefix = $"{nodePrefix}/aggregate/assignment/{Encode(assignment.Id.Value)}";
            var assignmentLocation = $"{nodeLocation}/aggregates/{assignment.Id.Value}";
            var targetExpectation = ResolveTargetExpectation(
                shapeResolver,
                aggregate.ResultShape,
                assignment.Target,
                $"{assignmentLocation}/target",
                diagnostics);
            ValidateAggregateResultTarget(
                assignment.Operation,
                targetExpectation,
                $"{assignmentLocation}/target",
                diagnostics);
            if (assignment.Value is { } value)
            {
                var expectation = GetAggregateValueExpectation(assignment.Operation);
                sites.Add(CreateSite(
                    $"{assignmentPrefix}/value",
                    value,
                    scope,
                    expectation,
                    $"{assignmentLocation}/value",
                    RelationQueryExpressionSiteKind.AggregateAssignmentValue,
                    node: aggregate.Id,
                    assignment: assignment.Id));
            }

            if (assignment.Filter is { } filter)
            {
                sites.Add(CreateSite(
                    $"{assignmentPrefix}/filter",
                    filter,
                    scope,
                    ExprExpectation.Boolean,
                    $"{assignmentLocation}/filter",
                    RelationQueryExpressionSiteKind.AggregateAssignmentFilter,
                    node: aggregate.Id,
                    assignment: assignment.Id));
            }
        }
    }

    static void AddTemporalJoinSites(
        ICollection<PendingExpressionSite> sites,
        TemporalJoinQueryNode temporalJoin,
        ExprScope leftScope,
        ExprScope rightScope,
        string nodePrefix,
        string nodeLocation)
    {
        switch (temporalJoin.Match)
        {
            case TemporalPointInIntervalMatch pointInInterval:
                if (pointInInterval.Point is not null)
                {
                    sites.Add(CreateSite(
                        $"{nodePrefix}/temporalJoin/pointInInterval/point",
                        pointInInterval.Point,
                        leftScope,
                        TemporalExpectation,
                        $"{nodeLocation}/match/point",
                        RelationQueryExpressionSiteKind.TemporalJoinPoint,
                        node: temporalJoin.Id));
                }
                AddTemporalIntervalSites(
                    sites,
                    temporalJoin.Id,
                    pointInInterval.Interval,
                    intervalOrdinal: 0,
                    rightScope,
                    $"{nodePrefix}/temporalJoin/pointInInterval/interval/0",
                    $"{nodeLocation}/match/interval");
                break;

            case TemporalIntervalOverlapMatch overlap:
                AddTemporalIntervalSites(
                    sites,
                    temporalJoin.Id,
                    overlap.Left,
                    intervalOrdinal: 0,
                    leftScope,
                    $"{nodePrefix}/temporalJoin/intervalOverlap/interval/0",
                    $"{nodeLocation}/match/left");
                AddTemporalIntervalSites(
                    sites,
                    temporalJoin.Id,
                    overlap.Right,
                    intervalOrdinal: 1,
                    rightScope,
                    $"{nodePrefix}/temporalJoin/intervalOverlap/interval/1",
                    $"{nodeLocation}/match/right");
                break;
        }
    }

    static void AddTemporalIntervalSites(
        ICollection<PendingExpressionSite> sites,
        QueryNodeId node,
        TemporalInterval? interval,
        int intervalOrdinal,
        ExprScope scope,
        string sitePrefix,
        string location)
    {
        if (interval is null)
            return;

        AddTemporalBoundSite(
            sites,
            node,
            interval.Lower,
            intervalOrdinal,
            RelationQueryExpressionSiteKind.TemporalJoinIntervalLowerBound,
            scope,
            $"{sitePrefix}/lower",
            $"{location}/lower");
        AddTemporalBoundSite(
            sites,
            node,
            interval.Upper,
            intervalOrdinal,
            RelationQueryExpressionSiteKind.TemporalJoinIntervalUpperBound,
            scope,
            $"{sitePrefix}/upper",
            $"{location}/upper");
    }

    static void AddTemporalBoundSite(
        ICollection<PendingExpressionSite> sites,
        QueryNodeId node,
        TemporalIntervalBound? bound,
        int intervalOrdinal,
        RelationQueryExpressionSiteKind kind,
        ExprScope scope,
        string siteId,
        string location)
    {
        if (bound is not ExpressionTemporalIntervalBound { Value: not null } expressionBound)
            return;

        sites.Add(CreateSite(
            siteId,
            expressionBound.Value,
            scope,
            expressionBound.NullBehavior == TemporalNullBoundBehavior.Unbounded
                ? NullableTemporalExpectation
                : TemporalExpectation,
            $"{location}/value",
            kind,
            node,
            ordinal: intervalOrdinal));
    }

    static void AddInvariantSites(
        ICollection<PendingExpressionSite> sites,
        RelationDefinition relation,
        ExprScope outputScope,
        string prefix)
    {
        var invariants = (relation.Invariants.IsDefault ? [] : relation.Invariants)
            .Select(static (invariant, index) => (Invariant: invariant, Index: index))
            .Where(static item => item.Invariant is not null
                && !string.IsNullOrWhiteSpace(item.Invariant.Name))
            .OrderBy(static item => item.Invariant.Name, StringComparer.Ordinal)
            .ThenBy(static item => item.Index)
            .ToArray();

        foreach (var (invariant, index) in invariants)
        {
            if (invariant.Expression is null)
                continue;
            sites.Add(CreateSite(
                $"{prefix}/invariant/{Encode(invariant.Name)}",
                invariant.Expression,
                outputScope,
                ExprExpectation.Boolean,
                $"/definition/invariants/{index}/expression",
                RelationQueryExpressionSiteKind.RelationInvariant,
                invariantName: invariant.Name));
        }
    }

    static PendingExpressionSite CreateSite(
        string id,
        Expr expression,
        ExprScope scope,
        ExprExpectation expectation,
        string location,
        RelationQueryExpressionSiteKind kind,
        QueryNodeId? node = null,
        QueryAssignmentId? assignment = null,
        int? ordinal = null,
        string? invariantName = null) =>
        new(
            new(
                new(id),
                expression,
                scope,
                expectation,
                RelationLanguageProfile,
                diagnosticLocation: location),
            kind,
            node,
            assignment,
            ordinal,
            invariantName);

    static ExprScope CreateScope(
        RelationQueryBindingEnvironment environment,
        ImmutableArray<ExprScopeParameter> parameters,
        RelationQueryShapeResolver shapeResolver,
        ImmutableArray<ExprCapabilityId> ambientCapabilities)
    {
        var bindings = environment.Bindings
            .Where(static binding => !string.IsNullOrWhiteSpace(binding.Key.Value))
            .OrderBy(static binding => binding.Key.Value, StringComparer.Ordinal)
            .Select(binding => new ExprScopeBinding(
                binding.Key,
                shapeResolver.GetBindingContract(binding.Value),
                binding.Value.Availability == RelationQueryBindingAvailability.AlwaysPresent
                    ? ExprBindingAvailability.AlwaysPresent
                    : ExprBindingAvailability.MayBeAbsent))
            .ToImmutableArray();
        var implicitBinding = bindings.Length == 1 ? bindings[0].Id : (ValueBindingId?)null;
        return new(
            bindings,
            implicitBinding,
            parameters,
            ambientCapabilities: ambientCapabilities.IsDefault ? [] : ambientCapabilities);
    }

    static ImmutableArray<ExprScopeParameter> CreateParameters(RelationQueryDefinition definition)
    {
        if (definition.Body?.Parameters.IsDefault != false)
            return [];

        return
        [
            .. definition.Body.Parameters
                .Where(static parameter => parameter is not null
                    && !string.IsNullOrWhiteSpace(parameter.Id.Value)
                    && parameter.Type is not null
                    && Enum.IsDefined(parameter.Presence)
                    && Enum.IsDefined(parameter.DefaultKind))
                .GroupBy(static parameter => parameter.Id.Value, StringComparer.Ordinal)
                .Where(static group => group.Count() == 1)
                .Select(static group => group.Single())
                .OrderBy(static parameter => parameter.Id.Value, StringComparer.Ordinal)
                .Select(static parameter => CreateParameter(parameter))
        ];
    }

    static ExprScopeParameter CreateParameter(QueryParameterDefinition parameter) =>
        new(parameter.Id.Value, parameter.EffectiveValueContract, parameter.Presence);

    static ExprExpectation GetAggregateValueExpectation(AggregateOperator operation) => operation switch
    {
        AggregateOperator.Sum or AggregateOperator.Average => new(ExprResultCategory.Numeric),
        AggregateOperator.Min or AggregateOperator.Max => new(ExprResultCategory.Comparable),
        AggregateOperator.Any or AggregateOperator.All => ExprExpectation.Boolean,
        _ => ExprExpectation.Any
    };

    static void ValidateAggregateResultTarget(
        AggregateOperator operation,
        ExprExpectation targetExpectation,
        string location,
        ICollection<DocumentValidationDiagnostic> diagnostics)
    {
        if (targetExpectation.Value is not { } target || !Enum.IsDefined(operation))
            return;

        var targetType = target.GetEffectiveType();
        var targetMatches = operation switch
        {
            AggregateOperator.Count => targetType == new ScalarTypeRef(ScalarTypeKind.Int64),
            AggregateOperator.Sum => IsNumeric(target),
            AggregateOperator.Average => targetType == new ScalarTypeRef(ScalarTypeKind.Decimal),
            AggregateOperator.Min or AggregateOperator.Max => IsComparable(target),
            AggregateOperator.Any or AggregateOperator.All =>
                targetType == new ScalarTypeRef(ScalarTypeKind.Bool),
            _ => true
        };
        if (targetMatches && target.Cardinality == FieldCardinality.Single)
            return;

        diagnostics.Add(new(
            Code: "relationQuery.expression.resultTypeMismatch",
            Severity: DiagnosticSeverity.Error,
            Message: $"Aggregate operation '{operation}' result does not satisfy target field contract.",
            Location: location));
    }

    static IEnumerable<DocumentValidationDiagnostic> ValidateAggregateOperandTargetTypes(
        RelationQueryDefinition definition,
        ImmutableArray<RelationQueryExpressionSiteAnalysis> sites,
        RelationQueryShapeResolver shapeResolver)
    {
        if (definition.Body is null)
            yield break;

        var valueSites = sites
            .Where(static site => site.Kind == RelationQueryExpressionSiteKind.AggregateAssignmentValue
                && site.Node is not null
                && site.Assignment is not null)
            .GroupBy(static site => (Node: site.Node!.Value, Assignment: site.Assignment!.Value))
            .Where(static group => group.Count() == 1)
            .ToDictionary(static group => group.Key, static group => group.Single());

        foreach (var aggregate in definition.Body.Nodes
                     .OfType<AggregateQueryNode>()
                     .GroupBy(static node => node.Id)
                     .Where(static group => group.Count() == 1)
                     .Select(static group => group.Single())
                     .OrderBy(static node => node.Id.Value, StringComparer.Ordinal))
        {
            foreach (var assignment in aggregate.Aggregates
                         .Where(static assignment => assignment is not null)
                         .GroupBy(static assignment => assignment.Id)
                         .Where(static group => group.Count() == 1)
                         .Select(static group => group.Single())
                         .OrderBy(static assignment => assignment.Id.Value, StringComparer.Ordinal))
            {
                if (assignment.Value is null
                    || !valueSites.TryGetValue((aggregate.Id, assignment.Id), out var valueSite)
                    || valueSite.Analysis.KnownResult is not { } operand
                    || !shapeResolver.TryGetTargetExpectation(
                        aggregate.ResultShape,
                        assignment.Target,
                        out var targetExpectation)
                    || targetExpectation.Value is not { } target)
                {
                    continue;
                }

                var operandType = operand.GetEffectiveType();
                var targetType = target.GetEffectiveType();
                var requiresExactType = assignment.Operation switch
                {
                    AggregateOperator.Sum => IsNumeric(operand) && IsNumeric(target),
                    AggregateOperator.Min or AggregateOperator.Max =>
                        IsComparable(operand) && IsComparable(target),
                    _ => false
                };
                if (!requiresExactType
                    || operandType is null
                    || targetType is null
                    || operandType == targetType)
                {
                    continue;
                }

                var contract = assignment.Operation == AggregateOperator.Sum
                    ? "the same exact numeric type"
                    : "the same exact comparable type";
                yield return new(
                    Code: "relationQuery.expression.resultTypeMismatch",
                    Severity: DiagnosticSeverity.Error,
                    Message: $"Aggregate operation '{assignment.Operation}' requires its operand and target to use {contract}.",
                    Location: $"{NodeLocation(aggregate.Id)}/aggregates/{assignment.Id.Value}/target");
            }
        }
    }

    static bool IsNumeric(ExprValueContract value) =>
        value.GetResultCategory() is ExprResultCategory.Numeric or ExprResultCategory.Integer;

    static bool IsComparable(ExprValueContract value) =>
        value.GetResultCategory() is ExprResultCategory.Numeric
            or ExprResultCategory.Integer
            or ExprResultCategory.Text
            or ExprResultCategory.Temporal;

    static ExprExpectation ResolveTargetExpectation(
        RelationQueryShapeResolver shapeResolver,
        QualifiedShapeId shape,
        FieldPath target,
        string location,
        ICollection<DocumentValidationDiagnostic> diagnostics)
    {
        if (shapeResolver.TryGetTargetExpectation(shape, target, out var expectation))
            return expectation;

        if (shapeResolver.HasGraph(shape.GraphId)
            && !target.Segments.IsDefaultOrEmpty)
        {
            diagnostics.Add(new(
                Code: "relationQuery.expression.targetFieldUnknown",
                Severity: DiagnosticSeverity.Error,
                Message: $"Target field '{SafePath(target)}' cannot be resolved in shape '{shape}'.",
                Location: location));
        }

        return ExprExpectation.Any;
    }

    static string SafePath(FieldPath path) => string.Join(
        FieldPath.Separator,
        path.Segments.Select(static segment => segment.Kind switch
        {
            SegmentKind.Field => segment.Segment ?? "<missing>",
            SegmentKind.Element => "[]",
            _ => $"<invalid:{((int)segment.Kind).ToString(CultureInfo.InvariantCulture)}>"
        }));

    static ImmutableArray<ShapeGraph> NormalizeShapeGraphs(IEnumerable<ShapeGraph> shapeGraphs)
    {
        ArgumentNullException.ThrowIfNull(shapeGraphs);
        var snapshots = shapeGraphs.ToImmutableArray();
        if (snapshots.Any(static graph => graph is null))
            throw new ArgumentException("Shape snapshots cannot contain null graphs.", nameof(shapeGraphs));

        var duplicate = snapshots
            .GroupBy(static graph => graph.Id)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException(
                $"Shape graph id '{duplicate.Key.Value}' is supplied more than once.",
                nameof(shapeGraphs));
        }

        return [.. snapshots.OrderBy(static graph => graph.Id.Value, StringComparer.Ordinal)];
    }

    static (ImmutableArray<ShapeGraph> Usable, DocumentValidationResult Validation)
        ValidateShapeSnapshots(ImmutableArray<ShapeGraph> shapeGraphs)
    {
        List<ShapeGraph> usable = [];
        List<DocumentValidationDiagnostic> diagnostics = [];
        foreach (var graph in shapeGraphs)
        {
            var validation = ShapeGraphDocumentSemanticValidator.Validate(
                ShapeGraphDocument.FromGraph(graph));
            var prefix = $"/shapeGraphs/{Encode(graph.Id.Value)}";
            diagnostics.AddRange(validation.Diagnostics.Select(diagnostic => diagnostic with
            {
                Location = diagnostic.Location is null
                    ? prefix
                    : $"{prefix}{diagnostic.Location}"
            }));
            if (validation.IsValid)
                usable.Add(graph);
        }

        return (
            [.. usable],
            DocumentValidationResult.FromDiagnostics(diagnostics));
    }

    static DocumentValidationResult ValidateBindingShapeReferences(
        ImmutableArray<RelationQueryBindingShape> bindings,
        RelationQueryShapeResolver shapeResolver) =>
        DocumentValidationResult.FromDiagnostics(
            bindings
                .Where(binding => binding.Shape is { } shape
                    && shapeResolver.HasGraph(shape.GraphId)
                    && !shapeResolver.HasShape(shape))
                .Select(static binding => binding.Shape!.Value)
                .Distinct()
                .OrderBy(static shape => shape.GraphId.Value, StringComparer.Ordinal)
                .ThenBy(static shape => shape.ShapeId.Value, StringComparer.Ordinal)
                .Select(static shape => new DocumentValidationDiagnostic(
                    Code: "relationQuery.binding.shapeUnknown",
                    Severity: DiagnosticSeverity.Error,
                    Message: $"Binding shape '{shape}' is absent from the supplied shape-graph snapshot.",
                    Location: $"/shapeGraphs/{Encode(shape.GraphId.Value)}/shapes/{Encode(shape.ShapeId.Value)}")));

    static IEnumerable<DocumentValidationDiagnostic> ProjectDiagnostics(ExprAnalysisResult analysis)
    {
        foreach (var diagnostic in analysis.Validation.Diagnostics)
        {
            if (diagnostic.Code == ExprAnalysisDiagnosticCodes.CapabilityUnsupported
                && HasUnsatisfiedCapabilityUse(
                    analysis,
                    diagnostic,
                    ExprCapabilities.CurrentItem)
                && analysis.Validation.Diagnostics.Any(candidate =>
                    candidate.Code == ExprAnalysisDiagnosticCodes.CurrentItemUnavailable
                    && string.Equals(candidate.SchemaLocation, diagnostic.SchemaLocation, StringComparison.Ordinal)))
            {
                continue;
            }

            var code = diagnostic.Code switch
            {
                ExprAnalysisDiagnosticCodes.ExpressionMissing => "relationQuery.expression.missing",
                ExprAnalysisDiagnosticCodes.BindingNotVisible => "relationQuery.expression.bindingMissing",
                ExprAnalysisDiagnosticCodes.BindingInvalid => "relationQuery.expression.bindingInvalid",
                ExprAnalysisDiagnosticCodes.ImplicitBindingUnavailable => "relationQuery.expression.fieldBindingAmbiguous",
                ExprAnalysisDiagnosticCodes.ParameterInvalid => "relationQuery.expression.parameterIdMissing",
                ExprAnalysisDiagnosticCodes.ParameterNotDeclared => "relationQuery.expression.parameterMissing",
                ExprAnalysisDiagnosticCodes.CurrentItemUnavailable => "relationQuery.expression.currentItemUnsupported",
                ExprAnalysisDiagnosticCodes.DependencyNotAllowed
                    when analysis.Site.Expectation.AllowedDependencies == ExprDependencyKind.Parameter =>
                    "relationQuery.page.keysetBoundaryRowDependent",
                ExprAnalysisDiagnosticCodes.DependencyNotAllowed => "relationQuery.expression.dependencyNotAllowed",
                ExprAnalysisDiagnosticCodes.FieldPathInvalid => "relationQuery.fieldPath.invalid",
                ExprAnalysisDiagnosticCodes.FieldPathUnknown => "relationQuery.expression.fieldPathUnknown",
                ExprAnalysisDiagnosticCodes.CapabilityUnsupported
                    when IsAggregateCapabilityDiagnostic(analysis, diagnostic) =>
                    "relationQuery.expression.aggregateUnsupported",
                ExprAnalysisDiagnosticCodes.CapabilityUnsupported
                    when HasUnsatisfiedCapabilityUse(
                        analysis,
                        diagnostic,
                        ExprCapabilities.CurrentItem) =>
                    "relationQuery.expression.currentItemUnsupported",
                ExprAnalysisDiagnosticCodes.CapabilityUnsupported => "relationQuery.expression.capabilityUnsupported",
                ExprAnalysisDiagnosticCodes.AmbientCapabilityUnavailable => "relationQuery.expression.ambientCapabilityUnavailable",
                ExprAnalysisDiagnosticCodes.FunctionUnknown => "relationQuery.expression.functionUnknown",
                ExprAnalysisDiagnosticCodes.FunctionArityInvalid => "relationQuery.expression.functionArityInvalid",
                ExprAnalysisDiagnosticCodes.OperationUnknown => "relationQuery.expression.operationUnknown",
                ExprAnalysisDiagnosticCodes.ResultCategoryMismatch => "relationQuery.expression.resultCategoryMismatch",
                ExprAnalysisDiagnosticCodes.ResultTypeMismatch => "relationQuery.expression.resultTypeMismatch",
                ExprAnalysisDiagnosticCodes.NodeUnsupported => "relationQuery.expression.nodeUnsupported",
                _ => $"relationQuery.{diagnostic.Code}"
            };
            yield return diagnostic with { Code = code };
        }
    }

    static bool IsAggregateCapabilityDiagnostic(
        ExprAnalysisResult analysis,
        DocumentValidationDiagnostic diagnostic) =>
        ExprSemanticsCatalog.Default.AggregateOperators.Any(definition =>
            HasUnsatisfiedCapabilityUse(analysis, diagnostic, definition.OperationCapability));

    static bool HasUnsatisfiedCapabilityUse(
        ExprAnalysisResult analysis,
        DocumentValidationDiagnostic diagnostic,
        ExprCapabilityId capability) =>
        analysis.CapabilityUses.Any(use =>
            !use.IsSatisfied
            && use.Requirement.Kind == ExprCapabilityRequirementKind.Operation
            && use.Requirement.Capability == capability
            && string.Equals(use.ExpressionPath, diagnostic.SchemaLocation, StringComparison.Ordinal));

    static DocumentValidationResult SortValidation(DocumentValidationResult validation) =>
        DocumentValidationResult.FromDiagnostics(
            validation.Diagnostics
                .OrderBy(static diagnostic => diagnostic.Location, StringComparer.Ordinal)
                .ThenBy(static diagnostic => diagnostic.SchemaLocation, StringComparer.Ordinal)
                .ThenBy(static diagnostic => diagnostic.Code, StringComparer.Ordinal)
                .ThenBy(static diagnostic => diagnostic.Message, StringComparer.Ordinal));

    static ExprCapabilityProfile CreateRelationLanguageProfile()
    {
        var semantics = ExprSemanticsCatalog.Default;
        HashSet<ExprCapabilityId> excluded =
        [
            .. semantics.AggregateOperators.Select(static definition => definition.OperationCapability)
        ];
        return new(semantics.Capabilities.Where(capability => !excluded.Contains(capability)));
    }

    static string DefinitionSitePrefix(RelationQueryDefinition definition) => definition switch
    {
        RelationDefinition relation => $"relation/{Encode(relation.Id?.Value)}",
        QueryDefinition query => $"query/{Encode(query.Id.Value)}",
        _ => "relation-query/unknown"
    };

    static string Encode(string? value) =>
        Uri.EscapeDataString(string.IsNullOrWhiteSpace(value) ? "missing" : value);

    static string NodeLocation(QueryNodeId nodeId) => $"/definition/body/nodes/{nodeId.Value}";

    sealed record PendingExpressionSite(
        ExprSite Site,
        RelationQueryExpressionSiteKind Kind,
        QueryNodeId? Node,
        QueryAssignmentId? Assignment,
        int? Ordinal,
        string? InvariantName);
}

/// <summary>
/// Resolves expression value and assignment-target contracts from exact shape-graph snapshots.
/// </summary>
sealed class RelationQueryShapeResolver
{
    const int MaximumStructuralExpansionDepth = 64;
    readonly ImmutableDictionary<GraphId, ShapeGraph> graphs;
    // Resolver instances are scoped to one analysis, so this cache shares immutable transient expansions across sites.
    readonly Dictionary<QualifiedShapeId, Shape> expandedShapes = [];

    /// <summary>Creates a resolver over deterministic shape snapshots.</summary>
    /// <param name="shapeGraphs">Exact shape-graph snapshots keyed by unique graph identity.</param>
    public RelationQueryShapeResolver(ImmutableArray<ShapeGraph> shapeGraphs)
    {
        graphs = shapeGraphs.ToImmutableDictionary(static graph => graph.Id);
    }

    /// <summary>Gets the richest value contract available for a binding-flow value.</summary>
    /// <param name="binding">Binding-flow shape, type, and availability analysis.</param>
    /// <returns>A portable value contract enriched from a matching shape snapshot when available.</returns>
    public ExprValueContract GetBindingContract(RelationQueryBindingAnalysis binding)
    {
        var bindingShape = binding.Shape is { } shapeIdentity && IsUsableShapeIdentity(shapeIdentity)
            ? binding.Shape
            : null;

        if (bindingShape is { } qualifiedShape
            && graphs.TryGetValue(qualifiedShape.GraphId, out var graph)
            && graph.TryGetShape(qualifiedShape.ShapeId, out var shape))
        {
            if (!expandedShapes.TryGetValue(qualifiedShape, out var expandedShape))
            {
                expandedShape = ExpandNamedStructuralFields(graph, shape);
                expandedShapes.Add(qualifiedShape, expandedShape);
            }
            if (binding.Type is not null)
            {
                return new(
                    type: binding.Type,
                    shape: qualifiedShape,
                    shapeDefinition: expandedShape);
            }

            return ExprValueContract.FromShape(expandedShape, qualifiedShape);
        }

        if (binding.Type is not null)
            return new(type: binding.Type, shape: bindingShape);

        return new(shape: bindingShape);
    }

    static Shape ExpandNamedStructuralFields(ShapeGraph graph, Shape shape) => new(
        shape.Id,
        [
            .. shape.Fields.Select(field => field with
            {
                Type = field.Cardinality == FieldCardinality.Many
                    || field.Type is ArrayTypeRef
                    ? ExpandNamedStructuralType(
                        graph,
                        field.Type,
                        activeTypes: [],
                        depth: 0)
                    : field.Type
            })
        ],
        shape.Constraints,
        shape.Annotations);

    static TypeRef ExpandNamedStructuralType(
        ShapeGraph graph,
        TypeRef type,
        HashSet<TypeId> activeTypes,
        int depth)
    {
        if (depth >= MaximumStructuralExpansionDepth)
            return type;

        return type switch
        {
            ArrayTypeRef array => new ArrayTypeRef(
                ExpandNamedStructuralType(graph, array.ElementType, activeTypes, depth + 1)),
            ObjectTypeRef objectType => new ObjectTypeRef(
            [
                .. objectType.Fields.Select(field => field with
                {
                    Type = ExpandNamedStructuralType(
                        graph,
                        field.Type,
                        activeTypes,
                        depth + 1)
                })
            ]),
            NamedTypeRef named => ExpandNamedStructuralType(graph, named, activeTypes, depth),
            _ => type
        };
    }

    static TypeRef ExpandNamedStructuralType(
        ShapeGraph graph,
        NamedTypeRef named,
        HashSet<TypeId> activeTypes,
        int depth)
    {
        if (activeTypes.Contains(named.TypeId)
            || !graph.TryGetType(named.TypeId, out var definition)
            || definition is not TypeDefinition.Structural { Fields.IsDefaultOrEmpty: false } structural)
        {
            return named;
        }

        activeTypes.Add(named.TypeId);
        try
        {
            return new ObjectTypeRef(
            [
                .. structural.Fields.Select(field => new ObjectFieldTypeDef(
                    field.Name.Value,
                    field.Cardinality == FieldCardinality.Many
                        ? new ArrayTypeRef(ExpandNamedStructuralType(
                            graph,
                            field.Type,
                            activeTypes,
                            depth + 1))
                        : ExpandNamedStructuralType(
                            graph,
                            field.Type,
                            activeTypes,
                            depth + 1),
                    field.Presence))
            ]);
        }
        finally
        {
            activeTypes.Remove(named.TypeId);
        }
    }

    static bool IsUsableShapeIdentity(QualifiedShapeId shape) =>
        !string.IsNullOrWhiteSpace(shape.GraphId.Value)
        && !string.IsNullOrWhiteSpace(shape.ShapeId.Value);

    /// <summary>Tests whether a graph snapshot with the supplied identity is available.</summary>
    /// <param name="graph">Graph identity to test.</param>
    /// <returns><see langword="true"/> when the graph snapshot is available; otherwise <see langword="false"/>.</returns>
    public bool HasGraph(GraphId graph) => graphs.ContainsKey(graph);

    /// <summary>Tests whether a graph-qualified shape is available in a usable snapshot.</summary>
    /// <param name="shape">Graph-qualified shape identity to test.</param>
    /// <returns><see langword="true"/> when the exact shape is available; otherwise <see langword="false"/>.</returns>
    public bool HasShape(QualifiedShapeId shape) =>
        graphs.TryGetValue(shape.GraphId, out var graph)
        && graph.TryGetShape(shape.ShapeId, out _);

    /// <summary>Tries to get the expression expectation for a field written in an output shape.</summary>
    /// <param name="shape">Graph-qualified output shape.</param>
    /// <param name="target">Target field path written by the expression.</param>
    /// <param name="expectation">Exact target value expectation when resolvable.</param>
    /// <returns><see langword="true"/> when the target contract resolves; otherwise <see langword="false"/>.</returns>
    public bool TryGetTargetExpectation(
        QualifiedShapeId shape,
        FieldPath target,
        out ExprExpectation expectation)
    {
        if (TryGetFieldContract(shape, target, out var contract))
        {
            expectation = new(GetResultCategory(shape.GraphId, contract), contract);
            return true;
        }

        expectation = ExprExpectation.Any;
        return false;
    }

    ExprResultCategory GetResultCategory(GraphId graphId, ExprValueContract contract)
    {
        var category = contract.GetResultCategory();
        if (category != ExprResultCategory.Any
            || contract.GetEffectiveType() is not NamedTypeRef named
            || !graphs.TryGetValue(graphId, out var graph)
            || !graph.TryGetType(named.TypeId, out var definition))
        {
            return category;
        }

        return definition switch
        {
            TypeDefinition.Structural => ExprResultCategory.Object,
            TypeDefinition.Enum => ExprResultCategory.Scalar,
            _ => ExprResultCategory.Any
        };
    }

    bool TryGetShape(QualifiedShapeId id, out Shape shape)
    {
        if (graphs.TryGetValue(id.GraphId, out var graph)
            && graph.TryGetShape(id.ShapeId, out var resolved))
        {
            shape = resolved;
            return true;
        }

        shape = null!;
        return false;
    }

    /// <summary>Tries to resolve the effective value contract at one shape-relative field path.</summary>
    /// <param name="shapeId">Graph-qualified shape containing the path.</param>
    /// <param name="path">Field path to resolve.</param>
    /// <param name="contract">Effective field contract when resolution succeeds.</param>
    /// <returns><see langword="true"/> when the field contract resolves; otherwise <see langword="false"/>.</returns>
    public bool TryGetFieldContract(
        QualifiedShapeId shapeId,
        FieldPath path,
        out ExprValueContract contract)
    {
        contract = null!;
        if (!graphs.TryGetValue(shapeId.GraphId, out var graph)
            || !graph.TryGetShape(shapeId.ShapeId, out var shape)
            || path.Segments.IsDefaultOrEmpty)
        {
            return false;
        }

        ExprValueContract? current = null;
        for (var index = 0; index < path.Segments.Length; index++)
        {
            var segment = path.Segments[index];
            if (index == 0)
            {
                if (segment.Kind != SegmentKind.Field
                    || string.IsNullOrWhiteSpace(segment.Segment)
                    || !shape.TryGetField(segment.Segment, out var field))
                {
                    return false;
                }

                current = ExprValueContract.FromField(field);
                continue;
            }

            if (!TryNavigate(graph, current!, segment, out current))
                return false;
        }

        contract = current!;
        return true;
    }

    static bool TryNavigate(
        ShapeGraph graph,
        ExprValueContract current,
        FieldPathSegment segment,
        out ExprValueContract? next)
    {
        next = null;
        var effectiveType = current.GetEffectiveType();
        if (segment.Kind == SegmentKind.Element)
        {
            if (effectiveType is not ArrayTypeRef array)
                return false;

            next = ComposePathValue(current, new(array.ElementType));
            return true;
        }

        if (segment.Kind != SegmentKind.Field || string.IsNullOrWhiteSpace(segment.Segment))
            return false;

        switch (effectiveType)
        {
            case ObjectTypeRef objectType:
                {
                    var field = objectType.Fields.FirstOrDefault(candidate =>
                        string.Equals(candidate.Name, segment.Segment, StringComparison.Ordinal));
                    if (field is null)
                        return false;

                    next = ComposePathValue(
                        current,
                        new(field.Type, presence: field.Presence));
                    return true;
                }
            case NamedTypeRef named
                when graph.TryGetType(named.TypeId, out var definition)
                     && definition is TypeDefinition.Structural structural
                     && structural.TryGetField(segment.Segment, out var field):
                next = ComposePathValue(
                    current,
                    new(
                        field.Type,
                        cardinality: field.Cardinality,
                        presence: field.Presence,
                        nullability: field.Nullability));
                return true;
            default:
                return false;
        }
    }

    static ExprValueContract ComposePathValue(
        ExprValueContract parent,
        ExprValueContract child) => new(
        child.Type,
        child.Shape,
        child.Cardinality,
        parent.Presence == FieldPresence.Optional || child.Presence == FieldPresence.Optional
            ? FieldPresence.Optional
            : FieldPresence.Required,
        parent.Nullability == FieldNullability.Nullable || child.Nullability == FieldNullability.Nullable
            ? FieldNullability.Nullable
            : FieldNullability.NonNullable,
        child.ShapeDefinition);
}

/// <summary>
/// Deterministic expression analysis for one canonical relation or query definition.
/// </summary>
public sealed class RelationQueryExpressionAnalysisResult
{
    /// <summary>Creates a relation/query expression analysis result.</summary>
    /// <param name="catalogDocument">Exact relationship-catalog snapshot consumed, when present.</param>
    /// <param name="shapeGraphs">
    /// Exact supplied shape-graph snapshots retained for provenance, including invalid snapshots quarantined from resolution.
    /// </param>
    /// <param name="siteAnalyses">Per-site expression analyses with typed canonical origins.</param>
    /// <param name="bindingShapes">Shape and availability analysis for every node output binding.</param>
    /// <param name="requirements">Combined expression requirements.</param>
    /// <param name="validation">Combined structure, catalog, portability, and expression validation.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="requirements"/> or <paramref name="validation"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="siteAnalyses"/> contains a <see langword="null"/> entry.</exception>
    internal RelationQueryExpressionAnalysisResult(
        RelationshipCatalogDocument? catalogDocument,
        ImmutableArray<ShapeGraph> shapeGraphs,
        ImmutableArray<RelationQueryExpressionSiteAnalysis> siteAnalyses,
        ImmutableArray<RelationQueryBindingShape> bindingShapes,
        ExprRequirements requirements,
        DocumentValidationResult validation)
    {
        var normalizedSiteAnalyses = siteAnalyses.IsDefault ? [] : siteAnalyses;
        if (normalizedSiteAnalyses.Any(static site => site is null))
            throw new ArgumentException("Expression-site analyses cannot contain null entries.", nameof(siteAnalyses));

        CatalogDocument = catalogDocument;
        ShapeGraphs = shapeGraphs.IsDefault
            ? []
            : [.. shapeGraphs.OrderBy(static graph => graph.Id.Value, StringComparer.Ordinal)];
        SiteAnalyses =
        [
            .. normalizedSiteAnalyses.OrderBy(
                static site => site.Analysis.Site.Id.Value,
                StringComparer.Ordinal)
        ];
        Sites = [.. SiteAnalyses.Select(static site => site.Analysis)];
        BindingShapes = bindingShapes.IsDefault
            ? []
            :
            [
                .. bindingShapes
                    .OrderBy(static binding => binding.Node.Value, StringComparer.Ordinal)
                    .ThenBy(static binding => binding.Binding.Value, StringComparer.Ordinal)
            ];
        Requirements = Guard.RequireNotNull(requirements);
        Validation = Guard.RequireNotNull(validation);
    }

    /// <summary>Exact relationship-catalog snapshot consumed by analysis, when supplied.</summary>
    public RelationshipCatalogDocument? CatalogDocument { get; }

    /// <summary>Catalog content fingerprint declared by the consumed catalog snapshot, when supplied.</summary>
    public RelationshipCatalogFingerprint? CatalogFingerprint => CatalogDocument?.CatalogFingerprint;

    /// <summary>
    /// Exact supplied shape-graph snapshots sorted by graph identity; invalid snapshots remain for provenance but do not
    /// participate in shape resolution.
    /// </summary>
    public ImmutableArray<ShapeGraph> ShapeGraphs { get; }

    /// <summary>Per-site expression analyses with typed origins, sorted by stable site identity.</summary>
    public ImmutableArray<RelationQueryExpressionSiteAnalysis> SiteAnalyses { get; }

    /// <summary>
    /// Shared per-site expression analyses sorted by stable site identity.
    /// </summary>
    /// <remarks>
    /// This compatibility view contains the same analysis instances exposed through
    /// <see cref="SiteAnalyses"/> without their typed relation/query origins.
    /// </remarks>
    public ImmutableArray<ExprAnalysisResult> Sites { get; }

    /// <summary>Shape and availability analysis for bindings visible at every logical node output.</summary>
    public ImmutableArray<RelationQueryBindingShape> BindingShapes { get; }

    /// <summary>Deterministic union of requirements derived from all expression sites.</summary>
    public ExprRequirements Requirements { get; }

    /// <summary>Combined structure, catalog, portability, and expression validation.</summary>
    public DocumentValidationResult Validation { get; }

    /// <summary>Combined structured diagnostics.</summary>
    public IReadOnlyList<DocumentValidationDiagnostic> Diagnostics => Validation.Diagnostics;

    /// <summary>Whether the definition and all analyzed expression sites are valid.</summary>
    public bool IsValid => Validation.IsValid;
}
