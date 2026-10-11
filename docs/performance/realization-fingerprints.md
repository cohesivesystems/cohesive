# Streaming relation realization fingerprints

Relations' realization, requirement-set and bound-realization fingerprints now stream the existing
length-prefixed binary canonical form through Core's `Sha256BufferWriter`. The existing append
walks, normalization, ordering, attribution and profile versions remain unchanged. UTF-8 text,
big-endian lengths/integers, null markers, booleans and sequence order remain authoritative. The
writer pools temporary storage and retains no complete canonical byte output. Its capacity can
grow to the largest individual requested scalar; it is not a constant-memory guarantee for an
unbounded string. No admission or contextual evidence is cached.

Realization ordering keys use Core's existing `ValueStringBuilder`, starting with 256 stack
characters and using pooled fallback. Invariant UTF-16 length prefixes are formatted into a
small stack buffer. The sequence is still enumerated once, null and empty remain distinct, and
only the resulting key string is retained. A `finally` returns fallback storage if enumeration
throws. These are existing reusable Core mechanisms, extending the owning Relations algorithms;
no Ari-local abstraction or alternate canonical format is introduced.

## Qualification

Golden realization, requirement-set and bound digests were captured from unchanged parent
c5e9c615 before switching writers. Existing semantic tests cover declaration order, culture,
separator-bearing identifiers, changes to relevant contracts and excluded descriptions.
Allocation guards exercise a large pooled key and a 128 KiB scalar fingerprint. The key guard
permits only the resulting string plus small enumerator overhead; the fingerprint guard rejects
full-output buffer growth. This does not promise allocation-free normalization or final results.

The full Relations suite passes 1,111 tests. Core/integration qualification passes 4,423 tests
with 33 skips. Both allocation guards fail against the unchanged parent and pass on final source:
old allocation is 84,160 bytes for a 17,170-character key and 402,680 bytes for a large-scalar
fingerprint. This proves the checks protect the removed temporary representations.

Ari source-assembly qualification passes 852 tests with 18 skips. Twenty precompiled test-wrapper
failures lack the newer `IEntityRepository.IdentityField` member; the exact failure set matches
the unchanged-parent compatibility qualification. There are no new failures. This remains a
source-module run, not a successful published-package adoption.

## Matched Ari case

October 11, 2026, Apple M5 Max/macOS arm64, .NET SDK 10.0.201/runtime 10.0.5, Release.
Ari b5d64ae (#210), Cohesive parent c5e9c615 (#415), matched source dependency assemblies.
Only Relations changes. Each fresh process executes the complete normal X12 004010 public
compile-shape test, then repeats it in-process. `GC.GetTotalAllocatedBytes(precise: true)` includes
catalog, seeds, preview, execution, assertions and cleanup. No concurrent build/test/measurement
work ran during either case.

| Variant | Cold allocated bytes | Repeated allocated bytes |
|---|---:|---:|
| Direct projection parent | 519,551,560 | 253,877,584 |
| Streaming realization fingerprints and pooled keys | 488,410,992 | 222,703,200 |

This saves 31.1 MB cold (6.0%) and 31.2 MB repeated (12.3%), using decimal MB.
[Measurements](evidence/realization-fingerprints/ari-case.csv) preserve single elapsed samples;
no end-to-end latency improvement is established. This is source qualification, not published
package adoption, retained-heap or UAT evidence.

## Opt-in benchmark results

BenchmarkDotNet 0.15.8 ShortRun (three measured iterations), identical benchmark source on
parent and final implementations, runs separately with no concurrent CPU work. Allocated KB
uses 1024 bytes. Normalization remains inside the fingerprint measurement.

| Workload | Parent allocation | Final allocation | Parent mean | Final mean |
|---|---:|---:|---:|---:|
| Fingerprint, 1 requirement | 9.95 KB | 8.05 KB | 1.632 μs | 1.677 μs |
| Fingerprint, 128 requirements | 254.31 KB | 190.31 KB | 56.944 μs | 60.196 μs |
| Fingerprint, 1,024 requirements | 1,988.25 KB | 1,476.11 KB | 561.440 μs | 502.780 μs |
| Key, 4-character field | 328 B | 120 B | 44.48 ns | 45.78 ns |
| Key, 128-character field | 1,160 B | 368 B | 74.99 ns | 55.79 ns |
| Key, 4,096-character field | 25,008 B | 8,312 B | 754.28 ns | 428.67 ns |

Fingerprint allocations fall 19–26%; key allocations fall 63–68%. Timing is mixed for smaller
fingerprints: the 128-requirement mean rises 5.7%, with a wide final confidence interval. These
short runs support the allocation result, not a universal throughput improvement. The largest
fingerprint avoids full-output buffer growth and associated large-object collections.
[Parent fingerprint](evidence/realization-fingerprints/parent-fingerprint.csv),
[final fingerprint](evidence/realization-fingerprints/final-fingerprint.csv),
[parent key](evidence/realization-fingerprints/parent-sequencekey.csv) and
[final key](evidence/realization-fingerprints/final-sequencekey.csv) reports retain uncertainty.

## Reproduction

```bash
dotnet test src/Cohesive.Relations.Tests/Cohesive.Relations.Tests.csproj -c Release
dotnet run --project src/Cohesive.Relations.Benchmarks/Cohesive.Relations.Benchmarks.csproj -c Release -- --filter '*RealizationFingerprintBenchmarks*' '*RealizationSequenceKeyBenchmarks*'
```

Opt-in benchmarks keep report construction outside measurement and fingerprint normalization
inside it. Report sizes are 1, 128 and 1,024 requirements; keys span stack and pooled storage.
Run the identical benchmark file on parent and final source separately. Ari's existing protocol
preparation harness uses matched source overlays restored after each run.
