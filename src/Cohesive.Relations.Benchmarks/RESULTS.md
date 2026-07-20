# Cohesive.Relations Benchmark Results

## Conclusions

- In the ARI-145 1,024-row ShortRun, the generated kernel is approximately 1.60× handwritten for the
  simple DTO and 1.63× for the joined DTO. It allocates exactly the same bytes as handwritten mapping.
- AutoMapper's compiled constructor-member plan is approximately 1.33× handwritten for the simple DTO
  and 1.27× for the joined DTO over the same canonical rows. The Cohesive kernel is therefore about
  1.21× and 1.29× AutoMapper respectively. AutoMapper does not provide Cohesive's requirement-gap,
  completeness, diagnostic, or provenance semantics.
- The full canonical mapper is approximately 1.78× handwritten for simple mapping and 1.54× for joined
  mapping. It adds about 8.5 KB per 1,024-row batch for typed provenance rows and result bookkeeping.
  Kernel-only and full-canonical timings use different loop/delegate orchestration, so their means
  should not be interpreted as a strictly additive envelope cost.
- Fresh Cohesive kernel compilation is about 5.2–5.5× faster than fresh AutoMapper configuration,
  validation, and eager compilation in this ShortRun, while a cached Cohesive lookup is about 63–66 ns.
- Physical planning, federated physical execution, and diagnostic-heavy mapping are descriptive
  allocation/performance baselines. They are not CI thresholds; optimize them only from representative
  end-to-end profiles.

## History

### 2026-07-19 (ARI-145)

- Base commit: `2214f5c7172a3c75ed169b6144296f4ac1793501`
- Branch: `eulerfx/ari-145-establish-canonical-relation-query-conformance-and`
- Worktree: dirty; includes the uncommitted ARI-145 conformance and benchmark implementation
- BenchmarkDotNet: 0.15.8
- OS: macOS Tahoe 26.5.2 (25F84), Darwin 25.5.0
- Hardware: Apple M5 Max, Arm64, 18 physical/logical cores
- SDK/runtime: .NET SDK 10.0.201; .NET 10.0.5 Arm64 RyuJIT
- Environment overrides: none; ShortRun defaults were used

Warm materialization command:

```bash
dotnet run \
  --project src/Cohesive.Relations.Benchmarks/Cohesive.Relations.Benchmarks.csproj \
  -c Release --no-build -- \
  --job Short --filter "*RelationDtoWarmBenchmarks*"
```

Representative 1,024-row results:

| Scenario | Mapper | Mean | Op/s | vs handwritten | vs AutoMapper | Allocated |
|---|---|---:|---:|---:|---:|---:|
| Simple | Handwritten | 23.11 μs | 43,271 | 1.00× | 0.75× | 57,368 B |
| Simple | AutoMapper member plan | 30.69 μs | 32,581 | 1.33× | 1.00× | 57,368 B |
| Simple | Cohesive compiled kernel | 36.99 μs | 27,031 | 1.60× | 1.21× | 57,368 B |
| Simple | Cohesive full canonical | 41.06 μs | 24,353 | 1.78× | 1.34× | 65,898 B |
| Simple | Existing observation mapper | 300.59 μs | 3,327 | 13.01× | 9.79× | 1,660,952 B |
| Joined | Handwritten | 79.57 μs | 12,568 | 1.00× | 0.79× | 106,520 B |
| Joined | AutoMapper member plan | 100.67 μs | 9,934 | 1.27× | 1.00× | 106,520 B |
| Joined | Cohesive compiled kernel | 129.73 μs | 7,709 | 1.63× | 1.29× | 106,520 B |
| Joined | Cohesive full canonical | 122.17 μs | 8,185 | 1.54× | 1.21× | 115,051 B |
| Joined | Existing observation mapper | 955.80 μs | 1,046 | 12.01× | 9.49× | 5,052,440 B |

AutoMapper uses explicit constructor-member mappings over the same prebuilt canonical
`RelationQueryOutputRow` arrays as the Cohesive kernel comparison. Configuration validation,
`CompileMappings()`, mapper creation, input-array construction, and output correctness checks run in
`GlobalSetup`. A custom whole-object converter is not used.

Cold compilation command:

```bash
dotnet run \
  --project src/Cohesive.Relations.Benchmarks/Cohesive.Relations.Benchmarks.csproj \
  -c Release --no-build -- \
  --job Short --filter "*RelationDtoCompilationBenchmarks*"
```

| Scenario | Operation | Mean | Relative to fresh Cohesive | Allocated |
|---|---|---:|---:|---:|
| Simple | Fresh Cohesive compile | 75.70 μs | 1.00× | 30,073 B |
| Simple | Cached Cohesive compile | 65.75 ns | 0.001× | 48 B |
| Simple | Fresh AutoMapper configure/validate/compile | 389.94 μs | 5.15× | 102,585 B |
| Joined | Fresh Cohesive compile | 165.90 μs | 1.00× | 59,130 B |
| Joined | Cached Cohesive compile | 62.57 ns | &lt;0.001× | 48 B |
| Joined | Fresh AutoMapper configure/validate/compile | 907.27 μs | 5.47× | 154,607 B |

The three-sample AutoMapper cold measurements have wide confidence intervals and should be treated
as order-of-magnitude setup observations, not precise ratios.

Physical planning/execution command:

```bash
dotnet run \
  --project src/Cohesive.Relations.Benchmarks/Cohesive.Relations.Benchmarks.csproj \
  -c Release --no-build -- \
  --job Short --filter "*RelationQueryPhysical*"
```

| Operation | Scale | Mean | Allocated |
|---|---:|---:|---:|
| Physical planning | 32-row bound | 383.0 μs | 1.25 MB |
| Physical planning | 1,024-row bound | 498.7 μs | 1.25 MB |
| Batched acquisition/deduplication/correlation | 32 roots | 1.534 ms | 3.55 MB |
| Batched acquisition/deduplication/correlation | 1,024 roots | 36.569 ms | 46.13 MB |

The execution setup uses 50% distinct Customer keys and 25% distinct Equipment keys. At 1,024 roots,
the validated 32-key limit produces exactly 16 Customer batches and 8 Equipment batches with no N+1
reads. Deterministic in-memory readers use a prebuilt ordinal identity index and exclude backend and network latency.

Diagnostic-scale command:

```bash
dotnet run \
  --project src/Cohesive.Relations.Benchmarks/Cohesive.Relations.Benchmarks.csproj \
  -c Release --no-build -- \
  --job Short --filter "*RelationDtoDiagnosticScaleBenchmarks*"
```

| Missing joined inputs | Mean | Allocated |
|---:|---:|---:|
| 32 rows | 60.33 μs | 32.24 KB |
| 1,024 rows | 1.929 ms | 1,024.51 KB |

An initial ARI-145 run exposed an accidental allocation and lookup-complexity regression in
`ObservationValue.TryGetProperty`: the method enumerated the object dictionary for every field read
even though construction already normalizes object storage to ordinal lookup semantics. Restoring direct
`TryGetValue` and adding a zero-allocation lookup regression test reduced the generated-kernel gap from
the interim 5–7× range to the 1.60–1.63× baseline above. This is a local lookup correction, not mapper
fusion or a weakening of canonical validation.

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
