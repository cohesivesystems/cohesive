# Prepare CLR projection metadata once

`ObservationValue.FromObject` already cached readable properties, but still reconstructed JSON attributes,
resolved property names and inspected declared types/sequences for every object and property.
Core now prepares one immutable projection array per exact runtime Type with its existing
`WeakPreparationCache`. Metadata uses weak identity retention, shared first preparation and retryable
failure. Inputs, getter values, cycle tracking and fresh immutable outputs remain per invocation.
No new result cache, public API, serializer profile or fingerprint format. Existing declared converter
and immutable projection paths remain authoritative; ignored members and property order are preserved.

`ShapeTypeInspector` was evaluated: its shape/nullability metadata serves different semantics. This
projection-specific plan reuses its readable property discovery rather than expanding a public shape
contract or preparing an unrelated graph. Reflection getter calls and scalar boxing remain.

## Qualification

All 5,015 Core tests and 1,137 Relations tests pass. All 999 Ari engine tests pass against the
candidate override (18 existing emulator skips). Fresh-value mutation/concurrency regression and the existing converter, cycle,
null, bytes and polymorphism tests protect semantics. Fixed allocation guards include retained output
and fail for both baseline fixtures (128 rows: 254,216 B; 1,024 rows: 2,031,880 B).

The standalone row benchmark includes returned observations; fixture creation and first preparation
are outside its measured loop. At 1,024 rows it goes from 1,896.49 KiB / 3.351 ms to 721,191 B / 0.864 ms.
These are microbenchmark estimates, not a complete-workflow claim. [Before](before-benchmark.md) and
[after](after-benchmark.md) retain all sizes and timing intervals.

## Original X12 workflow

Verified official alpha.154 dependencies; both variants replace only their local Core module. All
other DLLs and extended harness bytes match. No shared package cache is changed. Two alternating
process pairs per source, 2,048 cases/process, 16,384 successful cases. Fixed late window 1537–2048:
CPU includes GC and JIT. Report median within process then median across processes; two process pairs
are limited CPU evidence, not a latency guarantee. Retain every favorable and unfavorable window.

| X12 | Allocations before → after | Median CPU before → after | Mean CPU before → after |
| --- | --- | --- | --- |
| 004010 | 4.214 → 4.025 MB | 15.111 → 15.447 ms | 15.490 → 15.785 ms |
| 005030 | 4.399 → 4.209 MB | 16.327 → 16.006 ms | 16.993 → 16.717 ms |

CPU point estimates are mixed: 004010 +2.2%, 005030 -2.0%. The allocation reduction is consistent;
no general CPU-speed claim is made. Do not hide the unfavorable 004010 result behind the microbenchmark.

[All windows](case-windows.csv) include cold, second, early 9–16 and late cases with JIT totals.
Graph fingerprints match the public baseline. [Manifest](manifest.json) identifies source, modules,
public release and raw counter hashes. This prototype has not been published.

Reproduce with `docs/performance/query-identity-preparation/cpu_compare.py`, using
`--changed-module Cohesive.dll --iterations 2048 --runs 2`,
the frozen runtimes and Ari's original protocol-preparation Program.cs via `--ari-program`.
Microbenchmark: `ValueConversion --filter '*ClrProjectionMetadataBenchmarks*' --inProcess --job short`.
