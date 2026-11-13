# Large-batch admission for prepared DTO mapping

The [load-summary workload](relation-dto-root-admission.md) joins each load to one customer
and two line items. The [packed-sort implementation](relation-dto-packed-identity-sort.md)
used pooled root storage up to 65,536 ordinal slots. At two fields per root, a 32,769-load
batch crossed that limit. The fused mapper then allocated an owned observation per root and
sorted complete root entries.

Eligible array batches now borrow the ordinal storage in blocks of up to 65,536 slots. A
single index order covers all blocks. The mapper validates the entire input, sorts by escaped
identity, and rejects duplicate identities before constructing DTOs. It returns and clears
reference-bearing buffers on success, cancellation, and failure. Unknown-size inputs and
batches above the evaluator's remaining row budget retain the owned path. Unsupported CLR
projections still use ordinary root admission.

Each ordinal rental stays bounded. Total borrowed storage grows with the batch within the
evaluator's row budget. Managed allocation per operation measures fresh allocations and
excludes reused buffers, so it is distinct from peak working set.

## Warm mapping

The matched run used one BenchmarkDotNet executable, replacing only `Cohesive.Relations.dll`
from the baseline revision `ee7963e3`. The host was an Apple M5 Max with .NET 10.0.13 Arm64,
BenchmarkDotNet 0.15.8, and AutoMapper 16.2.0. It used eight measured iterations, three warmups,
and a 125 ms target iteration time. The scale fixture sets the row budget to four times the
load count, uses `load-{i}` identities, and checks the entire result against the AutoMapper
output sorted by ID before timing.

| Loads | Baseline | Pooled blocks | Managed allocation, baseline → pooled blocks |
| ---: | ---: | ---: | ---: |
| 8,192 | 0.839 ms | 0.703 ms | 454 → 463 KiB |
| 32,768 | 4.31 ms | 3.62 ms | 1.86 → 1.86 MiB |
| 32,769 | 17.27 ms | 5.94 ms | 8.75 → 1.93 MiB |
| 100,000 | 68.31 ms | 13.41 ms | 31.66 → 6.30 MiB |
| 250,000 | 96.85 ms | 38.39 ms | 75.14 → 17.10 MiB |

An earlier baseline run measured 9.37 ms at 32,769 loads and 34.17 ms at 100,000. The first
pooled-block run measured 4.26 ms and 14.70 ms respectively. The large-batch speedup held
across these repeats, while its exact size varied. The managed allocation difference was
consistent across the former limit.

### Prepared AutoMapper controls

Both AutoMapper controls reuse customer and total lookups. The ordered control sorts the
complete DTO array with `StringComparer.Ordinal` semantics. The fixture identities use only
URI-unreserved characters, so that order agrees with Cohesive's escaped identity order.
Both controls assume valid unique inputs. Cohesive still validates roots and checks uniqueness.

| Loads | Cohesive | AutoMapper in input order | AutoMapper with ordered output |
| ---: | ---: | ---: | ---: |
| 100,000 | 12.48 ms | 11.50 ms | 20.36 ms |
| 250,000 | 33.68 ms | 37.61 ms | 70.01 ms |

The input-order comparison changes with run and batch size. At 250,000 loads the earlier
pooled-block run measured 42.59 ms for Cohesive and 34.44 ms for AutoMapper in input order.
In the ordered-controls run, Cohesive allocated 5.34 and 13.35 MiB at 100,000 and 250,000
loads. The AutoMapper controls allocated 9.92 and 24.80 MiB. Cohesive's observed warm
allocation varies with buffer reuse between runs.

### Longer identities

Replacing root IDs and matching line-item keys with `load-{i % 4}-common-long-segment-{i}`
forces the string-sort path. At 100,000 loads, pooled blocks reduced warm time from 46.45 ms
to 33.08 ms and managed allocation from 31.65 MiB to 5.34 MiB. The longer keys still cost
more to order than the short keys in this workload.

## First use

The physical executor now constructs the escaped occurrence prefix once per input and validates
source field results directly. Selection, value-contract, and identity checks keep their previous
precedence. The DTO compiler reuses each static field's value contract across all lookup rows.
The first map still acquires and validates the related sources through the canonical evaluator.

| Loads | Baseline first map | Candidate first map |
| ---: | ---: | ---: |
| 100,000 | 4.23 s | 4.40 s |
| 250,000 | 11.86 s | 11.82 s |

