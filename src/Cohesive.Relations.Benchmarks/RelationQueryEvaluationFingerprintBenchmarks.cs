using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using BenchmarkDotNet.Attributes;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.Serialization;
using Cohesive.Relations.TestFixtures;

namespace Cohesive.Relations.Benchmarks;

[MemoryDiagnoser]
public class RelationQueryEvaluationFingerprintBenchmarks
{
    RelationQueryEvaluation evaluation = null!;

    [GlobalSetup]
    public void Setup()
    {
        evaluation = FederatedLoadRelationFixture.QueryDocument
            .Evaluate(
                new("benchmark/fingerprint"),
                FederatedLoadRelationFixture.ShapeGraphDocuments,
                FederatedLoadRelationFixture.RelationshipCatalogDocument)
            .Select(FederatedLoadRelationFixture.RowsResultId)
            .Build();
    }

    [Benchmark(Baseline = true)]
    public RelationQueryEvaluationFingerprint MonolithicReference()
    {
        var options = RelationQueryEvaluationJsonSerializer.CreateOptions();
        var node = JsonSerializer.SerializeToNode(evaluation, options) as JsonObject
            ?? throw new InvalidOperationException("Failed to materialize the benchmark evaluation.");
        node.Remove("fingerprint");
        var canonical = CanonicalJsonWriter.GetCanonicalBytes(
            node,
            options,
            RelationCanonicalJsonArrayOrderings.Evaluation);
        return new(
            RelationQueryEvaluationFingerprinter.Algorithm,
            RelationQueryEvaluationFingerprinter.Canonicalization,
            Convert.ToHexStringLower(SHA256.HashData(canonical)));
    }

    [Benchmark]
    public RelationQueryEvaluationFingerprint SegmentedWithWarmCompilation() =>
        RelationQueryEvaluationFingerprinter.Compute(evaluation);
}
