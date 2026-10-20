# Prepared inline portability checks

`PortableExecutionValidator` used to traverse the same immutable inline schema on every
value admission, allocating duplicate-name sets and diagnostic path storage even on success.
A 128-field flat contract allocated 30 KB per check; nested/collection contracts allocated 53 KB.
The original X12 behavior case repeatedly admits values against the same declared contracts.

## Boundary and design

Canonical `TypeRef` instances remain authoritative. Core's existing `WeakPreparationCache`
now records successful, graph-independent portability checks for exact `ObjectTypeRef`,
`ArrayTypeRef` and `EnumTypeRef` instances. Scalars stay direct. No public API is added.
The existing graph-owned `ObservationValidationPlan` prepares instance-matching links/indexes;
using it here would require a graph for graph-free schema checks and conflate two responsibilities.

Only successful inline walks with no named reference publish evidence. A per-walk dependency
counter prevents every ancestor of a named reference from publishing even after successful
resolution, including recursive named definitions. Named types and shapes still resolve from
that call's graph; missing and incompatible graphs keep their original diagnostics. Failures
and unknown consumer subclasses are not cached. Future graph-dependent type cases must retain
this boundary. Contract cardinality, presence and nullability, observation validity and concrete
compatibility remain checked on every call. External document integrity/admission is unchanged.

Retention is weak by declaration identity; nothing caches values, results, graphs, tenants or
source reads. Concurrent first calls may repeat the initial walk before publishing the same
successful proof; warm reads use the existing lock-free prepared read. This deliberately avoids
holding per-type locks across recursive validation or adding another preparation graph.

## Qualification

Apple M5 Max arm64, macOS 27.0.1, SDK 10.0.402/runtime 10.0.13, Release. The
[control](control-benchmarks.md) and [candidate](candidate-benchmarks.md) BenchmarkDotNet
0.15.8 ShortRun reports use frozen before/after binaries with the in-process toolchain,
three warmup and three measured iterations. They measure warm execution, not cold publication.

| 128-field workload | Schema allocation before → after | Full value allocation before → after | Full value mean before → after |
| --- | --- | --- | --- |
| Flat | 30,187 → 288 B | 33,412 → 3,360 B | 7.24 → 2.44 µs |
| Nested | 52,869 → 288 B | 55,943 → 3,360 B | 12.69 → 4.82 µs |
| Collection | 52,869 → 288 B | 59,295 → 6,433 B | 17.86 → 9.55 µs |

The fixed 288 B is ordinary validation-context allocation. Full value checks still walk instances.
A median-of-seven regression check caps schema validation at 1,024 B for flat and nested/array
128-field declarations; it excludes fixture construction and cold population. Other regressions
cover fresh graph/shape resolution, changed values/modifiers, copied/malformed declarations,
exact diagnostic paths, concurrent first access and weak retention. Existing immutability and
portable-value suites remain part of qualification.

## Original X12 cases

The unchanged original in-memory workflow test uses the shared immutable fixture plans from
Ari #241. Only `Cohesive.dll` differs between frozen variants: the control is an unchanged local
build of main at `2921c063`, and the candidate adds this validator change. Other packages remain
public alpha.150. These are local source qualification results, not a released-package upgrade.

Five alternating process pairs per source, each running 16 cases, passed 320 cases. A separate
no-tiering diagnostic used three alternating pairs per source and another 192 successful cases.
No tests, builds, benchmarks or traces overlapped the untraced measurements. Windows were cold,
second case and cases 9–16; report per-process medians, then the median across processes.

| X12 | Second-case allocation before → after | Second-case CPU before → after | No-tiering cases 9–16 CPU before → after |
| --- | --- | --- | --- |
| 004010 | 6.14 → 5.26 MB | 149.9 → 148.0 ms | 59.9 → 60.8 ms |
| 005030 | 6.35 → 5.48 MB | 169.8 → 170.0 ms | 68.9 → 67.7 ms |

Repeated allocation falls about **0.9 MB (14%)**. Cold allocation changes from 144.80 → 142.96 MB
and 145.84 → 144.16 MB; even first use repeats contracts within the workflow. There is **no
established end-to-end CPU improvement**: default-tiering later windows vary with JIT, and the
no-tiering diagnostic is roughly unchanged (one version 1.5% slower, the other 1.7% faster).
Do not extrapolate the synthetic benchmark gains to workflow or suite latency. CPU includes GC
and JIT; those overlap and must not be subtracted. Allocations are cumulative, not retained heap.

Both canonical graph fingerprints remain byte-for-byte identical. Compact [measurements](x12-measurements.csv)
and the [manifest](manifest.json) retain windows, binary/source hashes and workload provenance.

Validation completed: **4,954 Core tests passed / 41 existing infrastructure skips**;
**999 Ari engine tests passed / 18 existing emulator skips** against the exact measured
candidate Core binary; all **47 final focused portability/immutability checks passed**.
The Ari package cache was not modified; conformance used an isolated test-output copy.
