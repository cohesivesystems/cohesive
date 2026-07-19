using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Cohesive.Adapters.Cosmos;
using Cohesive.Adapters.Elastic;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.IR;
using Cohesive.Relations.Physical;
using Cohesive.Relations.Realization;
using Cohesive.Tests.Elastic;

namespace Cohesive.Tests.Relations;

public sealed class ExpressionAuthoredAdapterCompilationTests
{
    static readonly FieldPath IdPath = FieldPath.FromField("id");
    static readonly FieldPath StatusPath = FieldPath.FromField("status");
    static readonly FieldPath CustomerNamePath = FieldPath.FromField("customerName");
    static readonly FieldPath StopLocationsPath = FieldPath.FromField("stopLocations");

    [Fact]
    public void ExpressionAuthoredRowsAndAggregation_CompileThroughCosmosWithoutCanonicalRewrite()
    {
        var fixture = CreateCosmosFixture();
        var realization = RelationQueryRealizationCompiler.Compile(
            fixture.Plan,
            CosmosRelationQueryTargetProfile.Default,
            CosmosRelationQueryTargetProfile.Policy,
            RelationQueryResultObservability.NotRequested);
        Assert.True(realization.IsRealizable, Format(realization.Diagnostics));

        var placement = RelationQueryPlacement.For(fixture.Plan);
        var source = placement.Source(
            sourceKey: "tests/cosmos-expression/loads",
            targetProfile: CosmosRelationQueryTargetProfile.Default);
        var authoredInput = placement.PlaceSource(source, fixture.SourceShape)
            .Identity(load => load.Id)
            .FieldsBySemanticPath();
        var authoredPlacement = placement.Build().RequireValue();
        var placedInput = authoredPlacement.GetInput(authoredInput);
        var storage = CosmosRelationQueryBinding.For(
                placedInput,
                explicitAuthority: "tests/cosmos-expression/v1")
            .Account(new Uri("https://localhost:8081"))
            .Database("operations")
            .Container("loads")
            .Identity(load => load.Id)
            .FieldsBySemanticPath()
            .StableUnique(load => load.Id)
            .ExactOrdering(load => load.Id)
            .MaximumInputRows(10_000)
            .Build()
            .RequireValue();

        var result = new CosmosRelationQueryCompiler().Compile(
            new RelationQueryBoundRealizationRequest(
                fixture.Plan,
                realization,
                authoredPlacement.Placement),
            storage);

        Assert.True(result.IsSuccessful, Format(result.Diagnostics));
        Assert.Equal(2, result.Artifacts.Length);
        Assert.All(result.Artifacts, artifact =>
            AssertCanonicalProvenance(fixture, authoredPlacement.Placement, artifact.Provenance));

        var rows = Assert.Single(result.Artifacts, static artifact =>
            artifact.Branch.Kind == RelationQueryNativeResultKind.QueryRows);
        Assert.Equal(
            "SELECT c[\"id\"] AS f0, c[\"status\"] AS f1 FROM c "
            + "WHERE (c[\"status\"] = @p0) ORDER BY c[\"id\"] ASC OFFSET 0 LIMIT 25",
            rows.Statement.Text);

        var aggregation = Assert.Single(result.Artifacts, static artifact =>
            artifact.Branch.Kind == RelationQueryNativeResultKind.QueryAggregation);
        Assert.Equal(
            "SELECT COUNT(1) AS f0 FROM c WHERE (c[\"status\"] = @p0)",
            aggregation.Statement.Text);
        AssertConfigurationDecision(
            storage.ConfigurationDecisions,
            "maximumInputRows",
            RelationQueryConfigurationValueOrigin.Explicit,
            "tests/cosmos-expression/v1");
    }

