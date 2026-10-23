# Shared record layouts for JSON parsing and ordinal access

## Question and boundaries

The [contiguous field-storage experiment](observation-values-field-storage.md) reduced some allocations but lost construction and lookup time. This experiment instead asks whether an already known record shape can remove repeated property-name decoding, per-record name indexes and runtime name resolution.

The semantic authority remains `ShapeGraph` and its field definitions. `ObservationLayout` is an existing shared physical interpretation of one exact graph-qualified shape, and `OrdinalObservationFields` is an existing immutable value-vector interpretation. This work adds benchmarks and conformance coverage; it changes no production parser, storage, admission rule or compiler binding policy.

The two ingestion paths are deliberately explicit:

- **Generic, validated:** `ObservationJsonReader.ReadValue`, followed by one `ObservationValidator.TryValidateAgainstShape` call. Subsequent reads use `ObservationValue.TryGetProperty`.
- **Shared, validated:** `ObservationJsonReader.TryReadShape` into a fresh owned value array and presence bitmap. Successful validation is followed by ownership transfer to the existing ordinal field wrapper. Subsequent reads use ordinals resolved against that exact layout during setup. The composition is benchmark-only, not another production ingestion API.

Both paths validate the same complete numeric records and produce equal values, hashes and exact canonical bytes. Their invalid-input contracts remain different: generic JSON retains the last decoded duplicate key and arbitrary exact-case names before admission; strict layout JSON rejects unknown, wrong-case and duplicate root fields. Unknown fields also fail shape admission on the generic path, but with a different failure boundary. The benchmark is a comparison on the common valid-record domain, **not permission to substitute strict parsing at an arbitrary JSON boundary**. Ordinary present Undefined values are not silently reinterpreted: ordinal shaped storage uses Undefined as its absent-slot sentinel, while JSON cannot encode an explicit Undefined value.

A third measured method uses the existing public `IndexedObservationOccurrence.FromJson` API and its ordinal reads. It includes occurrence/buffer bookkeeping and demonstrates the cost of a callable production route, rather than only the benchmark's lower-level composition.

## Method

`ObservationSharedLayoutBenchmarks` uses 4, 16 and 32 required Int64 fields, with reverse input order. Every record is independently parsed and owns its values. One graph and layout are retained across all occurrences; setup and query binding are excluded from warm timing. Read counts are 0, 1, 16 and 64 per parsed record. Queries cycle through first, middle, last and missing names; separately allocated query strings avoid reference-identity hits against decoded names. The one-read case probes the first field. A bound invalid ordinal represents the explicit miss probe, not a proposed compiler policy for unknown selectors.

Separate retained-value methods compare generic name reads, shared-storage name reads and shared-storage ordinal reads in sixteen-query batches. Reported lookup costs divide batch time by sixteen, including loop and dispatch overhead. These measurements separate representation effects from prebinding effects.

Three fresh-process runs per method and parameter set use BenchmarkDotNet with `--inProcess`, four warmup iterations and six measured iterations of 200 ms. Each process runs exactly one case; paired ingestion order alternates between repeats. No unrelated width or method shares the timed process. Both paths use the same frozen assembly closure, recorded by SHA-256. Builds, tests, traces and heap diagnostics do not overlap timed runs. Runtime priority elevation was unavailable.

A separate `--shared-layout-retained WIDTH ROUTE COUNT` mode keeps 20,000 objects alive across forced full compacting collections, reporting allocation and live-heap deltas separately. It uses the validated last-GC heap-size-minus-fragmentation method from the previous investigation, now factored into one shared benchmark helper. The outer value array and shared graph/layout are allocated before the heap baseline. A separate counter reports allocations incurred by first creating the layout, excluding prior graph/shape construction; it includes cache initialization and is not a precise retained-metadata size or cold-latency measurement.

See [the evidence directory](evidence/observation-values-shared-layout/README.md) for scripts, full results and test logs.

## Verification and rejected preliminary runs

The verified dictionary Core DLL exactly matches the frozen pre-vector control (SHA-256 `698305941afad42c05f26b4e4edd3ed28b6e094760d04db6259de6c708246f5a`), including all 11,812 method bodies. A pre-timing allocation smoke check requires 1,680 B/record for sixteen-field generic objects. Each timing result is checked for exactly one selected method and exact width/read parameters.

Preliminary data were discarded after two harness issues: a prefix glob selected both one and sixteen reads, and incremental compilation initially retained the previous vector experiment's DLL despite restored source. No preliminary timings are used in the results. The runner now uses closing-delimited exact parameter filters, a frozen Core hash guard and the allocation smoke check. The previous field-storage report's restoration note and test evidence have also been corrected; its frozen candidate/control comparisons remain valid.

Against the freshly verified dictionary DLL, 143 core tests and 17 public indexed-ingestion tests pass. These include canonical equality/hashing, input ownership, escaped and segmented names, exact graph qualification, missing/Null distinctions, duplicate/case/unknown rules, ordinal bounds, typed restoration and bounded allocation. The new allocation checks allow only the owned value vector and ordinal wrapper on successful warm numeric reads.

## Results

Median of three process means for validated parsing followed by the requested number of reads. Negative change means faster; allocation is per record. Zero reads isolates validated construction.

