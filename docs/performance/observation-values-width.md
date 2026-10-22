# Observation JSON object width and dictionary insertion

## Question and semantic boundary

The previous [CPU/JIT investigation](observation-values-cpu-jit.md) found that parser code generation depends on the shape used to train shared methods. Four-field records stay inline; sixteen-field objects cross the eight-field storage limit. This experiment measures the boundary directly before changing it.

`ObservationJsonObjectBuilder` remains the sole authority for promotion, ordinal decoded-name equality, plain-reader last-property-wins replacement, tagged-reader duplicate rejection, and transfer of freshly owned storage. The experiment introduces no new semantic model or public API. Every operation retains a fresh object; allocations include decoded property names, retained field storage and, for wider objects, dictionary construction/growth.

## Method

`ObservationJsonWidthBenchmarks` covers 1, 4, 8, 9, 16 and 32 distinct numeric properties. Setup checks equality against the DOM interpretation, then parses either a four-field or thirty-two-field object for one second. BenchmarkDotNet subsequently warms and measures the selected width. This captures early compilation history followed by sustained target-width use, not permanently frozen training profiles.

Each width/training combination runs in a fresh process with `--inProcess`; cases never share a process. Three alternating control/candidate pairs use four warmup iterations and six measured iterations of 200 ms. Runtime/dependency closure and benchmark assembly are identical; only `Cohesive.dll` differs. Timing runs do not overlap builds, tests or other benchmarks. The low-priority host could not elevate process priority; repeated means are evidence rather than a portable latency guarantee.

The candidate replaces the dictionary-backed indexer branch in `Set` with a private `NoInlining` helper. Inline lookup, promotion and `TryAdd` remain unchanged. This specifically tests whether independently compiling insertion avoids narrow-object training suppressing useful dictionary code generation; it does not assume a fixed JIT inlining budget is the cause.

Raw results, runtime manifest and scripts are in [the evidence directory](evidence/observation-values-width/README.md).

## Results and decision

Median of the three process means, with positive change meaning slower:

| Warmup | Fields | Control ns | Split ns | Change | Bytes/op |
|---:|---:|---:|---:|---:|---:|
| 4 | 1 | 58.81 | 59.41 | +1.0% | 136 |
| 4 | 4 | 168.33 | 165.70 | -1.6% | 376 |
| 4 | 8 | 313.54 | 310.66 | -0.9% | 696 |
| 4 | 9 | 389.56 | 353.76 | -9.2% | 1400 |
| 4 | 16 | 563.56 | 604.49 | +7.3% | 1680 |
| 4 | 32 | 1227.73 | 1295.83 | +5.5% | 4296 |
| 32 | 1 | 59.60 | 58.94 | -1.1% | 136 |
| 32 | 4 | 164.09 | 162.77 | -0.8% | 376 |
| 32 | 8 | 308.38 | 314.68 | +2.0% | 696 |
| 32 | 9 | 356.93 | 354.44 | -0.7% | 1400 |
| 32 | 16 | 560.29 | 571.99 | +2.1% | 1680 |
| 32 | 32 | 1221.75 | 1211.31 | -0.9% | 4296 |

The insertion split is rejected. It improves the nine-field case after narrow training, but regresses sixteen fields in all three paired runs under both training shapes. Thirty-two fields after narrow training are inconsistent (one pair improves, two regress). Narrow objects have no material consistent improvement, and every case allocates exactly the same amount on both sides. A private helper and forced compilation boundary are not justified by this tradeoff. Production builder source is restored byte-for-byte to its pre-experiment contents.

The eight-to-nine per-operation allocation jump is real, while timing around that boundary depends on compilation history and dictionary/inline lookup costs. Wider objects also incur dictionary growth (32 fields allocate 4,296 bytes/op). This is evidence to investigate the construction representation itself rather than retain an inlining annotation. These results alone do not justify changing the eight-field limit.

Retained work: the reusable width benchmark and expanded differential conformance fixtures at 4, 16 and 32 fields, including duplicate replacements at each measured width. No new production type or authority was introduced. Broader pipeline benchmarks were not rerun because the production experiment was rejected; earlier pipeline evidence remains applicable.

A useful next experiment would compare incremental dictionary promotion with an owned contiguous field builder, measuring construction, retained bytes and later lookup together. Such a change must preserve duplicate policy and avoid shifting construction savings into repeated lookup costs.

## Verification

The candidate and restored source each pass 79 focused tests covering parser differential conformance, duplicate replacement across all measured widths, decoded string ownership/reuse, tagged streaming reads and value semantic contracts. The original/restored builder source SHA-256 digests match. Benchmark builds succeeded with existing unrelated analyzer/nullability warnings, and `git diff --check` passes.

The follow-up [field storage comparison](observation-values-field-storage.md) measures construction, subsequent lookup and surviving heap for bounded vectors versus dictionary promotion.
