using BenchmarkDotNet.Attributes;
using Cohesive.Execution;

namespace Cohesive.Relations.Benchmarks;

[MemoryDiagnoser]
public class FingerprintTextBenchmarks
{
    [Params(64, 4096)] public int DigestLength { get; set; }
    [Params(false, true)] public bool ContainsDelimiters { get; set; }
    ExecutionDefinitionFingerprint fingerprint = null!;

    [GlobalSetup]
    public void Setup() => fingerprint = new("sha256",
        ContainsDelimiters ? "profile:version%/v1" : "profile/v1", new string('a', DigestLength));

    [Benchmark(Baseline = true)]
    public string PriorInterpolation() => $"{fingerprint.Algorithm}:{fingerprint.Canonicalization}:{fingerprint.Value}";

    [Benchmark]
    public string SharedFormatter() => fingerprint.ToCanonicalString();
}
