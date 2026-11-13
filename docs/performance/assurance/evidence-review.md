# Evidence review: capture and restoration

Baseline: `c495740faf514db986bdd2d2f215963affca8ac1` (merged #521).
Comparison: the review-fix implementation with structured trace documents, a reference-evaluator-owned
weak identity registry, and dependency extraction from validated selector declarations.

The same `AssuranceEvidenceBenchmarks` source ran against separately built baseline and changed
assemblies. Setup creates and evaluates a complete invariant trace, then retains an assessment.
`Capture` measures `ClaimEvidence.FromTrace` on that already evaluated trace. `RestoreAssessment`
measures untrusted JSON restoration with actual trace re-evaluation. Setup/initial evaluation is
excluded; no external-document cache is populated. Returned evidence owns its metadata and shares
the immutable trace rather than copying its JSON. The registry uses weak keys and adds bookkeeping
for new evaluator results; it does not retain otherwise-discarded traces.

BenchmarkDotNet ShortRun, in-process emit, one warmup and three measurement iterations. These are
indicative timings with wide confidence intervals, not production latency guarantees. The stable
allocation difference is the stronger result. Restoration remains approximately the same cost and
is not claimed faster: imported data still requires validation and trace evaluation.

## Before

```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Apple M5 Max, 1 CPU, 18 logical and 18 physical cores
.NET SDK 10.0.402
  [Host] : .NET 10.0.13 (10.0.13, 10.0.1326.50504), Arm64 RyuJIT armv8.0-a

Job=ShortRun  Toolchain=InProcessEmitToolchain  IterationCount=3  
LaunchCount=1  WarmupCount=1  

```
| Method            | Count | Mean      | Error     | StdDev    | Gen0     | Gen1     | Gen2     | Allocated   |
|------------------ |------ |----------:|----------:|----------:|---------:|---------:|---------:|------------:|
| **Capture**           | **100**   |  **3.083 ms** | **17.111 ms** | **0.9379 ms** |  **78.1250** |  **15.6250** |        **-** |   **692.94 KB** |
| RestoreAssessment | 100   |  1.513 ms |  3.552 ms | 0.1947 ms |  93.7500 |  31.2500 |        - |   793.45 KB |
| **Capture**           | **2000**  | **20.651 ms** | **12.744 ms** | **0.6985 ms** | **937.5000** | **312.5000** | **125.0000** | **13020.88 KB** |
| RestoreAssessment | 2000  | 24.886 ms |  2.979 ms | 0.1633 ms | 875.0000 | 343.7500 | 125.0000 |  13572.3 KB |

## After

```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Apple M5 Max, 1 CPU, 18 logical and 18 physical cores
.NET SDK 10.0.402
  [Host] : .NET 10.0.13 (10.0.13, 10.0.1326.50504), Arm64 RyuJIT armv8.0-a

Job=ShortRun  Toolchain=InProcessEmitToolchain  IterationCount=3  
LaunchCount=1  WarmupCount=1  

```
| Method            | Count | Mean          | Error          | StdDev      | Gen0      | Gen1     | Gen2     | Allocated   |
|------------------ |------ |--------------:|---------------:|------------:|----------:|---------:|---------:|------------:|
| **Capture**           | **100**   |      **1.250 μs** |      **0.2571 μs** |   **0.0141 μs** |    **0.4978** |        **-** |        **-** |     **4.07 KB** |
| RestoreAssessment | 100   |  1,670.609 μs |  2,425.4279 μs | 132.9459 μs |   93.7500 |  23.4375 |        - |   783.15 KB |
| **Capture**           | **2000**  |      **1.302 μs** |      **0.9799 μs** |   **0.0537 μs** |    **0.4978** |        **-** |        **-** |     **4.07 KB** |
| RestoreAssessment | 2000  | 25,936.247 μs | 15,290.9268 μs | 838.1473 μs | 1250.0000 | 593.7500 | 375.0000 | 13542.82 KB |

## Interpretation and regression protection

At 2,000 observations, capture allocation falls from about 13,021 KB to 4.07 KB, and at 100 it falls
from about 693 KB to the same 4.07 KB. The original trace storage remains retained; the change avoids
creating another serialized payload and rerunning the evaluator. Reference extraction reads the
validated selector requirements rather than visiting every observation, because completed batch
validation establishes that every observation selector is declared there.

`TrustedCaptureAllocationDoesNotScaleWithObservationCount` warms both cases and compares allocation
for 1 versus 2,000 observations with identical contracts and dependency sets. Identity-reuse tests
also assert that reference-produced results are reused while public record copies are rechecked.
Wire-tampering tests cover standalone evidence and nested assessment restore. No timing threshold
is imposed on CI.

Run the checked-in fixture through the existing benchmark project:

```sh
dotnet run --project src/Cohesive.Relations.Benchmarks -c Release -- \
  --filter '*AssuranceEvidenceBenchmarks*' --job short --warmupCount 1 --iterationCount 3 --inProcess
```

The comparison here used a minimal temporary runner with the same existing BenchmarkDotNet 0.15.8
dependency and explicit references to the selected release binaries, to avoid unrelated benchmark
project dependencies. No production dependency was added.
