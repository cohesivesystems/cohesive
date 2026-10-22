# Compact ordinal storage for sparse nested observations

This experiment extends the [nested density study](observation-values-nested-density.md). It compares a packed present-value vector with the previous fixed ordinal vector and density-aware small/dictionary storage, while keeping original whole-input admission and nested semantics authoritative.

## Representation and boundary

`CompactOrdinalObservationFields` is an internal immutable physical representation. The existing `ObservationLayout` remains the authority for field identities, shape qualification and canonical lexical order. Values are packed in declaration ordinal order. Through 64 declared fields, presence fits in one inline bitmap. Wider layouts store one bitmap and prefix population count per 64 fields. A bound read tests presence and uses the prefix plus population count of lower bits to address the packed vector; lookup does not scan preceding words or resolve a field name.

The benchmark candidate selects packing only for non-root records wider than eight fields with at most half their unique decoded fields present. Dense records keep the existing fixed ordinal vector. This is an experimental selection rule shared with the density-aware comparator, not a claimed optimal crossover. Source slots are borrowed during construction and copied into newly owned immutable arrays; pooled parsing scratch never escapes. Undefined slots mean absence, while Null is present, matching the existing ordinal representation.

Two minimal Core integration points recognize the new immutable representation: snapshotting retains its owned storage, and canonical traversal uses its layout-prepared lexical permutation. No public API, external dependency or production ingestion selection is added. The parser and selection policy remain in the benchmark project. The sparse enumerator walks set bits, unlike the dense enumerator; keeping their physical algorithms separate avoids adding generic/interface dispatch to dense reads. Both use the same layout and semantic interpreters.

## Semantic checks

The original reader and validator remain authoritative. Exact decoded nested duplicates keep the last value. Case variants, unknown fields, unsupported kinds and malformed inputs fall back through the original whole-input reader, preserving its accepted spelling, values and failures. Strict root duplicates retain the existing rejection behavior. Named structural references and arrays follow the original nested rules; private projected child layouts are benchmark metadata, not new domain identities.

The differential suite checks 223 scenarios across the original fixed candidate, separated fixed candidate, density-aware candidate and packed candidate. It compares outcomes, equality, hashes, canonical bytes, reads and ownership. Seventeen Core tests additionally cover empty records, word boundaries through width 1,025, leading empty words, all observation kinds, absence versus Null, source-buffer mutation, mutable byte-output independence, and allocation-free bound reads. The width-256/four-present construction test requires at most 512 allocated bytes per stored record.

## Measurement

All routes run from one frozen dependency closure. Each timing is a fresh BenchmarkDotNet process, four warmups and six 200 ms measurement iterations, repeated three times with counterbalanced route order. Tables report the median of the three process means; ranges are retained in the evidence. The read fixture performs 16 accesses per row: ordinal zero, midpoint, last and missing, repeated four times. The wide read fixture places fields every fourth ordinal, exercising a present midpoint in a later bitmap word. Read-only timing excludes parsing and preparation.

Parsing includes construction and complete original admission, not just token decoding. Retained heap diagnostics run separately before timing, retain multiple completed trees, and exclude prepared layout metadata and the retention array. Their allocation counters include complete admission and can differ from warmed timed runs. Single nested fixtures contain four wrapper objects and one sparse leaf. Array percentages are generated from declared field positions; 6% of width 256 means 16 present fields (6.25%). Mixed arrays alternate quarter-full and full rows. Dense controls take the fixed path.

## Parsing and complete admission

Times are μs per complete tree. Columns are existing reader, fixed ordinal vector, density-aware small/dictionary, and packed ordinal. Allocation is B/tree for the last two routes.

