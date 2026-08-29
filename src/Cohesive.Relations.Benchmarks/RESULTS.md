# Cohesive Benchmark Results

## Conclusions

- The allocation-light observation validator reduces successful validation from 6.27–20.23 KB to 0 B and from
  1.85–2.78 μs to 262–282 ns across the flat, nested-object, and array-heavy DefaultJob scenarios. Creating an
  `Observation` from an already-owned immutable value allocates only the retained 112 B object.
- Direct canonical conversion reduces warm representative-state materialization from 1.017 μs and 4.22 KB to
  157.9 ns and 152 B through a compiled plan. That is approximately 6.44× faster, eliminates 4,168 B per operation,
  and matches the handwritten destination-object allocation floor. The default cache adds approximately 29 ns and
  no steady-state allocation.
- Prebinding a compiled materializer to a shared immutable top-level observation layout reduced indexed CLR
  materialization by 7.0–14.2% for the representative mixed seven-field state and by 10.8–17.4% for sixteen flat
  scalar fields across repeated ShortRuns. Both ordinal paths remain at the destination-only allocation floor. Raw
  ordinal field access is about 2.3–3.2× faster than semantic or indexed name lookup across the measured 4-, 16-,
  and 64-field cases.
- Direct sixteen-field ordinal validation takes 211.1 ns and 0 B versus 726.3 ns and 3,968 B after dictionary
  projection. Complete immutable indexed hydration takes 289.8 ns and 1,424 B. Direct canonical JSON from the
  indexed buffer takes 360.5 ns and 0 B, 45.0% less time than the equivalent 655.0 ns dictionary-backed write.
- Streaming JSON-to-value hydration of sixteen flat scalars takes 916.5 ns and 2,496 B, 17.4% less time and 360 B
  less allocation than the former `JsonDocument` path. Shape-bound JSON-to-indexed hydration takes 897.0 ns and
  1,424 B, versus 1.813 μs and 4,448 B through `JsonDocument`, semantic observation construction, and ordinal
  projection: 50.5% less time and 68.0% less allocation.
- Canonical observation serialization now takes 658.3 ns and 344 B for returned UTF-8 or 688.2 ns and 632 B for a
  returned string. Reusable caller-owned output takes 643.2 ns with 0 B of steady-state allocation. Compared with the
  previous 0.86 μs implementation, returned UTF-8 is 1.31× faster and eliminates 6,456 B; returned strings are 1.25×
  faster and eliminate 6,768 B. SHA-256 fingerprinting takes 3.406 μs while streaming bounded JSON chunks and
  retaining a fixed 360 B of managed allocation without materializing the JSON payload.
- In the ARI-145 1,024-row DefaultJob verification run, the generated kernel is approximately 1.74×
  handwritten for the simple DTO and 1.54× for the joined DTO. It allocates exactly the same bytes as
  handwritten mapping.
- AutoMapper's compiled constructor-member plan is approximately 1.32× handwritten for the simple DTO
  and 1.25× for the joined DTO over the same canonical rows. Its mean is 24.0% lower than the Cohesive
  simple kernel and 18.7% lower than the joined kernel at this scale; the difference is therefore
  scenario- and scale-dependent rather than a uniform 5%. AutoMapper does not provide Cohesive's
  requirement-gap, completeness, diagnostic, or provenance semantics.
- The full canonical mapper is approximately 1.98× handwritten for simple mapping and 1.43× for joined
  mapping. It adds about 8.5 KB per 1,024-row batch for typed provenance rows and result bookkeeping.
  Kernel-only and full-canonical timings use different loop/delegate orchestration, so their means
  should not be interpreted as a strictly additive envelope cost.
- Fresh Cohesive kernel compilation is about 5.2–5.5× faster than fresh AutoMapper configuration,
  validation, and eager compilation in this ShortRun, while a cached Cohesive lookup is about 63–66 ns.
- Physical planning, federated physical execution, and diagnostic-heavy mapping are descriptive
  allocation/performance baselines. They are not CI thresholds; optimize them only from representative
  end-to-end profiles.

## History

### 2026-08-29 (streaming and shape-bound observation JSON hydration)

