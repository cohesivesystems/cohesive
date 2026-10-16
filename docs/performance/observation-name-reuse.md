# Property-name reuse during observation projection

The original X12 Process tests still project complete shape graphs through JSON into observations.
`SmallObservationFields.Read` and the large-object reader decoded every property name independently,
so 1,024 rows with the same field name retained 1,024 equal strings. This change reuses those strings
within one conversion. Graphs and protocol documents remain authoritative; no Ari model/cache is added.

## Scope and invariants

Core extends `ObservationValue.FromJsonElement` and both existing object storage readers.
Graph-qualified ordinal materializers already serve schema-bound values; this generic JSON acquisition
boundary has no shape evidence to use them. The canonical writer's UTF-8 name buffers serve ordering
without retaining CLR names; this reader must produce owned dictionary keys. A global interning cache
was rejected because it could retain arbitrary consumer names. The bounded traversal helper belongs
in Core, with no new public API or competing shape/field catalog. One traversal
owns a direct-mapped, 256-entry pooled table containing raw JSON property references and their owned
strings. Raw UTF-8 name equality is checked before reuse; hash collisions only cause another decode.
Different escape spellings may decode separately, preserving the same ordinal field semantics.
The same recursive walk handles values, number precision, ordering and last-property-wins behavior.
Declared serializer projection continues rejecting duplicates before this walk.

The table is cleared on success and exceptions, so no parser or tenant payload survives in the pool.
There is no global string interning, no persistent cache identity, no validation evidence reuse and no
wire/fingerprint change. Ownership is a single scoped reference object with idempotent disposal;
there are no copyable buffer leases. Different traversals, including concurrent ones, own separate tables.
Payloads below 1 KiB bypass preparation. A traversal with no reuse in its first 32 name lookups
returns its table and resumes ordinary name decoding. This is an initial no-hit heuristic, not a
guarantee that every mixed workload benefits. Table memory is bounded,
although strings that the result itself requires still scale with the input.

A source-instance encoding-cache prototype was measured first and discarded: the original X12 cases
did not reuse CLR graph instances at that boundary, so it provided no meaningful allocation saving.
The first name-reuse prototype saved allocations but regressed tiny-input latency; the small-input
bypass addresses that measured cost. Neither discarded prototype is part of this change.

## Measurement boundary

October 16, 2026, Apple M5 Max arm64, macOS 27.0.1, SDK 10.0.402/runtime 10.0.13, Release.
Control: official alpha.143 release artifacts from merged #443 at
`341feaa9f35a9f20afc4c6c1058ee7fc4d8840c9`, publication run
[38956089702](https://github.com/cohesivesystems/cohesive/actions/runs/38956089702).
The control Core DLL is checked byte-for-byte against that release's NuGet artifact.
Candidate runtime: `2915bbf1`, matching Core test closure and generator. Ari is merged #227 plus its alpha.143
version/acceptance-pin upgrade `ef6245885adfbf07c5a0628eefcc8a330ad29815` (merged as Ari #228). No runtime product source changes distinguish control and candidate.
Five alternating fresh-process pairs per source run the existing
`CanonicalProcessCommitsDataDefinedSpecThroughRepositoryTransitions(source, false, false)` twice.
Repeated cases share static preparation/pools but create fresh repositories and fixtures.
`GC.GetTotalAllocatedBytes(precise: true)` measures whole-case cumulative allocation, not retained heap.
No local builds/tests overlap final counters, traces or matched microbenchmarks.

## Results

| Original X12 case | Alpha.143 repeated MB | Candidate MB | Reduction |
| --- | ---: | ---: | ---: |
| 004010 204 | 43.39 | 41.18 | 2.21 MB / 5.1% |
| 005030 204 | 49.20 | 46.65 | 2.56 MB / 5.2% |

Cold medians fall from 185.02 to 182.85 MB and 189.66 to 187.23 MB (1.2% / 1.3%).
[Raw counter pairs](evidence/observation-name-reuse/counters.csv) contain all 40 observations.
This establishes cumulative allocation savings, not retained-heap, CI-duration or service-latency gains.

Seven-run warm micro medians for 1,024 one-field rows fall from 172,088 to 131,200 bytes for 8-character
repeated names, and from 679,992 to 131,696 bytes for 256-character names. Unique names retain their
required independent strings and add 32 bytes per large conversion for traversal state. Single-row
8/256-character cases stay at 224/720 bytes. The fixed 200-byte-per-row allocation guard for repeated
long names has about 36% headroom and would fail the previous 664-byte-per-row path.
[Control](evidence/observation-name-reuse/micro-control.csv),
[candidate](evidence/observation-name-reuse/micro-candidate.csv).

BenchmarkDotNet ShortRun (in-process, three measured iterations) reports repeated 1,024-row projections
at 35.85 → 33.17 μs for short names and 70.17 → 57.20 μs for long names. Unique-name projections retain
about 4–5% CPU overhead (35.84 → 37.45 μs / 70.23 → 73.02 μs); tiny conversions add about 4–7 ns
without allocation growth. These tradeoffs remain explicit: the cache is a measured win for repeated
schema names, not a universal speedup. [Control](evidence/observation-name-reuse/benchmark-control.md),
[candidate](evidence/observation-name-reuse/benchmark-candidate.md). The larger unique-name
regression in the initial prototype was rejected; the early exit and inline ordinary path bound it.

Both complete X12 graph digests match the release control exactly.
[Digests](evidence/observation-name-reuse/graph-hashes.csv),
[module identities](evidence/observation-name-reuse/assemblies.csv).

Fresh EventPipe traces have no missing stacks or lost events. Sampled repeated totals are within
0.34% of their own precise child counters. Existing alpha.139 rules and the alpha.141 summarizer are
unchanged. The candidate's disjoint categories are approximately:

| Category | 004010 MB | 005030 MB |
| --- | ---: | ---: |
| Observation projection | 9.05 | 10.40 |
| Protocol JSON | 9.07 | 11.04 |
| Other EDI compilation | 6.51 | 7.68 |
| Hosted typed decode | 6.67 | 7.77 |

The [complete budgets](evidence/observation-name-reuse/allocation-budget.csv) include setup/other,
catalog, query and storage categories. [Trace quality](evidence/observation-name-reuse/trace-quality.csv).
Call-site estimates overlap categories: annotation-map decoding alone still accounts for about 4.26 MB
in the 004010 trace. First tree construction, typed graph reconstruction and protocol JSON remain
larger representation boundaries; this change does not remove them or measure SQLite rewrites.

Core: 4,761 tests passed, 41 configured-environment skips. Ari: 1,352 tests passed, 18 emulator skips.
Regressions cover escaped/Unicode names, duplicate last-wins behavior, malformed keys, independent
parallel traversal, parser release after both success and failure, and an allocation ceiling.


## Reproduction

Build Core's test closure, generator and `eng/runtime-performance/value-conversion` in Release.
`ValueConversion.dll --property-name-counters` reports seven-run warm medians.
`--filter '*PropertyNameProjectionBenchmarks*' --job short --inProcess` runs the checked-in benchmark;
freeze its complete output and replace only the control Core DLL with the official release DLL.
Build/freeze Ari's protocol-preparation harness once with the public packages and once with the documented
`UseSourceAssemblies.targets`, `CohesiveSourceAssemblyDirectory` and matching `CohesiveCodeGenCliDll`.
Use `eng/runtime-performance/durable-bookkeeping/compare-ari.py --runs 5` to compare them.
