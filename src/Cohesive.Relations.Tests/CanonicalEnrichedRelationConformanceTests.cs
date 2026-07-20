using System.Collections.Immutable;
using System.Text.Json;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Acquisition;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Diagnostics;
using Cohesive.Relations.Explain;
using Cohesive.Relations.IR;
using Cohesive.Relations.Physical;
using Cohesive.Relations.Realization;

namespace Cohesive.Relations.Tests;

/// <summary>End-to-end conformance gates over the shared federated enriched-relation fixture.</summary>
public sealed class CanonicalEnrichedRelationConformanceTests
{
    [Fact]
    public void ExpressionAndStructuralAuthoring_ProduceTheSameCanonicalRelationAndCatalogSemantics()
    {
        var expression = FederatedLoadExpressionAuthoringFixture.Create();
        var structural = FederatedLoadExpressionAuthoringFixture.CreateStructuralEquivalent();

        Assert.True(expression.Relation.Validation.IsValid, Format(expression.Relation.Validation.Diagnostics));
        Assert.True(structural.Validation.IsValid, Format(structural.Validation.Diagnostics));
        Assert.Equal(
            RelationQueryJsonSerializer.Serialize(expression.Relation.CreateDocument(), indented: false),
            RelationQueryJsonSerializer.Serialize(structural.CreateDocument(), indented: false));
        Assert.Equal(
            expression.Relation.CreateDocument().DefinitionFingerprint,
            structural.CreateDocument().DefinitionFingerprint);

        var relationships = expression.RelationshipCatalog.Catalog.Relationships.ToDictionary(
            static relationship => relationship.Id);
        Assert.Equal(2, relationships.Count);
        AssertRelationship(
            relationships[FederatedLoadRelationFixture.LoadCustomerRelationshipId],
            FederatedLoadRelationFixture.LoadCustomerRelationship);
        AssertRelationship(
            relationships[FederatedLoadRelationFixture.LoadEquipmentRelationshipId],
            FederatedLoadRelationFixture.LoadEquipmentRelationship);
        Assert.All(
            expression.Relation.Provenance.Identities,
            static identity => Assert.True(Enum.IsDefined(identity.Origin)));
        Assert.All(
            structural.Provenance.Identities,
            static identity => Assert.Equal(RelationQueryAuthoringIdentityOrigin.Explicit, identity.Origin));
    }

    [Fact]
    public void PersistedSnapshots_RehydrateAndCompileToTheSameStaticContract()
    {
        var originalDocument = FederatedLoadRelationFixture.RequiredRelationDocument;
        var originalCatalog = FederatedLoadRelationFixture.RelationshipCatalogDocument;
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var restoredDocument = RelationQueryJsonSerializer.Deserialize(
            RelationQueryJsonSerializer.Serialize(originalDocument, indented: false));
        var restoredCatalog = RelationshipCatalogJsonSerializer.Deserialize(
            RelationshipCatalogJsonSerializer.Serialize(originalCatalog, indented: false));
        ImmutableArray<ShapeGraphDocument> restoredShapes =
        [
            .. FederatedLoadRelationFixture.ShapeGraphDocuments.Select(document =>
                JsonSerializer.Deserialize<ShapeGraphDocument>(JsonSerializer.Serialize(document, options), options)
                ?? throw new InvalidOperationException("Failed to rehydrate a shape-graph document."))
        ];

        var original = Compile(
            originalDocument,
            FederatedLoadRelationFixture.ShapeGraphDocuments,
            originalCatalog);
        var restored = Compile(restoredDocument, restoredShapes, restoredCatalog);

        Assert.Equal(
            RelationQueryCompiledPlanReferenceFingerprinter.Compute(RelationQueryCompiledPlanReference.From(original)),
            RelationQueryCompiledPlanReferenceFingerprinter.Compute(RelationQueryCompiledPlanReference.From(restored)));
        AssertExactStaticContract(original);
        AssertExactStaticContract(restored);

        var physical = FederatedLoadPhysicalExecutionFixture.Create(restored);
        Assert.Equal(
            RelationQueryCompiledPlanReferenceFingerprinter.Compute(RelationQueryCompiledPlanReference.From(restored)),
            RelationQueryCompiledPlanReferenceFingerprinter.Compute(physical.Placement.Plan));
    }

