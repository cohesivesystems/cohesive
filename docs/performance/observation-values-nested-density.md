# Nested fallback profiling and density-aware storage

## Decision

**Retain separated fallback as the promising execution shape. Keep density selection experimental.** The old case-variant slowdown is primarily a tiering/inlining placement effect, not the work spent discovering a case mismatch. With ordinary runtime settings, fallback changes from +14.1% (old combined method) to +1.5% (separated fixed storage) or +0.5% (density candidate) relative to the direct reader. These small remaining medians are not a universal zero-overhead guarantee.

The half-occupancy prototype removes the earlier sparse retained-memory penalty. At 25% occupancy, 16/32-field retained trees shrink by 59%/64% versus fixed vectors; at 256 fields/6.25% actual occupancy they shrink by 87%. But compact storage gives up constant-time ordinal reads, and CPU results vary materially with width, occupancy and caller context. This is not evidence for a universal 50% selection threshold or automatic production migration.

All changes are in the benchmark project and performance documentation. The original graph/types, JSON semantics and validator remain authoritative. The experiment starts from the prior [nested layout benchmark](observation-values-nested-layouts.md), commit `940739c1`.

## Fallback: evidence and mechanism

The initial CPU/JIT capture uses a frozen profiling build with the original parser intact, plus two ablations: forced fallback without speculation and the speculative prefix without fallback/admission. One exact case runs per fresh process: sixteen fields, 128 rows, uppercase nested names. The sampled loop is marked by `NestedLayoutProfile.Measure`; setup/warmup stacks are excluded.

| Initial ablation | Repeats | ns | B |
|---|---:|---:|---:|
| ParseExisting | 3 | 204121.69 | 290710 |
| ParseShared | 3 | 238796.80 | 281464 |
| ParseForcedFallback | 3 | 203213.32 | 290709 |
| ProbeShared | 3 | 132.30 | 592 |

The failed prefix is only about 0.13 µs and 592 B, while the original combined route is about 35 µs slower than direct ingestion. Forced fallback matches the direct route. Avoiding one failed ordinal lookup or a small allocation cannot account for that gap.

Matching native code and CLR method-load tiers show:

- The original `NestedLayoutCandidate.Parse` contains `stackalloc` and is compiled as **Tier0-FullOpts**, 1,016 native bytes. Its fallback path remains incorporated in that non-tiering code shape.
- The direct benchmark's reader wrapper reaches **OptimizedTier1**, 2,868 native bytes, with additional profiled inlining. The standalone original `ParseExisting` helper remains 444 bytes; caller placement changes the effective optimized path.
- Separating the speculative/full-admission path into a non-inlined helper leaves a tierable wrapper owning fallback. Its wrapper and fallback reach **OptimizedTier1** (2,928 and 2,820 bytes in the capture), and the benchmark wrapper reaches 2,956 bytes. The helper retains bounded stack presence storage and the same validator.

Native sizes are diagnostic evidence from the corresponding process, not a code-size optimization target. The old control is intentionally retained alongside the separated version so the JIT-relevant shape can be measured without changing its algorithm. Production adoption should keep one implementation of the successful parser/framing contract; these are benchmark controls, not parallel semantic authorities.

EventPipe managed-thread samples spend most active managed time in decoding, case-insensitive comparison/hash work and validation. Many samples land in GC polling; those are sampled thread time, not proof that all corresponding time is active CPU. Trace overhead and GC waiting mask small throughput differences, so performance conclusions use the separate untraced BDN runs. Complete traces, native code, method versions, sample counts and offsets are retained in the evidence.

## Fallback qualification

Three counterbalanced repeats, six warmups and ten measured 300 ms iterations per fresh process; same final binary for all routes:

| Fallback route | Repeats | ns | vs existing | B |
|---|---:|---:|---:|---:|
| ParseExisting | 3 | 207873.41 | +0.0% | 290696 |
| ParseShared | 3 | 237172.38 | +14.1% | 291288 |
| ParseSharedSeparated | 3 | 210952.76 | +1.5% | 281448 |
| ParseDensityAware | 3 | 208854.92 | +0.5% | 280912 |

A bounded causal control disables tiered compilation for two fresh-process repeats per route. It is a diagnostic, not a recommended runtime configuration; absolute costs increase substantially. Within that configuration, the old combined gap collapses to about 1%:

| Tiering-disabled route | Repeats | ns | vs direct |
|---|---:|---:|---:|
| ParseExisting | 2 | 423993.55 | +0.0% |
| ParseShared | 2 | 428207.99 | +1.0% |
| ParseSharedSeparated | 2 | 436755.87 | +3.0% |

This combination of ablation, method-version/native evidence, separated-path qualification and runtime control supports tiering/inlining placement as the main explanation for the old regression. It does not isolate every last percent of wrapper overhead or imply that all future callers will compile identically.

## Density policy and ownership