| Scenario | Width | Rows | Present % | Existing μs | Fixed μs | Density μs | Packed μs | Density B | Packed B |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| array | 16 | 128 | 25 | 68.53 | 68.93 | 74.46 | 73.11 | 99451 | 108667 |
| array | 32 | 128 | 25 | 159.08 | 146.43 | 168.85 | 143.11 | 146558 | 151678 |
| array | 256 | 128 | 6 | 624.60 | 816.55 | 694.69 | 764.81 | 277677 | 235699 |
| array | 256 | 128 | 25 | 1577.44 | 1130.30 | 1079.55 | 1161.82 | 996555 | 791763 |
| array | 32 | 128 | 50 | 209.70 | 183.80 | 182.36 | 197.48 | 277664 | 224384 |
| nested | 16 | 1 | 25 | 0.90 | 0.92 | 0.96 | 0.89 | 1224 | 1296 |
| named-array | 32 | 128 | 25 | 163.46 | 179.53 | 170.98 | 172.10 | 146559 | 151704 |
| mixed-array | 32 | 128 | 100 | 263.52 | 180.18 | 202.15 | 188.08 | 145024 | 147583 |
| array | 16 | 128 | 100 | 133.11 | 103.35 | 120.28 | 119.18 | 77965 | 77949 |
| array | 16 | 4096 | 25 | 2561.67 | 3441.38 | 2994.68 | 3051.04 | 3204410 | 3512429 |

## Prebound reads and combined work

Read-only results are for 128 rows and 2,048 accesses per operation; every route allocates zero bytes.

| Fixture | Route | Median μs | Process range μs |
|---|---|---:|---:|
| array, width 16, quarter-full | ReadExisting | 8.593 | 8.498–8.595 |
| array, width 16, quarter-full | ReadOrdinals | 2.624 | 2.470–2.666 |
| array, width 16, quarter-full | ReadDensityAware | 8.458 | 8.394–8.980 |
| array, width 16, quarter-full | ReadCompactOrdinal | 2.406 | 2.360–2.407 |
| sparse-array, width 256, quarter-full | ReadExisting | 9.733 | 9.668–9.885 |
| sparse-array, width 256, quarter-full | ReadOrdinals | 2.589 | 2.498–2.629 |
| sparse-array, width 256, quarter-full | ReadDensityAware | 11.528 | 11.234–11.530 |
| sparse-array, width 256, quarter-full | ReadCompactOrdinal | 4.686 | 4.631–4.877 |

| Parse + same reads | Existing μs | Fixed μs | Density μs | Packed μs |
|---|---:|---:|---:|---:|
| Width 32, 128 quarter-full rows | 163.13 | 138.11 | 166.98 | 144.48 |
| Width 16, 4096 quarter-full rows | 3275.21 | 3597.40 | 3263.52 | 2964.40 |

## Retained memory

Bytes per complete retained tree, excluding prepared metadata. Preparation allocation and raw totals are included in `retained.txt`.

| Scenario | Width | Rows | Present % | Existing B | Fixed B | Density B | Packed B | Packed vs fixed | Packed vs density |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| array | 16 | 128 | 25 | 34584 | 77944 | 31864 | 29816 | -61.7% | -6.4% |
| array | 32 | 128 | 25 | 55224 | 143480 | 52344 | 46200 | -67.8% | -11.7% |
| array | 256 | 128 | 6 | 150376 | 1060984 | 137336 | 90232 | -91.5% | -34.3% |
| array | 256 | 128 | 25 | 944248 | 1060984 | 496760 | 286840 | -73.0% | -42.3% |
| array | 32 | 128 | 50 | 140536 | 143480 | 137336 | 78968 | -45.0% | -42.5% |
| nested | 16 | 1 | 25 | 984 | 1056 | 696 | 680 | -35.6% | -2.3% |
| named-array | 32 | 128 | 25 | 64744 | 143480 | 52344 | 46200 | -67.8% | -11.7% |
| mixed-array | 32 | 128 | 100 | 165240 | 143480 | 97912 | 94840 | -33.9% | -3.1% |
| array | 16 | 128 | 100 | 140536 | 77944 | 77944 | 77944 | +0.0% | +0.0% |
| array | 16 | 4096 | 25 | 1018648 | 2490488 | 1015928 | 950392 | -61.8% | -6.5% |
| sparse-array | 256 | 128 | 25 | 944248 | 1060984 | 496760 | 286840 | -73.0% | -42.3% |

