using System.Collections.Immutable;
using BenchmarkDotNet.Attributes;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Storage.Commits;

namespace Cohesive.Relations.Benchmarks;

/// <summary>Initial allocation/CPU baseline for the new durable commit declaration boundary, without database I/O.</summary>
[MemoryDiagnoser]
public class StorageCommitBenchmarks
{
    readonly StorageCommitAddress receipt = new("synthetic", "tenant/a", "operation/1");
    readonly PortableValue result = PortableValue.Concrete(new(new ScalarTypeRef(ScalarTypeKind.String)), ObservationValue.FromString("accepted"));
    ImmutableArray<StorageCommitWrite> writes;
    StorageCommitIntent intent = null!;
    string json = null!;

    /// <summary>Write count, including the largest write set fitting beside a Cosmos receipt.</summary>
    [Params(1, 10, 99)]
    public int Writes { get; set; }

    /// <summary>Prepares reusable immutable authoring inputs and durable wire data.</summary>
    [GlobalSetup]
    public void Setup()
    {
        var value = PortableValue.Concrete(new(new ScalarTypeRef(ScalarTypeKind.String)), ObservationValue.FromString(new string('x', 256)));
        writes = [.. Enumerable.Range(0, Writes).Select(index => new StorageCommitWrite(new("synthetic", "tenant/a", $"item/{index:D3}"), value))];
        intent = new(receipt, writes, result);
        json = StorageCommitJson.Serialize(intent);
    }

    /// <summary>Validates, canonicalizes and fingerprints a complete commit intent.</summary>
    /// <returns>A newly materialized immutable declaration.</returns>
    [Benchmark]
    public StorageCommitIntent ConstructAndFingerprint() => new(receipt, writes, result);

    /// <summary>Persists the materialized declaration to canonical portable JSON.</summary>
    /// <returns>Canonical wire JSON.</returns>
    [Benchmark]
    public string Serialize() => StorageCommitJson.Serialize(intent);

    /// <summary>Reconstructs and revalidates a persisted intent for exact retry after restart.</summary>
    /// <returns>The reconstructed declaration with a recomputed fingerprint.</returns>
    [Benchmark]
    public StorageCommitIntent DeserializeAndFingerprint() => StorageCommitJson.Deserialize(json);
}