- Base commit: `4d36894bb9d01e9cbdf51d9792880f679c480f5e`
- Branch: `codex/observation-ordinal-access`
- Worktree: dirty; includes the streaming reader, direct indexed hydration, tests, and benchmark coverage
- BenchmarkDotNet: 0.15.8
- OS: macOS Tahoe 26.5.2 (25F84), Darwin 25.5.0
- Hardware: Apple M5 Max, Arm64, 18 physical/logical cores
- SDK/runtime: .NET SDK 10.0.201; .NET 10.0.5 Arm64 RyuJIT

```bash
dotnet run \
  --project src/Cohesive.Relations.Benchmarks/Cohesive.Relations.Benchmarks.csproj \
  -c Release --no-build --no-restore -- \
  --job Short \
  --filter "*ObservationJsonHydrationBenchmarks*"
```

Sixteen flat `Int64` fields parsed from a prebuilt UTF-8 JSON payload:

| Operation | Mean | Allocated | Relative time | Relative allocation |
|---|---:|---:|---:|---:|
| `JsonDocument` to untyped `ObservationValue` | 1.110 μs | 2,856 B | 1.00× | 1.00× |
| Streaming reader to untyped `ObservationValue` | 916.5 ns | 2,496 B | 0.83× | 0.87× |
| `JsonDocument` → semantic observation → indexed occurrence | 1.813 μs | 4,448 B | 1.00× | 1.00× |
| Shape-bound streaming reader → indexed occurrence | 897.0 ns | 1,424 B | 0.49× | 0.32× |

The general converter no longer constructs a transient JSON DOM. Its reader pre-counts object properties and array
items using a copied `Utf8JsonReader`, then transfers ownership of exact-capacity storage into the immutable value.
That second token scan is cheaper than immutable-builder growth and copying for this payload while reducing retained
allocation.

The shape-bound path hashes unescaped root property-name UTF-8 against the shared layout, restores typed primitives,
fills the retained value and presence arrays directly, and delegates semantic acceptance to the existing ordinal
validator. It therefore includes parse, schema restoration, validation, and immutable occurrence construction while
allocating exactly the same 1,424 B as construction from already-populated sixteen-field ordinal buffers. The former
end-to-end path additionally paid for the JSON DOM, root dictionary-backed value, semantic observation, and ordinal
projection. Immutable layout membership and field-definition validity are established once by the layout's private
construction boundary rather than re-walked for every parsed occurrence.

These are ShortRun measurements with three measurement iterations. Process-priority elevation was unavailable on the
host, but BenchmarkDotNet reported no critical validation errors. Allocation regression tests independently guard the
direct indexed path.

### 2026-08-29 (shared observation layouts and ordinal-bound CLR materialization)

- Base commit: `1cc12abe2ebcd677484f11bbf7c02e4644ddde39`
- Branch: `codex/observation-ordinal-access`
- Worktree: dirty; includes the uncommitted shared-layout, ordinal materializer, tests, and benchmark coverage
- BenchmarkDotNet: 0.15.8
- OS: macOS Tahoe 26.5.2 (25F84), Darwin 25.5.0
- Hardware: Apple M5 Max, Arm64, 18 physical/logical cores
- SDK/runtime: .NET SDK 10.0.201; .NET 10.0.5 Arm64 RyuJIT
- Environment overrides: `DOTNET_CLI_HOME=/tmp/codex-dotnet-ordinal-bench`,
  `DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1`

```bash
dotnet run \
  --project src/Cohesive.Relations.Benchmarks/Cohesive.Relations.Benchmarks.csproj \
  -c Release --no-build --no-restore -- \
  --job Short \
  --filter "*ObservationFieldAccessBenchmarks*"

dotnet run \
  --project src/Cohesive.Relations.Benchmarks/Cohesive.Relations.Benchmarks.csproj \
  -c Release --no-build --no-restore -- \
  --job Short \
  --filter "*ObservationProjectionBenchmarks.Materialize*" \
           "*ObservationFlatScalarMaterializationBenchmarks*"

dotnet run \
  --project src/Cohesive.Relations.Benchmarks/Cohesive.Relations.Benchmarks.csproj \
  -c Release --no-build --no-restore -- \
  --job Short \
  --filter "*ObservationMaterializerCompilationBenchmarks*"

dotnet run \
  --project src/Cohesive.Relations.Benchmarks/Cohesive.Relations.Benchmarks.csproj \
  -c Release --no-build --no-restore -- \
  --job Short \
  --filter "*ObservationOrdinalIngestionBenchmarks*" \
           "*ObservationProjectionBenchmarks.Write*CanonicalJsonToCallerOwnedBuffer"
```