## Core integration controls

An independent matched harness/dependency closure compares the previous Core with the new Core; only `Cohesive.dll` differs. Neither direct-parser fixture selects compact storage. These bounded controls check for obvious integration regressions, rather than establish an equivalence bound.

| Workload | Before μs | After μs | Change | Before B | After B |
|---|---:|---:|---:|---:|---:|
| flat | 0.542 | 0.534 | -1.4% | 1680 | 1680 |
| collection | 32.748 | 32.731 | -0.1% | 34987 | 34987 |

## Decision and limits

Keep the packed representation as an explicit experiment; do not switch production ingestion by default.

- **The read objective is met.** Width-16 compact reads take 2.406 μs versus 8.458 μs for density-aware storage (3.52× faster), with the fixed vector at 2.624 μs. Width-256 strided compact reads take 4.686 μs versus 11.528 μs (2.46× faster); the fixed vector remains faster at 2.589 μs. The wide rank lookup has a real cost, but avoids falling back to names. All read routes allocate zero bytes.
- **Retained memory improves beyond the previous sparse policy.** Width-16 quarter-full arrays retain 29,816 B/tree versus 77,944 B fixed and 31,864 B density-aware. Width-256 arrays with 16 present fields retain 90,232 B versus 1,060,984 B fixed and 137,336 B density-aware: 91.5% and 34.3% reductions. At width 256/64 present, packing saves 73.0% versus fixed and 42.3% versus density-aware. These are complete-tree figures, including the root and array.
- **Construction has no universal winner.** Width-16 quarter-full packed parse/admission allocates about 9.3% more than density-aware storage (108,667 versus 99,451 B), despite retaining less. Width-256/16-present packing allocates about 15.1% less but takes 764.81 versus 694.69 μs, about 10.1% longer. At half-full width 32 it saves about 19.2% allocation but takes about 8.3% longer. The current two-pass freeze, allocation shape and complete validator all contribute to the measured operation; this experiment does not isolate their CPU shares.
- **Combined work can recover the read benefit.** At width 32/128 quarter-full rows, packed parse plus reads is faster than density-aware storage; at width 16/4,096 rows it is about 9% faster. Those results do not imply a general parsing speedup or an optimal density threshold.
- **Dense selection still costs CPU.** The dense candidate retains the fixed representation and has matching retained/allocation figures, yet its pooled scratch/selection path is slower than the direct fixed candidate. Identical resulting storage does not mean identical construction cost.

Process variability is material. For example, width-32 quarter-full packed parsing spans 141.93–204.56 μs; named-array packed parsing spans 139.28–219.91 μs; width-256/16-present existing parsing spans 612.38–953.95 μs. Treat small parsing differences as provisional. The read-only repeats are substantially tighter. This bounded matrix uses Int64 fixtures on one host, warm prepared layouts and three process repeats; it establishes a memory/read tradeoff rather than broad workload superiority or tail-latency guarantees. CPU/JIT profiling of packed construction and validation, and a read/write-frequency-aware crossover study, remain deferred.

Verification: 5,172 Core tests passed, 60 environment-dependent tests skipped; 1,149 Relations tests passed; 223 differential semantic scenarios passed across four candidate variants. The tested Core and frozen timing Core have identical bodies/flags/local signatures/exception regions for 11,886 methods. These binaries include the new internal storage type; no claim of equivalence to the previous Core is made. The two matched ordinary-parser controls have unchanged allocation and under 1.5% median timing differences, with overlapping process ranges. They are regression checks, not statistical equivalence proofs.

All measurements, source/dependency hashes, scripts, raw outputs and checks are retained in [the evidence directory](evidence/observation-values-compact-ordinals/README.md). No build, test, trace, heap diagnostic or compression overlapped timed runs. The experiment remains on the isolated branch; the primary checkout is unchanged.
