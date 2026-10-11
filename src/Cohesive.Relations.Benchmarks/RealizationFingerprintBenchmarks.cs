using System.Collections.Immutable;
using BenchmarkDotNet.Attributes;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Realization;

namespace Cohesive.Relations.Benchmarks;

[MemoryDiagnoser]
[ShortRunJob]
public class RealizationFingerprintBenchmarks
{
    RelationQueryRealizationReport report = null!;

    [Params(1, 128, 1024)]
    public int Requirements { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var plan = new RelationQueryCompiledPlanReference("compiler/v1", "schema/v1",
            new("sha256", "definition/v1", "definition"), new("sha256", "shapes/v1", "shapes"), null,
            new("sha256", "demand/v1", "demand"), [new("input/test")]);
        var requirements = Enumerable.Range(0, Requirements).Select(i => new RelationQueryRealizationRequirement(
            new($"requirement/filter/{i:D4}/é🙂"),
            new LogicalRelationQueryCapability(RelationQueryLogicalCapabilityKind.Filter))).ToImmutableArray();
        var profile = new RelationQueryTargetCapabilityProfile(new("target/test"), new("target/test/v1"),
            [plan.DefinitionSchemaVersion], [plan.CompilerProfile]);
        report = RelationQueryRealizationCompiler.Match(plan, requirements, profile,
            new RelationQueryRealizationPolicy(new("policy/test"), "conventions/v1"));
        if (Compute() != report.Fingerprint) throw new InvalidOperationException("Fingerprint differs.");
    }

    [Benchmark]
    public RelationQueryRealizationFingerprint Compute() => RelationQueryRealizationFingerprinter.Compute(report);
}

[MemoryDiagnoser]
[ShortRunJob]
public class RealizationSequenceKeyBenchmarks
{
    string?[] values = null!;

    [Params(4, 128, 4096)]
    public int Characters { get; set; }

    [GlobalSetup]
    public void Setup() => values = [null, "", "é🙂", new('x', Characters), "separator/\u001e\u001f"];

    [Benchmark]
    public string Compute() => RelationQueryRealizationOrdering.SequenceKey(values);
}