    [Fact]
    public void StaticPlan_ExposesExactEnrichedInputLineageAndDependencies()
    {
        var plan = FederatedLoadPhysicalExecutionFixture.Create(
            FederatedLoadRelationFixture.RequiredRelationDocument).Plan;

        AssertExactStaticContract(plan);
        AssertValueLineage(
            plan,
            FederatedLoadRelationFixture.SearchIdPath,
            FederatedLoadRelationFixture.LoadShapeId,
            FederatedLoadRelationFixture.LoadIdPath);
        AssertValueLineage(
            plan,
            FederatedLoadRelationFixture.SearchCustomerNamePath,
            FederatedLoadRelationFixture.CustomerShapeId,
            FederatedLoadRelationFixture.CustomerNamePath);
        AssertValueLineage(
            plan,
            FederatedLoadRelationFixture.SearchEquipmentNumberPath,
            FederatedLoadRelationFixture.EquipmentShapeId,
            FederatedLoadRelationFixture.EquipmentNumberPath);

        var rowOutput = Assert.Single(plan.RequirementGraph.Outputs, static output => output.Field is null);
        var identity = Assert.Single(
            plan.Lineage.Entries.Single(entry => entry.Output.Id == rowOutput.Id).Contributions,
            static contribution => contribution.Effect == RelationQueryRequirementEffect.Identity);
        var identityField = Assert.IsType<RelationQueryFieldInput>(identity.Input);
        Assert.Equal(FederatedLoadRelationFixture.LoadShapeId, identityField.Field.Shape);
        Assert.Equal(FederatedLoadRelationFixture.LoadIdPath, identityField.Field.Path);

        AssertCorrelationDependency(
            plan,
            FederatedLoadRelationFixture.LoadCustomerIdPath,
            FederatedLoadRelationFixture.SearchCustomerNamePath);
        AssertCorrelationDependency(
            plan,
            FederatedLoadRelationFixture.LoadEquipmentIdPath,
            FederatedLoadRelationFixture.SearchEquipmentNumberPath);
    }

    [Fact]
    public void CombinedQuery_ProjectsPortableExplainAndExactContributorObservability()
    {
        var compilation = RelationQueryStaticCompiler.Compile(new(
            FederatedLoadRelationFixture.ConformanceQueryDocument,
            FederatedLoadRelationFixture.ShapeGraphDocuments,
            FederatedLoadRelationFixture.RelationshipCatalogDocument));
        Assert.True(compilation.IsSuccessful, Format(compilation.Diagnostics));
        var plan = Assert.IsType<CompiledRelationQueryPlan>(compilation.Plan);
        var realization = RelationQueryInMemoryInterpreter.Default.Realize(plan);
        var placement = FederatedLoadPhysicalExecutionFixture.CreatePlacement(plan);
        var physical = RelationQueryPhysicalPlanner.Compile(
            plan,
            realization,
            placement,
            FederatedLoadPhysicalExecutionFixture.CreatePolicy());
        Assert.True(physical.IsSuccessful, Format(physical.Diagnostics));

        var artifact = RelationQueryExplainProjector.Project(
            compilation,
            realization,
            placement,
            physicalPlanning: physical);

        Assert.Collection(
            artifact.Stages,
            static stage => Assert.IsType<RelationQueryStaticCompilationExplainStage>(stage),
            static stage => Assert.IsType<RelationQueryProfileFeasibilityExplainStage>(stage),
            static stage => Assert.IsType<RelationQuerySourcePlacementExplainStage>(stage),
            static stage => Assert.IsType<RelationQueryPhysicalPlanningExplainStage>(stage));
        var staticStage = Assert.IsType<RelationQueryStaticCompilationExplainStage>(artifact.Stages[0]);
        var explainedPlan = Assert.IsType<RelationQueryStaticPlanExplanation>(staticStage.Plan);
        Assert.Equal(RelationQueryResultObservability.ExactContributors, explainedPlan.Observability);
        Assert.Equal(RelationQueryResultObservability.ExactContributors, realization.Observability);
        Assert.Equal(
            [
                $"query:{FederatedLoadRelationFixture.AggregationResultId.Value}",
                $"query:{FederatedLoadRelationFixture.RowsResultId.Value}"
            ],
            explainedPlan.Branches.Select(static branch => branch.Id.Value));
        Assert.Equal(realization.Fingerprint, artifact.CapabilitySummary?.ProfileFeasibility);
        Assert.Null(artifact.CapabilitySummary?.BoundRealization);

        var json = RelationQueryExplainJsonSerializer.Serialize(artifact);
        var restored = RelationQueryExplainJsonSerializer.Deserialize(json);
        Assert.Equal(artifact.Fingerprint, restored.Fingerprint);
        Assert.True(artifact.CapabilitySummary!.HasSameSemantics(restored.CapabilitySummary));
        Assert.Equal(json, RelationQueryExplainJsonSerializer.Serialize(restored));
    }

