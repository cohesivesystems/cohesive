using System.Collections.Immutable;
using BenchmarkDotNet.Attributes;
using Cohesive.Relations.IR;
using Cohesive.Relations.Physical;

namespace Cohesive.Relations.Benchmarks;

[MemoryDiagnoser]
public class QueryIdentityFingerprintBenchmarks
{
    [Params(1, 128, 1024)] public int Count { get; set; }
    [Params("sorted", "reverse", "duplicates")] public string Order { get; set; } = "sorted";
    ImmutableArray<QueryNodeId> ids;
    [GlobalSetup]
    public void Setup()
    {
        var values = Enumerable.Range(0, Count).Select(i => new QueryNodeId($"id/{i:D4}/é🙂"));
        if (Order == "reverse") values = values.Reverse();
        if (Order == "duplicates") values = values.Concat(values).Reverse();
        ids = [.. values];
    }
    [Benchmark]
    public string Compute()
    {
        using var writer = new PhysicalFingerprintWriter("benchmarks/ordinal-id-set/v1");
        writer.AppendIds(ids, static id => id.Value);
        return writer.Hash();
    }
}

[MemoryDiagnoser]
public class QueryProvenanceNormalizationBenchmarks
{
    [Params(1, 128, 1024)] public int Count { get; set; }
    [Params("sorted", "reverse")] public string Order { get; set; } = "sorted";
    ImmutableArray<QueryNodeId> ids;
    [GlobalSetup]
    public void Setup()
    {
        var values = Enumerable.Range(0, Count).Select(i => new QueryNodeId($"node/{i:D4}"));
        ids = [.. Order == "reverse" ? values.Reverse() : values];
    }
    [Benchmark] public RelationQueryPhysicalStageProvenance Normalize() => new(nodes: ids);
}
