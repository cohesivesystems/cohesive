using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Cohesive.MaterializationHarness.Model;
using Cohesive.Model;
using Cohesive.Relations.TestFixtures;

namespace Cohesive.Relations.Benchmarks;

/// <summary>Strict ingestion and complete CLR projection over the repository's canonical freight-order application model.</summary>
[Config(typeof(RelationBenchmarkConfig)), MemoryDiagnoser]
public class ObservationNestedApplicationBenchmarks
{
    ObservationMaterializer<FreightOrder>? freight;
    ObservationMaterializer<JoinedBatch>? joined;
    ObservationMaterializer<IdOnly>? sparse;
    byte[] json = null!;

    /// <summary>Small, repeated, case-variant or large freight aggregates, plus sparse admission controls.</summary>
    [Params("joined-128", "joined-large", "freight-8", "freight-128", "freight-case-128", "freight-large", "sparse-128", "sparse-large")]
    public string Workload { get; set; } = "freight-8";

    /// <summary>Uses the application's canonical graph and verifies every projected field against its source DTO.</summary>
    /// <exception cref="InvalidOperationException">Strict ingestion changes a value.</exception>
    [GlobalSetup]
    public void Setup()
    {
        if (Workload.StartsWith("joined", StringComparison.Ordinal))
        {
            var metadata = new ClrShapeGraphBuilder().AddShape<JoinedBatch>().BuildResult(new("joined-load-batch-application"));
            var shape = metadata.GetShape<JoinedBatch>();
            var count = Workload == "joined-large" ? 4096 : 128;
            JoinedBatch source = new([.. Enumerable.Range(0, count).Select(i => new LoadSearchDto(
                "load-" + i, "customer-" + i % 8, "Customer café " + i % 8, "Shipper", "equipment-" + i % 16,
                "EQ-" + i % 16, "DryVan", i % 2 == 0 ? "Open" : "Assigned", i + 0.125m))]);
            json = JsonSerializer.SerializeToUtf8Bytes(source);
            joined = ObservationMaterializer.For<JoinedBatch>(shape).Compile(ObservationLayout.Create(shape));
            if (JsonSerializer.Serialize(source) != JsonSerializer.Serialize(joined.MaterializeJson(json)))
                throw new InvalidOperationException("Joined load batch projection differs.");
        }
        else if (Workload.StartsWith("freight", StringComparison.Ordinal))
        {
            var semantics = FreightOrderMaterializationModel.Create();
            var graph = semantics.CompilationRequest.ShapeDocuments.Single(d => d.Graph.Id == FreightOrderMaterializationModel.GraphId).Graph;
            var shape = new GraphShapeId(graph, FreightOrderMaterializationModel.OrderShapeId.ShapeId);
            var count = Workload == "freight-8" ? 8 : Workload == "freight-large" ? 4096 : 128;
            FreightOrder source = new()
            {
                Id = "order-17", TenantId = "tenant-3", OrderNumber = "FR-0017", CustomerAccountId = "account-8",
                EquipmentClass = "DryVan", CreatedAt = new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero),
                Stops = [.. Enumerable.Range(0, count).Select(i => new FreightOrderStop
                    { Id = "stop-" + i, Sequence = i + 1, StopType = i % 2 == 0 ? "Pickup" : "Drop", LocationId = "location-" + i % 16 })]
            };
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            var text = JsonSerializer.Serialize(source, options);
            if (Workload == "freight-case-128") text = text.Replace("\"sequence\"", "\"SEQUENCE\"", StringComparison.Ordinal);
            json = Encoding.UTF8.GetBytes(text);
            freight = ObservationMaterializer.For<FreightOrder>(shape).WithSerializerOptions(options).Compile(ObservationLayout.Create(shape));
            if (JsonSerializer.Serialize(source, options) != JsonSerializer.Serialize(freight.MaterializeJson(json), options))
                throw new InvalidOperationException("Freight aggregate projection differs.");
        }
        else
        {
            ObjectTypeRef row = new([.. Enumerable.Range(0, 32).Select(i => new ObjectFieldTypeDef("field" + i,
                new ScalarTypeRef(ScalarTypeKind.Int64), presence: i == 0 ? FieldPresence.Required : FieldPresence.Optional))]);
            Shape definition = new(new("sparse"), [new(new("Id"), new ScalarTypeRef(ScalarTypeKind.Int64)),
                new(new("rows"), row, cardinality: FieldCardinality.Many)]);
            ShapeGraph graph = new(new("strict-sparse-application-control"), [definition]); GraphShapeId shape = new(graph, definition.Id);
            sparse = ObservationMaterializer.For<IdOnly>(shape).Compile(ObservationLayout.Create(shape));
            var count = Workload == "sparse-large" ? 4096 : 128;
            json = Encoding.UTF8.GetBytes("{\"Id\":17,\"rows\":[" + string.Join(',', Enumerable.Repeat("{\"field0\":1,\"field1\":2,\"field2\":3,\"field3\":4}", count)) + "]}");
            if (sparse.MaterializeJson(json).Id != 17) throw new InvalidOperationException("Sparse projection differs.");
        }
    }

    /// <summary>Includes strict parsing, complete admission, owned storage, and the existing CLR projection policy.</summary>
    /// <returns>The complete freight DTO or sparse control DTO.</returns>
    [Benchmark]
    public object IngestAndProject() => joined is not null ? joined.MaterializeJson(json) : freight is not null ? freight.MaterializeJson(json) : sparse!.MaterializeJson(json);

    /// <summary>Batch envelope over the repository's existing joined-load projection contract.</summary>
    /// <param name="Rows">Completely admitted joined DTOs.</param>
    public sealed record JoinedBatch(ImmutableArray<LoadSearchDto> Rows);

    /// <summary>CLR target for a sparse payload that must still be completely admitted.</summary>
    /// <param name="Id">Admitted root identity.</param>
    public sealed record IdOnly(long Id);
}
