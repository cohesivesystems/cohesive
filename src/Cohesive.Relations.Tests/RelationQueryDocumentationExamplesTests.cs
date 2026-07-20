using System.Text.Json.Serialization;
using Cohesive.Relations.Acquisition;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Diagnostics;
using Cohesive.Relations.Explain;
using Cohesive.Relations.IR;

namespace Cohesive.Relations.Tests;

public sealed class RelationQueryDocumentationExamplesTests
{
    [Fact]
    public async Task MinimalDtoMappingSnippet_AuthorsCompilesExecutesAndMapsWithoutInfrastructureAuthoring()
    {
        var author = RelationQuery.Expression();
        var loads = author.Source<Load>();

        var loadDtos = author.Project(
            loads,
            (Load load) => new LoadDto
            {
                Id = load.Id,
                Status = load.Status
            });

        var relation = loadDtos.BuildRelation(dto => dto.Id);
        var compilation = RelationQueryStaticCompiler.Compile(new(
            relation.CreateDocument(),
            author.ShapeDocuments,
            author.CreateRelationshipCatalogDocument()));

        Assert.True(relation.Validation.IsValid, Format(relation.Validation.Diagnostics));
        Assert.True(compilation.IsSuccessful, Format(compilation.Diagnostics));
        var plan = Assert.IsType<CompiledRelationQueryPlan>(compilation.Plan);
        Assert.Equal(
            ["id", "status"],
            plan.InputContract.Sources.Single().Fields
                .Select(static field => field.Input.Field.Path.ToString())
                .Order(StringComparer.Ordinal));

        var evaluation = author
            .Evaluate(relation, new("documentation/load-dto/load-42"))
            .Supply(
                [new Load
                {
                    Id = "load-42",
                    CustomerId = "customer-7",
                    EquipmentId = "equipment-3",
                    Status = "Open"
                }],
                static load => load.Id)
            .Build();
        var outcome = await RelationQueryEvaluator.CreateSuppliedOnly().EvaluateAsync(evaluation);
        Assert.True(outcome.IsSuccessful, Format(outcome.Result?.Diagnostics ?? []));
        var mapperCompilation = RelationDtoMapperCompiler.Default.Compile<LoadDto>(outcome.Compilation.Plan!);
        var mapping = mapperCompilation.Mapper!.Map(outcome.PhysicalExecution!);
        var dto = Assert.Single(mapping.Rows).Value;
        Assert.Equal("load-42", dto.Id);
        Assert.Equal("Open", dto.Status);
    }

    [Fact]
    public void ProgressiveEnrichmentSnippet_CompilesOnlyDemandedLoadCustomerAndEquipmentFields()
    {
        var authored = AuthorCompleteRelation();
        var compilation = RelationQueryStaticCompiler.Compile(new(
            authored.Relation.CreateDocument(),
            authored.Author.ShapeDocuments,
            authored.Author.CreateRelationshipCatalogDocument()));

        Assert.True(compilation.IsSuccessful, Format(compilation.Diagnostics));
        var plan = Assert.IsType<CompiledRelationQueryPlan>(compilation.Plan);
        Assert.Equal(
            ["customerId", "equipmentId", "id"],
            plan.InputContract.Sources.Single().Fields
                .Select(static field => field.Input.Field.Path.ToString())
                .Order(StringComparer.Ordinal));
        Assert.Equal(2, plan.InputContract.Traversals.Length);
        Assert.Equal(
            ["name", "type"],
            plan.InputContract.Traversals
                .Single(traversal => traversal.ResultShape == authored.Author.Clr.Shape<Customer>().Id)
                .Fields
                .Select(static field => field.Input.Field.Path.ToString())
                .Order(StringComparer.Ordinal));
        Assert.Equal(
            ["number"],
            plan.InputContract.Traversals
                .Single(traversal => traversal.ResultShape == authored.Author.Clr.Shape<Equipment>().Id)
                .Fields
                .Select(static field => field.Input.Field.Path.ToString()));
    }