    [Fact]
    public void ExpressionAuthoredLoadSearchRowsAndAggregation_CompileThroughElasticWithoutCanonicalRewrite()
    {
        var fixture = CreateElasticFixture();
        var realization = RelationQueryRealizationCompiler.Compile(
            fixture.Plan,
            ElasticRelationQueryTargetProfile.Default,
            ElasticRelationQueryTargetProfile.Policy,
            RelationQueryResultObservability.NotRequested);
        Assert.True(realization.IsRealizable, Format(realization.Diagnostics));

        var placement = RelationQueryPlacement.For(fixture.Plan);
        var source = placement.Source(
            sourceKey: "tests/elastic-expression/load-search",
            targetProfile: ElasticRelationQueryTargetProfile.Default);
        var authoredInput = placement.PlaceSource(source, fixture.SourceShape)
            .Identity(document => document.Id)
            .FieldsBySemanticPath();
        var authoredPlacement = placement.Build().RequireValue();
        var placedInput = authoredPlacement.GetInput(authoredInput);
        var storage = ElasticRelationQueryBinding.For(
                placedInput,
                explicitAuthority: "tests/elastic-expression/v1")
            .Index("load-search")
            .PaginationConsistency(ElasticRelationQueryPaginationConsistency.StableSearchView)
            .FieldsExplicitly()
            .Keyword(
                document => document.Id,
                FieldPath.Parse("id.keyword"),
                ElasticRelationQueryFieldSemanticCapabilities.ExactTerm
                | ElasticRelationQueryFieldSemanticCapabilities.ExactOrdering
                | ElasticRelationQueryFieldSemanticCapabilities.StableUniqueOrdering,
                "tests/ordinal-keyword/v1",
                sourceField: IdPath)
            .Field(
                document => document.CustomerName,
                field => field
                    .Source(CustomerNamePath, ElasticRelationQueryFieldValueEncoding.JsonString)
                    .Query(FieldPath.Parse("customerName.keyword"), ElasticRelationQueryFieldMappingKind.Keyword)
                    .RootDocument()
                    .Attest(
                        ElasticRelationQueryFieldSemanticCapabilities.WildcardSuffix,
                        "tests/ordinal-keyword/v1"))
            .CollectionKeyword(
                document => document.StopLocations,
                FieldPath.Parse("stopLocations.keyword"),
                "tests/ordinal-keyword-array/v1")
            .Build()
            .RequireValue();

        RelationQueryBoundRealizationRequest request = new(
            fixture.Plan,
            realization,
            authoredPlacement.Placement);
        var result = new ElasticRelationQueryCompiler().Compile(request, storage);

        Assert.True(result.IsSuccessful, Format(result.Diagnostics));
        Assert.Equal(2, result.Artifacts.Length);
        Assert.All(result.Artifacts, artifact =>
            AssertCanonicalProvenance(fixture, authoredPlacement.Placement, artifact.Provenance));

        var parameters = new Dictionary<QueryParameterId, ObservationValue>
        {
            [new("customer-name-suffix")] = ObservationValue.FromString("Inc"),
            [new("location")] = ObservationValue.FromString("SEA")
        };
        var rows = Assert.Single(result.Artifacts, static artifact =>
            artifact.Branch.Kind == RelationQueryNativeResultKind.QueryRows);
        using var rowsJson = ElasticSdkRequestTestSupport.Serialize(rows.Bind(parameters));
        Assert.Contains("customerName.keyword", rowsJson.RootElement.GetRawText(), StringComparison.Ordinal);
        Assert.Contains("Inc", rowsJson.RootElement.GetRawText(), StringComparison.Ordinal);
        Assert.Contains("stopLocations.keyword", rowsJson.RootElement.GetRawText(), StringComparison.Ordinal);
        Assert.Contains("SEA", rowsJson.RootElement.GetRawText(), StringComparison.Ordinal);

        var aggregation = Assert.Single(result.Artifacts, static artifact =>
            artifact.Branch.Kind == RelationQueryNativeResultKind.QueryAggregation);
        using var aggregationJson = ElasticSdkRequestTestSupport.Serialize(aggregation.Bind(parameters));
        Assert.Equal(0, aggregationJson.RootElement.GetProperty("size").GetInt32());
        Assert.True(aggregationJson.RootElement.GetProperty("track_total_hits").GetBoolean());
        Assert.Contains("customerName.keyword", aggregationJson.RootElement.GetRawText(), StringComparison.Ordinal);
        Assert.Contains("Inc", aggregationJson.RootElement.GetRawText(), StringComparison.Ordinal);
        Assert.Contains("stopLocations.keyword", aggregationJson.RootElement.GetRawText(), StringComparison.Ordinal);
        AssertConfigurationDecision(
            storage.ConfigurationDecisions,
            "indexName",
            RelationQueryConfigurationValueOrigin.Explicit,
            "tests/elastic-expression/v1");
    }

