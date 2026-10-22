# Format shape-graph field diagnostics only after failure

Core's shape-graph semantic validator is the authority for field type/presence/cardinality/nullability
admission. It previously constructed a subject and URI-escaped location for every shape and named
structural field, even when metadata was valid. Evaluate the existing checks first and format once
only when at least one fails. Iterate named types directly instead of allocating an `OfType` iterator.

This extends the existing Core validator. No Ari wrapper, public API, semantic model, preparation
cache, admission evidence, or temporary bridge is added. Every invocation checks every declaration.
Diagnostic codes, severities, messages, escaped locations and declaration order are unchanged;
the existing URI encoder and graph diagnostics retain authority. Typed protocol specifications and
CLR projections remain interpretations of their versioned source artifacts.

## Tests and focused allocation

The full Core test project passes 5,020 tests (60 existing environment-gated skips). All five Ari
solution test projects pass 1,412 tests (18 existing gated skips) using a local output-only Core override.
Exact diagnostic assertions cover both owner kinds, all four failures on one field, reserved/Unicode
names and declaration order. Four median-of-seven allocation guards at 128/4,096 fields and both
owner kinds pass on the candidate and fail on unchanged Core. The 1,024-byte cap permits fixed
validation overhead but rejects the former per-field strings. No timing assertion runs in CI.

The opt-in `--metadata-validation-counters` warms each already-constructed graph eight times and
measures seven current-thread allocation samples, including the semantic validation result.
Initial graph construction and graph diagnostic preparation are outside this focused warm boundary.
These are cumulative allocations, not retained heap. [Exact counters](focused-allocations.csv):

| Fields | Owner | Before | After |
| --- | --- | ---: | ---: |
| 128 | Shape | 46,032 B | 32 B |
| 128 | Named structural | 48,632 B | 32 B |
| 4,096 | Shape | 1,474,512 B | 32 B |
| 4,096 | Named structural | 1,597,112 B | 32 B |

## Original X12 workflow

Both frozen Ari variants include #247's EDI scratch fix and official alpha.155 dependencies. Only
`Cohesive.dll` differs: both modules were built from source at alpha.155's merged source with the
same SDK, configuration and source path. Other modules and the comparison harness are identical.
The baseline is therefore after the EDI scratch fix, not the older public-alpha.155 allocation figure.
Neither this Core prototype nor its runtime override has been published or put into the package cache.

Four alternating process pairs per source, 2,048 original cases/process, 32,768 passing cases. Use
cases 1537–2048 as the fixed late window. First aggregate within each process, then take the median
across four process replicas per variant; cases within a process are not independent replicas.
CPU includes GC/JIT. No owned builds, tests, benchmarks or traces overlap these timing runs.

| X12 | Warm allocations before → after | Median CPU before → after | Mean CPU before → after |
| --- | --- | --- | --- |
| 004010 | 3.911 → 3.716 MB | 14.423 → 14.632 ms | 14.941 → 14.979 ms |
| 005030 | 4.075 → 3.846 MB | 16.461 → 15.982 ms | 16.828 → 16.550 ms |

[All process windows](case-windows.csv) retain cold, second, early and late measurements, including
unfavorable pairs and JIT totals. These limited local measurements are not a universal latency claim.
Graph fingerprints match both variants and the verified published baseline. The [manifest](manifest.json)
records qualified source/module identities and raw-counter hashes.

## Focused timing and the 004010 follow-up

BenchmarkDotNet 0.15.8 ShortRun, InProcessEmitToolchain, three warmup and three measurement
iterations; Apple M5 Max, Arm64, macOS 27.0.1, .NET SDK 10.0.402 / runtime 10.0.13. Both frozen
benchmark assemblies match; only their Core module differs. All four cases succeed per variant.
[Complete means/errors/allocation counters](focused-benchmarks.csv) retain the short-run uncertainty.

| Fields | Owner | Focused time before → after |
| --- | --- | ---: |
| 128 | Shape | 8.151 → 0.482 μs |
| 128 | Named structural | 9.284 → 0.467 μs |
| 4,096 | Shape | 256.847 → 15.229 μs |
| 4,096 | Named structural | 293.682 → 15.888 μs |

This is a 17–20× improvement at the warm semantic-validation boundary, not a whole-case CPU claim.
The primary 004010 process-median CPU estimate was 1.45% higher and its process-mean estimate 0.25%
higher, so four additional alternating matched pairs ran with unchanged binaries and runtime defaults.
All 16,384 additional cases passed. [All follow-up windows](followup-004010-windows.csv) preserve
both favorable and unfavorable results. Their unpaired aggregate process medians also remain slightly
higher (14.510 → 14.834 ms); this should not be represented as an end-to-end CPU improvement.

Across all eight 004010 pairs, the mean of paired differences in late-window mean CPU is
+0.021 ms (median paired difference −0.054 ms). Directions vary across runs, so these measurements
have not established a repeatable 004010 CPU improvement or regression. The measured allocation
reduction is stable. Primary and follow-up results remain separate rather than replacing the primary
result with a more favorable summary. In total 49,152 original X12 cases pass; fingerprints are unchanged.

## Reproduction

Build `eng/runtime-performance/value-conversion/ValueConversion.csproj` in Release, then run its
`ValueConversion.dll --metadata-validation-counters`. The `ShapeGraphMetadataValidationBenchmarks`
class measures the same existing validator through BenchmarkDotNet; fixture construction is excluded.
For frozen output comparisons use `--filter '*ShapeGraphMetadataValidationBenchmarks*' --job short --inProcess`
with each output copy so BenchmarkDotNet does not rebuild against the other variant's source.
Full-case comparison uses `docs/performance/query-identity-preparation/cpu_compare.py` with
`--changed-module Cohesive.dll --iterations 2048 --runs 4` and Ari's authoritative harness source.
