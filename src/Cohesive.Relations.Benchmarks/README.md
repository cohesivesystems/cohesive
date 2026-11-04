# Cohesive performance benchmarks

This project establishes descriptive performance baselines for canonical observation creation,
validation, CLR materialization, and JSON serialization as well as relation compilation,
physical planning and execution, diagnostics, and relation-to-CLR DTO materialization. It is
intentionally separate from the xUnit projects: benchmarks are executable measurement programs,
not correctness gates.

The fixtures come from `Cohesive.Relations.TestFixtures`, so executable tests and benchmarks can
exercise the same semantic definitions, runtime evidence, and CLR DTO contracts.

## Benchmark groups

- `EntityTransitionEndToEndBenchmarks` and `--entity-transition-allocations` measure one entity API transition from
  request bytes to response bytes through the ASP.NET binding, over in-memory and SQLite repositories, with a stage
  partition; see [results](../../docs/performance/entity-transition-end-to-end.md).

- `TypedDocumentCanonicalizationBenchmarks` compares immutable typed strict JSON canonicalization
  with mutable node expansion; see [evidence and contract](../../docs/performance/typed-document-canonicalization/README.md).

- `ClrTypeRefMappingBenchmarks` measures traversal-scoped CLR reflection preparation across flat,
  nested, collection, and bounded large type graphs; see [evidence and lifetime contract](../../docs/performance/clr-type-mapping/README.md).

- **Representative selection:** full reference execution over ten candidates per key at 100, 1,000 and 10,000 rows,
  with plans and evidence prepared in setup; see `RepresentativeSelectionBenchmarks` and [results](RESULTS.md).

- **Observation creation and validation:** already-owned immutable values, caller-owned field snapshots,
  successful validation, and diagnostic production across flat-scalar, nested-object, and array-heavy values.
- **Observation physical ingestion:** direct ordinal validation, caller-owned snapshot construction, single-owner
  builder transfer, and shape-bound JSON hydration into retained indexed storage.
- **Observation projections:** a handwritten destination-allocation lower bound, warm compiled and default-cached CLR
  materialization, plus canonical UTF-8 and string JSON serialization for representative application state.
- **Warm kernel:** hand-written materialization, the shared core materializer over validated semantic observations,
  the same materializer over the explicit `IndexedObservationOccurrence` path, a
  preconfigured AutoMapper 16.2.0 canonical-row baseline, the generated construction kernel without result
  bookkeeping, and the full compiled canonical mapper.
- **Compilation:** a cold compiler instance, a cached repeat compilation, and fresh AutoMapper
  configuration validation plus eager mapping-plan compilation.
- **End to end:** canonical in-memory interpretation followed by typed materialization.
- **Diagnostics:** missing joined input and incompatible output-value conversion paths, measured
  independently from successful mapping.
- **Physical:** planning and bounded acquisition/correlation over the shared federated Load fixture.
- **Execution stages:** requirement analysis, evidence indexing, in-memory execution, observation projection,
  warm CLR materialization, and canonical JSON output over simple and joined scenarios. Caller-owned JSON buffers
  isolate serialization work from result-buffer allocation.
- **Evaluation fingerprinting:** the monolithic v3 canonical-document reference compared with segmented hashing that
  reuses the exact immutable compilation snapshot while always reserializing per-evaluation evidence.

The successful warm cases cover a single-source `LoadSummaryDto` and a flattened
`Load + Customer + Equipment -> LoadSearchDto` relation at 1, 32, and 1,024 rows. The kernel-only
cases isolate generated CLR construction from canonical plan, terminal, and row-shape validation;
cancellation checks; exception-to-diagnostic translation; row provenance; and mapping-result construction.
Generated field-presence, nullability, and conversion checks remain part of the kernel. Results include mean
time, relative ratio, operations per second, allocated bytes, and GC counts.

### AutoMapper comparison boundary