| Fields | Reads | Pairs | Generic ns | Shared+ordinal ns | Change | Generic B | Shared B |
|---:|---:|---:|---:|---:|---:|---:|---:|
| 4 | 0 | 3 | 217.12 | 179.84 | -17.2% | 376 | 192 |
| 4 | 1 | 3 | 223.71 | 182.07 | -18.6% | 376 | 192 |
| 4 | 16 | 3 | 293.93 | 206.74 | -29.7% | 376 | 192 |
| 4 | 64 | 3 | 511.07 | 261.83 | -48.8% | 376 | 192 |
| 16 | 0 | 3 | 721.09 | 673.14 | -6.6% | 1680 | 576 |
| 16 | 1 | 3 | 724.28 | 683.28 | -5.7% | 1680 | 576 |
| 16 | 16 | 3 | 790.46 | 708.18 | -10.4% | 1680 | 576 |
| 16 | 64 | 3 | 998.10 | 759.72 | -23.9% | 1680 | 576 |
| 32 | 0 | 3 | 1517.21 | 1345.94 | -11.3% | 4296 | 1088 |
| 32 | 1 | 3 | 1520.94 | 1371.11 | -9.9% | 4296 | 1088 |
| 32 | 16 | 3 | 1590.08 | 1382.16 | -13.1% | 4296 | 1088 |
| 32 | 64 | 3 | 1798.76 | 1441.07 | -19.9% | 4296 | 1088 |

### Retained reads

| Fields | Generic name ns/query | Shared name ns/query | Ordinal ns/query | Generic / ordinal |
|---:|---:|---:|---:|---:|
| 4 | 4.36 | 4.60 | 1.12 | 3.9× |
| 16 | 4.20 | 5.03 | 1.09 | 3.9× |
| 32 | 4.58 | 4.77 | 1.18 | 3.9× |

Ordinal reads are about four times faster than generic name reads in this mixed probe. Shared storage alone does not accelerate name-based reads; it is slightly slower in these cases. Bind hot consumers to the shared layout once rather than merely swapping storage behind repeated name lookup. These are warm mixed-query costs, not a universal field-access speedup.

### Public production API cross-check

| Fields | Generic + 16 reads ns | Public indexed + 16 reads ns | Change | Public allocation B |
|---:|---:|---:|---:|---:|
| 4 | 293.93 | 204.59 | -30.4% | 224 |
| 16 | 790.46 | 705.75 | -10.7% | 608 |
| 32 | 1590.08 | 1387.60 | -12.7% | 1120 |

The public indexed route uses 32 more allocated bytes per record than the benchmark's lower-level Core composition, while preserving the existing occurrence, shape validation, ownership and strict JSON boundary. Its timings independently support the proposed use; adding a production Core factory is not required to use the existing indexed API.

### Retained memory

| Fields | Generic allocation B | Shared allocation B | Generic retained B (approx.) | Shared retained B (approx.) |
|---:|---:|---:|---:|---:|
| 4 | 376 | 192 | 376 | 192 |
| 16 | 1680 | 576 | 1680 | 576 |
| 32 | 4296 | 1088 | 3360 | 1088 |

Approximate heap deltas agree with owned object sizes after rounding. Warmup temporaries can contribute a one-record offset to the baseline, negligible across 20,000 retained records. Generic thirty-two-field allocation includes discarded dictionary growth tables, explaining its allocation/retained difference. Shared storage retains its one value vector and wrapper; property names and name-index metadata are shared with the layout.

First layout-factory allocations in the diagnostic process are approximately 42–47 KB, including cold runtime/encoding/cache initialization. This is not retained per-layout size. Shared metadata and binding must be amortized over repeated records; per-record numbers exclude them on both routes.

## Decision and limits

**Shared layouts are promising for known-shape ingestion and prebound field access.** Construction alone improves modestly in the 16/32-field cases; the clearer wins are lower per-record allocation and avoiding repeated name lookup. More reads increase the benefit. The existing public indexed ingestion path can already realize these gains.

No production parsing or storage policy is changed by this benchmark task. Keep general JSON semantics intact. Use the shared path only where the boundary has the exact shape and already requires strict root admission; do not globally infer/cache tenant record schemas or silently reject formerly accepted duplicate/unknown fields. Bind reads to the same exact layout and keep existing semantic validation.

The evidence covers dense flat numeric records and warm repeated use. It does not establish speedups for sparse wide shapes, nested/string-heavy data, tagged payloads, cold one-off records, layout contention or end-to-end storage/network pipelines. Existing public conformance tests cover shaped primitive restoration and union behavior, but those are correctness evidence rather than timing evidence here. The next implementation step should target an actual strict, shape-aware caller and bind its hot reads to the existing layout; extend representative workload measurements before broader adoption.

Abstraction audit: parsing and validation remain in the existing reader/validator; storage uses the existing ordinal type; no new semantic catalog or production cache is introduced. The only shared mechanism extracted is diagnostic heap collection for the two benchmark families. Allocation regression tests enforce the successful numeric-path ownership bound; timing remains in BenchmarkDotNet evidence.

Final checks: the 108 retained timing cases each select one exact method/width/read combination; all three paired repeats favor the shared ingestion path in every measured combination. Retained-read methods allocate zero bytes. The 18 heap diagnostics, invalid-argument checks, old field-storage diagnostic smoke check and final frozen-manifest verification pass. All three test/benchmark Core copies match the accepted dictionary DLL. `git diff --check` passes.
