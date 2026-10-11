using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Cohesive.Execution;
using Cohesive.Model.Serialization;

namespace Cohesive.Relations.Benchmarks;

/// <summary>Compares document-owned digest reuse with the previous normalized computation boundary.</summary>
[MemoryDiagnoser, CategoriesColumn, GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
public class ExecutionDefinitionFingerprintReuseBenchmarks
{
    ExecutionDefinitionDocument document = null!;
    JsonElement payload;
    Func<ExecutionIrSchemaVersion, ExecutionDefinitionKind, JsonElement,
        ImmutableArray<ExecutionDefinitionExtension>, ExecutionDefinitionFingerprint> recompute = null!;
    static readonly ExecutionProvenance Provenance = new(new("benchmark", "1"), new("benchmark/fingerprint-reuse"), DocumentOrigin.Generated);

    [Params("flat", "nested", "collection", "large")]
    public string Shape { get; set; } = "flat";

    [GlobalSetup]
    public void Setup()
    {
        // Resolve the previous computation boundary once; benchmark calls perform no reflection.
        recompute = typeof(ExecutionDefinitionFingerprinter)
            .GetMethod("ComputeNormalized", BindingFlags.NonPublic | BindingFlags.Static)!
            .CreateDelegate<Func<ExecutionIrSchemaVersion, ExecutionDefinitionKind, JsonElement,
                ImmutableArray<ExecutionDefinitionExtension>, ExecutionDefinitionFingerprint>>();
        const string Row = "{\"name\":\"example\",\"sequence\":123,\"enabled\":true,\"values\":[1,2,null]}";
        var json = Row;
        if (Shape == "nested")
            for (var depth = 0; depth < 24; depth++) json = "{\"child\":" + json + "}";
        if (Shape is "collection" or "large")
            json = "{\"rows\":[" + string.Join(",", Enumerable.Repeat(Row, Shape == "large" ? 4096 : 128)) + "]}";
        using var parsed = JsonDocument.Parse(json);
        payload = parsed.RootElement.Clone();
        document = Create();
        if (Recompute(document) != ExecutionDefinitionFingerprinter.Compute(document))
            throw new InvalidOperationException("Cached fingerprint differs from independent normalized computation.");
    }

    [Benchmark(Baseline = true), BenchmarkCategory("Warm")]
    public ExecutionDefinitionFingerprint RecomputeWarmDocument() => Recompute(document);

    [Benchmark, BenchmarkCategory("Warm")]
    public ExecutionDefinitionFingerprint ReuseWarmDocument() => ExecutionDefinitionFingerprinter.Compute(document);

    [Benchmark(Baseline = true), BenchmarkCategory("ImportedFirstUse")]
    public ExecutionDefinitionFingerprint ImportAndRecompute() => Recompute(Import());

    [Benchmark, BenchmarkCategory("ImportedFirstUse")]
    public ExecutionDefinitionFingerprint ImportAndCompute() => ExecutionDefinitionFingerprinter.Compute(Import());

    [Benchmark(Baseline = true), BenchmarkCategory("AuthoredFirstUse")]
    public ExecutionDefinitionFingerprint CreateAndRecompute() => Recompute(Create());

    [Benchmark, BenchmarkCategory("AuthoredFirstUse")]
    public ExecutionDefinitionFingerprint CreateAndReuse() => ExecutionDefinitionFingerprinter.Compute(Create());

    ExecutionDefinitionDocument Create() => ExecutionDefinitionDocument.Create(new("benchmark"), new("fingerprint"), new("1"), new Definition(payload), Provenance);
    public sealed record Definition(JsonElement Content);

    ExecutionDefinitionDocument Import() => new(document.Kind, document.Metadata, document.Definition, document.Extensions);
    ExecutionDefinitionFingerprint Recompute(ExecutionDefinitionDocument value) => recompute(
        value.Metadata.SchemaVersion, value.Kind, value.Definition, value.Extensions);
}
