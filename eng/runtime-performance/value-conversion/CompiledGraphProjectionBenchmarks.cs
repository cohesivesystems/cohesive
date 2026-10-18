using System.Collections.Immutable;
using BenchmarkDotNet.Attributes;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Prelude;
using Cohesive.Relations.Execution;

[MemoryDiagnoser]
public class CompiledGraphProjectionBenchmarks
{
    static readonly ValueContract Contract = new(new JsonTypeRef());
    static readonly FieldPath SchemaVersion = FieldPath.FromField(nameof(ShapeGraphDocument.SchemaVersion));
    ShapeGraphDocument source = null!;
    ObservationValue observation;

    [Params(1, 128, 1024)] public int Fields { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        source = ShapeGraphDocument.FromGraph(new(new("benchmark"), [new(new("message"),
            [.. Enumerable.Range(0, Fields).Select(i => new FieldDefinition(new($"field{i}"),
                new ScalarTypeRef(ScalarTypeKind.String), annotations: ImmutableDictionary<AnnotationKey, AnnotationValue>.Empty
                    .Add(new("description"), AnnotationValue.FromString("é<>&😀"))
                    .Add(new("structured"), AnnotationValue.FromObject(new { Nested = 42 }))))])]));
        observation = Project();
        if (DecodeFresh().Success?.Graph.Shapes[0].Fields.Length != Fields)
            throw new InvalidOperationException("Graph projection differs.");
        if (ReferenceEquals(DecodeNewGraphStorage().Success!.Graph, DecodeNewGraphStorage().Success!.Graph))
            throw new InvalidOperationException("The first-decode benchmark reused graph storage.");
    }

    [Benchmark]
    public ObservationValue Project() => ObservationValue.FromObject(source);

    // Includes constructing the new immutable graph in both variants. This deliberately defeats
    // identity preparation; Project above measures repeated projection of one retained graph.
    [Benchmark]
    public ObservationValue AuthorAndProjectNewGraph() => ObservationValue.FromObject(ShapeGraphDocument.FromGraph(
        new(source.Graph.Id, source.Graph.Shapes, source.Graph.NamedTypes, source.Graph.Diagnostics, source.Graph.Annotations), source.Metadata));

    [Benchmark]
    public Result<ShapeGraphDocument, DocumentValidationDiagnostic> DecodeNewGraphStorage()
    {
        // WithField can retain a semantically equal child. Explicitly snapshot both dictionaries
        // so first graph admission is measured even when immutable child projections are cached.
        var graph = ObservationValue.FromObject(observation.GetProperty("Graph").Fields!
            .ToDictionary(static field => field.Key, static field => field.Value, StringComparer.Ordinal));
        var fields = observation.Fields!.ToDictionary(static field => field.Key, static field => field.Value, StringComparer.Ordinal);
        fields["Graph"] = graph;
        var fresh = ObservationValue.FromObject(fields);
        return HostedQueryValueAdapter.Decode<ShapeGraphDocument>(PortableValue.Concrete(Contract, fresh), Contract);
    }

    [Benchmark]
    public Result<ShapeGraphDocument, DocumentValidationDiagnostic> DecodeFresh()
    {
        // Fresh envelope; unchanged graph storage deliberately retains its prepared interpretation.
        var fresh = observation.WithField(SchemaVersion, ObservationValue.FromString(source.SchemaVersion));
        return HostedQueryValueAdapter.Decode<ShapeGraphDocument>(PortableValue.Concrete(Contract, fresh), Contract);
    }
}
