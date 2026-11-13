```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Apple M5 Max, 1 CPU, 18 logical and 18 physical cores
.NET SDK 10.0.402
  [Host] : .NET 10.0.13 (10.0.13, 10.0.1326.50504), Arm64 RyuJIT armv8.0-a

Job=ShortRun  Toolchain=InProcessEmitToolchain  IterationCount=3
LaunchCount=1  WarmupCount=1

```
| Method   | Count | Mean        | Error      | StdDev    | Ratio | RatioSD | Gen0     | Gen1     | Gen2    | Allocated  | Alloc Ratio |
|--------- |------ |------------:|-----------:|----------:|------:|--------:|---------:|---------:|--------:|-----------:|------------:|
| **Baseline** | **100**   |    **33.73 μs** |   **0.559 μs** |  **0.031 μs** |  **1.00** |    **0.00** |  **15.5640** |   **1.0986** |       **-** |  **127.37 KB** |        **1.00** |
| Indexed  | 100   |    35.84 μs |  13.604 μs |  0.746 μs |  1.06 |    0.02 |  20.9351 |   3.4790 |       - |  171.41 KB |        1.35 |
|          |       |             |            |           |       |         |          |          |         |            |             |
| **Baseline** | **2000**  | **2,882.99 μs** | **273.612 μs** | **14.998 μs** |  **1.00** |    **0.01** | **277.3438** |  **85.9375** | **39.0625** | **2182.63 KB** |        **1.00** |
| Indexed  | 2000  | 1,119.10 μs | 125.461 μs |  6.877 μs |  0.39 |    0.00 | 357.4219 | 140.6250 | 70.3125 | 2831.38 KB |        1.30 |

## Interpretation and reproduction

Review of PR #512, baseline `2d7e5e26`. Both methods evaluated the same prebuilt claim
and batch to `Satisfied`, with a 10M comparison budget so the old scan could complete.
Each trigger has one matching outcome one tick later; triggers are spaced 20 ticks apart.
Input contracts are primitive Int64 values; input preparation is not timed. The original
evaluator was copied into an isolated BenchmarkDotNet harness, adapting only event payload
access to the new role-neutral field and the internal diagnostic-code accessor. No other
baseline matching or binding logic changed. Warm execution includes validation, matching,
input resolution and result allocation; it excludes source lowering and serialization.

Current workload source: [AssuranceTraceBenchmarks](../../../src/Cohesive.Relations.Benchmarks/AssuranceTraceBenchmarks.cs).
Run `dotnet run -c Release --project src/Cohesive.Relations.Benchmarks -- --filter '*AssuranceTraceBenchmarks*' --job short --inProcess --warmupCount 1 --iterationCount 3`.
The existing benchmark project/dependency is reused; production dependencies are unchanged.

This short in-process run is diagnostic evidence, not a CI timing threshold or a general
throughput promise. At 2,000 windows, indexed evaluation is about 2.6× faster; at 100,
cache overhead offsets the saved scanning. Cache arrays and indexing increase allocated
bytes about 30–35% here. The priority is skipping provably ineligible work and validating
reused inputs once. Large overlapping windows may still have quadratic eligible matching.

The deterministic regression in `TraceClaimEvaluatorTests` evaluates 2,000 trigger/witness
pairs with only 2,000 allowed visits. Separate tests retain budget exhaustion for eligible
nonmatches, lazy validation, and one contract diagnostic per reused malformed input.
