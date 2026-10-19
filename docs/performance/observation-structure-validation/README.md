# Reuse structural observation evidence

A Process step repeatedly admits the same immutable spec and compiled graph through preview,
query and transition boundaries. Inline schema preparation already avoids repeated declaration
walks, but each admission still walked every observation field and element, validated scalar
payloads, updated diagnostic cursors and checked JSON projection requirements. In the published
alpha.151 X12 profile, `PortableExecutionValidator` accounted for about 57–60% of sampled managed
CPU. That sample percentage is attribution, not an estimate of recoverable wall time.

## Ownership and admission

Core's `ObservationValue` remains the authority. Its constructors snapshot ordinary caller
collections; framework-owned fields and immutable collections preserve their immutable storage.
The validator uses the existing weak preparation cache to retain successful structural evidence
by that exact composite storage identity. The evidence contains only maximum relative container
depth, JSON-normalization requirements and a saturating node count. It retains no payload,
graph, contract, diagnostics, tenant or source result. Concurrent first readers may duplicate a
walk; publication follows completion, and no slot lock spans recursion. Failures are never
published. Weak keys permit collection with the owning payload.

This extends the existing Core preparation mechanism rather than introducing an Ari cache.
`ObservationValidationPlan` remains responsible for graph-owned type matching; a structural
proof cannot replace that plan or a call's contextual admission. Type compatibility, contract
cardinality/presence/nullability and graph/shape resolution still run for every invocation.
External/persisted decoding creates new storage and requires fresh structural validation.
Immutable updates create new ancestor storage, while unchanged children may reuse their proofs.

Proofs keep exact relative depth and byte/invalid-UTF-16 requirements even when the first check
did not request CLR projection. Reparenting a checked subtree or changing the JSON depth option
therefore cannot suppress the existing fallback. Normalization is recorded, not performed by
this optimization. Invalid subtrees still produce current, deterministically ordered paths.

Only subtrees with at least 32 visited nodes publish entries. Small one-shot rows avoid entry
allocation; they still perform the full structural walk. This is an internal preparation policy,
not a validation limit. First-use costs include collecting the summary and weak publication;
repeated-use gains must be considered alongside that cost.

## Reproduction and measurement boundary

Run `ObservationStructureBenchmarks` in `eng/runtime-performance/value-conversion` for flat,
nested and collection-heavy payloads at 8, 128 and 1,024 fields. `Repeated` excludes construction
and primes successful admission in setup. `CreateAndValidate` includes construction and first
validation of fresh storage on every operation, including any preparation allocation. These are
different workloads and their times must not be subtracted to infer a validation-only cost.

Before/after reports use frozen binaries and BenchmarkDotNet's in-process toolchain to avoid
rebuilding the baseline from candidate source. The original X12 behavior test is unchanged:
`CanonicalProcessCommitsDataDefinedSpecThroughRepositoryTransitions`, versions 004010 and 005030.
Both variants use Ari main at `1c2b78c6`, public alpha.151 dependencies and shared fixture plans.
Only the local Core DLL differs: baseline source `ce93b53f`, versus this change. This qualifies
source assemblies; it is not a public-package adoption or native scheduler performance result.

Apple M5 Max arm64, macOS 27.0.1, SDK 10.0.402/runtime 10.0.13, Release. Five alternating
fresh-process pairs per source run 16 cases each. A separate no-tiering diagnostic uses three
pairs per source. Report each process's median for the fixed case window, then the median over
processes. CPU includes GC and JIT; neither is subtracted. Allocated bytes are cumulative and
include preparation; they are not retained heap size. Builds, tests, tracing and benchmarks do
not overlap the untraced X12 measurements.

## Results

All 512 original X12 cases passed, with both graph fingerprints unchanged. The separate
no-tiering cases 9–16 establish a recurring CPU reduction in addition to the default-runtime
second-case result:

