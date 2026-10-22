# Layout-bound nested observation validation

This experiment follows [compact ordinal storage](observation-values-compact-ordinals.md). It profiles nested admission and tests explicit validation-plan bindings to existing ordinal layouts, without changing the stored representation or making a new validator authoritative.

## Mechanism and semantic boundary

The existing `ObservationValidator` and original graph/type declarations retain authority for field presence, nullability, cardinality, scalar interpretation, named types, arrays, recursion, depth and diagnostics. `ObservationValidationPlan.BindLayouts` makes a private copy of the reachable prepared plan links (including cycles), sharing original immutable declarations and prepared metadata. Published graph plans are never modified or made aware of another caller's binding.

Each bound record requires a complete layout with exact unambiguous names and the original field type references, cardinality, presence and nullability. Bindings store a declaration-index-to-ordinal permutation. The internal root admission entry checks the exact original graph and field types; the private projected child graph is physical metadata rather than a replacement domain identity.

The validator recognizes only framework-owned immutable dense or compact storage whose layout object is exactly the bound object. That evidence proves all present keys belong to the complete declared layout, so unknown-member discovery can be skipped. Each declared field is then read by its prebound ordinal, through the same original field/type validation operations. Other storage and layout instances keep the original name-based lookup. Union discriminator exceptions disable this shortcut. Required fields are still checked in declaration order; this experiment intentionally does not add required-field bitmaps or present-only traversal.

On failure, root admission reruns the original unbound detailed interpreter. Nested case variants, unknown members, unsupported typed restoration and malformed JSON retain the benchmark parser's existing whole-input fallback. Strict root duplicate rejection and nested exact decoded last-value-wins behavior remain unchanged. There is no public API, external dependency, new physical storage format, global binding registry or automatic production selection.

Binding inputs are borrowed only during preparation. Bound permutations and child links are privately owned and published only after their closure is complete. Values retain their existing immutable storage; they do not acquire a reference to the private validation closure. Preparation is per parser, while warm validation is per admitted tree/row.

## Profiling evidence

Before the change, sparse nested validation attempts an exact name lookup for every declaration. The first absent optional name creates a per-record case-insensitive dictionary of the present fields. Unknown-member discovery and field validation both traverse the declared fields.

Three initial eight-second warmed profiles cover compact validation at width 16/quarter-full and width 256/16-present, plus complete packed ingestion at width 16. Four final profiles cover matched reference/bound validation at width 16, bound validation at width 256, and bound ingestion. EventPipe sample stacks and matching JIT disassembly are retained.

The initial width-16 validation profile has 5,874 selected samples; dictionary lookup and case-insensitive hashing account for 1,228 leaf samples (20.9%). At width 256 they account for 1,652 of 5,920 (27.9%). Much of the remaining nested body is inlined into `TryValidateFieldValue`, so sampling cannot assign every operation precisely. In the final matched width-16 profiles, these dictionary/hash leaf samples fall from 1,060 of 5,864 to zero of 5,967 for the bound route. The bound wide profile also has zero such leaf samples.

Sampling percentages are not CPU-cycle attribution. The parse captures contain many runtime suspension/GC-poll frames, and even allocation-free validation has poll frames; these must not be counted as managed allocation cost or evidence of application lock contention. Warm BenchmarkDotNet timing and allocation measurements below are the performance evidence.

## Measurement

Every exact benchmark runs in a fresh process, with four warmups and six 200 ms measurement iterations, repeated three times with counterbalanced route order. Tables report medians of process means; min/max process means and raw BDN statistics are retained. All main comparisons share one frozen dependency closure. A separate matched harness compares the previous and final Core with only `Cohesive.dll` changed.

Validation-only pairs read the exact same retained tree, root value slots and presence bitmap, excluding parsing and metadata preparation. End-to-end pairs parse identical input into the same physical strategy and include complete authoritative root/nested admission. Combined cases add the same prebound field reads. Retained diagnostics run separately before timing and exclude preparation and retention arrays. Layout/plan preparation benchmarks create fresh parsers and layouts over already warmed governing-graph metadata; their results are not cold-process or first-JIT costs.

Fixtures include a single record, a four-wrapper nested chain, inline and named-record arrays, sparse and dense records, and 4,096-row arrays. Width 256 at the 6% setting contains 16 fields (6.25%). All timing fixtures use Int64 fields; semantic tests cover failures and fallback separately.

## Validation of packed trees

Times are μs per complete tree; allocation is B/operation.

