# Fixed tagged ObservationValue metadata

Measured October 18, 2026, following the bounded string-reuse increment. The lossless
`PortableValueJsonConverter` now compares `$kind` directly against UTF-8 wire names instead
of decoding each name and sending it through the traversal-local retained-text cache.
The ordinary JSON reader and writer are unchanged.

## Semantic authority and constraints

`ObservationValueKind` remains the semantic authority and the existing converter owns wire
spellings. The parser's reverse mapping is replaced in place. No new public type, cache,
format version or dependency is introduced. `ValueTextEquals` handles escaped and segmented
names with ordinal case-sensitive matching. Only an unknown name is decoded for its diagnostic.
Payload conversion, duplicate rejection, deferred payload-before-kind reading, ownership,
fresh admission and canonical writing retain their existing rules.

Tests cover every enum case with canonical, escaped and reversed metadata, including tokens
split between byte segments. Null/undefined decoding has a deterministic zero-allocation
regression check after serializer warmup. Unknown, wrong-case, malformed-surrogate and invalid
UTF-8 names are rejected. Exact checkpoint sizes and SHA-256 hashes match the control for all
four representative workloads.

## Isolated measurements

BenchmarkDotNet 0.15.8, .NET 10.0.13, Apple Arm64. Both sides use the same benchmark assembly
and dependency closure; only `Cohesive.dll` differs, verified by the stored manifest. The control
is the preceding string-reuse implementation (`db83cf8c…`), the candidate is `47edf8e3…`, also
verified against the tested Core assembly. Measurements ran sequentially without builds or tests.
The initial exploratory control overlapped test compilation and is excluded from evidence.

Initial matched runs use three warmups and five 250 ms measurement iterations. Times are means;
short local runs are directional evidence, not a latency guarantee.

| Tagged decode | Control time | Candidate time | Control allocation | Candidate allocation |
|---|---:|---:|---:|---:|
| Null | 55.50 ns | 48.94 ns | 32 B | 0 B |
| Undefined | 60.30 ns | 50.64 ns | 40 B | 0 B |
| Escaped null | 65.69 ns | 68.38 ns | 32 B | 0 B |
| Flat portable envelope | 940.6 ns | 960.2 ns | 1.46 KiB | 1.28 KiB |
| Nested portable envelope | 2.721 μs | 2.729 μs | 4.09 KiB | 3.28 KiB |
| 128-record portable envelope | 64.620 μs | 63.240 μs | 38.53 KiB | 38.42 KiB |
| 4096-record portable envelope | 2.396 ms | 2.367 ms | 1134.07 KiB | 1135.00 KiB |

The tradeoff is explicit: successful fixed metadata needs no owned text, but escaped metadata
performs decoded comparisons and was about 4% slower. Flat envelope throughput was about 2%
lower despite reducing allocation by 12%. Larger tagged workloads show only small timing changes;
this increment does not claim another large-payload speedup or resolve the preceding ordinary
flat-JSON parsing regression. Bounded caches use process-randomized buckets, so allocation on
large repeated workloads can vary with collisions even for identical binaries.

## Whole checkpoint recovery

Three fresh processes per side measured complete decoding and fresh recovery admission. The
first pair used the initial settings above; two additional pairs used six warmups and eight
250 ms iterations, alternating side order. All reports are retained. Across the three runs:

| Workload | Control mean-time range | Candidate mean-time range | Median control allocation | Median candidate allocation |
|---|---:|---:|---:|---:|
| Flat | 56.50–59.19 μs | 56.13–58.45 μs | 37,535 B | 37,231 B |
| Nested | 103.73–110.74 μs | 103.61–105.22 μs | 47,307 B | 45,716 B |
| 128 records | 0.961–1.422 ms | 0.945–1.450 ms | 158,073 B | 158,011 B |
| 4096 records | 26.109–27.692 ms | 26.135–26.939 ms | 6,556,178 B | 6,672,397 B |

The candidate's 128-record median time was 1.311 ms versus 1.099 ms for control, with substantial
within-run variance as well as overlapping process ranges. Large-checkpoint median allocation
was 1.8% higher and individual processes varied much more. These measurements do not establish
an end-to-end improvement or a large-workload allocation guarantee. The decision to retain this
small local change prioritizes eliminating transient format-label allocation, with modest,
repeatable allocation savings for flat/nested values. No new cache policy or speculative
framework is introduced to chase the noisy checkpoint result. Broader cache-policy tuning and
ordinary JSON's earlier small-input CPU regression remain deferred measured work.

## Verification

The full Core suite passed 5,009 tests with 54 skipped. Four subsequently added malformed-tag
cases and the corrected escaped-singleton test were included in the final focused run: all 23
tagged-decoder checks passed. Production code did not change between these runs. The measured
candidate Core hash matches the tested assembly. Existing build warnings remain.

Raw BenchmarkDotNet reports, assembly hashes, exact checkpoint wire hashes and test logs are in
[evidence/observation-values-tagged-metadata](evidence/observation-values-tagged-metadata/).
The new `ObservationTaggedMetadataBenchmarks` separates fixed-tag allocation from retained
payload storage. Reproduce with the same frozen benchmark/dependency closure on both sides and
substitute only Core:

```text
dotnet Cohesive.Relations.Benchmarks.dll --filter '*ObservationTaggedMetadataBenchmarks*' '*ObservationBoundaryConversionBenchmarks.DecodePortable*' '*ObservationCheckpointBenchmarks.DecodeAndAdmit*' --inProcess --warmupCount 3 --iterationCount 5 --iterationTime 250 --artifacts <results>
dotnet Cohesive.Relations.Benchmarks.dll --checkpoint-wire-sizes
```