    [Fact]
    public async Task ReferenceAndBoundedPhysicalExecution_ProduceIdenticalDtosWithExactOccurrenceProvenance()
    {
        var compilation = FederatedLoadPhysicalExecutionFixture.Create(
            FederatedLoadRelationFixture.RequiredRelationDocument,
            maximumBatchSize: 2);
        var scenario = FederatedLoadConformanceData.CreatePhysicalScenario(compilation);
        var physical = await new RelationQueryPhysicalExecutor(scenario.Readers).ExecuteAsync(new(
            compilation.Plan,
            compilation.PhysicalPlan,
            compilation.Realization,
            new("conformance/federated/physical/complete"),
            suppliedSources: [scenario.SuppliedLoads],
            capabilities: FederatedLoadPhysicalExecutionFixture.AvailableCapabilities(compilation.Plan)));
        var reference = RelationQueryInMemoryInterpreter.Default.Execute(new(
            compilation.Plan,
            FederatedLoadConformanceData.CreateReferenceEvidence(compilation.Plan)));

        Assert.Equal(RelationQueryExecutionStatus.Succeeded, reference.Status);
        Assert.Equal(RelationQueryExecutionStatus.Succeeded, physical.Status);
        Assert.Empty(physical.Diagnostics);
        var customerRequest = Assert.Single(scenario.Customers.Requests);
        var customerLookup = Assert.IsType<RelationQueryIdentityBatchLookup>(customerRequest.Constraint);
        Assert.Equal(["customer-1"], customerLookup.Identities.ToArray());
        Assert.Equal(
            [FederatedLoadRelationFixture.CustomerNamePath],
            customerRequest.Fields.Select(static field => field.SemanticPath));
        var equipmentRequest = Assert.Single(scenario.Equipment.Requests);
        var equipmentLookup = Assert.IsType<RelationQueryIdentityBatchLookup>(equipmentRequest.Constraint);
        Assert.Equal(["equipment-1"], equipmentLookup.Identities.ToArray());
        Assert.Equal(
            [FederatedLoadRelationFixture.EquipmentNumberPath],
            equipmentRequest.Fields.Select(static field => field.SemanticPath));

        var mapperCompilation = RelationDtoMapperCompiler.Default.Compile<FederatedLoadSearchRow>(compilation.Plan);
        Assert.True(mapperCompilation.IsSuccessful, Format(mapperCompilation.Diagnostics));
        var mapper = Assert.IsType<CompiledRelationDtoMapper<FederatedLoadSearchRow>>(mapperCompilation.Mapper);
        var referenceMapping = mapper.Map(reference);
        var physicalMapping = mapper.Map(physical);
        Assert.True(referenceMapping.IsSuccessful, Format(referenceMapping.Diagnostics));
        Assert.True(physicalMapping.IsSuccessful, Format(physicalMapping.Diagnostics));
        Assert.Equal(scenario.Expected, referenceMapping.Rows.Select(static row => row.Value));
        Assert.Equal(scenario.Expected, physicalMapping.Rows.Select(static row => row.Value));
        Assert.Equal(
            referenceMapping.Rows.Select(static row => row.Value),
            physicalMapping.Rows.Select(static row => row.Value));
        Assert.All(referenceMapping.Rows, static row => Assert.Equal(3, row.Source.InputOccurrences.Length));
        Assert.All(physicalMapping.Rows, static row => Assert.Equal(3, row.Source.InputOccurrences.Length));
        Assert.All(physicalMapping.Rows, static row => Assert.NotNull(row.Source.Root));
    }

