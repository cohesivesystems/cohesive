using System.Text.Json.Serialization;
using BenchmarkDotNet.Attributes;
using Cohesive.Model;
using Cohesive.Model.Authoring;

namespace Cohesive.Relations.Benchmarks;

/// <summary>Measures shared enum metadata discovery while retaining per-call type graph construction.</summary>
[MemoryDiagnoser]
public class EnumCatalogMappingBenchmarks
{
    readonly DefaultClrTypeRefMapper mapper = new();
    Type input = null!;

    [Params("flat", "nested", "collection", "large")]
    public string Shape { get; set; } = "flat";

    [GlobalSetup]
    public void Setup()
    {
        input = Shape switch
        {
            "flat" => typeof(Disposition),
            "nested" => typeof(Branch),
            "collection" => typeof(Collection),
            "large" => typeof(Large),
            _ => throw new InvalidOperationException(Shape)
        };
        for (var i = 0; i < 16; i++) _ = Map();
    }

    [Benchmark]
    public TypeRef Map() => mapper.Map(input, null);

    [JsonConverter(typeof(JsonStringEnumConverter))]
    enum Disposition
    {
        [JsonStringEnumMemberName("accepted")] Accepted,
        [JsonStringEnumMemberName("rejected")] Rejected,
        [JsonStringEnumMemberName("deferred")] Deferred
    }
    sealed record Leaf(Disposition First, Disposition Second, Disposition Third, Disposition Fourth);
    sealed record Branch(Leaf First, Leaf Second, Leaf Third, Leaf Fourth);
    sealed record Collection(IReadOnlyList<Branch> Required, IReadOnlyList<Branch?> Optional);
    sealed record Large(Branch A, Branch B, Branch C, Branch D, Branch E, Branch F, Branch G, Branch H);
}