| X12 | No-tiering later allocation before → after | No-tiering later CPU before → after | Default second-case CPU before → after |
| --- | --- | --- | --- |
| 004010 | 5,133,428 → 5,060,532 B (−1.4%) | 62.32 → 33.47 ms (−46.3%) | 158.10 → 58.11 ms |
| 005030 | 5,353,564 → 5,283,776 B (−1.3%) | 73.12 → 37.68 ms (−48.5%) | 181.00 → 63.91 ms |

Default-runtime later allocations are 5,149,752 → 5,071,152 B and 5,381,336 → 5,291,652 B.
Default later CPU is 88.08 → 56.50 ms and 101.80 → 55.80 ms, but those windows still include
runtime tiering effects. The no-tiering later windows report zero JIT time in both variants.
Default cold CPU is 1,781 → 1,689 ms and 1,816 → 1,706 ms; cold allocation is 143.06 → 142.99 MB
and 143.89 → 144.15 MB. There is no material cold allocation reduction. Even a cold workflow
reuses payloads internally; this does not imply that a single first admission becomes cheaper.

### Isolated repeated and first-use work

The [control](control-benchmarks.md) and [candidate](candidate-benchmarks.md) BenchmarkDotNet
reports use three warmups and three measured iterations with a 250 ms target iteration time.
The JSON contract isolates structural work; typed field matching is intentionally absent here
and still runs in actual typed admissions. All repeated fixtures allocate 288 B for the validation
context (one baseline large result rounds to 289 B). Shallow baseline validation was already
allocation-constant: the main benefit here is avoiding CPU work, not eliminating per-field objects.

| 128-field shape | Repeated mean before → after | Create + first-validation mean before → after | Create + first-validation allocation before → after |
| --- | --- | --- | --- |
| Flat | 737.6 → 37.5 ns | 5.18 → 5.95 µs | 30,109 → 30,166 B |
| Nested | 2,213 → 37.9 ns | 14.51 → 17.89 µs | 117,162 → 117,215 B |
| Collection | 4,625 → 37.5 ns | 19.22 → 23.00 µs | 139,693 → 139,746 B |

This is a reuse optimization with measurable first-use costs. Fresh 8-field collection payloads
rise from 1.25 → 1.76 µs. Below the 32-node threshold, repeated 8-field flat and nested payloads
also regress from 84 → 134 ns and 172 → 300 ns because they compute evidence but do not publish
it. Their allocation remains unchanged; constructing and validating these fresh small values
has point estimates of 403 → 451 ns and 921 → 1,058 ns. Some ShortRun confidence intervals are
wide, particularly fresh nested and large collection fixtures; consult the full reports instead
of treating each point estimate as an exact ratio. Small one-shot inputs and live proof metadata
are the residual tradeoff for this deliberately bounded preparation policy. The X12 paired result
supports this choice for reused large payloads, not a claim that every foundational workload improves.

### Regression protection

Median-of-seven allocation checks cover flat, nested, collection and bounded-large observations,
plus a 128-level shared subtree. The ceiling is 1,024 B after preparation. On the unchanged Core
binary the deep case fails at **8,496 B**, while the candidate passes; this protects the removed
walk and diagnostic cursor growth, rather than only an allocation difference against an old helper.
Other tests exercise caller dictionary/array snapshots, immutable patches, changed contracts,
malformed diagnostics and paths, depth changes and reparenting, bytes, invalid UTF-16 names and
values, concurrent first checks and weak retention. No timing test or benchmark runs in the
normal unit suite.

The [process windows](x12-process-windows.csv), [graph fingerprints](graph-fingerprints.csv),
and [manifest](manifest.json) retain source/runtime identities and reproducible measurement
boundaries. Full raw counters and frozen runtimes remain local under
`/tmp/structural-validation-x12`; the checked-in CSV preserves one row per process/window.

Validation: **4,969 Core tests passed / 41 existing infrastructure skips**, and **26 focused
preparation/projection checks passed**. Ari's **999 engine tests passed / 18 existing emulator skips** against the measured candidate Core DLL in an isolated
test-output copy. Neither the source package cache nor published dependencies were modified.