    [Fact]
    public async Task CombinedRowsAndAggregationQuery_ExecutesBothBranchesFromOneSharedAcquisition()
    {
        const int RootCount = 3;
        var compilation = FederatedLoadPhysicalExecutionFixture.Create(
            FederatedLoadRelationFixture.ConformanceQueryDocument,
            maximumBatchSize: 2);
        var scenario = FederatedLoadConformanceData.CreateEnumeratedPhysicalScenario(
            compilation,
            RootCount,
            distinctCustomerCount: 2,
            distinctEquipmentCount: 2);

        var physical = await new RelationQueryPhysicalExecutor(scenario.Readers).ExecuteAsync(new(
                compilation.Plan,
                compilation.PhysicalPlan,
                compilation.Realization,
                new("conformance/federated/query/rows-and-count"),
                capabilities: FederatedLoadPhysicalExecutionFixture.AvailableCapabilities(compilation.Plan)));

        Assert.Equal(RelationQueryExecutionStatus.Succeeded, physical.Status);
        Assert.Empty(physical.Diagnostics);
        var evidence = Assert.IsType<RelationQueryRuntimeEvidence>(physical.Evidence);
        var execution = Assert.IsType<RelationQueryExecutionResult>(physical.Interpretation);
        var reference = RelationQueryInMemoryInterpreter.Default.Execute(new(
            compilation.Plan,
            evidence,
            RelationRequirementGapPolicy.Conventional));
        AssertEquivalentQueryResults(execution, reference);
        Assert.Equal(2, execution.QueryResults.Length);
        var rows = execution.QueryResults.Single(result =>
            result.Result == FederatedLoadRelationFixture.RowsResultId);
        Assert.Equal(RelationQueryExecutionResultKind.Rows, rows.Kind);
        Assert.Equal(RootCount, rows.Rows.Length);
        Assert.Equal(
            ["load-1", "load-2", "load-3"],
            rows.Rows.Select(row => row.Value.GetProperty(FederatedLoadRelationFixture.SearchIdFieldName).String));
        var aggregation = execution.QueryResults.Single(result =>
            result.Result == FederatedLoadRelationFixture.AggregationResultId);
        Assert.Equal(RelationQueryExecutionResultKind.Aggregation, aggregation.Kind);
        Assert.Equal(
            RootCount,
            Assert.Single(aggregation.Rows).Value
                .GetProperty(FederatedLoadRelationFixture.AggregateLoadCountFieldName)
                .Int64);

        Assert.Single(scenario.Loads.Requests);
        Assert.Single(scenario.Customers.Requests);
        Assert.Single(scenario.Equipment.Requests);
        Assert.Equal(
            ["customer-1", "customer-2"],
            scenario.Customers.Requests
                .SelectMany(static request =>
                    Assert.IsType<RelationQueryIdentityBatchLookup>(request.Constraint).Identities)
                .Order(StringComparer.Ordinal));
        Assert.Equal(
            ["equipment-1", "equipment-2"],
            scenario.Equipment.Requests
                .SelectMany(static request =>
                    Assert.IsType<RelationQueryIdentityBatchLookup>(request.Constraint).Identities)
                .Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(
        RelationQuerySourceReadState.Partial,
        RelationQueryTraversalEvidenceState.Completed)]
    [InlineData(
        RelationQuerySourceReadState.Failed,
        RelationQueryTraversalEvidenceState.Failed)]
    public async Task CombinedQuery_RetainsAttributableNonCompleteRelatedReads(
        RelationQuerySourceReadState readState,
        RelationQueryTraversalEvidenceState traversalState)
    {
        var compilation = FederatedLoadPhysicalExecutionFixture.Create(
            FederatedLoadRelationFixture.ConformanceQueryDocument);
        var scenario = FederatedLoadConformanceData.CreateEnumeratedPhysicalScenario(
            compilation,
            customerResultFactory: _ => new(
                readState,
                evidenceReference: $"conformance/federated/customer/{readState}"));

        var physical = await new RelationQueryPhysicalExecutor(scenario.Readers).ExecuteAsync(new(
            compilation.Plan,
            compilation.PhysicalPlan,
            compilation.Realization,
            new($"conformance/federated/query/{readState}"),
            capabilities: FederatedLoadPhysicalExecutionFixture.AvailableCapabilities(compilation.Plan)));

        Assert.Equal(RelationQueryExecutionStatus.Incomplete, physical.Status);
        Assert.Empty(physical.Diagnostics);
        var evidence = Assert.IsType<RelationQueryRuntimeEvidence>(physical.Evidence);
        var execution = Assert.IsType<RelationQueryExecutionResult>(physical.Interpretation);
        var customerTraversal = compilation.Plan.InputContract.Traversals.Single(traversal =>
            traversal.Definition.Id == FederatedLoadRelationFixture.LoadCustomerRelationshipId);
        var related = Assert.Single(evidence.Traversals, traversal =>
            traversal.Input == customerTraversal.Input.Id);
        Assert.Equal(traversalState, related.State);
        Assert.Equal(RelationQueryEvidenceCompleteness.Partial, related.Completeness);
        Assert.Empty(related.Results);
        Assert.Single(scenario.Customers.Requests);
        Assert.Empty(scenario.Equipment.Requests);
        Assert.Equal(
            readState,
            Assert.Single(physical.SourceReads, trace =>
                trace.Source == FederatedLoadPhysicalExecutionFixture.CustomersSource).State);

        var rows = execution.QueryResults.Single(result =>
            result.Result == FederatedLoadRelationFixture.RowsResultId);
        Assert.Equal(RelationQueryExecutionOutputState.Incomplete, rows.State);
        var aggregation = execution.QueryResults.Single(result =>
            result.Result == FederatedLoadRelationFixture.AggregationResultId);
        Assert.Equal(RelationQueryExecutionOutputState.Complete, aggregation.State);
        Assert.Equal(
            1L,
            Assert.Single(aggregation.Rows).Value
                .GetProperty(FederatedLoadRelationFixture.AggregateLoadCountFieldName)
                .Int64);
    }

    [Fact]
    public async Task CombinedQuery_CancellationAfterRootReadStopsBeforeRelatedIo()
    {
        var compilation = FederatedLoadPhysicalExecutionFixture.Create(
            FederatedLoadRelationFixture.ConformanceQueryDocument);
        using CancellationTokenSource cancellation = new();
        var scenario = FederatedLoadConformanceData.CreateEnumeratedPhysicalScenario(
            compilation,
            afterLoadRead: _ => cancellation.Cancel());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new RelationQueryPhysicalExecutor(scenario.Readers).ExecuteAsync(
                new(
                    compilation.Plan,
                    compilation.PhysicalPlan,
                    compilation.Realization,
                    new("conformance/federated/query/canceled"),
                    capabilities: FederatedLoadPhysicalExecutionFixture.AvailableCapabilities(compilation.Plan)),
                cancellation.Token).AsTask());

        Assert.Single(scenario.Loads.Requests);
        Assert.Empty(scenario.Customers.Requests);
        Assert.Empty(scenario.Equipment.Requests);
    }

