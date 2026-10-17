# Entity-transition fingerprint preparation

Fresh atomic commits were hashing their entire candidate state, result and execution evidence in the
commit constructor. Requests also hashed their creation intent even for ordinary updates. The shared
native commit protocol then compared full fingerprints when the provider returned the same immutable
candidate object. Those traversals added no evidence to the successful conditional write.

## Semantics and ownership

The existing native version/absence and unique-receipt fences remain authoritative for the write. No
new fence, replay identity, serializer, Ari cache or fingerprint profile is introduced. A native result
retaining the exact immutable commit needs no full-content comparison. A reconstructed result must
still match the complete commit fingerprint, and conditional-conflict lookup retains exact request,
creation-intent and commit comparisons. Exceptions and ambiguous acknowledgements still propagate.

Request fingerprints remain eager, preserving exact input-bearing locator identity. Creation-intent and
commit fingerprints prepare only when read. Two owner-identity caches reuse Core's existing
`WeakPreparationCache`: successful preparation publishes once across concurrent readers, failed
factories remain retryable, and keys/digests are collectible with their immutable owners. Cached
evidence does not enter record equality. Different or reloaded objects prepare independently.

Persisted stores still request the fingerprints they retain, and external admission still verifies
canonical content. This is primarily a saving for native in-memory success and unused creation intent;
it is **not** evidence that Cosmos/PostgreSQL/SQLite commits skip persistent integrity work. Fusing
mandatory persistent serialization and hashing remains separate work.

## Matched X12 counters

October 17, 2026; Apple M5 Max arm64, macOS 27.0.1, SDK 10.0.402/runtime 10.0.13, Release.
The original Ari `CanonicalProcessCommitsDataDefinedSpecThroughRepositoryTransitions(source, false, false)`
runs twice per fresh process, including fixture construction, preview, compilation, native in-memory
execution and assertions. The second call has fresh repositories/fixtures and shared application
preparation/pools; it is not a stabilized long-running production service.