The root keeps its existing ordinal representation. A nested record wider than Core's eight-field inline limit rents a temporary value vector, clears its active slice, parses canonical names through its prepared layout and counts distinct present fields. Null counts as present; exact decoded duplicates overwrite without increasing count.

At end of the record:

- At **half occupancy or below**, freeze into the existing small-field representation (up to eight properties) or a pre-sized owned dictionary. Names are shared immutable schema strings.
- Above half occupancy, copy into a fresh exact ordinal vector and retain the shared layout.
- Records at or below the existing eight-field bound keep the fixed-vector route.

The pool rental is returned and cleared in `finally`, on success, malformed input and fallback. No pooled buffer escapes into retained values. Selection is per actual record, so mixed arrays can contain compact and ordinal records without choosing a layout from the first row or inferring/caching input schemas.

Nested case variants, unknown names, unsupported types and ambiguous declarations still use the original whole-input reader. Required/missing/null rules, decoded duplicate behavior, typed restoration, union selection, root strictness and failure precedence stay under the same reader/validator. Complete admission precedes projection. Private synthetic child shapes remain benchmark-only physical artifacts as in the preceding experiment.

## Parsing and full admission

Normal-runtime results use three repeats, four warmups and six measured 200 ms iterations, one exact case per fresh process. Fixed and density routes use the same separated fallback structure. Percentages are requested fixture density; 6% of 256 declares sixteen present fields (6.25% actual). Mixed rows alternate quarter/full occupancy. Each input contains a strict `payload` envelope. Plans, metadata preparation and fixture generation are excluded.

| Scenario | Width | Rows | Present % | Repeats | Existing ns | Fixed ns | Density ns | Density vs existing | Density vs fixed | B existing / fixed / density |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| array | 16 | 128 | 25 | 3 | 69040.82 | 67978.90 | 72182.24 | +4.6% | +6.2% | 102200 / 150648 / 99448 |
| array | 16 | 128 | 50 | 3 | 113033.49 | 94333.20 | 153713.40 | +36.0% | +62.9% | 149464 / 177272 / 146552 |
| array | 16 | 128 | 100 | 3 | 138861.25 | 107271.21 | 112946.59 | -18.7% | +5.3% | 140568 / 77944 / 77944 |
| array | 32 | 128 | 25 | 3 | 160936.40 | 136543.16 | 297276.91 | +84.7% | +117.7% | 149464 / 242808 / 146552 |
| array | 32 | 128 | 50 | 3 | 203964.54 | 183202.62 | 209835.47 | +2.9% | +14.5% | 280856 / 282744 / 277624 |
| array | 32 | 128 | 100 | 3 | 326555.14 | 216937.43 | 212221.40 | -35.0% | -2.2% | 554136 / 143480 / 143480 |
| array | 256 | 128 | 25 | 3 | 1785878.39 | 1149403.64 | 1067047.54 | -40.3% | -7.2% | 1.81673e+06 / 1.55967e+06 / 996472 |
| array | 256 | 128 | 50 | 3 | 3431026.89 | 1543650.82 | 1556584.59 | -54.6% | +0.8% | 3.86166e+06 / 1.95903e+06 / 1.79519e+06 |
| array | 256 | 128 | 100 | 3 | 7649862.83 | 1716231.01 | 1764419.85 | -76.9% | +2.8% | 6.49334e+06 / 1.06098e+06 / 1.06098e+06 |
| array | 32 | 128 | 75 | 3 | 292118.56 | 235746.11 | 246059.78 | -15.8% | +4.4% | 613976 / 362616 / 362616 |
| array | 256 | 128 | 6 | 3 | 689470.62 | 811442.35 | 692807.15 | +0.5% | -14.6% | 280856 / 1.20025e+06 / 277624 |
| array | 4 | 128 | 100 | 3 | 33750.06 | 28395.96 | 28237.61 | -16.3% | -0.6% | 34616 / 28792 / 28792 |
| array | 16 | 4096 | 100 | 3 | 4874379.77 | 3683239.01 | 3740796.08 | -23.3% | +1.6% | 4.72154e+06 / 2.49049e+06 / 2.49049e+06 |
| mixed-array | 32 | 128 | 100 | 3 | 261589.97 | 181127.54 | 197964.23 | -24.3% | +9.3% | 287080 / 193144 / 145016 |

Small/medium compact cases are sensitive to return/caller context. The 32-field, quarter-full density parse process means are **297.28, 172.30 and 362.00 µs**. The combined parse/admission/read benchmark is **164.98, 163.50 and 165.72 µs**, despite doing additional reads. The existing route also has one slow process in that parse-only case. Thus the parse-only median is not an intrinsic algorithmic cost estimate: result lifetime, GC and JIT/caller effects have not been isolated for the density path. Raw observations are retained, rather than omitting that row or substituting the faster combined result.