    [Fact]
    public async Task CombinedQuery_MissingRequiredReaderFailsPreflightWithoutIoOrInterpretation()
    {
        var compilation = FederatedLoadPhysicalExecutionFixture.Create(
            FederatedLoadRelationFixture.ConformanceQueryDocument);
        var scenario = FederatedLoadConformanceData.CreateEnumeratedPhysicalScenario(compilation);

        var physical = await new RelationQueryPhysicalExecutor(
            [scenario.Loads, scenario.Customers]).ExecuteAsync(new(
                compilation.Plan,
                compilation.PhysicalPlan,
                compilation.Realization,
                new("conformance/federated/query/missing-equipment-reader"),
                capabilities: FederatedLoadPhysicalExecutionFixture.AvailableCapabilities(compilation.Plan)));

        Assert.Equal(RelationQueryExecutionStatus.Failed, physical.Status);
        Assert.Null(physical.Evidence);
        Assert.Null(physical.Interpretation);
        var diagnostic = Assert.Single(physical.Diagnostics, static diagnostic =>
            diagnostic.Code == RelationQueryPhysicalExecutionDiagnosticCodes.SourceReaderMissing);
        Assert.Equal(FederatedLoadPhysicalExecutionFixture.EquipmentSource, diagnostic.Source);
        Assert.Empty(scenario.Loads.Requests);
        Assert.Empty(scenario.Customers.Requests);
        Assert.Empty(scenario.Equipment.Requests);
    }