    [Fact]
    public void RowsAndAggregationSnippet_CompilesBothNamedBranchesFromOneLogicalGraph()
    {
        var author = RelationQuery.Expression();
        var loads = author.Source<Load>();
        var customers = author.Traverse<Load, Customer>(loads, load => load.CustomerId);
        var loadEquipment = author.Relationship<Load, Equipment>(load => load.EquipmentId);
        var equipment = author.Traverse(
            customers,
            loads.Binding,
            loadEquipment,
            requirement: QueryInputRequirement.Optional);
        var documents = author.Project(
            equipment,
            (Load load, Customer customer, Equipment unit) => new LoadSearchDto
            {
                Id = load.Id,
                CustomerId = load.CustomerId,
                CustomerName = customer.Name,
                CustomerType = customer.Type,
                EquipmentNumber = unit.Number
            },
            loads.Binding,
            customers.Binding);
        var rows = author.Rows(documents, id: "rows");
        var summary = author.Aggregate(
            equipment.Node,
            author.Clr.Shape<LoadSearchSummary>(),
            aggregate => aggregate.Count(result => result.LoadCount));
        var aggregation = author.Aggregation(summary, id: "summary");
        var query = author.BuildQuery(
            new QueryId("load-search"),
            new QueryName("LoadSearch"),
            rows,
            aggregation);

        var compilation = RelationQueryStaticCompiler.Compile(new(
            query.CreateDocument(),
            author.ShapeDocuments,
            author.CreateRelationshipCatalogDocument()));

        Assert.True(compilation.IsSuccessful, Format(compilation.Diagnostics));
        var plan = Assert.IsType<CompiledRelationQueryPlan>(compilation.Plan);
        Assert.Equal(2, plan.ExecutionSlice.QueryResults.Length);
        Assert.Contains(
            plan.ExecutionSlice.QueryResults,
            static result => result.Id.Value == "rows");
        Assert.Contains(
            plan.ExecutionSlice.QueryResults,
            static result => result.Id.Value == "summary");
    }

