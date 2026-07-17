using System.Text.Json.Serialization;
using Cohesive.Adapters.Cosmos;
using Cohesive.Adapters.Elastic;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.IR;
using Cohesive.Relations.Physical;
using Cohesive.Relations.Realization;

namespace Cohesive.Tests.Relations;

public sealed class ExpressionAuthoredAdapterCompilationTests
{
    static readonly FieldPath IdPath = FieldPath.FromField("id");
    static readonly FieldPath StatusPath = FieldPath.FromField("status");

    [Fact]
    public void ExpressionAuthoredQuery_CompilesThroughCosmosWithoutCanonicalRewrite()
    {
        var fixture = CreateExpressionAuthoredFixture();
        var realization = RelationQueryRealizationCompiler.Compile(
            fixture.Plan,
            CosmosRelationQueryTargetProfile.Default,
            CosmosRelationQueryTargetProfile.Policy,
            RelationQueryResultObservability.NotRequested);
        Assert.True(realization.IsRealizable, Format(realization.Diagnostics));
        var (placement, sourcePlacement) = CreatePlacement(
            fixture.Plan,
            CosmosRelationQueryTargetProfile.Default,
            CosmosRelationQueryStorageBinding.SemanticPathConventionSet,
            provider: "tests/cosmos-expression");
        var storage = CosmosRelationQueryStorageBinding.FromSemanticPathConvention(
            new("tests/cosmos-expression/v1"),
            sourcePlacement,
            CosmosRelationQueryTargetProfile.Target,
            CosmosRelationQueryTargetProfile.ProfileId,
            "loads",
            IdPath,
            stableUniqueOrderingPaths: [IdPath],
            exactOrderingPaths: [IdPath],
            maximumInputRows: 10_000);

        var result = new CosmosRelationQueryCompiler().Compile(
            new(fixture.Plan, realization, placement),
            storage);

        Assert.True(result.IsSuccessful, Format(result.Diagnostics));
        var artifact = Assert.Single(result.Artifacts);
        Assert.Equal(fixture.Document.DefinitionFingerprint, artifact.Provenance.Plan.DefinitionFingerprint);
        Assert.Equal(
            RelationQueryJsonSerializer.Serialize(fixture.Document, indented: false),
            RelationQueryJsonSerializer.Serialize(fixture.Plan.Provenance.DefinitionDocument, indented: false));
        Assert.Equal(
            "SELECT c[\"id\"] AS f0, c[\"status\"] AS f1 FROM c "
            + "WHERE (c[\"status\"] = @p0) ORDER BY c[\"id\"] ASC OFFSET 0 LIMIT 25",
            artifact.Statement.Text);
    }

    [Fact]
    public void ExpressionAuthoredQuery_CompilesThroughElasticWithoutCanonicalRewrite()
    {
        var fixture = CreateExpressionAuthoredFixture();
        var realization = RelationQueryRealizationCompiler.Compile(
            fixture.Plan,
            ElasticRelationQueryTargetProfile.Default,
            ElasticRelationQueryTargetProfile.Policy,
            RelationQueryResultObservability.NotRequested);
        Assert.True(realization.IsRealizable, Format(realization.Diagnostics));
        var (placement, sourcePlacement) = CreatePlacement(
            fixture.Plan,
            ElasticRelationQueryTargetProfile.Default,
            ElasticRelationQueryStorageBinding.SemanticPathConventionSet,
            provider: "tests/elastic-expression");
        var sourceContract = Assert.Single(fixture.Plan.InputContract.Sources);
        var storage = new ElasticRelationQueryStorageBinding(
            new("tests/elastic-expression/v2"),
            sourcePlacement.Source,
            sourcePlacement.Id,
            ElasticRelationQueryTargetProfile.Target,
            ElasticRelationQueryTargetProfile.ProfileId,
            "loads",
            [
                .. sourceContract.Fields.Select(CreateElasticFieldBinding)
            ],
            conventionSetVersion: ElasticRelationQueryStorageBinding.SemanticPathConventionSet);

        var result = new ElasticRelationQueryCompiler().Compile(
            new(fixture.Plan, realization, placement),
            storage);

        Assert.True(result.IsSuccessful, Format(result.Diagnostics));
        var artifact = Assert.Single(result.Artifacts);
        Assert.Equal(fixture.Document.DefinitionFingerprint, artifact.Provenance.Plan.DefinitionFingerprint);
        Assert.Equal(
            RelationQueryJsonSerializer.Serialize(fixture.Document, indented: false),
            RelationQueryJsonSerializer.Serialize(fixture.Plan.Provenance.DefinitionDocument, indented: false));

        var request = artifact.Bind(new Dictionary<QueryParameterId, ObservationValue>
        {
            [new("status")] = ObservationValue.FromString("InTransit")
        });
        var root = Assert.IsType<global::Elastic.Clients.Elasticsearch.QueryDsl.BoolQuery>(request.Query!.Bool);
        var term = Assert.Single(root.Filter!).Term!;
        Assert.Equal("status", term.Field.ToString());
        Assert.Equal("id", Assert.Single(request.Sort!).Field!.Field.ToString());
    }

