using BenchmarkDotNet.Attributes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cohesive.Model.Serialization;
using Cohesive.Model;
using Cohesive.Relations.IR;
using Cohesive.Relations.Serialization;
using Cohesive.Relations.TestFixtures;

namespace Cohesive.Relations.Benchmarks;

/// <summary>Fingerprints new definitions using warmed serializer metadata; declarations are prepared outside the boundary.</summary>
[MemoryDiagnoser]
public class RelationDefinitionFingerprintBenchmarks
{
    RelationQueryDefinition definition = null!;

    [Params("flat", "nested", "collection", "large")]
    public string Shape { get; set; } = "flat";

    [GlobalSetup]
    public void Setup()
    {
        var query = (Cohesive.Relations.IR.QueryDefinition)FederatedLoadRelationFixture.QueryDocument.Definition;
        var nodes = query.Body.Nodes.ToBuilder();
        var input = FederatedLoadRelationFixture.ProjectionNodeId;
        Expr predicate = Expr.Const(true);
        if (Shape == "nested")
            for (var depth = 0; depth < 12; depth++) predicate = Expr.And(predicate, Expr.Const(true));
        var count = Shape == "large" ? 512 : Shape == "collection" ? 128 : Shape == "nested" ? 1 : 0;
        for (var index = 0; index < count; index++)
        {
            QueryNodeId id = new($"filter/{index}");
            nodes.Add(new FilterQueryNode(id, input, predicate));
            input = id;
        }
        definition = query with
        {
            Body = new(nodes.ToImmutable(), query.Body.Parameters),
            Results = [new RowsQueryResultDefinition(FederatedLoadRelationFixture.RowsResultId, input)]
        };
        for (var index = 0; index < 16; index++) _ = Fingerprint();
        if (MutableTreeWithSharedMetadata() != Fingerprint())
            throw new InvalidOperationException("Legacy and immutable relation fingerprints differ.");
    }

    // Isolate tree/hash-buffer cost from the separate serializer-metadata reuse improvement.
    [Benchmark(Baseline = true)]
    public RelationQueryDefinitionFingerprint MutableTreeWithSharedMetadata()
    {
        var options = RelationQueryJsonSerializer.GetReadOnlyOptions();
        var node = JsonSerializer.SerializeToNode(definition, options)!;
        var canonical = CanonicalJsonWriter.GetCanonicalBytes(node, options, RelationCanonicalJsonArrayOrderings.Definition);
        var prefix = Encoding.UTF8.GetBytes(RelationQueryDocument.CurrentSchemaVersion + "\0");
        var bytes = new byte[prefix.Length + canonical.Length];
        prefix.CopyTo(bytes, 0);
        canonical.CopyTo(bytes, prefix.Length);
        return new(RelationQueryDefinitionFingerprinter.Algorithm, RelationQueryDefinitionFingerprinter.Canonicalization,
            Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    [Benchmark]
    public RelationQueryDefinitionFingerprint Fingerprint() => RelationQueryDefinitionFingerprinter.Compute(definition);
}