The warm AutoMapper cases consume the same already-materialized canonical `RelationQueryOutputRow` values as the
hand-written and generated-kernel cases. AutoMapper uses explicit constructor-member maps because canonical rows carry
portable `ObservationValue` fields rather than CLR source-object properties. The member expressions perform the same
checked field reads as the hand-written baseline; the measured AutoMapper work is its compiled mapping plan and
collection orchestration plus DTO construction. A whole-object custom converter is deliberately avoided because that
would measure AutoMapper dispatch around the hand-written mapper rather than AutoMapper's member-plan execution.

`MapperConfiguration`, configuration validation, `CompileMappings()`, mapper creation, canonical-row array creation,
and a complete output-correctness comparison all run in `GlobalSetup`, outside the warm measurements. The separate
compilation benchmarks create, validate, and eagerly compile a fresh configuration. The suite calls `Map`, not
`ProjectTo`, because it measures in-memory DTO materialization rather than provider query translation.

This comparison does not imply equivalent semantics. AutoMapper does not perform relation interpretation, source
acquisition, missing-input analysis, result completeness, or provenance bookkeeping. Those costs remain visible in
the separately labeled full canonical and end-to-end cases. AutoMapper is a benchmark-only dependency and is not
referenced by any production Cohesive project.

AutoMapper 16 is independently licensed. This repository does not embed a license key; benchmark users are
responsible for evaluating its terms and supplying any applicable license through AutoMapper's supported
configuration. The benchmark passes a shared no-op logger factory so license messages do not add console or logging
I/O to configuration measurements; it does not construct or reconfigure logging inside a timed method.

### Physical execution boundary

The physical execution cases supply 32 or 1,024 Load roots, reuse Customer and Equipment keys across roots, and read
the distinct keys in batches of at most 32. Setup validates the exact unique-key and batch counts, canonical output
ordering, and DTO values before measurement. Timed readers are deterministic in-memory fakes with request recording
disabled and serve batched identity reads from a prebuilt ordinal index. The result therefore measures acquisition
planning, key extraction/deduplication, batching, evidence construction, and local correlation without a repeated
fake-store table scan, network latency, or backend latency.

## Commands

Build in Release mode:

```shell
dotnet build src/Cohesive.Relations.Benchmarks/Cohesive.Relations.Benchmarks.csproj \
  -c Release -warnaserror
```

Run a short compilation/invocation smoke check. A Dry job verifies benchmark discovery and wiring;
it is not a performance result:

```shell
dotnet run \
  --project src/Cohesive.Relations.Benchmarks/Cohesive.Relations.Benchmarks.csproj \
  -c Release --no-build -- \
  --job Dry --filter "*"
```

Run a quicker representative measurement:

```shell
dotnet run \
  --project src/Cohesive.Relations.Benchmarks/Cohesive.Relations.Benchmarks.csproj \
  -c Release --no-build -- \
  --job Short --filter "*Observation*"
```

Run the default measurement jobs:

```shell
dotnet run \
  --project src/Cohesive.Relations.Benchmarks/Cohesive.Relations.Benchmarks.csproj \
  -c Release --no-build -- \
  --filter "*Observation*"
```

The broad `*` filter discovers every benchmark group. Use `*Observation*` for the observation lifecycle or a
class-name filter such as `*ObservationCreationBenchmarks*`, `*ObservationProjectionBenchmarks*`,
`*ObservationMaterializerCompilationBenchmarks*`, `*RelationDtoWarmBenchmarks*`, or
`*RelationQueryExecutionStageBenchmarks*` when measuring one concern in isolation. Use
`*RelationQueryEvaluationFingerprintBenchmarks*` to compare the v3 reference and warm segmented fingerprint paths.
The projection benchmarks
independently track returned UTF-8, returned strings, reusable caller-owned JSON buffers, streamed fingerprints,
and warm CLR materialization so allocation and CPU tradeoffs remain visible. Use
`*RelationQueryExecutionStageBenchmarks.Execute*` for a focused execution-kernel comparison after the stage suite
has identified execution as the dominant cost.