The first-map probe creates a fresh evaluator per sample in a warmed process. Evaluator setup is
outside the timed interval. The table uses medians of three samples at 100,000 loads and two at
250,000. This probe showed no clear change in first-use latency.

At 100,000 loads, the baseline profile sampled 2,792 mapping stacks and the candidate sampled
2,285. Preparation appeared in 99% of stacks in both captures. In the candidate, evidence and
gap analysis appeared in 31% and grouped aggregation in 16%. These are inclusive sampling
shares. Sampled allocation weight fell from approximately 1.04 GB to 0.87 GB. The validation
delegates and repeated value-contract allocations dropped out of the top allocation types.

## Verification

All 1,235 Relations tests passed in Release with warnings treated as errors. The new
differential test compares the full canonical output across multiple blocks, including
unmatched roots, and verifies the duplicate diagnostic when two blocks contain the same
identity. Its warm allocation bound also rejects the old implementation, which allocated
8,477,032 bytes against a 5,243,360-byte ceiling for the same input. The final benchmarks
used the tested Relations assembly.

## Preparation follow-up

The evidence analyzer now processes canonically ordered field runs. It binds the compiled field
contract once per input and indexes that input's evidence by occurrence. Owner, value-contract,
duplicate, and input-kind checks still run in the canonical analyzer. Invalid input runs retain
one diagnostic per applicable occurrence, and the gap analysis still uses the same coverage and
policy rules.

The canonical aggregate interpreter reuses one grouping-key buffer. It looks up borrowed key
spans with the existing relation value equality and hashing, and copies a key into immutable
storage only on a new group. The group retains that key for output. The interpreter reuses its
assignment value buffer and preserves first-seen group order, root partitioning, per-group
evaluation order, and contributor provenance.

The same scale fixture ran against the pooled-block build and the preparation candidate. Only the
Relations assembly changed. The probe timed the first map on each fresh evaluator after evaluator
creation. The short-key BenchmarkDotNet run measured repeated maps on an already prepared evaluator.

| Loads | First map, baseline → candidate | Warm map, baseline → candidate |
| ---: | ---: | ---: |
| 8,192 | 333 → 311 ms | 0.695 → 0.709 ms |
| 32,768 | 1.118 → 0.883 s | 4.775 → 3.661 ms |
| 32,769 | — | 5.117 → 3.970 ms |
| 100,000 | 3.831 → 3.600 s | 13.710 → 13.332 ms |
| 250,000 | 11.842 → 10.468 s | 35.748 → 39.720 ms |

A first-map repeat ran the candidate first, followed by the baseline. It measured 3.129 → 2.425 s
at 100,000 loads and 7.827 → 5.892 s at 250,000. The candidate improved both batch sizes in
both run orders, with differences in absolute timing between the two runs.

The 250,000-load warm result was slower in the matched scale run and in an isolated repeat
that ran the candidate first. The isolated result was 33.19 → 35.65 ms with both builds
allocating 17.10 MiB per operation. The preparation changes do not directly change the warm
mapping loop. The cause of the measured difference was not isolated.

The same-run 100,000-load ordered control took 12.89 ms for Cohesive, 11.03 ms for prepared
AutoMapper in input order, and 19.10 ms for prepared AutoMapper with sorted output. At 250,000
loads those times were 40.71 ms, 31.39 ms, and 59.55 ms respectively. The 100,000-load long-key
control took 30.41 ms versus 33.08 ms in the previous pooled-block run. Its managed allocation
was 7.25 MiB versus 5.34 MiB in that run, consistent with the observed buffer reuse variability.

Paired EventPipe traces for the first map at 100,000 loads captured 2,810 baseline stacks and
2,226 candidate stacks. Evidence analysis appeared in 916 and 302 stacks respectively. Grouped
aggregation appeared in 448 and 440 stacks, with sampled allocation attributed to aggregate
execution falling from 133 MB to 62 MB. Total sampled allocation weight fell from 866 MB to
779 MB. The traces measure sample counts and approximate allocation weight. They supplement the
unprofiled wall-clock probe.

All 1,235 Relations tests passed in Release with warnings treated as errors. The aggregate test
interleaves grouping keys and checks contributor provenance. The evidence test checks diagnostic
attribution for multiple owners of the same wrong-kind input. The final benchmark assembly
matches the tested assembly.

## Warm regression follow-up