Repeated top-level scalar reads, with no allocation in any path:

| Fields | Semantic name | Indexed name | Indexed ordinal | Ordinal vs semantic | Ordinal vs indexed name |
|---:|---:|---:|---:|---:|---:|
| 4 | 21.61 ns | 18.25 ns | 7.71 ns | 2.80× faster | 2.37× faster |
| 16 | 100.86 ns | 73.95 ns | 32.18 ns | 3.13× faster | 2.30× faster |
| 64 | 436.46 ns | 317.62 ns | 134.84 ns | 3.24× faster | 2.36× faster |

Warm CLR materialization through indexed occurrences:

| State | Handwritten | Name-bound plan | Ordinal-bound plan | Ordinal improvement | Allocated |
|---|---:|---:|---:|---:|---:|
| Mixed seven-field state with nested object and array | 132.4 ns | 170.3 ns | 158.4 ns | 7.0% | 152 B |
| Sixteen flat `Int64` scalar fields | 118.5 ns | 347.8 ns | 287.2 ns | 17.4% | 144 B |

After the ordinal read was emitted directly into the compiled expression tree and its reflection-only forwarding
helper was removed, a focused same-process ShortRun measured 167.2 ns versus 143.4 ns for the mixed state (14.2%)
and 264.0 ns versus 235.6 ns for the flat state (10.8%). Both retained the same 152 B and 144 B destination-only
allocations. The spread between ShortRuns is why the conclusion reports ranges rather than treating either run as a
stable timing threshold.

The immutable layout is retained and shared across indexed occurrences. Ordinal-bound materializers select their fast
path with one exact-layout reference comparison; semantic observations and readers using another layout retain the
name-based compatibility path. Conversion, missing-field policy, and serializer behavior remain centralized, so the
two execution paths do not create parallel serialization authorities. Ordinal addressing is intentionally limited to
top-level fields. Nested `ObservationValue` objects remain canonical dictionary-backed semantic values until a
representative nested-path benchmark demonstrates that another physical interpretation is worthwhile.

In the final focused ShortRun, fresh conventional plan compilation after process-wide metadata caches were warm
measured 3.834 ms and 18.99 KB. Compiling both the name fallback and ordinal-specialized plan measured 6.463 ms and
54.63 KB. Emitting the ordinal read directly into the expression tree reduced its earlier 10.694 ms compilation mean,
while the larger expression tree increased its transient allocation from 35.5 KB. This is a one-time cold cost for a
reusable cached plan, not per-observation overhead, but remains a visible tradeoff to monitor if plans become
short-lived.

Direct validation and hydration from a sixteen-field reverse-ordered physical layout:

| Operation | Mean | Allocated | Relative to projected validation |
|---|---:|---:|---:|
| Dictionary projection plus canonical validation | 726.3 ns | 3,968 B | 1.00× |
| Direct ordinal-buffer validation | 211.1 ns | 0 B | 0.29× |
| Complete immutable indexed hydration | 289.8 ns | 1,424 B | — |

The indexed hydration allocation is the retained sixteen-element `ObservationValue` snapshot, presence snapshot,
and occurrence. It no longer creates transient field dictionaries or a discarded semantic `Observation` merely to
validate the physical values.

Canonical serialization of the representative mixed seven-field state into reusable caller-owned output measured
655.0 ns and 0 B from the dictionary-backed `Observation`, versus 360.5 ns and 0 B directly from the indexed buffer.
The shared layout caches canonical top-level field order and encoded property names once; nested object and array
values continue through the same canonical value writer used by semantic observations.

These are ShortRun measurements with three measurement iterations. Process-priority elevation was unavailable on the
host, but BenchmarkDotNet reported no critical validation errors. The deterministic allocation tests separately
verify that ordinal materialization allocates only the destination object graph.

### 2026-08-28 (pooled canonical observation JSON and streaming fingerprints)

