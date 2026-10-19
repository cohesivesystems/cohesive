# Reuse canonical query identity sets

The original X12 workflow repeatedly creates query placement/stage metadata and fingerprints it.
Even when ID arrays were already immutable, sorted and unique, provenance construction grouped,
sorted and copied them, and physical/realization fingerprinting projected them through another
`Distinct`/sort/array pipeline. Alpha.151's repeated-case traces attributed roughly 0.5 MB to those
two normalization sites; the final end-to-end comparison below uses the upgraded runtime.

## Responsibility and invariants

Cohesive owns query artifact normalization and its binary fingerprint format. The existing
`LengthPrefixedFingerprintEncoding` now provides the shared ordinal string-set operation used by
both Relations writers. Exact ordinal order proves uniqueness, so canonical immutable input can
be encoded directly. Other input retains the existing set normalization. There is no cache,
thread-local state, pool, new public API, or fingerprint-format version change. Keys are pure
projections of immutable ID values. Ordering is UTF-16 ordinal, including supplementary Unicode.

Physical-stage provenance validates every entry before returning already-canonical immutable
storage. Noncanonical input sorts a new owned vector with its projected keys and checks adjacent
keys for duplicates; it never mutates caller storage. Invalid entries still take precedence over
duplicate errors. Required semantic attribution and qualified evidence checks remain intact.

This extends existing Core encoding and Relations-owned normalization instead of adding an Ari
adapter. Existing evaluator preparation caches do not solve the same problem across newly bound
evaluators; extending their lifetime requires preserving repository, tenant/partition, capability
and policy identity. This change does not cache bindings, results, parameters, authorization or
source reads. Physical execution still performs its existing integrity/admission checks.

## Qualification

The unchanged source baseline is main at `4f948efb` (merged #458). BenchmarkDotNet 0.15.8 uses
frozen before/after runtimes, its in-process toolchain, three warmups and three measurements with
a 250 ms target iteration time. Fixtures cover unary, 128 and bounded-large 1,024 identities,
canonical/reversed order and duplicate-heavy input. Duplicate fixtures contain two copies of the
same set before reversing; `Count` is the distinct count. Provenance construction measures both
canonical and reversed immutable arrays. Fixture creation is outside the measured methods.

The helper was simplified after a scratch-sort prototype doubled CPU for duplicate-heavy input.
The final implementation keeps the ordinary normalization fallback rather than adding pool
ownership machinery for a path not shown to dominate the real workload. Only final reports are
retained here.

Median-of-seven regression guards require zero warm allocation for direct set encoding at 128
and 1,024 entries with a reused output buffer, and cap canonical 128-node provenance construction
at 256 B including its retained result. The isolated unchanged provenance implementation fails
that guard at 26,048 B. The candidate retains the original immutable vector and allocates only
the 96 B provenance result. Exact token bytes and permutations/duplicates cover ordinal ordering;
checked-in pre-change digests pin realization, placement and physical-plan artifacts for the
shared federated query fixture at two policy sizes.

Local final validation: 4,976 Core tests passed / 41 existing infrastructure skips, and 1,137
Relations tests passed. One earlier full run hit the existing single-sample named-enum allocation
guard; its isolated and complete reruns passed without widening its zero-byte limit. The separate
release-reliability change applies the repository's seven-sample pattern to that guard.

## Results

All **512 original X12 cases passed**, as did all **999 Ari engine tests / 18 existing emulator
skips** against the exact measured candidate modules in isolated test output. Both graph
fingerprints and the pinned realization/placement/physical-plan digests are unchanged.

The frozen runtime uses verified public alpha.152 packages from merged publisher `8f7087a8`,
Ari application source `1c2b78c6` with alpha.152 pins, and shared fixture plans. Only local
`Cohesive.dll` and `Cohesive.Relations.dll` differ between variants. The baseline is unchanged
runtime source at `4f948efb`; the intervening release repair changes tests/scripts only. This is
source qualification, not publication of the new optimization. No package cache was modified.

Apple M5 Max arm64, macOS 27.0.1, SDK 10.0.402/runtime 10.0.13, Release. Five alternating
fresh-process pairs per source use 16 cases each. Three separate pairs per source disable
tiering. Aggregate per-process medians for the fixed window, then medians over processes.
No builds, tests, tracing or benchmarks overlap untraced workflow measurements. CPU includes
JIT and GC and neither is subtracted; bytes are cumulative allocation, not retained heap.

| X12 | No-tiering cases 9–16 allocation before → after | No-tiering cases 9–16 CPU before → after | Default second-case allocation before → after |
| --- | --- | --- | --- |
| 004010 | 5,059,136 → 4,483,284 B (−11.4%) | 32.43 → 31.58 ms | 5,180,960 → 4,604,712 B |
| 005030 | 5,287,760 → 4,706,808 B (−11.0%) | 36.23 → 35.13 ms | 5,400,232 → 4,835,656 B |

Default-runtime later allocation is 5,062,788 → 4,495,820 B and 5,294,148 → 4,719,612 B.
Default later CPU is **53.86 → 60.26 ms** and **52.73 → 63.50 ms**, despite second-case CPU
being nearly unchanged at 54.17 → 53.03 ms and 59.93 → 59.15 ms. No-tiering later windows
report zero JIT and point estimates about 2.6–3.0% faster. Those default-window regressions are
retained: there is **no established general end-to-end CPU improvement**. The allocation saving
is consistent; do not extrapolate the microbenchmark timing ratios to suite/runtime latency.

Default cold allocation changes from 143.19 → 141.26 MB and 144.08 → 142.40 MB; cold CPU
is roughly unchanged (1,561 → 1,542 ms and 1,572 → 1,562 ms). A cold workflow still creates
and hashes multiple query artifacts, so this is not a single-constructor cold measurement.

### Isolated mechanisms

[Control fingerprints](control-fingerprints.md), [candidate fingerprints](candidate-fingerprints.md),
[control provenance](control-provenance.md) and [candidate provenance](candidate-provenance.md)
retain all ShortRun estimates and confidence intervals, including reverse/duplicate workloads.

| 128-ID operation | Allocation before → after | Mean before → after |
| --- | --- | --- |
| Canonical ID-set hashing, including hasher/digest | 10,672 → 344 B | 5.01 → 3.39 µs |
| Reversed ID-set hashing | 10,672 → 10,672 B | 5.89 → 6.06 µs |
| Duplicate-heavy ID-set hashing | 10,672 → 10,672 B | 6.71 → 6.89 µs |
| Canonical provenance construction | 25,536 → 96 B | 9.03 → 0.46 µs |
| Reversed provenance construction | 25,536 → 2,192 B | 10.69 → 3.64 µs |

Noncanonical hashing retains its original normalization cost and broad timing behavior. The
canonical encoder regression checks exclude the retained hasher/digest and measure zero temporary
allocation with reused output. Provenance allocation includes its retained 96 B result. No timing
assertions or benchmark loops run in the normal suite.

The [manifest](manifest.json), [process windows](x12-process-windows.csv),
[graph fingerprints](graph-fingerprints.csv), and [pre-change artifact digests](pre-change-artifact-digests.csv)
record source/binary identity, measurement boundaries and reproducibility. Full counters and
frozen runtimes remain local under `/tmp/query-id-x12`.
