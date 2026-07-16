# Cohesive.Relations Benchmarks

This project establishes descriptive performance baselines for canonical relation-to-CLR DTO
materialization. It is intentionally separate from the xUnit projects: benchmarks are executable
measurement programs, not correctness gates.

The fixtures come from `Cohesive.Relations.TestFixtures`, so executable tests and benchmarks can
exercise the same semantic definitions, runtime evidence, and CLR DTO contracts.

## Benchmark groups

- **Warm kernel:** hand-written materialization, the existing `ObservationObjectMapper<T>` baseline,
  the generated construction kernel without result bookkeeping, and the full compiled canonical mapper.
- **Compilation:** a cold compiler instance and a cached repeat compilation.
- **End to end:** canonical in-memory interpretation followed by typed materialization.
- **Diagnostics:** missing joined input and incompatible output-value conversion paths, measured
  independently from successful mapping.

The successful warm cases cover a single-source `LoadSummaryDto` and a flattened
`Load + Customer + Equipment -> LoadSearchDto` relation at 1, 32, and 1,024 rows. The kernel-only
cases isolate generated CLR construction from canonical plan, terminal, and row-shape validation;
cancellation checks; exception-to-diagnostic translation; row provenance; and mapping-result construction.
Generated field-presence, nullability, and conversion checks remain part of the kernel. Results include mean
time, relative ratio, operations per second, allocated bytes, and GC counts.

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
  --job Dry --filter "*RelationDto*"
```

Run a quicker representative measurement:

```shell
dotnet run \
  --project src/Cohesive.Relations.Benchmarks/Cohesive.Relations.Benchmarks.csproj \
  -c Release --no-build -- \
  --job Short --filter "*RelationDto*"
```

Run the default measurement jobs:

```shell
dotnet run \
  --project src/Cohesive.Relations.Benchmarks/Cohesive.Relations.Benchmarks.csproj \
  -c Release --no-build -- \
  --filter "*RelationDto*"
```

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
