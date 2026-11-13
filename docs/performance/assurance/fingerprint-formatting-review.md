```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Apple M5 Max, 1 CPU, 18 logical and 18 physical cores
.NET SDK 10.0.402
  [Host] : .NET 10.0.13 (10.0.13, 10.0.1326.50504), Arm64 RyuJIT armv8.0-a

Job=ShortRun  Toolchain=InProcessEmitToolchain  IterationCount=3
LaunchCount=1  WarmupCount=1

```
| Method             | DigestLength | ContainsDelimiters | Mean      | Error      | StdDev   | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------------- |------------- |------------------- |----------:|-----------:|---------:|------:|--------:|-------:|----------:|------------:|
| **PriorInterpolation** | **64**           | **False**              |  **22.20 ns** |   **8.809 ns** | **0.483 ns** |  **1.00** |    **0.03** | **0.0229** |     **192 B** |        **1.00** |
| SharedFormatter    | 64           | False              |  32.76 ns |  10.879 ns | 0.596 ns |  1.48 |    0.04 | 0.0229 |     192 B |        1.00 |
|                    |              |                    |           |            |          |       |         |        |           |             |
| **PriorInterpolation** | **64**           | **True**               |  **22.73 ns** |  **10.188 ns** | **0.558 ns** |  **1.00** |    **0.03** | **0.0249** |     **208 B** |        **1.00** |
| SharedFormatter    | 64           | True               |  64.70 ns |  15.644 ns | 0.857 ns |  2.85 |    0.07 | 0.0421 |     352 B |        1.69 |
|                    |              |                    |           |            |          |       |         |        |           |             |
| **PriorInterpolation** | **4096**         | **False**              | **479.73 ns** |  **96.065 ns** | **5.266 ns** |  **1.00** |    **0.01** | **0.9861** |    **8256 B** |        **1.00** |
| SharedFormatter    | 4096         | False              | 635.31 ns |  52.490 ns | 2.877 ns |  1.32 |    0.01 | 0.9861 |    8256 B |        1.00 |
|                    |              |                    |           |            |          |       |         |        |           |             |
| **PriorInterpolation** | **4096**         | **True**               | **446.86 ns** | **126.153 ns** | **6.915 ns** |  **1.00** |    **0.02** | **0.9880** |    **8272 B** |        **1.00** |
| SharedFormatter    | 4096         | True               | 627.68 ns |  91.366 ns | 5.008 ns |  1.40 |    0.02 | 1.0052 |    8416 B |        1.02 |

## Scope and reproduction

PR #512 source-identity/formatter review. Run:

```sh
dotnet run --project src/Cohesive.Relations.Benchmarks -c Release -- --filter '*FingerprintTextBenchmarks*' --job short --inProcess --warmupCount 1 --iterationCount 3
```

[Workload source](../../../src/Cohesive.Relations.Benchmarks/FingerprintTextBenchmarks.cs)
compares prior handwritten interpolation with the shared fingerprint projection. Inputs are
constructed before measurement. Ordinary components produce identical text and allocate only
the result string; a deterministic core regression also checks allocation equality. A 4096
character digest is a bounded-large stress input, not a normal SHA-256 digest.

The formatter scans for delimiters, adding CPU work (about 11 ns for the 64-character ordinary
case in this short run). Escaped inputs intentionally have different semantics: the old text
was ambiguous; the shared formatter escapes percent and colon. Replacement strings cost extra
allocations on that path. Domain constructors remain responsible for semantic validation;
formatting does not repeat it. Returned strings are caller-owned and are not cached.

Composite references that embed this text can allocate an additional intermediate string;
this benchmark isolates the triple formatter, not whole infrastructure compilation. No general
throughput improvement is claimed. The priority is a single unambiguous encoding with
unchanged ordinary text and unchanged domain fingerprint JSON/hash authority. Short in-process
measurements are diagnostic evidence, not timing thresholds.
