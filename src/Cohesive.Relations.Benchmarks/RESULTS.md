# Cohesive.Relations Benchmark Results

## Conclusions

- At 1,024 rows, the generated kernel is approximately 2.08× the handwritten simple mapper and
  2.24× the handwritten joined mapper while allocating exactly the same number of bytes.
- The full canonical mapping path is approximately 2.11× handwritten for simple mapping and 2.47×
  for joined mapping. The canonical envelope adds about 1.7% over the simple kernel and 10.4% over
  the joined kernel, so diagnostics and provenance bookkeeping are not the primary source of the
  remaining difference from handwritten code.
- The full compiled mapper is approximately 7.1–7.2× faster than the existing observation mapper at
  scale and allocates approximately 94.9–97.4% less memory.
- Further optimization should focus first on generated field reads and checked scalar conversions,
  and only if end-to-end profiling identifies DTO materialization as a meaningful bottleneck.

## History

### 2026-07-15 (ARI-129)

- Base commit: `fd9b38615ad69980990601c67b9de6459cbf3b87`
- Branch: `eulerfx/ari-129-implement-runtime-compiled-dto-mapping-kernels-and`
- Worktree: dirty; includes the uncommitted ARI-129 implementation and benchmark baseline

```bash
dotnet run \
  --project src/Cohesive.Relations.Benchmarks/Cohesive.Relations.Benchmarks.csproj \
  -c Release --no-build -- \
  --job Short --filter "*RelationDtoWarmBenchmarks*"
```

```text
BenchmarkDotNet v0.15.8, macOS Tahoe 26.5.2 (25F84) [Darwin 25.5.0]
Apple M5 Max, 1 CPU, 18 logical and 18 physical cores
.NET SDK 10.0.201
[Host]   : .NET 10.0.5 (10.0.5, 10.0.526.15411), Arm64 RyuJIT armv8.0-a
ShortRun : .NET 10.0.5 (10.0.5, 10.0.526.15411), Arm64 RyuJIT armv8.0-a

Job=ShortRun  IterationCount=3  LaunchCount=1
WarmupCount=3
```