| Scenario | Width | Rows | Present % | Reference μs | Bound μs | Speedup | Reference B | Bound B |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| record | 16 | 1 | 25 | 0.368 | 0.059 | 6.27× | 584 | 0 |
| nested | 16 | 1 | 25 | 0.501 | 0.096 | 5.22× | 584 | 0 |
| array | 16 | 128 | 25 | 47.100 | 6.677 | 7.05× | 74752 | 0 |
| array | 256 | 128 | 6 | 741.101 | 78.559 | 9.43× | 141312 | 0 |
| named-array | 32 | 128 | 25 | 87.339 | 13.044 | 6.70× | 101376 | 0 |
| array | 16 | 128 | 100 | 34.182 | 14.613 | 2.34× | 0 | 0 |
| array | 16 | 4096 | 25 | 1568.071 | 212.925 | 7.36× | 2392064 | 0 |

## Complete packed ingestion

Times are μs per complete tree; allocation is B/operation.

| Scenario | Width | Rows | Present % | Reference μs | Bound μs | Speedup | Reference B | Bound B |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| record | 16 | 1 | 25 | 0.633 | 0.294 | 2.16× | 912 | 328 |
| nested | 16 | 1 | 25 | 0.912 | 0.479 | 1.90× | 1296 | 712 |
| array | 16 | 128 | 25 | 75.139 | 32.206 | 2.33× | 108664 | 33912 |
| array | 256 | 128 | 6 | 853.415 | 207.514 | 4.11× | 235640 | 94328 |
| named-array | 32 | 128 | 25 | 137.569 | 61.021 | 2.25× | 151672 | 50296 |
| array | 16 | 128 | 100 | 111.525 | 92.321 | 1.21× | 77944 | 77944 |
| array | 16 | 4096 | 25 | 2993.682 | 1397.602 | 2.14× | 3473766 | 1081581 |

## Fixed-vector controls and combined reads

These controls use the same binding mechanism over fixed vectors; the physical format is unchanged within each pair.

| Operation | Present % | Reference μs | Bound μs | Reference B | Bound B |
|---|---:|---:|---:|---:|---:|
| Validation | 25 | 50.653 | 7.359 | 72704 | 0 |
| Complete parse | 25 | 72.078 | 28.116 | 150648 | 77944 |
| Validation | 100 | 33.895 | 14.582 | 0 | 0 |
| Complete parse | 100 | 107.344 | 88.079 | 77944 | 77944 |

| Packed parse + identical prebound reads | Reference μs | Bound μs | Speedup |
|---|---:|---:|---:|
| Width 32, 128 quarter-full rows | 146.642 | 64.612 | 2.27× |
| Width 16, 4096 quarter-full rows | 3015.722 | 1456.293 | 2.07× |

## Preparation and fallback

Preparation below includes fresh layout/parser allocation over warm governing graph metadata. The bound route additionally owns a private linked closure and permutations.

| Width | Reference μs | Bound μs | Reference B | Bound B |
|---|---:|---:|---:|---:|
| 16 | 3.253 | 3.715 | 13136 | 14588 |
| 256 | 67.609 | 60.570 | 158115 | 177733 |

| Case-variant fallback, width 16/128 rows | Median μs | Process range μs | B |
|---|---:|---:|---:|
| ParseCompactOrdinal | 211.830 | 210.247–215.970 | 290752 |
| ParseBoundCompactOrdinal | 213.702 | 209.115–221.537 | 280912 |

## Retained trees and ordinary-parser controls

Binding does not change stored values. Complete-tree retained bytes match in every pair, excluding prepared parser metadata.

| Scenario | Width | Rows | Present % | Retained B/tree, both routes | Reference allocation B/tree | Bound allocation B/tree |
|---|---:|---:|---:|---:|---:|---:|
| record | 16 | 1 | 25 | 296 | 912 | 328 |
| nested | 16 | 1 | 25 | 680 | 1296 | 712 |
| array | 16 | 128 | 25 | 29816 | 108664 | 33912 |
| array | 256 | 128 | 6 | 90232 | 235640 | 94328 |
| named-array | 32 | 128 | 25 | 46200 | 151672 | 50296 |
| array | 16 | 128 | 100 | 77944 | 77944 | 77944 |
| array | 16 | 4096 | 25 | 950392 | 3473528 | 1081464 |

The ordinary-parser comparison uses a matched previous harness, swapping only Core. Neither route selects a bound plan. These are bounded regression controls, not statistical equivalence proofs.