    [Fact]
    public async Task MissingCustomer_ProducesAttributableGapAndExplicitlyIncompletePartialDto()
    {
        var compilation = FederatedLoadPhysicalExecutionFixture.Create(
            FederatedLoadRelationFixture.RequiredRelationDocument);
        var scenario = FederatedLoadConformanceData.CreatePhysicalScenario(
            compilation,
            includeFirstCustomer: false);
        var physical = await new RelationQueryPhysicalExecutor(scenario.Readers).ExecuteAsync(new(
            compilation.Plan,
            compilation.PhysicalPlan,
            compilation.Realization,
            new("conformance/federated/physical/missing-customer"),
            suppliedSources: [scenario.SuppliedLoads],
            capabilities: FederatedLoadPhysicalExecutionFixture.AvailableCapabilities(compilation.Plan)));
        var reference = RelationQueryInMemoryInterpreter.Default.Execute(new(
            compilation.Plan,
            FederatedLoadConformanceData.CreateReferenceEvidence(compilation.Plan, includeCustomer: false)));

        Assert.Equal(RelationQueryExecutionStatus.Incomplete, reference.Status);
        Assert.Equal(RelationQueryExecutionStatus.Incomplete, physical.Status);
        var referenceGap = Assert.Single(reference.RequirementGapAnalysis.Gaps);
        var physicalGap = Assert.Single(
            Assert.IsType<RelationQueryExecutionResult>(physical.Interpretation).RequirementGapAnalysis.Gaps);
        AssertGap(referenceGap, compilation.Plan);
        AssertGap(physicalGap, compilation.Plan);
        Assert.Equal(referenceGap.Cause, physicalGap.Cause);
        Assert.Equal(referenceGap.Input.Id, physicalGap.Input.Id);
        Assert.Equal(
            referenceGap.Impacts.Select(static impact => (impact.Output.Id, impact.Effect)),
            physicalGap.Impacts.Select(static impact => (impact.Output.Id, impact.Effect)));

        var mapperCompilation = RelationDtoMapperCompiler.Default.Compile<FederatedLoadSearchRow>(compilation.Plan);
        var mapper = Assert.IsType<CompiledRelationDtoMapper<FederatedLoadSearchRow>>(mapperCompilation.Mapper);
        var mapped = mapper.Map(physical, RelationDtoMappingFailurePolicy.CollectDiagnostics);
        Assert.Equal(RelationDtoMappingStatus.Incomplete, mapped.Status);
        Assert.Empty(mapped.FailedRows);
        var partial = Assert.Single(mapped.Rows);
        Assert.Equal("load-1", partial.Value.Id);
        Assert.Null(partial.Value.CustomerName);
        Assert.Equal("TRUCK-001", partial.Value.EquipmentNumber);
        Assert.False(partial.Source.IsComplete);
        Assert.Equal([physicalGap.Id], partial.Source.UnresolvedGaps.ToArray());
    }

    static CompiledRelationQueryPlan Compile(
        RelationQueryDocument document,
        ImmutableArray<ShapeGraphDocument> shapes,
        RelationshipCatalogDocument catalog)
    {
        var result = RelationQueryStaticCompiler.Compile(new(document, shapes, catalog));
        Assert.True(result.IsSuccessful, Format(result.Diagnostics));
        return Assert.IsType<CompiledRelationQueryPlan>(result.Plan);
    }