    static ExpressionAuthoredFixture<PortableLoad> CreateCosmosFixture()
    {
        var author = RelationQuery.Expression();
        var sourceShape = author.Clr.Shape<PortableLoad>();
        var status = author.Parameter<string>("status");
        var loads = author.Source(sourceShape);
        var filtered = author.Filter(
            loads.Node,
            (PortableLoad load) => load.Status == status.Value,
            loads.Binding,
            sourceReference: "portable-loads/filter-status");
        var projected = author.Project(
            filtered,
            (PortableLoad load) => new PortableLoadRow
            {
                Id = load.Id,
                Status = load.Status
            },
            loads.Binding,
            sourceReference: "portable-loads/project-row");
        var ordered = author.Order(
            projected.Node,
            (PortableLoadRow row) => row.Id,
            projected.Binding,
            sourceReference: "portable-loads/order-id");
        var paged = author.Page(
            ordered,
            new OffsetPageDefinition(limit: 25),
            sourceReference: "portable-loads/page");
        var summary = author.Aggregate<FilterQueryNode, PortableLoadCount>(
            filtered,
            aggregate => aggregate
                .Count(result => result.Count),
            sourceReference: "portable-loads/count");
        var rows = author.Rows(paged, projected.Binding, id: "rows");
        var aggregation = author.Aggregation(summary, id: "status-counts");
        var authored = author.BuildQuery(
            new("portable-loads"),
            new("PortableLoads"),
            rows,
            aggregation);

        return CompileFixture(authored, author.ShapeDocuments, sourceShape);
    }

    static ExpressionAuthoredFixture<LoadSearchDocument> CreateElasticFixture()
    {
        var author = RelationQuery.Expression();
        var sourceShape = author.Clr.Shape<LoadSearchDocument>();
        var customerNameSuffix = author.Parameter<string>("customer-name-suffix");
        var location = author.Parameter<string>("location");
        var documents = author.Source(sourceShape);
        var filtered = author.Filter(
            documents.Node,
            (LoadSearchDocument document) =>
                document.CustomerName.EndsWith(customerNameSuffix.Value, StringComparison.Ordinal)
                && document.StopLocations.Contains(location.Value),
            documents.Binding,
            sourceReference: "load-search/filter-customer-and-stop");
        var projected = author.Project(
            filtered,
            (LoadSearchDocument document) => new LoadSearchRow
            {
                Id = document.Id,
                CustomerName = document.CustomerName
            },
            documents.Binding,
            sourceReference: "load-search/project-row");
        var ordered = author.Order(
            projected.Node,
            (LoadSearchRow row) => row.Id,
            projected.Binding,
            sourceReference: "load-search/order-id");
        var paged = author.Page(
            ordered,
            new OffsetPageDefinition(limit: 25),
            sourceReference: "load-search/page");
        var count = author.Aggregate<FilterQueryNode, LoadSearchCount>(
            filtered,
            aggregate => aggregate.Count(result => result.Count),
            sourceReference: "load-search/count");
        var rows = author.Rows(paged, projected.Binding, id: "rows");
        var aggregation = author.Aggregation(count, id: "count");
        var authored = author.BuildQuery(
            new("load-search"),
            new("LoadSearch"),
            rows,
            aggregation);

        return CompileFixture(authored, author.ShapeDocuments, sourceShape);
    }