    [Fact]
    public async Task PostgresCosmosGuide_ComposedExecutionEnumeratesLoadsAndBatchesCustomersWithoutNPlusOne()
    {
        const int RootCount = 10;
        var demand = RelationQueryCompilationDemand.ForQueryResults(
        [
            QueryResultDemand.SelectedFields(
                FederatedLoadRelationFixture.RowsResultId,
                [
                    new(
                        FederatedLoadRelationFixture.LoadSearchShapeId,
                        FederatedLoadRelationFixture.SearchIdPath),
                    new(
                        FederatedLoadRelationFixture.LoadSearchShapeId,
                        FederatedLoadRelationFixture.SearchCustomerNamePath)
                ])
        ]);
        var compilation = FederatedLoadPhysicalExecutionFixture.Create(
            FederatedLoadRelationFixture.ConformanceQueryDocument,
            demand,
            maximumBatchSize: 2);
        var customerTraversal = Assert.Single(compilation.Plan.InputContract.Traversals);
        Assert.Equal(
            FederatedLoadRelationFixture.LoadCustomerRelationshipId,
            customerTraversal.Definition.Id);
        Assert.All(
            compilation.Placement.SourceInstances,
            static source => Assert.NotEmpty(source.TargetProfile.Capabilities));

        var loads = FederatedLoadPhysicalExecutionFixture.Source(
            compilation,
            FederatedLoadPhysicalExecutionFixture.LoadsSource);
        var customers = FederatedLoadPhysicalExecutionFixture.Source(
            compilation,
            FederatedLoadPhysicalExecutionFixture.CustomersSource);
        var loadReader = new DeterministicRelationQuerySourceReader(
            new(loads.Id, loads.ExecutionDomain, loads.TargetProfile),
            FederatedLoadConformanceData.CreateLoadRows(
                RootCount,
                distinctCustomerCount: 2,
                distinctEquipmentCount: 1));
        var customerReader = new DeterministicRelationQuerySourceReader(
            new(customers.Id, customers.ExecutionDomain, customers.TargetProfile),
            FederatedLoadConformanceData.CreateCustomerRows(count: 2));

        var evaluation = FederatedLoadRelationFixture.ConformanceQueryDocument
            .Evaluate(
                new("documentation/postgres-cosmos/customer-enrichment"),
                FederatedLoadRelationFixture.ShapeGraphDocuments,
                FederatedLoadRelationFixture.RelationshipCatalogDocument)
            .Select(
                FederatedLoadRelationFixture.RowsResultId,
                [
                    new(
                        FederatedLoadRelationFixture.LoadSearchShapeId,
                        FederatedLoadRelationFixture.SearchIdPath),
                    new(
                        FederatedLoadRelationFixture.LoadSearchShapeId,
                        FederatedLoadRelationFixture.SearchCustomerNamePath)
                ])
            .Build();
        RelationQueryEvaluator evaluator = new(
            _ => compilation.Placement,
            FederatedLoadPhysicalExecutionFixture.CreatePolicy(maximumBatchSize: 2),
            [loadReader, customerReader]);
        var outcome = await evaluator.EvaluateAsync(evaluation);
        var physical = outcome.PhysicalExecution!;

        Assert.Equal(RelationQueryExecutionStatus.Succeeded, physical.Status);
        Assert.Empty(physical.Diagnostics);
        Assert.Equal(2, physical.SourceReads.Length);
        var loadRequest = Assert.Single(loadReader.Requests);
        Assert.IsType<RelationQueryBoundedEnumeration>(loadRequest.Constraint);
        Assert.Equal(
            [
                FederatedLoadRelationFixture.LoadCustomerIdPath,
                FederatedLoadRelationFixture.LoadIdPath
            ],
            loadRequest.Fields
                .Select(static field => field.SemanticPath)
                .OrderBy(static path => path.ToString(), StringComparer.Ordinal));
        var customerRequest = Assert.Single(customerReader.Requests);
        var customerBatch = Assert.IsType<RelationQueryIdentityBatchLookup>(customerRequest.Constraint);
        Assert.Equal(["customer-1", "customer-2"], customerBatch.Identities.ToArray());
        Assert.Equal(
            [FederatedLoadRelationFixture.CustomerNamePath],
            customerRequest.Fields.Select(static field => field.SemanticPath));

        var execution = Assert.IsType<RelationQueryExecutionResult>(physical.Interpretation);
        var rows = Assert.Single(execution.QueryResults);
        Assert.Equal(FederatedLoadRelationFixture.RowsResultId, rows.Result);
        Assert.Equal(RootCount, rows.Rows.Length);

        var explain = RelationQueryExplainProjector.Project(outcome);
        Assert.Contains(
            explain.Stages,
            static stage => stage is RelationQueryPhysicalPlanningExplainStage
                { Status: RelationQueryExplainStageStatus.Complete });
        Assert.IsType<RelationQueryEvaluationExplainStage>(explain.Stages[^1]);
    }

