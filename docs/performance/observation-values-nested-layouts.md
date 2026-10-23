# Shared layouts for nested records and arrays

## Decision

The experiment compares shared child metadata and ordinal value vectors with the existing typed nested reader, while keeping full semantic admission in the existing validator. It is based on the integrated ObservationValue performance work (`d1c0ad8c`, PR #461), not the earlier parser on main. No production source or public API changes are made.

**Continue toward a selective dense-record implementation; do not adopt this prototype broadly.** Dense inline arrays parse/admit 15–45% faster at 128 rows and 12–58% faster at 4,096 rows. Prebound reads are 74–76% faster and allocate nothing; ordinary name reads on shared storage are 40–46% slower than existing storage. Combined 16-field parsing/admission plus reads improves 26–28%.

The costs are material: 16/32-field sparse trees retain 2.25×/2.60× as much memory, and the case-variant fallback remains about 16% slower in longer qualification. Numeric dense records demonstrate potential, not a universal representation win. Before production adoption, profile/address the fallback exit, select physical storage with density in mind, and give child layouts a type-owned representation without synthetic shape identity. Preserve the existing permissive nested matching and validation rules.


## Authority, semantics and operating boundary

The original graph, `ObjectTypeRef`/named structural declarations, typed JSON reader and `ObservationValidator` remain authoritative. The benchmark prepares a physical child plan once, then reuses Core's `ObservationLayout`, `OrdinalObservationFields`, array builder, equality, hashing and canonical writer. It does not infer layouts from records or intern names globally.

For this experiment only, child declarations are projected to private synthetic shapes to reuse the existing shape-qualified layout/storage primitives. Those identities describe benchmark execution artifacts, carry no domain admission evidence, and must not become a production authoring or persistence surface. A production design should attach child physical layouts directly to the owning type definitions rather than expose those synthetic graph identities.

The specialized route covers inline/named structural records, nesting, arrays and Int64 fields. It preserves absence and explicit null, reordered properties, escaped names and nested exact decoded duplicates with last-property-wins semantics. Root admission remains strict. Nested names that differ in case, unknown properties, ambiguous case-insensitive declarations, unsupported types (including temporal/binary hydration and unions), and recursive plans outside the preparation limit use the complete original typed-reader route. No nested casing policy is tightened or property spelling normalized. The fallback can discard partial candidate construction; its cost is measured explicitly.

Both routes use the same strict root framing and the same ordinal `ObservationValidator.TryValidateAgainstShape` entry point, including whole-tree validation and shape-error precedence before trailing-content errors. The shared route never trusts JSON merely because a physical plan exists. All result arrays/vectors are freshly owned; only immutable schema metadata is shared. Scratch arrays use the existing pool-and-finally mechanism and cannot escape into retained values.

Object semantic equality, hashes and canonical bytes agree. Dictionary enumeration for the specialized route follows layout declaration order rather than incoming property order; object insertion order is not preserved as physical metadata. Case-preserving fallback retains the original storage. The experiment makes no promise of identical noncanonical JSON property ordering.

## Fixtures and measurement

Every input has the same strict one-field `payload` envelope. Leaf records declare 4, 16 or 32 Int64 fields. The first is required; the others are optional and nullable. Dense fixtures supply every field. The sparse fixture supplies one field in four; its 256-field case tests a wide low-occupancy declaration. Nested chains have four `child` wrappers. Arrays contain 128 or 4,096 complete records; named fixtures use the graph's named structural declaration. Reordered fixtures reverse property order; case fixtures uppercase nested names and exercise the actual fallback.

Each parse benchmark includes fresh construction, typed hydration, full admission and complete input framing. Read benchmarks use retained admitted trees and sixteen reads per row: three hits (or sparse misses) and one missing name, repeated four times. Name reads on ordinary/shared storage and prebound ordinal reads are timed separately. Combined benchmarks include parsing, full validation and the same reads. Setup verifies complete equality, hash, canonical bytes and read checksums.

The frozen dependency closure and source hashes are recorded in [the evidence directory](evidence/observation-values-nested-layouts/README.md). Timings use one exact case per fresh process, three repeats, counterbalanced method order, four warmups and six measured 200 ms iterations. No builds, tests, heap diagnostics or other timed runs overlap timing. Figures are medians of process means. Allocation is per complete input tree, including temporary validation costs; large-input traversal-string collisions can vary ordinary-reader allocations between processes. These are bounded warm microbenchmarks, excluding plan preparation and schema creation.

## Parsing and complete admission

| Scenario | Width | Rows | Repeats | Existing ns | Shared ns | Time change | B existing / shared |
|---|---:|---:|---:|---:|---:|---:|---:|
| record | 4 | 1 | 3 | 310.51 | 277.01 | -10.8% | 472 / 288 |
| record | 16 | 1 | 3 | 1052.09 | 865.28 | -17.8% | 1776 / 672 |
| record | 32 | 1 | 3 | 2378.89 | 1650.30 | -30.6% | 4392 / 1184 |
| nested | 4 | 1 | 3 | 559.00 | 499.80 | -10.6% | 984 / 672 |
| nested | 16 | 1 | 3 | 1313.98 | 1132.64 | -13.8% | 2288 / 1056 |
| nested | 32 | 1 | 3 | 2650.59 | 1958.95 | -26.1% | 4904 / 1568 |
| array | 4 | 128 | 3 | 34781.75 | 29568.76 | -15.0% | 34619 / 28793 |
| array | 16 | 128 | 3 | 139402.85 | 110744.59 | -20.6% | 140658 / 77948 |
| array | 32 | 128 | 3 | 390597.46 | 214468.82 | -45.1% | 554315 / 143520 |
| array | 4 | 4096 | 3 | 1483941.19 | 1176232.06 | -20.7% | 1.03165e+06 / 930811 |
| array | 16 | 4096 | 3 | 5295650.00 | 4687162.86 | -11.5% | 4.43343e+06 / 2.51515e+06 |
| array | 32 | 4096 | 3 | 21104583.30 | 8946745.87 | -57.6% | 1.78579e+07 / 4.63128e+06 |
| named-array | 16 | 128 | 3 | 141793.34 | 104727.89 | -26.1% | 150422 / 77965 |
| named-array | 32 | 128 | 3 | 322499.65 | 249518.91 | -22.6% | 554168 / 143520 |
| sparse-array | 16 | 128 | 3 | 78109.19 | 71747.14 | -8.1% | 102208 / 150651 |
| sparse-array | 32 | 128 | 3 | 202235.20 | 138336.86 | -31.6% | 149485 / 242838 |
| sparse-array | 256 | 128 | 3 | 2496780.16 | 1119212.46 | -55.2% | 1.81736e+06 / 1.55992e+06 |
| reordered-array | 16 | 128 | 3 | 138726.96 | 110782.21 | -20.1% | 140652 / 77948 |
| reordered-array | 32 | 128 | 3 | 356358.09 | 216804.25 | -39.2% | 554171 / 143520 |
| case-array | 16 | 128 | 3 | 205255.76 | 242897.01 | +18.3% | 280970 / 281472 |

The primary case-variant fixture is 18.3% slower. A separate longer counterbalanced qualification (four pairs, six warmups, ten 300 ms iterations) gives **209.95 µs existing versus 243.55 µs shared/fallback, +16.0%**. Paired changes are +17.2%, +17.9%, +14.2%, +13.8%. Thus the fallback regression is repeatable in this prototype; its CPU cause is not isolated by these benchmarks. Traversal-name hash collisions vary its allocations, so the raw per-process allocation observations are retained rather than attributing a deterministic byte delta to fallback.


## Retained reads and combined work

| Width | Rows | Repeats | Existing names ns | Shared names ns | Bound ordinals ns | Bound change | B all routes |
|---:|---:|---:|---:|---:|---:|---:|---:|
| 4 | 128 | 3 | 10307.14 | 15082.45 | 2583.27 | -74.9% | 0 / 0 / 0 |
| 16 | 128 | 3 | 11194.91 | 15631.14 | 2643.51 | -76.4% | 0 / 0 / 0 |
| 32 | 128 | 3 | 10868.88 | 15267.24 | 2790.44 | -74.3% | 0 / 0 / 0 |

| Width | Rows | Repeats | Existing parse+reads ns | Shared parse+bound reads ns | Change | B existing / shared |
|---:|---:|---:|---:|---:|---:|---:|
| 16 | 128 | 3 | 155812.30 | 112554.04 | -27.8% | 140583 / 77966 |
| 16 | 4096 | 3 | 6435756.93 | 4781815.09 | -25.7% | 4.76724e+06 / 2.51385e+06 |

## Retained memory and preparation

Heap diagnostics run in separate processes before timing, hold complete result trees alive, force collections before/after construction, and exclude the result-holder array and shared metadata. Heap deltas are approximate; repeated whole trees amortize measurement noise. This is distinct from per-operation BDN allocation, which includes transient validation and parser scratch.

| Scenario | Width | Rows/tree | Trees | Existing retained B/tree | Shared retained B/tree | Change |
|---|---:|---:|---:|---:|---:|---:|
| record | 16 | 1 | 2000 | 1776 | 672 | -62.2% |
| nested | 16 | 1 | 2000 | 2288 | 1056 | -53.8% |
| array | 4 | 128 | 15 | 34584 | 28792 | -16.7% |
| array | 4 | 4096 | 2 | 1018648 | 917624 | -9.9% |
| array | 16 | 128 | 15 | 140536 | 77944 | -44.5% |
| array | 16 | 4096 | 2 | 4394232 | 2490488 | -43.3% |
| array | 32 | 128 | 15 | 434296 | 143480 | -67.0% |
| array | 32 | 4096 | 2 | 13893752 | 4587640 | -67.0% |
| sparse-array | 16 | 128 | 15 | 34584 | 77944 | +125.4% |
| sparse-array | 32 | 128 | 15 | 55224 | 143480 | +159.8% |
| sparse-array | 256 | 128 | 15 | 944248 | 1060984 | +12.4% |

Candidate preparation allocation is reported separately by each diagnostic process. It includes derived child graphs/layouts, nodes, cold runtime/cache initialization and intermediate construction, so it is not retained metadata size or a cold latency benchmark. Reusing a prepared plan across rows/requests is necessary to amortize that work. No cross-tenant or ambient cache is added.

The existing validator's `ObjectFieldLookup.TryGet` creates a temporary case-insensitive dictionary after an exact missing-field lookup. Sparse admission pays for that fallback on both routes. Shared vectors additionally reserve every declared slot; faster dispatch does not imply smaller retained sparse records. No validation shortcut was added to improve these results.

## Verification and abstraction audit

The executable conformance command covers **175 checks** across inline/named objects and arrays: missing, null, invalid numeric values, unknown/case-variant properties, exact/escaped duplicate names, reordered/escaped keys, strict roots, malformed/trailing input, temporal/binary/Json fallbacks, union selection/order, recursive named types, and explicit `ArrayTypeRef`. It compares admitted values, hashes/canonical bytes, validation diagnostics and parser exception types. Input bytes may be overwritten after parsing; separate calls and rows do not share owned construction storage. Invalid-input failure messages remain the reader/validator's responsibility; malformed-input message text is not asserted identical for the experimental fast parser.

The warmed 128×16 dense allocation check passes at **77,944 B/input**, under its 86,016 B bound. Production Core/Relations sources remain unchanged, so their previously completed suites are not repeated for these benchmark-only files. The measured Core method bodies are compared with the fully tested performance build in the evidence. The benchmark project builds without new warnings and `git diff --check` passes.

The experiment adds one private physical plan/node implementation and a differential/diagnostic harness. Record storage, token dispatch, array scratch, canonicalization and admission reuse existing primitives. The synthetic layout projection is intentionally temporary, bounded and benchmark-only; numeric specialization and fallback costs constrain what the data establish. General naming-policy compilation, string/temporal fast paths, union specialization, recursive compiled plans, packed sparse storage, and nested validation prebinding remain unimplemented. No automatic production adoption follows from this benchmark.

The subsequent [fallback profiling and density experiment](observation-values-nested-density.md) isolates the original fallback slowdown, measures a separated execution path and qualifies per-record storage selection. The tables above remain historical evidence for the original candidate.