The isolated 250,000-load BenchmarkDotNet recheck measured 33.38 ms for the pooled-block
baseline and 36.17 ms for the preparation build. Both allocated 17.10 MiB per measured map.
The earlier preparation result therefore reproduced under the original BenchmarkDotNet job.

The continuous 100-map CPU profiles reversed that ranking. The two scalar join dictionary
lookups accounted for 68% of sampled mapping stacks in the baseline and 56% in the preparation
build. A smaller eight-map window with GC disabled measured 33.14 ms versus 27.85 ms, with
zero collections and identical allocation. Forcing compaction before another 100-map window
produced nearly identical means of 81.21 and 82.06 ms. Both runs spent roughly 3.7 seconds
of that window in GC pauses. The preparation slowdown depends on measurement conditions.

Frozen ordinal lookup tables measured 45.26 ms in the isolated BenchmarkDotNet job and 34.57 ms
in the GC-free window, so the ordinary dictionaries were retained. A separate trial recorded a
borrowed root's block index and slot offset during admission. Direct indexing removed the
division and offset calculation from the mapping loop. The address moved with its row on the
whole-entry sort fallback, preserving validation, duplicate handling, and constructor order.

The isolated warm result with direct addressing was 33.89 ms. All 1,235 Relations tests passed
in Release with warnings treated as errors, and the benchmark used that tested assembly.

| Loads | Warm before → direct-address trial | First map in the trial |
| ---: | ---: | ---: |
| 8,192 | 0.709 → 0.723 ms | 274 ms |
| 32,768 | 3.661 → 3.926 ms | 777 ms |
| 32,769 | 3.970 → 3.977 ms | — |
| 100,000 | 13.332 → 11.885 ms | 2.176 s |
| 250,000 | 39.720 → 32.661 ms | 5.829 s |

The same-run 250,000-load controls measured 33.04 ms for direct addressing, 38.49 ms for
prepared AutoMapper in input order, and 69.43 ms for prepared AutoMapper plus sorting.
At 100,000 loads those times were 12.50 ms, 12.29 ms, and 20.83 ms respectively.

Re-running the isolated BenchmarkDotNet case with `--noForcedGCs` set `Force=False` in the
job. It measured 35.22 ms for the pooled-block baseline, 35.36 ms for the preparation
build, and 40.04 ms for the direct-address candidate. The original gap disappeared under this
setting. The runtime mechanism behind the forced-collection sensitivity remains unisolated.
The sustained 100-map controls also switched rank when the trial order was reversed. The
preparation build measured 86.88 and 95.91 ms per map and the direct-address trial 92.00 and
87.86 ms. GC pauses accounted for 3.9 to 4.5 seconds over the 100 calls.

In the long-key recheck at 100,000 loads, direct addressing took 32.07 ms versus 30.63 ms
for the preparation build. Both allocated 7.25 MiB per operation. The first direct-address
run took 39.49 ms, so absolute timings varied across launches.

An isolated 250,000-load first-map probe ran direct addressing, preparation, and the
pooled-block baseline in that order. Their medians were 10.84, 10.87, and 12.90 seconds.
This probe excludes evaluator creation and uses a fresh evaluator for each sample.

The benchmark now sets `GcForce(false)` for this fixture so collections occur under runtime
policy. The production kernel retains the preparation build. The direct-address trial had
mixed results across the standard, long-key, and GC controls.

The retained build completed the short-key warm sweep with `Force=False`. The first-map
probe separately used a fresh evaluator for each sample and excluded evaluator creation.

| Loads | Warm mapping | Managed allocation | First map |
| ---: | ---: | ---: | ---: |
| 8,192 | 0.965 ms | 448 KiB | 288 ms |
| 32,768 | 4.187 ms | 1.75 MiB | 778 ms |
| 32,769 | 4.144 ms | 1.75 MiB | — |
| 100,000 | 12.934 ms | 5.34 MiB | 2.070 s |
| 250,000 | 39.548 ms | 13.35 MiB | 6.104 s |

The 250,000-load same-run controls measured 34.73 ms for Cohesive, 36.67 ms for prepared
AutoMapper in input order, and 67.13 ms for prepared AutoMapper plus sorting. At 100,000
loads the three results were 12.97, 12.86, and 19.90 ms. The 100,000-load long-key case
took 28.36 ms and allocated 5.34 MiB. All 1,235 Relations tests passed with warnings as
errors, and the tested Relations assembly matched the earlier preparation candidate byte
for byte.