    static void AssertExactStaticContract(CompiledRelationQueryPlan plan)
    {
        Assert.Same(plan.RequirementGraph, plan.InputContract.Requirements);
        Assert.Same(plan.RequirementGraph, plan.Lineage.Requirements);
        Assert.Same(plan.RequirementGraph, plan.DependencyManifest.Requirements);
        var source = Assert.Single(plan.InputContract.Sources);
        Assert.Equal(FederatedLoadRelationFixture.LoadShapeId, source.Shape);
        Assert.Equal(RelationQuerySourceInputRole.RelationRoot, source.Role);
        Assert.Equal(
            [
                FederatedLoadRelationFixture.LoadCustomerIdPath,
                FederatedLoadRelationFixture.LoadEquipmentIdPath,
                FederatedLoadRelationFixture.LoadIdPath
            ],
            source.Fields.Select(static field => field.Input.Field.Path));

        Assert.Equal(2, plan.InputContract.Traversals.Length);
        var customer = plan.InputContract.Traversals.Single(traversal =>
            traversal.Definition.Id == FederatedLoadRelationFixture.LoadCustomerRelationshipId);
        Assert.Equal(QueryInputRequirement.Required, customer.Requirement);
        Assert.Equal(JoinKind.Left, customer.JoinKind);
        Assert.Equal(RelationshipTraversalCardinality.AtMostOne, customer.Cardinality);
        Assert.Equal(
            [FederatedLoadRelationFixture.CustomerNamePath],
            customer.Fields.Select(static field => field.Input.Field.Path));
        var equipment = plan.InputContract.Traversals.Single(traversal =>
            traversal.Definition.Id == FederatedLoadRelationFixture.LoadEquipmentRelationshipId);
        Assert.Equal(QueryInputRequirement.Optional, equipment.Requirement);
        Assert.Equal(JoinKind.Left, equipment.JoinKind);
        Assert.Equal(RelationshipTraversalCardinality.AtMostOne, equipment.Cardinality);
        Assert.Equal(
            [FederatedLoadRelationFixture.EquipmentNumberPath],
            equipment.Fields.Select(static field => field.Input.Field.Path));
        Assert.Equal(2, plan.InputContract.Identities.Length);
        Assert.Empty(plan.InputContract.Parameters);
        Assert.Equal(
            plan.RequirementGraph.Outputs.Select(static output => output.Id),
            plan.Lineage.Entries.Select(static entry => entry.Output.Id));
        Assert.Equal(
            plan.RequirementGraph.Inputs.Select(static input => input.Id),
            plan.DependencyManifest.Entries.Select(static entry => entry.Input.Id));
    }

    static void AssertValueLineage(
        CompiledRelationQueryPlan plan,
        FieldPath outputPath,
        QualifiedShapeId inputShape,
        FieldPath inputPath)
    {
        var output = Assert.Single(plan.RequirementGraph.Outputs, output => output.Field?.Path == outputPath);
        var contribution = Assert.Single(
            plan.Lineage.Entries.Single(entry => entry.Output.Id == output.Id).Contributions,
            static contribution => contribution.Effect == RelationQueryRequirementEffect.Value);
        var input = Assert.IsType<RelationQueryFieldInput>(contribution.Input);
        Assert.Equal(inputShape, input.Field.Shape);
        Assert.Equal(inputPath, input.Field.Path);
        Assert.Contains(
            contribution.Traces.SelectMany(static trace => trace.Steps),
            static step => step.SiteKind == RelationQueryExpressionSiteKind.ProjectionAssignmentValue);
    }

    static void AssertCorrelationDependency(
        CompiledRelationQueryPlan plan,
        FieldPath referencePath,
        FieldPath affectedOutput)
    {
        var reference = Assert.Single(
            plan.RequirementGraph.Inputs.OfType<RelationQueryFieldInput>(),
            input => input.Field.Shape == FederatedLoadRelationFixture.LoadShapeId
                && input.Field.Path == referencePath);
        var dependency = plan.DependencyManifest.Entries.Single(entry => entry.Input.Id == reference.Id);
        Assert.Contains(
            dependency.Impacts,
            impact => impact.Output.Field?.Path == affectedOutput
                && impact.Effect is RelationQueryRequirementEffect.Correlation
                    or RelationQueryRequirementEffect.Acquisition);
    }