    static ExpressionAuthoredFixture CreateExpressionAuthoredFixture()
    {
        var author = RelationQuery.Expression();
        var status = author.Parameter<string>("status");
        var loads = author.Source<PortableLoad>();
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
        var rows = author.Rows(paged, projected.Binding, id: "rows");
        var authored = author.BuildQuery(
            new("portable-loads"),
            new("PortableLoads"),
            rows);
        Assert.True(authored.Validation.IsValid, Format(authored.Validation.Diagnostics));

        var document = authored.CreateDocument();
        var compilation = RelationQueryStaticCompiler.Compile(new(
            document,
            author.ShapeDocuments));
        Assert.True(compilation.IsSuccessful, Format(compilation.Diagnostics));
        var plan = Assert.IsType<CompiledRelationQueryPlan>(compilation.Plan);
        Assert.Equal(document.DefinitionFingerprint, plan.Provenance.DefinitionFingerprint);
        return new(document, plan);
    }

    static (
        RelationQuerySourcePlacement Placement,
        RelationQuerySourcePlacementBinding SourcePlacement) CreatePlacement(
        CompiledRelationQueryPlan plan,
        RelationQueryTargetCapabilityProfile targetProfile,
        string conventionSetVersion,
        string provider)
    {
        var source = Assert.Single(plan.InputContract.Sources);
        RelationQuerySourceInstanceId sourceId = new($"source/{provider}");
        var sourcePlacement = new RelationQuerySourcePlacementBinding(
            new($"placement/{provider}"),
            source.Input.Id,
            source.Node,
            source.Binding,
            source.Shape,
            sourceId,
            RelationQuerySourcePlacementBindingKind.SourceSet,
            RelationQuerySourceAcquisitionKind.BoundedEnumeration,
            RelationQuerySourcePlacementOrigin.Explicit,
            new(source.Shape, IdPath.ToString()),
            [
                .. source.Fields.Select(static field => new RelationQuerySourceFieldBinding(
                    field.Input.Id,
                    field.Input.Field.Path,
                    field.Input.Field.Path.ToString()))
            ]);
        var sourceInstance = new RelationQuerySourceInstance(
            sourceId,
            new(provider),
            targetProfile,
            new(
                maximumBatchSize: 100,
                maximumBufferedRows: 10_000,
                maximumFanOut: 100,
                maximumConcurrency: 4));
        var placement = new RelationQuerySourcePlacement(
            RelationQuerySourcePlacement.CurrentSchemaVersion,
            RelationQueryCompiledPlanReference.From(plan),
            conventionSetVersion,
            [sourceInstance],
            [sourcePlacement]);
        return (placement, sourcePlacement);
    }

    static ElasticRelationQueryFieldBinding CreateElasticFieldBinding(
        RelationQueryFieldInputContract field)
    {
        var path = field.Input.Field.Path;
        var capabilities = path == IdPath
            ? ElasticRelationQueryFieldSemanticCapabilities.ExactOrdering
              | ElasticRelationQueryFieldSemanticCapabilities.StableUniqueOrdering
            : path == StatusPath
                ? ElasticRelationQueryFieldSemanticCapabilities.ExactTerm
                : throw new InvalidOperationException($"Unexpected expression-authored input field '{path}'.");
        return new(
            field.Input.Id,
            sourceField: path,
            queryField: path,
            mappingKind: ElasticRelationQueryFieldMappingKind.Keyword,
            retrievalKind: ElasticRelationQueryFieldRetrievalKind.Source,
            retrievalEncoding: ElasticRelationQueryFieldValueEncoding.JsonString,
            documentScope: ElasticRelationQueryFieldDocumentScope.RootDocument,
            semanticCapabilities: capabilities,
            semanticProfile: "tests/ordinal-keyword/v1");
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

    sealed record ExpressionAuthoredFixture(
        RelationQueryDocument Document,
        CompiledRelationQueryPlan Plan);
}
