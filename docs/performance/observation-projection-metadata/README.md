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

## 004010 CPU investigation before merge

The initial +2.2% median CPU result did **not reproduce** in four additional alternating process
pairs using unchanged binaries. All **16,384** cases passed (2,048/process; late window 1537–2048).
No owned builds, tests, traces or other benchmarks overlapped these untraced comparisons. CPU
includes JIT and GC. Processes are independent replicas; their 512 late cases are not 512 replicas.

| Pair | Baseline median CPU | Candidate median CPU | Change |
| --- | --- | --- | --- |
| 1 (candidate first) | 15.063 ms | 14.819 ms | -1.6% |
| 2 (control first) | 14.844 ms | 14.202 ms | -4.3% |
| 3 (candidate first) | 14.832 ms | 14.655 ms | -1.2% |
| 4 (control first) | 15.422 ms | 14.385 ms | -6.7% |

Across processes, median CPU is **14.953 → 14.520 ms (-2.9%)**; mean CPU is
**15.630 → 15.174 ms (-2.9%)**. Allocation remains **4.213 → 4.026 MB (-4.5%)**.
[All follow-up windows](cpu-investigation-windows.csv) retain cold, second, early and late results,
including sparse late JIT work (0.34–6.49 ms total per process window). The original unfavorable
result above remains in the record. This sample does not establish equivalence or a universal
speedup, and the cause of that original positive point estimate is not established.

A separate matched 2,048-case trace pair lost no events or late managed call-stack records.
Inclusive samples under `FromClrObjectProperties` or `FromPropertyValue` (counted once per stack)
fall **224/4,817 (4.65%) → 119/5,002 (2.38%)**. Custom-attribute leaf samples within that group
fall 100 → 42; reflection still occurs in nested runtime-converter dispatch and getters. These
are diagnostic managed samples, not CPU milliseconds. External samples are excluded; some leaf
symbols remain unresolved (70 → 33 within the group), so attribution is not complete.

Source review confirms the new warm metadata lookup replaces the prior readable-property cache
lookup; a published preparation slot returns without its preparation lock. Per-call values and
cycle checks remain fresh. No production source or runtime tuning change was warranted by this
investigation. [Identities and trace counts](cpu-investigation.json) record unchanged module and
harness hashes. Reproduce the follow-up with the comparator below, using
`--sources x12-004010-204 --runs 4 --candidate-first` instead of `--runs 2`.
The PR remains a draft and unmerged for review.

Reproduce with `docs/performance/query-identity-preparation/cpu_compare.py`, using
`--changed-module Cohesive.dll --iterations 2048 --runs 2`,
the frozen runtimes and Ari's original protocol-preparation Program.cs via `--ari-program`.
Microbenchmark: `ValueConversion --filter '*ClrProjectionMetadataBenchmarks*' --inProcess --job short`.
