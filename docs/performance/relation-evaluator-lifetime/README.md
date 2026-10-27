# Relation evaluator preparation lifetime

A long-lived immutable compilation request must not keep discarded evaluators, source readers or
repositories alive. The fix removes the evaluator capture from `PreparationCacheEntry`; callers pass
it transiently to `GetPreparation` instead. No cache, public API, admission rule or serialization
contract is replaced. This is a retention fix, not a claim of substantially lower allocation per case.

## Concrete failure and ownership

A host shares one acquisition query request but creates an evaluator for each repository/tenant
placement. Previously the evaluator's instance `ConditionalWeakTable` created values containing a
reference back to that evaluator. Because the request key remained live, its dependent handle kept
that value alive; the value retained the evaluator and thus the table itself. The cycle also retained
the placement factory, source catalog, repositories, entity snapshots and their decoded projections.

An offline ClrMD 4.2.751201 root analysis of the original X12 case established this path:

`static acquisition → compilation request → preparation entry → evaluator → source catalog → repository → observation → projection cache → portable document`

The [full type chain](retaining-chain.txt) is retained without payload data or ephemeral addresses.
The projection cache was retaining projections for **still-live owners**. Its weak ownership was not
the defect, so its design is unchanged. A correct weak-key cache can still participate in a retention
bug if its value reaches the owner of that cache while its key lives elsewhere.

`PreparationCacheEntry` now stores only the request and immutable prepared artifacts. The evaluator
is supplied only during a factory call. Successful preparation remains shared per evaluator; failed
factories remain retryable; exact request-scoped compilation and realization remain shared across
evaluators. Parameters, source reads, placement context and results remain invocation/evaluator scoped.
The existing `PreparationSlot` already supplies safe concurrent publication, so no new mechanism is needed.

## Regression coverage

Two new cases keep the compilation request live while discarding an evaluator and captured source-reader
state. Both must become collectible after full collections. The cases cover successful preparation and
a thrown placement factory. Both fail against the original implementation and pass with the fix.
Existing concurrent-first-use, retry, repeated-preparation, cross-evaluator sharing and result-isolation
coverage remains in the same suite.

Validation:

- Relations: **1,139 passed**, no skips.
- Cohesive.Tests: **5,020 passed**, 60 existing externally gated skips.
- Original X12 workflow: three alternating process pairs/source for collection diagnostics and normal
  counters, plus independent heap processes and extended runs; all passed.
- FIX: two original workflow cases per variant passed.
- Both X12 graph fingerprints match the published alpha.156 values exactly.

## Matched retained-memory evidence

These are local **source-qualified** measurements, not a published package or Ari adoption. Both
variants use identical frozen Ari test/runtime binaries and the committed EDI path cleanup. Only
`Cohesive.Relations.dll` differs, built from the same source path, SDK 10.0.402 and Release configuration.
The harness reports its actual entrypoint, loaded Relations path and SHA-256; those identities are
checked against each variant before measurement. Both frameworks are pinned to .NET 10.0.5 on Arm64
macOS 27.0.1. Runtime patch roll-forward is disabled for this diagnostic comparison.

Independent managed-heap graphs after full collections:

| X12 source | After 64 cases, before | After 64 cases, after | Reduction |
|---|---:|---:|---:|
| 004010 | 73.95 MB | 42.23 MB | 42.9% |
| 005030 | 78.64 MB | 42.41 MB | 46.1% |

The original graphs contain 64 document-sized buffers below portable projection-cache entries:
**25.77 MB / 30.32 MB**. The fixed graphs contain **zero** large buffers on that path. The compiler's
bounded source preparation and normal pooled buffers still retain their owned inputs intentionally.
The fixed offline root analysis no longer finds the request → evaluator chain; the pinned seed
catalog's portable document remains reachable as expected.

[Heap summaries](heap-summary.csv) use actual graph-node sizes, not representative object sizes from
the gcdump text report. These are retained managed graph bytes, not process RSS or total allocation.
One snapshot per source/variant/case-count establishes mechanism and scale, not a distribution.

Three-process collection diagnostics tell the same growth story. Median `GetTotalMemory` after
64 cases falls **119.99 → 72.85 MB** for 004010 and **127.39 → 73.98 MB** for 005030.
[All medians and ranges](retention.csv) are retained. A separate one-process 004010 run through
512 cases ends at **380.34 MB before / 77.00 MB after**, with the fixed measurements fluctuating
rather than accumulating one repository per case. [Extended rows](extended.csv) include the GC
heap and fragmentation counters. These approximate live-byte counters differ from gcdump totals;
they must not be substituted for one another or extrapolated as exact production memory budgets.

## Allocation and timing limits

Normal runs, without forced collections, use three alternating process pairs/source with 64 cases
per process. Taking the median within cases 33–64, then the median across processes:

| X12 source | Before allocated MB/case | After allocated MB/case |
|---|---:|---:|
| 004010 | 3.485 | 3.482 |
| 005030 | 3.580 | 3.565 |

Allocation is essentially unchanged; the improvement is releasing completed physical contexts.
[Per-process allocation summaries](allocation.csv) retain the bounds. This early window is not the
older 1537–2048 steady-state qualification, and no CPU or latency improvement is claimed.

[Manifest](manifest.json) records source/module identities, tests and raw-file checksums. Raw artifacts
remain under `/tmp/projection-cache-lifetime`; large heaps are not committed. Initial runs whose
symlinked entry assembly bypassed the replacement DLL are excluded completely. Runtime identity
verification is necessary in addition to hashing files present in an output directory.

The next independent cold-allocation target is Ari's eagerly initialized seed catalog. Publishing
this Cohesive fix and adopting its package version remain separate deliverables; no Ari-local cache
or production assembly override is introduced.