    static void AssertGap(RelationRequirementGap gap, CompiledRelationQueryPlan plan)
    {
        Assert.Equal(RelationRequirementGapCause.RelatedObservationNotFound, gap.Cause);
        var relationship = Assert.IsType<RelationQueryRelationshipInput>(gap.Input);
        Assert.Equal(FederatedLoadRelationFixture.LoadCustomerRelationshipId, relationship.Relationship);
        Assert.NotNull(gap.Occurrence);
        Assert.Equal(FederatedLoadRelationFixture.LoadBinding, gap.Occurrence!.Binding);
        var context = Assert.IsType<RelationRequirementGapRelationshipContext>(gap.RelationshipContext);
        Assert.Equal(RelationshipTraversalDirection.Forward, context.Direction);
        Assert.Equal(JoinKind.Left, context.JoinKind);
        Assert.Equal(RelationshipTraversalCardinality.AtMostOne, context.ExpectedCardinality);
        Assert.Equal(RelationQueryTraversalEvidenceState.Completed, context.ObservedState);
        Assert.Equal(RelationQueryEvidenceCompleteness.Complete, context.Completeness);
        Assert.Equal(0, context.ObservedCount);
        Assert.Equal(ObservationValue.FromString("customer-1"), context.ReferenceValue);
        Assert.Contains(FederatedLoadRelationFixture.CustomerNamePath, gap.RequiredFields.Select(static field => field.Path));
        Assert.Contains(RelationRequirementGapResolutionKind.ProvideRelatedObservation, gap.SuggestedResolutions);
        Assert.Contains(
            gap.Impacts,
            impact => impact.Output.Field?.Path == FederatedLoadRelationFixture.SearchCustomerNamePath);
        Assert.DoesNotContain(
            gap.Impacts,
            impact => impact.Output.Field?.Path == FederatedLoadRelationFixture.SearchEquipmentNumberPath);
        Assert.Equal(plan.Provenance.DefinitionFingerprint, gap.Provenance.DefinitionFingerprint);
        Assert.Equal(plan.Demand, gap.Demand);
    }

    static void AssertRelationship(RelationshipDefinition actual, RelationshipDefinition expected)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.SourceShape, actual.SourceShape);
        Assert.Equal(expected.SourceReference, actual.SourceReference);
        Assert.Equal(expected.TargetShape, actual.TargetShape);
        Assert.Equal(expected.TargetKey, actual.TargetKey);
    }

    static void AssertEquivalentQueryResults(
        RelationQueryExecutionResult actual,
        RelationQueryExecutionResult expected)
    {
        Assert.Equal(expected.Status, actual.Status);
        Assert.Equal(
            expected.RequirementGapAnalysis.Gaps.Select(static gap => gap.Id),
            actual.RequirementGapAnalysis.Gaps.Select(static gap => gap.Id));
        Assert.Equal(expected.Diagnostics, actual.Diagnostics);
        Assert.Equal(expected.QueryResults.Length, actual.QueryResults.Length);
        for (var resultIndex = 0; resultIndex < expected.QueryResults.Length; resultIndex++)
        {
            var expectedResult = expected.QueryResults[resultIndex];
            var actualResult = actual.QueryResults[resultIndex];
            Assert.Equal(expectedResult.Result, actualResult.Result);
            Assert.Equal(expectedResult.Kind, actualResult.Kind);
            Assert.Equal(expectedResult.Shape, actualResult.Shape);
            Assert.Equal(expectedResult.State, actualResult.State);
            Assert.Equal(
                expectedResult.Rows.Select(static row => (row.Value, row.Identity, row.Root)),
                actualResult.Rows.Select(static row => (row.Value, row.Identity, row.Root)));
        }
    }

    static string Format<T>(IEnumerable<T> diagnostics) => string.Join(Environment.NewLine, diagnostics);
}
