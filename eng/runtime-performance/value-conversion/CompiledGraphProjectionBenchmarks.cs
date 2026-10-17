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
    }

    [Benchmark]
    public ObservationValue Project() => ObservationValue.FromObject(source);

    [Benchmark]
    public Result<ShapeGraphDocument, DocumentValidationDiagnostic> DecodeFresh()
    {
        // A new root storage owner prevents a cached result from hiding first-decode work.
        var fresh = observation.WithField(SchemaVersion, ObservationValue.FromString(source.SchemaVersion));
        return HostedQueryValueAdapter.Decode<ShapeGraphDocument>(PortableValue.Concrete(Contract, fresh), Contract);
    }
}