## GitHub Actions

The automatic pull-request workflow does not execute BenchmarkDotNet. To run the benchmarks on demand, open
**Actions**, select **cohesive-benchmarks**, and choose **Run workflow**. The workflow accepts:

- A `Dry`, `Short`, or `Default` BenchmarkDotNet job. `Dry` is the default discovery and invocation smoke check.
- A BenchmarkDotNet filter, defaulting to `*` so newly added lifecycle benchmarks participate in discovery checks.

The workflow uploads `BenchmarkDotNet.Artifacts` for 14 days, including artifacts produced before a failed run. GitHub
hosted runners are appropriate for discovery and coarse on-demand comparisons, but their shared and variable hardware
should not be used for authoritative performance baselines. Record reviewed baselines on stable, documented hardware.

## Recording baselines

Initial measurements are observations, not pass/fail thresholds. When recording a reviewed result,
copy the relevant Markdown table into RESULTS.md:

- Cohesive commit and dirty-worktree state.
- BenchmarkDotNet and .NET SDK/runtime versions.
- OS, architecture, CPU, and power mode.
- Exact command and selected filters.
- Any tiered-PGO, ReadyToRun, or environment overrides.

Do not commit generated `BenchmarkDotNet.Artifacts`. Only curated baseline summaries belong in source
control.

### Service invocation instrumentation

`ServiceInvocationBenchmarks` compares prepared-runtime construction, construction plus an invocation,
and warm invocation with execution activity sampling disabled/enabled. Immutable transition/service
compilation and the initial entity write occur in setup; every measured invocation validates success
and advances the opaque concurrency token. One in-memory entity is reused; no database, HTTP server,
exporter, or host startup is measured. The enabled mode records activity data and enables the invocation count/duration meters with no-op callbacks, without exporting either.
Construction-plus-invocation is a fresh runtime over warmed dependencies, not process cold start.

Run `dotnet run --project src/Cohesive.Relations.Benchmarks -c Release -- --filter '*ServiceInvocationBenchmarks*'`.
Use `--job Dry` only to verify harness execution; its single samples are not performance conclusions.
Retain BenchmarkDotNet environment and allocation reports with any reported comparison.

Realization fingerprint and ordering-key qualification: run filters `*RealizationFingerprintBenchmarks*`
and `*RealizationSequenceKeyBenchmarks*`. See [workloads, before/after results and boundaries](../../docs/performance/realization-fingerprints.md).

### Warm DTO CPU/JIT attribution

`--relation-dto-profile <route> <seconds>` reuses the exact 1,024-row warm fixture. Routes are
`kernel-simple`, `kernel-joined`, `auto-simple`, and `auto-joined`; duration is bounded to 1–60 seconds.
Fixture compilation and correctness checks precede 256 warmups and the explicitly marked `Measure` loop.
This is a sampling target, not a replacement for BenchmarkDotNet timing or an end-to-end query benchmark.

```sh
dotnet src/Cohesive.Relations.Benchmarks/bin/Release/net10.0/Cohesive.Relations.Benchmarks.dll \
  --relation-dto-profile kernel-joined 12
```

Collect with an already-installed `dotnet-trace` and analyze stacks beneath `RelationDtoWarmProfile.Measure`.
Unfiltered trace summaries also include setup and GC activity. Do not overlap profiling with timed comparisons.
The scalar-reader experiment's matched-process measurements, rejected generic-converter experiment and CPU/JIT
attribution are retained in `docs/performance/evidence/relation-dto-scalar-readers`.


### DTO scalar and collection experiments

The guarded scalar-reader candidate emits exact-kind reads into the constructor expression and
loads binding metadata only for the original conversion/diagnostic fallback. Missing, null, alternate
scalar kinds and entity-reference checks retain their original behavior. No public API changes.