    [Fact]
    public async Task MissingCustomerSnippet_ProducesDocumentedRequirementGapAndPartialDto()
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
            new("documentation/missing-customer"),
            suppliedSources: [scenario.SuppliedLoads],
            capabilities: FederatedLoadPhysicalExecutionFixture.AvailableCapabilities(compilation.Plan)));

        Assert.Equal(RelationQueryExecutionStatus.Incomplete, physical.Status);
        var execution = Assert.IsType<RelationQueryExecutionResult>(physical.Interpretation);
        var gap = Assert.Single(execution.RequirementGapAnalysis.Gaps);
        Assert.Equal(RelationRequirementGapCause.RelatedObservationNotFound, gap.Cause);
        Assert.Equal(RelationQueryEvidenceCompleteness.Complete, gap.RelationshipContext!.Completeness);
        Assert.Equal(RelationshipTraversalCardinality.AtMostOne, gap.RelationshipContext.ExpectedCardinality);
        Assert.Equal(0, gap.RelationshipContext.ObservedCount);
        Assert.Equal(ObservationValue.FromString("customer-1"), gap.RelationshipContext.ReferenceValue);
        Assert.Contains(
            RelationRequirementGapResolutionKind.ProvideRelatedObservation,
            gap.SuggestedResolutions);

        var mapperCompilation = RelationDtoMapperCompiler.Default.Compile<FederatedLoadSearchRow>(compilation.Plan);
        var mapped = mapperCompilation.Mapper!.Map(
            physical,
            RelationDtoMappingFailurePolicy.CollectDiagnostics);
        var partial = Assert.Single(mapped.Rows);
        Assert.Equal(RelationDtoMappingStatus.Incomplete, mapped.Status);
        Assert.Equal("load-1", partial.Value.Id);
        Assert.Null(partial.Value.CustomerName);
        Assert.Equal("TRUCK-001", partial.Value.EquipmentNumber);
        Assert.False(partial.Source.IsComplete);
        Assert.Equal(gap.Id, Assert.Single(partial.Source.UnresolvedGaps));
    }

    static CompleteRelation AuthorCompleteRelation()
    {
        var author = RelationQuery.Expression();
        var loads = author.Source<Load>();
        var customers = author.Traverse<Load, Customer>(
            loads,
            load => load.CustomerId);
        var loadEquipment = author.Relationship<Load, Equipment>(load => load.EquipmentId);
        var equipment = author.Traverse(
            customers,
            loads.Binding,
            loadEquipment,
            requirement: QueryInputRequirement.Optional);
        var documents = author.Project(
            equipment,
            (Load load, Customer customer, Equipment unit) => new LoadSearchDto
            {
                Id = load.Id,
                CustomerId = load.CustomerId,
                CustomerName = customer.Name,
                CustomerType = customer.Type,
                EquipmentNumber = unit.Number
            },
            loads.Binding,
            customers.Binding);
        return new(author, documents.BuildRelation(dto => dto.Id));
    }

    static string Format<T>(IEnumerable<T> diagnostics) => string.Join(Environment.NewLine, diagnostics);

    sealed record CompleteRelation(
        RelationQueryExpressionAuthoring Author,
        RelationQueryAuthoringResult<RelationDefinition> Relation);

    public sealed class Load
    {
        [JsonPropertyName("id")]
        public required string Id { get; init; }

        [JsonPropertyName("customerId")]
        public required string CustomerId { get; init; }

        [JsonPropertyName("equipmentId")]
        public required string EquipmentId { get; init; }

        [JsonPropertyName("status")]
        public required string Status { get; init; }
    }

    public sealed class LoadDto
    {
        [JsonPropertyName("id")]
        public required string Id { get; init; }

        [JsonPropertyName("status")]
        public required string Status { get; init; }
    }

    public sealed class Customer
    {
        [JsonPropertyName("id")]
        public required string Id { get; init; }

        [JsonPropertyName("name")]
        public required string Name { get; init; }

        [JsonPropertyName("type")]
        public required string Type { get; init; }
    }

    public sealed class Equipment
    {
        [JsonPropertyName("id")]
        public required string Id { get; init; }

        [JsonPropertyName("number")]
        public required string Number { get; init; }
    }

    public sealed class LoadSearchDto
    {
        [JsonPropertyName("id")]
        public required string Id { get; init; }

        [JsonPropertyName("customerId")]
        public required string CustomerId { get; init; }

        [JsonPropertyName("customerName")]
        public string? CustomerName { get; init; }

        [JsonPropertyName("customerType")]
        public string? CustomerType { get; init; }

        [JsonPropertyName("equipmentNumber")]
        public string? EquipmentNumber { get; init; }
    }

    public sealed class LoadSearchSummary
    {
        [JsonPropertyName("loadCount")]
        public long LoadCount { get; init; }
    }
}