- Base commit: `2d3c37ac3eeba259d06b7c69874e81ff72f994f1`
- Branch: `codex/observation-json-performance`
- Worktree: dirty; includes the uncommitted pooled JSON writer, fingerprint sink, tests, and benchmark coverage
- BenchmarkDotNet: 0.15.8
- OS: macOS Tahoe 26.5.2 (25F84), Darwin 25.5.0
- Hardware: Apple M5 Max, Arm64, 18 physical/logical cores
- SDK/runtime: .NET SDK 10.0.201; .NET 10.0.5 Arm64 RyuJIT
- Environment overrides: `DOTNET_CLI_HOME=/tmp/codex-dotnet-json`,
  `DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1`

```bash
dotnet run \
  --project src/Cohesive.Relations.Benchmarks/Cohesive.Relations.Benchmarks.csproj \
  -c Release --no-build -- \
  --filter "*ObservationProjectionBenchmarks.ToCanonicalJson*" \
           "*ObservationProjectionBenchmarks.WriteCanonicalJson*" \
           "*ObservationProjectionBenchmarks.ComputeCanonicalFingerprint*"

dotnet run \
  --project src/Cohesive.Relations.Benchmarks/Cohesive.Relations.Benchmarks.csproj \
  -c Release --no-build -- \
  --filter "*ObservationProjectionBenchmarks.ComputeCanonicalFingerprint*"

dotnet run \
  --project src/Cohesive.Relations.Benchmarks/Cohesive.Relations.Benchmarks.csproj \
  -c Release --no-build -- \
  --filter "*ObservationMaterializerCompilationBenchmarks*"

dotnet run \
  --project src/Cohesive.Relations.Benchmarks/Cohesive.Relations.Benchmarks.csproj \
  -c Release --no-build -- \
  --filter "*ObservationProjectionBenchmarks.WriteCanonicalJsonToCallerOwnedBuffer*"
```

Canonical projection of the representative seven-field state containing a nested address and string array:

| Operation | Previous mean | Current mean | Speedup | Previous allocation | Current allocation |
|---|---:|---:|---:|---:|---:|
| Returned canonical UTF-8 | 863.5 ns | 658.3 ns | 1.31× | 6,800 B | 344 B |
| Returned canonical string | 860.3 ns | 688.2 ns | 1.25× | 7,400 B | 632 B |
| Reusable caller-owned UTF-8 output | — | 643.2 ns | — | — | 0 B |
| Bounded streamed SHA-256 fingerprint | — | 3.406 μs | — | payload-sized | 360 B |

The returned forms now allocate only their required result plus the small pooled-buffer owner. Caller-owned output
reuses both destination storage and a thread-local `Utf8JsonWriter`, while the writer is detached from caller storage
before it is cached. Fingerprinting streams bounded chunks of canonical bytes into incremental SHA-256 storage, so
its temporary memory and managed allocation remain constant as payload size grows.

After centralizing the canonical envelope tokens and adding differential writer conformance, the caller-owned path
was rerun independently at 643.2 ns and 0 B, confirming that the shared format authority introduced no regression.

Fresh conventional CLR materializer-plan compilation after process-wide CLR metadata caches are warm measured
3.777 ms and 18.75 KB. This is a descriptive lifecycle baseline distinct from the 157.9 ns / 152 B warm materializer
execution path.

All five DefaultJob benchmarks completed successfully across the recorded runs. Process-priority elevation was
unavailable on the host, but BenchmarkDotNet reported no critical validation errors. The JSON measurements came from
the 1 minute 25 second combined run; only the fingerprint implementation changed afterward. Its final bounded rerun
completed in 47 seconds, and the compilation run completed in 21 seconds.

### 2026-08-27 (allocation-floor observation CLR materialization)

- Base commit: `445f324bd82b61a75a82c40af08060f3d83cc0b7`
- Branch: `codex/observation-materializer-performance`
- Worktree: dirty; includes the uncommitted direct-conversion, tests, and benchmark-baseline implementation
- BenchmarkDotNet: 0.15.8
- OS: macOS Tahoe 26.5.2 (25F84), Darwin 25.5.0
- Hardware: Apple M5 Max, Arm64, 18 physical/logical cores
- SDK/runtime: .NET SDK 10.0.201; .NET 10.0.5 Arm64 RyuJIT
- Environment overrides: `DOTNET_CLI_HOME=/tmp/codex-dotnet-materializer`,
  `DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1`