Control uses public alpha.144. Candidate uses the identical frozen Ari harness and all identical runtime
assemblies except locally built `Cohesive.dll` (the shared preparation helper's friend access) and
`Cohesive.Storage.dll` (this change), based on merged Cohesive `dc7863c`. This is a **source-qualified
two-assembly candidate**, not a published package upgrade. See [manifest](evidence/entity-transition-fingerprints/manifest.json)
and [assembly hashes](evidence/entity-transition-fingerprints/runtime-differences.json).

Medians of five alternating control/candidate fresh-process pairs per source; MB means decimal MB.
Process CPU includes all process threads, GC/JIT/native work, and can exceed elapsed time. No task
build, test or benchmark overlapped counter or trace collection.

| Case | Control MB | Candidate MB | Control CPU ms | Candidate CPU ms | CPU saving |
| --- | ---: | ---: | ---: | ---: | ---: |
| 004010 cold | 180.00 | 157.63 | 1,953.2 | 1,811.2 | 7.3% |
| 004010 repeated | 28.20 | 28.27 | 566.4 | 311.7 | 45.0% |
| 005030 cold | 182.97 | 159.87 | 2,033.8 | 1,815.6 | 10.7% |
| 005030 repeated | 31.59 | 31.29 | 637.0 | 340.5 | 46.5% |

Repeated elapsed medians fall 518.8 → 303.3 ms / 589.9 → 330.6 ms (41.5% / 44.0%). Cold elapsed
falls 1,952.8 → 1,803.7 ms / 2,029.4 → 1,811.2 ms. Cold allocation falls 22.38 / 23.10 MB. Repeated
allocation changes are small (-0.24% saving for 004010, +0.94% for 005030); this is chiefly a CPU
improvement, not a warm-allocation breakthrough. [All 40 counters](evidence/entity-transition-fingerprints/counters.csv).
These local case results are not a whole-suite, CI, database-provider or UAT latency claim.

## Trace confirmation and remaining work

Three independent candidate traces per source use the existing Ari allocation markers and
`gc-verbose,dotnet-sampled-thread-time` profiles. The checked-in Ari analyzer applies the historical
alpha.139 closest-frame rules, assigning each sample once. All six traces have zero lost events,
zero missing managed-CPU stacks and zero missing allocation stacks. External thread samples are
excluded; they may include native work or waits. Managed sample shares are not shares of all process
CPU and must not be multiplied by process CPU counters to invent method timings.

No full entity-commit or creation-intent fingerprint samples remain in the repeated cases. Exact
request fingerprints remain: 53 of 660 / 72 of 784 managed samples (8.0% / 9.2%). The prior alpha.144
profile attributed roughly 30–36% to commit and 4–5% to intent pipelines; these historical shares
support attribution, while the alternating counters above establish the measured CPU change.

Remaining repeated allocations are still mostly observation projection (8.05 / 8.91 MB), hosted
decode/admission (4.31 / 4.81 MB) and other EDI compilation (5.22 / 6.22 MB). Sampling is approximate:
004010 run 2 includes more background allocation; use untraced counters for exact case totals.
Protocol JSON plus protocol hashes now accounts for about 19% of managed samples; projection and
hosted decode account for about 18%/16% and 16%/15%, respectively. Their increased relative share
does not establish an absolute regression after removing the larger hash pipeline.

Evidence: [CPU budgets](evidence/entity-transition-fingerprints/cpu-budget.csv),
[allocation budgets](evidence/entity-transition-fingerprints/allocation-budget.csv),
[fingerprint callers](evidence/entity-transition-fingerprints/fingerprint-cpu.csv),
[trace quality](evidence/entity-transition-fingerprints/trace-quality.csv), and
[instrumented counters](evidence/entity-transition-fingerprints/precise-counters.csv).
Raw traces are intentionally untracked; reproduction uses Ari's
`eng/runtime-performance/protocol-preparation` harness and its `analysis` tool.

## Isolated benchmark and regressions

`EntityTransitionFingerprintBenchmarks` runs the actual commit protocol with a synthetic native success
returning its exact candidate; network/database I/O and fixture/decision preparation are excluded.
Each operation constructs a new request and commit. The eager baseline explicitly reads the intent
and commit digests previously computed by constructors; it uses the new cache implementation, so it
is a mechanism comparison rather than a literal old-build benchmark. The X12 counters compare builds.
Payloads are 32 B, 16 KiB and 1 MiB entity binary values, not copies of Ari's graph workload.

| Payload | Eager mean | Fenced fresh mean | Eager allocated | Fenced allocated |
| --- | ---: | ---: | ---: | ---: |
| 32 B | 78.206 μs | 5.322 μs | 13.25 KiB | 2.56 KiB |
| 16 KiB | 101.597 μs | 5.325 μs | 13.15 KiB | 2.56 KiB |
| 1 MiB | 1,161.227 μs | 5.508 μs | 13.13 KiB | 2.56 KiB |

BenchmarkDotNet 0.15.8 ShortRun has three measured iterations and wide confidence intervals;
[raw report](evidence/entity-transition-fingerprints/benchmark.md) retains uncertainty. Run:

```bash
dotnet run -c Release --project src/Cohesive.Relations.Benchmarks -- --filter '*EntityTransitionFingerprintBenchmarks*'
```

Normal tests cover same-object native success, reconstructed matching/conflicting results, exact
retry conflicts, canonical receipt round trips, record equality/hash stability and concurrent reuse.
Golden request/intent/commit digests and canonical commit bytes were obtained independently from
published alpha.144 and match the candidate. Warm cached reads have a 64 B total ceiling across
128 pairs. Constructor-only tests use the median of seven batches with a 1,024 B per-commit ceiling
for both 32 B and 1 MiB states (544 B measured); request preparation and retained payload creation
are outside that narrow boundary. Timing thresholds stay out of the normal test suite.

Validation passed: 4,779 `Cohesive.Tests` tests (41 existing environment-gated skips), 233 SQLite
adapter tests, and 963 Ari engine tests (18 existing emulator skips) against the frozen candidate.
The detached Ari output initially lacked repository-relative fixture discovery; restoring those
fixture paths allowed the complete run to pass without changing its binaries. Native Cosmos and
PostgreSQL environment-gated tests were not qualified by these local runs. No package was published.