A compiled collection-loop prototype reused the same row expression and prebuilt canonical row arrays
as AutoMapper. Local ARM64 repeats favored it (20.073 vs 24.328 us simple; 77.594 vs 79.960 us joined,
1,024 rows). Linux CI did not establish a consistent benefit: on Intel Xeon 6973P-C, batch took
45.752 vs AutoMapper's 38.901 us simple and 177.710 vs 155.193 us joined. The scalar kernel was
50.945 / 172.902 us. All paths allocated the same destination bytes (57,368 / 106,520).
The batch prototype was therefore removed; ordinary mappers do not retain its expression or lazy slot.

All 42 CI cases completed in run 41763124452 at experimental revision d7fb02a4. The experiment,
including its extra allocation guard, is preserved as a patch against a62aca11 and raw reports under
`docs/performance/evidence/relation-dto-scalar-readers/batch`. The prototype was a fail-fast projection,
not a replacement for canonical mapping's admission, provenance, cancellation or collect-all diagnostics.
The public canonical mapper retains its behavior.

Earlier guarded scalar-reader CI (41761072832, AMD EPYC 9V45) trailed AutoMapper by 14.6% simple /
10.9% joined; the new Intel run trailed by 31.0% / 11.4%. Hosted hardware varies: these runs do not
establish a portable simple-case improvement or a causal before/after Linux speedup. Local matched
control/candidate evidence is retained under `guarded`. Use stable hardware before accepting a release
performance claim. The next promising experiment is direct ordinal access over validated relation-row
layouts, accounting separately for admission and output provenance costs.


### Direct ordinal DTO experiment

`CompiledOrdinalSimple` / `CompiledOrdinalJoined` use the existing validated
`IndexedObservationOccurrence` representation and exact bound-layout identity checks. Constructor and
member initialization come from the same DTO binding plan; exact-kind fast conversions and all generic
fallback conversions share their implementation with canonical mapping. No target expression is retained
by default mappers, and ordinal compilation is internal and opt-in for this experiment.

`OrdinalAdmissionSimple` / `OrdinalAdmissionJoined` allocate new ordinal storage from already-built
canonical rows using the existing ownership-transferring builder, including field-name binding and shape
validation. `OrdinalAdmissionAndMap*` includes that preparation plus DTO mapping. Metadata identities and
layouts are prepared in setup, as are AutoMapper's input row arrays and plans. Acquisition, query execution,
and original canonical-row construction are excluded from every path. Warm ordinal versus canonical-input
AutoMapper compares different physical storage; it is not a one-shot conversion speedup claim. The existing
`SharedCoreIndexed*` cases provide a same-storage comparator. None of the projection kernels claims the
public mapper's provenance, cancellation, admission, or collect-all diagnostics contract.


The ordinal experiment's Linux Short run [41876590708](https://github.com/cohesivesystems/cohesive/actions/runs/41876590708)
at revision `343f2e98` completed all 54 cases on AMD EPYC 7763 / .NET 10.0.14. Representative
1,024-row means (microseconds per complete output array):

| Path | Simple | Joined |
| --- | ---: | ---: |
| AutoMapper, canonical row input | 54.996 | 204.096 |
| Existing canonical scalar kernel | 64.514 | 228.914 |
| Direct bound ordinal kernel | 31.641 | 65.384 |
| Existing shared-core indexed materializer | 98.183 | 270.531 |
| Canonical-to-ordinal admission only | 414.838 | 851.545 |
| Canonical-to-ordinal admission plus DTOs | 463.945 | 960.897 |

Warm ordinal mapping is about 1.74x / 3.12x faster than AutoMapper on these different storage inputs,
and 3.10x / 4.14x faster than the existing same-storage indexed materializer. Each warm path allocates
only 57,368 / 106,520 destination bytes. Ordinal preparation plus mapping allocates 401,488 / 647,248
bytes. These are Short-job observations on a hosted runner, not stable release budgets. The full suite
also covers 1 and 32 rows; the ordinal kernel beat AutoMapper at both widths in both fixtures.