At sixteen fields/half occupancy, compact selection consistently loses substantial CPU despite smaller retained storage. At 256 fields/quarter occupancy it saves memory and is faster than both existing and fixed paths in the recorded medians. Dense guards preserve retained size, with a few percent pooled-scratch time overhead in some cases. The half-full rule is a measured prototype policy, not a proven optimal crossover.

## Reads and combined work

Sixteen exact-name/missing reads per retained row. The fixed route uses bound ordinals; the density route uses ordinals where its physical storage supports them and otherwise the existing exact-name path. These are semantically equivalent reads, but compact dictionary/small storage cannot preserve the fixed route's ordinal lookup speed without another physical design.

| Sparse read route | Repeats | ns | B |
|---|---:|---:|---:|
| ReadExisting | 3 | 8597.42 | 0 |
| ReadOrdinals | 3 | 2544.51 | 0 |
| ReadDensityAware | 3 | 8699.68 | 0 |

| Sparse combined route | Repeats | ns | B |
|---|---:|---:|---:|
| ParseExistingAndRead | 3 | 162657.66 | 149464 |
| ParseSharedSeparatedAndRead | 3 | 140085.88 | 242808 |
| ParseDensityAwareAndRead | 3 | 164981.20 | 146552 |

## Retained memory and allocation

Forty-two fresh-process heap diagnostics run before all timed work, retaining whole result trees. Deltas exclude holder arrays and already-live prepared metadata and are approximate. Allocation and retained memory are separate metrics: pooled scratch never escapes, but validation still creates temporary case-insensitive indexes when optional fields are missing.

| Scenario | Width | Rows | Present % | Trees | Existing retained B/tree | Fixed B/tree | Density B/tree | Density vs fixed |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| array | 16 | 128 | 25 | 15 | 34584 | 77944 | 31864 | -59.1% |
| array | 16 | 128 | 50 | 15 | 55224 | 77944 | 52344 | -32.8% |
| array | 16 | 128 | 100 | 15 | 140536 | 77944 | 77944 | +0.0% |
| array | 32 | 128 | 25 | 15 | 55224 | 143480 | 52344 | -63.5% |
| array | 32 | 128 | 50 | 15 | 150376 | 143480 | 137336 | -4.3% |
| array | 32 | 128 | 100 | 15 | 434296 | 143480 | 143480 | +0.0% |
| array | 256 | 128 | 25 | 15 | 944248 | 1060984 | 496760 | -53.2% |
| array | 256 | 128 | 50 | 15 | 1990776 | 1060984 | 896120 | -15.5% |
| array | 256 | 128 | 100 | 15 | 4203640 | 1060984 | 1060984 | +0.0% |
| array | 32 | 128 | 75 | 15 | 293896 | 143480 | 143480 | +0.0% |
| array | 256 | 128 | 6 | 15 | 140536 | 1060984 | 137336 | -87.1% |
| array | 4 | 128 | 100 | 15 | 34584 | 28792 | 28792 | +0.0% |
| array | 16 | 4096 | 100 | 2 | 4394232 | 2490488 | 2490488 | +0.0% |
| mixed-array | 32 | 128 | 100 | 15 | 170120 | 143480 | 97912 | -31.8% |

Preparation allocation is recorded per selected candidate separately and excluded from timing. Fixed preparation runs before density preparation, so cold process initialization is not symmetric; these counters are not a fair cold-start latency comparison. The immutable graph/type declarations remain shared. No global text interner, schema inference or ambient cache is introduced.

## Verification and limits

**223 differential scenarios across three candidate variants pass**, including all prior nested cases plus density sweeps, mixed storage, wide malformed input, fallback after pool rental and null/duplicate presence. Equality, hashes/canonical bytes, diagnostics, parser exception types, caller-input independence and output ownership remain checked. Warm dense 128×16 allocation is **77,944 B** (budget 86,016 B); quarter-full 128×16 is **99,448 B** (budget 125,952 B). Mixed rows are checked to select compact/ordinal storage independently.

The final Core matches all **11,842 method bodies** of the previously fully tested performance build. No production Core/Relations/Storage source changes are made. The benchmark project builds without new warnings and `git diff --check` passes. There are 12 initial ablation timings, 156 main timings, six tiering controls, four EventPipe/JIT captures and 42 heap diagnostics. Builds, tracing, analysis and heap diagnostics do not overlap timing.

[Evidence and reproduction](evidence/observation-values-nested-density/README.md) include both frozen binary/source manifests, raw reports/traces, scripts and per-process observations. The original profiling build and final density build are distinct and labeled; their measurements are not pooled together. Historical nested-layout evidence remains tied to its original source revision.

The next useful experiment is a sparse ordinal representation or a cheaper construction strategy that retains bound reads, together with profiling of the density path's caller/GC/JIT sensitivity. This benchmark does not add such a core storage abstraction, generalize synthetic shape identity into a public API, choose production density policy, or migrate existing ingestion boundaries.