    static ExpressionAuthoredFixture<T> CompileFixture<T>(
        RelationQueryAuthoringResult<QueryDefinition> authored,
        ImmutableArray<ShapeGraphDocument> shapeDocuments,
        RelationQueryClrShape<T> sourceShape)
        where T : notnull
    {
        Assert.True(authored.Validation.IsValid, Format(authored.Validation.Diagnostics));
        var document = authored.CreateDocument();
        var compilation = RelationQueryStaticCompiler.Compile(new(document, shapeDocuments));
        Assert.True(compilation.IsSuccessful, Format(compilation.Diagnostics));
        var plan = Assert.IsType<CompiledRelationQueryPlan>(compilation.Plan);
        Assert.Equal(document.DefinitionFingerprint, plan.Provenance.DefinitionFingerprint);
        Assert.Equal(
            RelationQueryJsonSerializer.Serialize(document, indented: false),
            RelationQueryJsonSerializer.Serialize(plan.Provenance.DefinitionDocument, indented: false));
        return new(document, plan, sourceShape);
    }

    static void AssertCanonicalProvenance<T>(
        ExpressionAuthoredFixture<T> fixture,
        RelationQuerySourcePlacement placement,
        RelationQueryNativeCompilationProvenance provenance)
        where T : notnull
    {
        Assert.Equal(fixture.Document.DefinitionFingerprint, provenance.Plan.DefinitionFingerprint);
        Assert.Equal(fixture.Plan.Provenance.DefinitionFingerprint, provenance.Plan.DefinitionFingerprint);
        Assert.Equal(RelationQueryCompiledPlanReference.From(fixture.Plan), provenance.Plan);
        Assert.Equal(placement.Fingerprint, provenance.Placement);
        Assert.Equal(
            RelationQueryJsonSerializer.Serialize(fixture.Document, indented: false),
            RelationQueryJsonSerializer.Serialize(fixture.Plan.Provenance.DefinitionDocument, indented: false));
    }

    static void AssertConfigurationDecision(
        IEnumerable<RelationQueryConfigurationDecision> decisions,
        string setting,
        RelationQueryConfigurationValueOrigin origin,
        string authority)
    {
        var decision = Assert.Single(decisions, candidate => candidate.Setting == setting);
        Assert.Equal(origin, decision.Origin);
        Assert.Equal(authority, decision.Authority);
    }

    static string Format<T>(IEnumerable<T> diagnostics) => string.Join(Environment.NewLine, diagnostics);

    sealed class PortableLoad
    {
        [JsonPropertyName("id")]
        public required string Id { get; init; }

        [JsonPropertyName("status")]
        public required string Status { get; init; }
    }

    sealed class PortableLoadRow
    {
        [JsonPropertyName("id")]
        public required string Id { get; init; }

        [JsonPropertyName("status")]
        public required string Status { get; init; }
    }

    sealed class PortableLoadCount
    {
        [JsonPropertyName("count")]
        public long Count { get; init; }
    }

    sealed class LoadSearchDocument
    {
        [JsonPropertyName("id")]
        public required string Id { get; init; }

        [JsonPropertyName("customerName")]
        public required string CustomerName { get; init; }

        [JsonPropertyName("stopLocations")]
        public required string[] StopLocations { get; init; }
    }

    sealed class LoadSearchRow
    {
        [JsonPropertyName("id")]
        public required string Id { get; init; }

        [JsonPropertyName("customerName")]
        public required string CustomerName { get; init; }
    }

    sealed class LoadSearchCount
    {
        [JsonPropertyName("count")]
        public long Count { get; init; }
    }

    sealed record ExpressionAuthoredFixture<T>(
        RelationQueryDocument Document,
        CompiledRelationQueryPlan Plan,
        RelationQueryClrShape<T> SourceShape)
        where T : notnull;
}
