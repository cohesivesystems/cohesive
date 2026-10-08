using BenchmarkDotNet.Attributes;
using Cohesive.Model;
using Cohesive.Model.Authoring;

namespace Cohesive.Relations.Benchmarks;

/// <summary>Measures warmed default CLR contract reuse for representative structural shapes.</summary>
[MemoryDiagnoser]
public class ClrTypeRefMappingBenchmarks
{
    readonly DefaultClrTypeRefMapper mapper = new();
    // A nonempty, unused override measures fresh traversal independently of default root caching.
    readonly DefaultClrTypeRefMapper traversalMapper = new(new Dictionary<Type, TypeRef>
    {
        [typeof(decimal)] = new ScalarTypeRef(ScalarTypeKind.Decimal)
    });
    Type input = null!;

    [Params("flat", "nested", "collection", "large")]
    public string Shape { get; set; } = "flat";

    [GlobalSetup]
    public void Setup() => input = Shape switch
    {
        "flat" => typeof(Leaf),
        "nested" => typeof(Branch),
        "collection" => typeof(Collection),
        "large" => typeof(Large),
        _ => throw new InvalidOperationException(Shape)
    };

    [Benchmark]
    public TypeRef Map() => mapper.Map(input, null);

    [Benchmark]
    public TypeRef Traverse() => traversalMapper.Map(input, null);

    sealed record Leaf(string Name, string? Description, long Sequence, DateTimeOffset Time);
    sealed record Branch(Leaf First, Leaf Second, Leaf Third, Leaf Fourth);
    sealed record Collection(IReadOnlyList<Branch> Required, IReadOnlyList<Branch?> Optional);
    sealed record Large(Branch A, Branch B, Branch C, Branch D, Branch E, Branch F, Branch G, Branch H);
}