| Method                          | Categories  | RowCount | Mean          | Error         | StdDev       | Op/s         | Ratio | RatioSD | Gen0     | Gen1     | Allocated | Alloc Ratio |
|-------------------------------- |------------ |--------- |--------------:|--------------:|-------------:|-------------:|------:|--------:|---------:|---------:|----------:|------------:|
| HandwrittenJoined               | Warm,Joined | 1        |      48.68 ns |      2.735 ns |     0.150 ns | 20,541,895.2 |  1.00 |    0.00 |   0.0153 |        - |     128 B |        1.00 |
| CompiledKernelOnlyJoined        | Warm,Joined | 1        |      96.74 ns |      5.862 ns |     0.321 ns | 10,336,674.2 |  1.99 |    0.01 |   0.0153 |        - |     128 B |        1.00 |
| CompiledCanonicalJoined         | Warm,Joined | 1        |     176.47 ns |      4.877 ns |     0.267 ns |  5,666,615.2 |  3.63 |    0.01 |   0.0563 |        - |     472 B |        3.69 |
| ExistingObservationMapperJoined | Warm,Joined | 1        |     761.99 ns |    106.371 ns |     5.831 ns |  1,312,351.1 | 15.65 |    0.11 |   0.5913 |   0.0010 |    4952 B |       38.69 |
|                                 |             |          |               |               |              |              |       |         |          |          |           |             |
| HandwrittenJoined               | Warm,Joined | 32       |   1,657.85 ns |    493.945 ns |    27.075 ns |    603,190.6 |  1.00 |    0.02 |   0.4692 |   0.0057 |    3928 B |        1.00 |
| CompiledKernelOnlyJoined        | Warm,Joined | 32       |   3,479.12 ns |    900.781 ns |    49.375 ns |    287,428.7 |  2.10 |    0.04 |   0.4692 |   0.0038 |    3928 B |        1.00 |
| CompiledCanonicalJoined         | Warm,Joined | 32       |   3,559.04 ns |    201.383 ns |    11.038 ns |    280,974.4 |  2.15 |    0.03 |   0.5379 |   0.0076 |    4520 B |        1.15 |
| ExistingObservationMapperJoined | Warm,Joined | 32       |  25,797.54 ns |  1,082.612 ns |    59.342 ns |     38,763.4 | 15.56 |    0.22 |  18.8599 |   0.9766 |  157912 B |       40.20 |
|                                 |             |          |               |               |              |              |       |         |          |          |           |             |
| HandwrittenJoined               | Warm,Joined | 1024     |  56,486.62 ns |  5,615.750 ns |   307.818 ns |     17,703.3 |  1.00 |    0.01 |  14.8926 |   2.9907 |  124952 B |        1.00 |
| CompiledKernelOnlyJoined        | Warm,Joined | 1024     | 126,486.07 ns | 21,737.621 ns | 1,191.512 ns |      7,906.0 |  2.24 |    0.02 |  14.8926 |   2.9297 |  124952 B |        1.00 |
| CompiledCanonicalJoined         | Warm,Joined | 1024     | 139,609.47 ns |  7,281.489 ns |   399.123 ns |      7,162.8 |  2.47 |    0.01 |  15.8691 |   3.6621 |  133484 B |        1.07 |
| ExistingObservationMapperJoined | Warm,Joined | 1024     | 987,449.49 ns | 76,594.912 ns | 4,198.426 ns |      1,012.7 | 17.48 |    0.10 | 603.5156 | 208.9844 | 5052440 B |       40.44 |
|                                 |             |          |               |               |              |              |       |         |          |          |           |             |
| HandwrittenSimple               | Warm,Simple | 1        |      19.05 ns |      0.605 ns |     0.033 ns | 52,487,643.6 |  1.00 |    0.00 |   0.0095 |        - |      80 B |        1.00 |
| CompiledKernelOnlySimple        | Warm,Simple | 1        |      33.71 ns |      0.980 ns |     0.054 ns | 29,669,038.6 |  1.77 |    0.00 |   0.0095 |        - |      80 B |        1.00 |
| CompiledCanonicalSimple         | Warm,Simple | 1        |      69.98 ns |      3.421 ns |     0.188 ns | 14,290,204.6 |  3.67 |    0.01 |   0.0507 |        - |     424 B |        5.30 |
| ExistingObservationMapperSimple | Warm,Simple | 1        |     264.42 ns |     10.509 ns |     0.576 ns |  3,781,817.5 | 13.88 |    0.03 |   0.1960 |        - |    1640 B |       20.50 |
|                                 |             |          |               |               |              |              |       |         |          |          |           |             |
| HandwrittenSimple               | Warm,Simple | 32       |     617.29 ns |     20.947 ns |     1.148 ns |  1,619,971.9 |  1.00 |    0.00 |   0.2851 |   0.0010 |    2392 B |        1.00 |
| CompiledKernelOnlySimple        | Warm,Simple | 32       |   1,134.84 ns |    265.704 ns |    14.564 ns |    881,179.5 |  1.84 |    0.02 |   0.2842 |        - |    2392 B |        1.00 |
| CompiledCanonicalSimple         | Warm,Simple | 32       |   1,490.13 ns |     19.472 ns |     1.067 ns |    671,083.9 |  2.41 |    0.00 |   0.3567 |   0.0019 |    2984 B |        1.25 |
| ExistingObservationMapperSimple | Warm,Simple | 32       |   9,432.85 ns |    196.172 ns |    10.753 ns |    106,012.5 | 15.28 |    0.03 |   6.1951 |   0.1068 |   51928 B |       21.71 |
|                                 |             |          |               |               |              |              |       |         |          |          |           |             |
| HandwrittenSimple               | Warm,Simple | 1024     |  20,878.68 ns |  1,959.623 ns |   107.414 ns |     47,895.7 |  1.00 |    0.01 |   9.0332 |   1.4343 |   75800 B |        1.00 |
| CompiledKernelOnlySimple        | Warm,Simple | 1024     |  43,371.58 ns |    810.611 ns |    44.432 ns |     23,056.6 |  2.08 |    0.01 |   9.0332 |   1.4648 |   75800 B |        1.00 |
| CompiledCanonicalSimple         | Warm,Simple | 1024     |  44,105.42 ns |  1,801.491 ns |    98.746 ns |     22,673.0 |  2.11 |    0.01 |  10.0708 |   1.6479 |   84331 B |        1.11 |
| ExistingObservationMapperSimple | Warm,Simple | 1024     | 319,219.85 ns |  2,958.948 ns |   162.190 ns |      3,132.6 | 15.29 |    0.07 | 198.2422 |  52.7344 | 1660952 B |       21.91 |

#### Interpretation

At 1,024 rows, the simple generated kernel takes 43.37 μs compared with 20.88 μs for handwritten
mapping, while the full canonical mapper takes 44.11 μs. The canonical envelope therefore adds only
about 0.73 μs per batch. The joined kernel takes 126.49 μs compared with 56.49 μs handwritten, and
the full canonical mapper takes 139.61 μs.

The kernel-only path allocates exactly the same number of bytes as handwritten mapping for both
scenarios at this scale. The full canonical path adds the typed provenance rows, immutable result
collections, and mapping result: approximately 8.3 KB per 1,024-row batch.

These measurements show that most of the remaining difference from handwritten code is inside the
generated field-read, validation, conversion, and construction kernel rather than the canonical
diagnostic and provenance envelope. Kernel-only does not disable field-presence, nullability, or type
conversion checks. The full path additionally performs plan, terminal, and row-shape validation;
cancellation checks; exception-to-diagnostic translation; provenance capture; completeness scanning;
and result construction.

This ShortRun establishes a descriptive baseline, not a regression threshold. Optimization should
remain deferred unless representative end-to-end profiles show that DTO materialization is a material
part of total query execution time.