| Workload | Before μs | After μs | Change | Before B | After B |
|---|---:|---:|---:|---:|---:|
| flat | 0.537 | 0.541 | +0.7% | 1680 | 1680 |
| collection | 32.609 | 32.796 | +0.6% | 34984 | 34984 |

## Decision and limits

The measurements support further integration of explicitly bound nested validation at a strict shape-aware ingestion boundary. This commit keeps selection in the benchmark, pending that integration work.

- **The targeted validation cost is substantial and removable.** On quarter-full width-16/128-row packed arrays, validation falls from 47.100 to 6.677 μs (7.05× faster) and from 74,752 allocated bytes to zero. The wide 256-field/16-present fixture falls from 741.101 to 78.559 μs (9.43×), again with zero allocation. Named structural arrays improve 6.70×; the 4,096-row sparse fixture improves 7.36×. All measured warm bound validation routes allocate zero bytes.
- **The gain survives complete ingestion.** The width-16/128-row sparse parse falls from 75.139 to 32.206 μs (2.33×) and from 108,664 to 33,912 allocated bytes (68.8% less). The wide sparse parse improves 4.11× with 60.0% less allocation; named arrays improve 2.25×; large sparse arrays improve 2.14×. Dense packed-strategy parsing improves about 1.21× with unchanged allocation, because dense values already avoid missing-field lookup dictionaries and construction remains significant.
- **The benefit is independent of compact storage.** Fixed-vector controls also improve substantially. Sparse fixed parsing is about 2.56× faster and removes its temporary lookup allocation. Compact storage retains the memory/read tradeoff established in the previous study; binding does not introduce another value representation or alter prebound reads.
- **Preparation remains explicit.** Private binding links and permutations add about 1.5 KB at width 16 and 20 KB at width 256 per prepared parser. Small-layout warm-graph preparation rises by roughly 0.46 μs. Wide preparation process ranges overlap, so the lower bound-route median is not evidence that binding is free or intrinsically faster to prepare. These measurements exclude first JIT/cache population and should not be presented as cold-start costs.
- **Fallback semantics are preserved; resource readings are qualified.** Case-variant fallback medians are 211.830 and 213.702 μs (+0.9%), with overlapping process ranges. Its reference allocation is 290,752 B; bound-route processes report either 290,752 or 280,912 B despite executing the same original fallback. The cause of that process variation was not isolated. Do not count the allocation difference as a bound-validation gain or assert an exact fallback allocation equivalence.

The ordinary-parser controls, where no bound plan is selected, have unchanged allocation and median differences under 1%. Their process ranges overlap. They are a bounded check against obvious integration regressions rather than statistical equivalence guarantees.

Some parsing/preparation results remain process-sensitive: wide reference validation spans 580.042–753.144 μs, wide reference parsing 702.037–892.949 μs, and wide reference preparation 59.180–77.267 μs. Bound wide validation is tighter at 78.000–79.651 μs. Speedups are medians of three process means, not tail-latency promises. Fixtures use one host, Int64 fields, warm schemas and the benchmark's one-field payload envelope; broader scalar mixes and application ingestion boundaries still need representative qualification.

The abstraction audit deliberately retains the original type/field interpreter, metadata cache and diagnostics. It adds private physical bindings to the existing plan rather than a second semantic field catalog or a new validation type family. Bindings cover one selected layout per exact type-reference instance; a different layout safely uses the existing generic path. Required-field bitmaps, present-only sparse validation, parse/validate fusion and automatic production selection are deferred. The remaining declaration-width loop is visible rather than hidden behind another speculative abstraction.

Verification: all 5,183 Core tests passed, 60 environment-dependent tests skipped; all 1,149 Relations tests passed. Eleven new tests cover dense/compact and reordered layouts, named records, missing/null/type failures, other-layout/case/unknown fallback, source graph/declaration boundaries, recursive plan cycles/depth and zero warm allocation. The 223 differential scenarios pass across six parser/storage/validation variants, comparing canonical bytes, hashes, outcomes/diagnostics and ownership. The tested Core and frozen timing Core have identical bodies, flags, local signatures and exception regions for 11,898 methods.

[Evidence and reproduction](evidence/observation-values-ordinal-validation/README.md) include all 150 timing processes, 14 retained diagnostics, seven CPU/JIT captures, exact source/dependency hashes and raw report archives. No build, test, trace, heap diagnostic or compression overlapped timed runs. All work remains in the isolated experiment checkout; the primary checkout is unchanged.
