# Small-input ObservationValue parsing investigation

Measured October 18, 2026. This investigation did **not** produce an accepted production
optimization. The existing reader, bounded cache policy and object builder are retained.

## Question and authority

The preceding string-reuse increment reported approximately 15% slower flat JSON parsing.
The task was to recover small-input throughput while preserving the demonstrated large-input
allocation savings. JSON interpretation remains authoritative in `ObservationJsonReader`;
container ownership and duplicate-name handling remain in `ObservationJsonContainers`.
`ObservationJsonPropertyNames.TokenScope` owns bounded per-traversal reuse, not semantic identity.
Typed hydration, failure cleanup, owned strings, ordinal names, last-property-wins plain objects,
and exact tagged wire behavior must remain intact.

Source inspection identified per-string eligibility checks, mutable activation counting, and
scope setup as possible costs. Four experiments tested simpler startup or comparison code.
These are hypotheses evaluated by timing, not a CPU sample profile proving a single cause.

## Matched results

BenchmarkDotNet 0.15.8, .NET 10.0.13, Apple Arm64; six warmups, eight 250 ms iterations,
in-process toolchain. Workloads and the benchmark assembly/dependency closure were fixed.
Only Core was substituted. Builds and tests did not overlap the timed runs. Each row is one
fresh benchmark process; small differences are insufficient evidence of a repeatable win.

| Implementation | Flat | Nested | 128 records | 4096 records |
|---|---:|---:|---:|---:|
| Current accepted implementation | 605.4 ns | 1.737 μs | 32.628 μs | 1.298 ms |
| Decode first 64 requests before eligibility checks | 624.3 ns | 1.920 μs | 32.609 μs | 1.322 ms |
| Same, force the later cache path out of line | 615.4 ns | 1.702 μs | 30.561 μs | 1.318 ms |
| Use the reader byte position for a 1 KiB prefix | 594.3 ns | 1.716 μs | 34.805 μs | 1.304 ms |
| Direct static ordinal equality in container scans | 600.6 ns | 1.697 μs | 32.882 μs | 1.305 ms |
| Preserved library from before token string reuse | 567.4 ns | 1.777 μs | 30.191 μs | 1.645 ms |

The matched current flat-input gap is **6.7%**, rather than the earlier report's 15%. This is a
new measurement under a matched setup, not a production improvement. The byte-position
experiment's 1.8% flat gain left most of the gap and had 6.7% slower collection parsing in its
run. It also changed activation policy and would need explicit handling of deferred readers'
relative byte coordinates before consideration for tagged decoding. The direct equality change
saved less than 1% on flat input. Neither result justifies accepting a production change.
The outlined counter path helped collection timing but regressed the primary flat workload.

Allocation evidence still supports retaining bounded reuse:

| Workload | Before reuse | Current |
|---|---:|---:|
| Flat | 1,680 B | 1,680 B |
| Nested | 4,720 B | 4,720 B |
| 128 records | 60,569 B | 34,987 B |
| 4096 records | 1,950,025 B | 1,028,501 B |

In this comparison, current large parsing was about 21% faster with 47% less allocation.
Pool/GC behavior and process-randomized cache collisions affect large-input results. Do not
interpret these local timing means as latency guarantees or the variation between prior reports
as an additional optimization.

## Retained work and verification

Four new regression cases protect the investigation's constraints: warmed arrays of 1, 16 and
64 short strings allocate only required owned result storage, and a long-text prefix followed
by repeated short strings preserves reuse and ownership after the caller's buffer is overwritten.
Existing conformance, malformed-input, typed-hydration and tagged-decoder tests are rerun with
these additions: **58 tests passed**. No experimental production source is retained.

The [raw evidence](evidence/observation-values-small-input/) includes all six reports, a manifest
for the accepted control, historical reference and final rejected direct-equality candidate, and
final test output. Earlier rejected candidate assemblies were overwritten during experimentation;
their reports are exploratory evidence, not fully archived release candidates. The original
implementation's source and tested binary are verified unchanged after restoration.

## Next decision

The small-input throughput issue remains open. A further attempt should use native CPU/JIT
profiling or a broader small-input benchmark matrix before introducing another reader strategy
or cache mode. The present evidence does not justify a parallel semantic parser, duplicated
kind dispatch, a global string interner, or removing the large-input allocation improvement.
For cache-policy work, keep short/unique/escaped/segmented/large-text workloads and whole
checkpoint recovery in the comparison; an isolated flat win is insufficient.

Reproduce the comparison with one frozen benchmark closure and substitute only Core:

```text
dotnet Cohesive.Relations.Benchmarks.dll --filter '*ObservationValuePipelineBenchmarks.ParseDirect*' --inProcess --warmupCount 6 --iterationCount 8 --iterationTime 250 --artifacts <results>
```

## CPU/JIT follow-up

The [subsequent profiling investigation](observation-values-cpu-jit.md) distinguishes isolated
flat parsing from collection-trained execution and records actual JIT inlining decisions.
The flat fixture crosses the eight-field inline-storage boundary while collection records do
not. The observed gap is sensitive to those workload shapes and compilation history; neither
forced cleanup nor cache-helper outlining established a worthwhile production improvement.