```bash
dotnet run \
  --project src/Cohesive.Relations.Benchmarks/Cohesive.Relations.Benchmarks.csproj \
  -c Release --no-build -- \
  --filter "*ObservationProjectionBenchmarks.Materialize*"
```

Warm projections of a seven-field state containing nested address and array values:

| Operation | Previous mean | Current mean | Speedup | Previous allocation | Current allocation |
|---|---:|---:|---:|---:|---:|
| Handwritten destination lower bound | — | 114.2 ns | — | — | 152 B |
| Compiled CLR materializer | 1.017 μs | 157.9 ns | 6.44× | 4.22 KB | 152 B |
| Default cached CLR materializer | 1.040 μs | 187.2 ns | 5.55× | 4.22 KB | 152 B |

The compiled plan is 1.38× the handwritten mean, or approximately 43.8 ns of abstraction overhead. Both
materializer paths allocate exactly the same 152 B as handwritten construction: the state record, nested address,
and two-element string array. The optimization removes per-field JSON text, UTF-8, reader, and serializer
materialization while retaining the JSON compatibility path for explicit serializer options, custom JSON contracts,
and unsupported target types.

All three benchmarks completed successfully in 1 minute 11 seconds. Process-priority elevation was unavailable on
the host, but BenchmarkDotNet reported no critical validation errors. Previous results are from the immediately
preceding DefaultJob on the same machine and runtime.

### 2026-08-27 (observation lifecycle DefaultJob verification)

- Base commit: `53e2801`
- Branch: `codex/observation-performance-benchmarks`
- Worktree: dirty; includes the uncommitted benchmark and validator implementation
- BenchmarkDotNet: 0.15.8
- OS: macOS Tahoe 26.5.2 (25F84), Darwin 25.5.0
- Hardware: Apple M5 Max, Arm64, 18 physical/logical cores
- SDK/runtime: .NET SDK 10.0.201; .NET 10.0.5 Arm64 RyuJIT
- Environment overrides: `DOTNET_CLI_HOME=/tmp/codex-dotnet-observation`,
  `DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1`

```bash
dotnet run \
  --project src/Cohesive.Relations.Benchmarks/Cohesive.Relations.Benchmarks.csproj \
  -c Release --no-build -- \
  --filter "*ObservationCreationBenchmarks*" "*ObservationProjectionBenchmarks*"
```

Creation and successful validation:

| Operation | Shape | Mean | Op/s | Allocated |
|---|---|---:|---:|---:|
| Create from immutable value | 16 flat scalars | 382.5 ns | 2,614,236 | 112 B |
| Create from mutable fields | 16 flat scalars | 593.8 ns | 1,683,943 | 2,112 B |
| Validate | 16 flat scalars | 261.5 ns | 3,823,693 | 0 B |
| Create from immutable value | 16-field nested object | 295.1 ns | 3,388,856 | 112 B |
| Create from mutable fields | 16-field nested object | 351.0 ns | 2,849,224 | 712 B |
| Validate | 16-field nested object | 266.4 ns | 3,753,642 | 0 B |
| Create from immutable value | 64-element array | 321.2 ns | 3,113,563 | 112 B |
| Create from mutable fields | 64-element array | 500.4 ns | 1,998,548 | 712 B |
| Validate | 64-element array | 281.8 ns | 3,548,276 | 0 B |

Deterministic failure diagnostics:

| Shape | Mean | Op/s | Allocated |
|---|---:|---:|---:|
| 16 flat scalars | 1.938 μs | 516,108 | 2,632 B |
| 16-field nested object | 1.346 μs | 742,955 | 3,200 B |
| 64-element array | 1.368 μs | 731,233 | 1,232 B |

Warm projections of a seven-field state containing nested address and array values:

| Operation | Mean | Op/s | Allocated |
|---|---:|---:|---:|
| Compiled CLR materializer | 1.017 μs | 983,666 | 4.22 KB |
| Default cached CLR materializer | 1.040 μs | 961,399 | 4.22 KB |
| Canonical UTF-8 JSON | 863.5 ns | 1,158,108 | 6.64 KB |
| Canonical JSON string | 860.3 ns | 1,162,446 | 7.23 KB |