Three counterbalanced ARM64 repeats agree on the direction: ordinal 12.372 / 26.576 us versus
AutoMapper 24.934 / 82.612 us. A matched control check using the old identical harness and changing
only `Cohesive.Relations.dll` found a 4.3% increase in simple scalar-kernel time, 0.9% joined, and
0.5% / 1.0% for the full canonical mapper after shared conversion factoring. That small canonical-path
tradeoff is recorded, not claimed as an improvement. An alternate string-check ordering did not
resolve it and was reverted; its first repeat overlapped a rebuild and is excluded completely.

Keep direct ordinal use opt-in until an ingestion or relation producer already owns validated ordinal
storage. Repacking an existing canonical result for one mapping is substantially slower. Native ordinal
ingestion, validation, and complete application execution still need end-to-end comparison before
production routing changes. This prototype supports the existing top-level scalar DTO contract; it
introduces no recursive mapping semantics or public compilation API. The binding and conversion rules
remain those of the relation mapper, not the more general core materializer.

The reproduction commands, source/runtime hashes, matched controls, raw reports, rejected probe, and
verification record are retained in `docs/performance/evidence/relation-dto-ordinals`. The implementation
adds no public types or dependencies, and does not retain target expression trees in ordinary mappers.


### Native ordinal relation projections

The internal `UseOrdinalProjection` interpreter strategy writes top-level projection assignments directly
into owned ordinal slots. Per-plan weak caching binds assignment ordinals and stable layouts once.
Undefined assignments remove a slot; unavailable expressions retain the reference interpreter's gap behavior.
Nested projections retain the reference path. Terminal field selection preserves ordinal backing, while
canonical field enumeration and canonical value equality remain unchanged. Projection construction itself
is not shape admission: partial results and gaps remain representable.

The ordinal DTO kernel now accepts the existing ordinal reader interface, allowing both validated indexed
occurrences and admitted ordinal-backed observations. This internal interface is trusted: a matching layout
is not proof of validation. `DirectPipeline*` benchmarks execute the whole query from the same evidence,
validate every output through `Observation.Create`, then construct DTOs. Tests assert admission retains
physical storage without repacking, repeated executions share layouts, and missing/invalid evidence retains
status, values, ordering, gaps and occurrence provenance. The default interpreter strategy is unchanged.

Run `--filter '*DirectPipeline*'` for twelve cases (simple/joined, 1/32/1024 rows, reference/ordinal).
Compilation caches are warmed in setup. The measured path includes interpreter execution, terminal shaping,
output validation and DTO construction, excluding external acquisition and serialization. The strategies
retain execution provenance before the benchmark consumes values; this is not a full public DTO-mapping
result comparison. Host-dependent local results suggest allocation reduction and modest/noisy latency gains;
the earlier warm-kernel ratios must not be treated as complete-query speedups.


[Linux direct-pipeline run 41927974838](https://github.com/cohesivesystems/cohesive/actions/runs/41927974838)
at `fee80586` completed all twelve cases on AMD EPYC 7763 / .NET 10.0.14. At 1,024 rows:

| Complete execution + admission + DTO mapping | Simple | Joined |
| --- | ---: | ---: |
| Reference, ms | 7.435 | 25.692 |
| Direct ordinal projection, ms | 6.673 | 25.085 |
| Reference allocation, bytes | 4,675,771 | 14,224,850 |
| Direct ordinal allocation, bytes | 4,249,610 | 13,208,979 |

Observed latency is about 10% / 2% lower and allocation about 9% / 7% lower. The joined timing difference
is small; Short-job hosted-runner results do not establish a portable latency guarantee. Local repeated
means showed 3% / 12% lower latency, demonstrating timing variability despite consistent allocation direction.
Retain opt-in execution until representative application workloads and stable-host measurements support
adoption. Raw environments, reports, source hashes and three local counterbalanced repeats are preserved
under `docs/performance/evidence/relation-direct-ordinals`. This completes the relation-projection producer
experiment; native adapter ingestion and serialization fusion are separate follow-ups.
