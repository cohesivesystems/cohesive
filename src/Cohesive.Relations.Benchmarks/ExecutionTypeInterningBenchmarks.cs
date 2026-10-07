using BenchmarkDotNet.Attributes;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Relations.Benchmarks;

/// <summary>Measures document-local type preparation; declaration construction is outside the boundary.</summary>
[MemoryDiagnoser]
public class ExecutionTypeInterningBenchmarks
{
    Definition definition = null!;
    static readonly ExecutionProvenance Provenance = new(new("benchmark", "1"), new("benchmark/type-interning"), DocumentOrigin.Generated);

    [Params("flat", "nested", "collection", "large")]
    public string Shape { get; set; } = "flat";

    [GlobalSetup]
    public void Setup()
    {
        var count = Shape == "large" ? 512 : Shape == "collection" ? 128 : 16;
        TypeRef value = new ObjectTypeRef([.. Enumerable.Range(0, count).Select(index =>
            new ObjectFieldTypeDef($"field{index}", new ScalarTypeRef(ScalarTypeKind.String)))]);
        if (Shape == "nested")
            for (var depth = 0; depth < 12; depth++) value = new ArrayTypeRef(value);
        definition = new([value, value]);
        // Serializer metadata initialization is distinct from per-document type preparation.
        for (var index = 0; index < 16; index++) _ = Prepare();
    }

    [Benchmark]
    public ExecutionDefinitionDocument Prepare() => ExecutionDefinitionDocument.Create(
        new("benchmark"), new("types"), new("1"), definition, Provenance);

    public sealed record Definition(TypeRef[] Values);
}