All 16 benchmarks completed successfully in 4 minutes 45 seconds. Process-priority elevation was unavailable on the
host, but BenchmarkDotNet reported no critical validation errors. The DefaultJob confirms that successful validation
is allocation-free and that the 112 B immutable-creation allocation is solely the retained `Observation`; CLR
materialization and JSON serialization are now the largest measured steady-state allocation surfaces.

### 2026-08-26 (allocation-light observation validation)

- Base commit: `c3a295613ee79164bb9376d2a5d4d56ae49853e7`
- Branch: `codex/observation-performance-benchmarks`
- Worktree: dirty; includes the uncommitted benchmark and validator implementation
- BenchmarkDotNet: 0.15.8
- OS: macOS Tahoe 26.5.2 (25F84), Darwin 25.5.0
- Hardware: Apple M5 Max, Arm64, 18 physical/logical cores
- SDK/runtime: .NET SDK 10.0.201; .NET 10.0.5 Arm64 RyuJIT
- Environment overrides: `DOTNET_CLI_HOME=/tmp/codex-dotnet-observation`,
  `DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1`

```bash
dotnet run \
  --project src/Cohesive.Relations.Benchmarks/Cohesive.Relations.Benchmarks.csproj \
  -c Release --no-build -- \
  --job Short \
  --filter "*ObservationCreationBenchmarks*"
```

Successful creation and validation:

| Operation | Shape | Before | After | Before allocation | After allocation |
|---|---|---:|---:|---:|---:|
| Create from immutable value | 16 flat scalars | 1.925 μs | 375.8 ns | 6.38 KB | 112 B |
| Validate | 16 flat scalars | 1.851 μs | 252.1 ns | 6.27 KB | 0 B |
| Create from immutable value | 16-field nested object | 2.729 μs | 289.4 ns | 9.20 KB | 112 B |
| Validate | 16-field nested object | 2.694 μs | 251.6 ns | 9.09 KB | 0 B |
| Create from immutable value | 64-element array | 2.923 μs | 301.0 ns | 20.34 KB | 112 B |
| Validate | 64-element array | 2.775 μs | 269.2 ns | 20.23 KB | 0 B |

Caller-owned mutable-field creation retains its required defensive snapshot and now allocates 2,112 B for the flat
case or 712 B for the nested-object and array-heavy cases. Invalid-value diagnostics remain independently measured;
this intermediate ShortRun reported 792–1,024 B because path and message construction is deferred until validation
fails. The final DefaultJob above supersedes those diagnostic allocation measurements after expanded deterministic
diagnostic coverage.

These are ShortRun comparisons from the same worktree and machine. A stable DefaultJob run should be used before
setting regression thresholds.

### 2026-08-26 (observation lifecycle initial baseline)

- Base commit: `c3a295613ee79164bb9376d2a5d4d56ae49853e7`
- Branch: `codex/observation-performance-benchmarks`
- Worktree: dirty; includes the uncommitted observation benchmark implementation
- BenchmarkDotNet: 0.15.8
- OS: macOS Tahoe 26.5.2 (25F84), Darwin 25.5.0
- Hardware: Apple M5 Max, Arm64, 18 physical/logical cores
- SDK/runtime: .NET SDK 10.0.201; .NET 10.0.5 Arm64 RyuJIT
- Environment overrides: `DOTNET_CLI_HOME=/tmp/codex-dotnet-observation`,
  `DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1`

```bash
dotnet run \
  --project src/Cohesive.Relations.Benchmarks/Cohesive.Relations.Benchmarks.csproj \
  -c Release --no-build -- \
  --job Short \
  --filter "*ObservationCreationBenchmarks*" "*ObservationProjectionBenchmarks*"
```

Creation and validation:

| Operation | Shape | Mean | Op/s | Allocated |
|---|---|---:|---:|---:|
| Create from immutable value | 16 flat scalars | 1.925 μs | 519,553 | 6.38 KB |
| Create from mutable fields | 16 flat scalars | 1.780 μs | 561,876 | 8.23 KB |
| Validate | 16 flat scalars | 1.851 μs | 540,208 | 6.27 KB |
| Create from immutable value | 16-field nested object | 2.729 μs | 366,447 | 9.20 KB |
| Create from mutable fields | 16-field nested object | 2.882 μs | 346,924 | 9.69 KB |
| Validate | 16-field nested object | 2.694 μs | 371,177 | 9.09 KB |
| Create from immutable value | 64-element array | 2.923 μs | 342,120 | 20.34 KB |
| Create from mutable fields | 64-element array | 2.928 μs | 341,576 | 20.84 KB |
| Validate | 64-element array | 2.775 μs | 360,347 | 20.23 KB |

