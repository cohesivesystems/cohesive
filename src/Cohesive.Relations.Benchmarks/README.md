# Cohesive performance benchmarks

This project establishes descriptive performance baselines for canonical observation creation,
validation, CLR materialization, and JSON serialization as well as relation compilation,
physical planning and execution, diagnostics, and relation-to-CLR DTO materialization. It is
intentionally separate from the xUnit projects: benchmarks are executable measurement programs,
not correctness gates.

The fixtures come from `Cohesive.Relations.TestFixtures`, so executable tests and benchmarks can
exercise the same semantic definitions, runtime evidence, and CLR DTO contracts.

## Benchmark groups

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
`*RelationQueryExecutionStageBenchmarks*` when measuring one concern in isolation. The projection benchmarks
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