Warm projections of a seven-field state containing nested address and array values:

| Operation | Mean | Op/s | Allocated |
|---|---:|---:|---:|
| Compiled CLR materializer | 1.050 μs | 952,430 | 4.22 KB |
| Default cached CLR materializer | 1.083 μs | 923,671 | 4.22 KB |
| Canonical UTF-8 JSON | 932.0 ns | 1,072,988 | 6.64 KB |
| Canonical JSON string | 927.6 ns | 1,078,068 | 7.23 KB |

ShortRun uses three measurement iterations and has wide confidence intervals for some cases. The results establish
the initial order of magnitude and allocation surface; optimization comparisons should use the same fixtures and a
reviewed DefaultJob run on stable hardware. The mutable-field timing being nominally lower than immutable creation in
the flat case is measurement noise; its additional 1.85 KB snapshot allocation is the meaningful distinction.

### 2026-07-19 (ARI-145)

- Base commit: `2214f5c7172a3c75ed169b6144296f4ac1793501`
- Branch: `eulerfx/ari-145-establish-canonical-relation-query-conformance-and`
- Worktree: dirty; includes the uncommitted ARI-145 conformance and benchmark implementation
- BenchmarkDotNet: 0.15.8
- OS: macOS Tahoe 26.5.2 (25F84), Darwin 25.5.0
- Hardware: Apple M5 Max, Arm64, 18 physical/logical cores
- SDK/runtime: .NET SDK 10.0.201; .NET 10.0.5 Arm64 RyuJIT
- Environment overrides: none

#### DefaultJob warm verification

```bash
dotnet run \
  --project src/Cohesive.Relations.Benchmarks/Cohesive.Relations.Benchmarks.csproj \
  -c Release --no-build -- \
  --filter "*RelationDtoWarmBenchmarks*"
```

Representative 1,024-row results:

| Scenario | Mapper | Mean | Op/s | vs handwritten | vs AutoMapper | Allocated |
|---|---|---:|---:|---:|---:|---:|
| Simple | Handwritten | 22.75 μs | 43,958 | 1.00× | 0.76× | 57,368 B |
| Simple | AutoMapper member plan | 30.12 μs | 33,201 | 1.32× | 1.00× | 57,368 B |
| Simple | Cohesive compiled kernel | 39.64 μs | 25,227 | 1.74× | 1.32× | 57,368 B |
| Simple | Cohesive full canonical | 45.01 μs | 22,219 | 1.98× | 1.49× | 65,898 B |
| Simple | Existing observation mapper | 301.51 μs | 3,317 | 13.25× | 10.01× | 1,660,952 B |
| Joined | Handwritten | 81.45 μs | 12,278 | 1.00× | 0.80× | 106,520 B |
| Joined | AutoMapper member plan | 101.92 μs | 9,812 | 1.25× | 1.00× | 106,520 B |
| Joined | Cohesive compiled kernel | 125.39 μs | 7,975 | 1.54× | 1.23× | 106,520 B |
| Joined | Cohesive full canonical | 116.40 μs | 8,591 | 1.43× | 1.14× | 115,051 B |
| Joined | Existing observation mapper | 950.06 μs | 1,053 | 11.66× | 9.32× | 5,052,440 B |

In this longer run, AutoMapper's mean at 1,024 rows is 24.0% lower than the Cohesive simple kernel
and 18.7% lower than the joined kernel. Against the full canonical mapper, its mean is 33.1% lower for
the simple case and 12.4% lower for the joined case. At one row, however, the Cohesive kernel is faster
than AutoMapper in both scenarios, and at 32 rows AutoMapper's advantage over the kernel is 14.0% for
simple mapping and 6.8% for joined mapping. The crossover shows why the result should be reported by
scenario and scale rather than summarized as a single percentage. Kernel-only and AutoMapper paths
allocate identically at each measured scale.

#### ShortRun baseline

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
