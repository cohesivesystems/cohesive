# Dictionary promotion versus bounded contiguous field storage

## Scope and semantic authority

This follows the [object-width investigation](observation-values-width.md), which rejected a dictionary insertion outlining experiment. The question here is whether changing physical construction and retained storage can improve object costs without shifting them into subsequent lookup.

The candidate keeps inline storage through eight distinct fields, spills into a private sixteen-element vector, grows it to thirty-two elements, then promotes to an owned dictionary at the thirty-third distinct field. Completed vectors are sorted in ordinal key order and transferred to the existing `SmallObservationFields` wrapper. Its lookup remains linear through eight fields and uses binary search above eight. The DOM projection remains unchanged and serves as a differential reference. Existing prepared projections with 9–16 fields also use the changed lookup method; the experiment does not introduce a second vector type.

`ObservationJsonObjectBuilder` remains responsible for decoded duplicate detection/replacement and storage promotion. Plain JSON keeps last-property-wins behavior; tagged objects reject duplicates. Caller-owned mutable input is never adopted. Published vectors retain exclusive array ownership, and their unused capacity contains no references. Intermediate vectors become collectible after growth or dictionary promotion. The maximum vector width bounds the scan cost; larger input uses the existing dictionary strategy.

## Measurements

`ObservationJsonFieldStorageBenchmarks` measures numeric objects with 4, 8, 9, 16, 32, 33 and 64 fields:

- `Parse`: input-to-owned-value construction, including decoded names, promotion, sorting and storage allocation.
- `Lookup`: sixteen queries on a retained value, evenly distributed between first, middle, last and missing names. Results are reported per query (`OperationsPerInvoke = 16`), include query-loop overhead and allocate no storage. Query strings are separately allocated from decoded names.
- `ParseAndLookup`: fresh parsing followed by that sixteen-query batch, reported per object.

Setup verifies equality with the DOM interpretation and checks consumed lookup totals. It then trains parsing and lookup on four-field or sixty-four-field objects for one second. Each method, width and training combination runs in its own fresh process with `--inProcess`, four warmup iterations and six measured iterations of 200 ms. Early training can influence compilation even though later warmup and measurement use the target width. The two frozen dependency closures have identical benchmark/runtime assemblies and differ only in `Cohesive.dll`. Timed runs do not overlap builds, tests, traces or other timed runs.

A separate CLI mode, `--field-storage-retained WIDTH COUNT`, keeps a batch of freshly parsed objects alive across forced full compacting GC. It reports surviving heap delta (`HeapSizeBytes - FragmentedBytes` from `GetGCMemoryInfo`) separately from thread allocation. The outer value array and benchmark fixture are allocated before the baseline and excluded; results are approximate heap observations rather than CI thresholds. Growth buffers and discarded dictionary tables count toward allocation but should not survive collection.

Environment and raw results are recorded in [the evidence directory](evidence/observation-values-field-storage/README.md).

## Timing outcome

Three pairs of the combined parse-plus-sixteen-reads workload confirm regressions under both training regimes. Positive changes mean slower:

| Fields | Narrow training | Wide training | Allocation, dictionary → vector |
|---:|---:|---:|---:|
| 9 | +17.4% | +13.4% | 1,400 → 1,056 B |
| 16 | +58.4% | +51.2% | 1,680 → 1,336 B |
| 32 | +103.2% | +85.5% | 4,296 → 3,280 B |

The component screen explains the combined cost. After narrow training, sixteen-field parsing increases from 554 to 841 ns and mixed lookup from 4.10 to 8.41 ns/query. After wide training the corresponding values are 536 → 809 ns and 4.47 → 9.79 ns/query. Thirty-two-field lookup in the narrow screen increases from 4.28 to 13.66 ns/query. These component values are single process pairs; the combined result is the repeatedly qualified finding.

Fallback also matters: the single-pair thirty-three-field screen allocates 4,336 → 7,136 B and slows combined work by 64.7%, because it pays for both vectors before dictionary creation. At sixty-four fields allocation decreases (10,256 → 8,376 B) because the dictionary capacity/growth sequence changes, but combined work is still 26% slower in that screen. The unmodified four/eight-field paths remain close in this screen; this is not a claim of measured improvement on narrow objects.

These results apply to this bounded sorted-vector candidate and generated key/query distribution. They do not establish that every contiguous representation is slower. In particular, a graph-qualified ordinal layout avoids different work and is a separate existing physical realization.

## Heap-accounting validation

The first diagnostic used `GC.GetTotalMemory(true)`. Even the unchanged four-field case reported approximately 15 MB of surviving growth after only 7.52 MB of total thread/process allocation. A separate probe confirmed that this counter disagreed with `GetGCMemoryInfo` in the installed runtime: full compacted heap size minus fragmentation was consistent with the allocation count, while `GetTotalMemory` was not. No runtime root cause is asserted here.

Those readings are preserved as **invalid evidence** (`unreliable-get-total-memory.csv`) and excluded from retained-memory conclusions. The corrected diagnostic uses explicit full compacting collections and the last GC's heap size minus fragmentation. Timing runs use the original frozen benchmark harness; corrected heap runs use a separately recorded harness revision that changes only the heap diagnostic code. Both control/candidate runtime pairs still differ only in Core.

## Retained memory and decision

Corrected heap deltas agree across all three fresh-process pairs at each width. These medians exclude the outer `ObservationValue[]` and fixture:

| Fields | Dictionary retained B/object | Vector retained B/object | Change |
|---:|---:|---:|---:|
| 4 | 376 | 376 | +0.0% |
| 8 | 696 | 696 | +0.0% |
| 9 | 1400 | 1056 | -24.6% |
| 16 | 1680 | 1336 | -20.5% |
| 32 | 3360 | 2616 | -22.1% |
| 33 | 3400 | 5168 | +52.0% |
| 64 | 7344 | 6408 | -12.7% |

The thirty-two-field candidate allocates 3,280 B/object but retains 2,616 B: its discarded sixteen-element buffer accounts for the 664 B difference. The dictionary control allocates 4,296 B and retains 3,360 B because earlier table storage is collectible. At thirty-three fields, delaying promotion changes both temporary buffers and final dictionary capacity, so retained memory rises as well as allocation. At sixty-four fields, the candidate's different capacity sequence reduces retained memory but still loses construction time.

**Reject the candidate.** The bounded vector improves memory for some widths, but not CPU, subsequent lookup or fallback behavior. Both production files are restored to their exact pre-experiment bytes. No new storage type, lookup policy or tuning constant remains in production. The existing dictionary boundary remains at nine distinct fields.

Retained additions are the reusable construction/lookup/heap benchmark, CLI dispatch, differential conformance cases across growth/fallback boundaries, tagged duplicate checks, and this evidence. The candidate passes 100 focused semantic tests. Broader pipeline timing and full-suite retesting are unnecessary for this rejected production change; the subsequent fresh-build verification below checks the restored implementation against the expanded cases. No portable timing threshold is added.

The result argues against simply raising the small-vector limit. A future representation experiment should avoid both repeated string scans during construction and multiple string comparisons per lookup; reusing existing graph-qualified ordinal layouts where the caller already knows the record shape is a separate option. This investigation does not select or implement another representation.

## Complete timing table

Medians of process means; repeat counts distinguish screened components from the repeatedly qualified combined cases. `Lookup` is per query; other rows are per object.

| Warmup | Fields | Method | Pairs | Dictionary ns | Vector ns | Change | Dictionary B | Vector B |
|---:|---:|---|---:|---:|---:|---:|---:|---:|
| 4 | 4 | Lookup | 1 | 4.59 | 4.49 | -2.2% | 0 | 0 |
| 4 | 4 | Parse | 1 | 159.34 | 159.86 | +0.3% | 376 | 376 |
| 4 | 4 | ParseAndLookup | 1 | 230.57 | 225.02 | -2.4% | 376 | 376 |
| 4 | 8 | Lookup | 1 | 7.53 | 7.15 | -5.0% | 0 | 0 |
| 4 | 8 | Parse | 1 | 303.97 | 300.94 | -1.0% | 696 | 696 |
| 4 | 8 | ParseAndLookup | 1 | 416.71 | 413.64 | -0.7% | 696 | 696 |
| 4 | 9 | Lookup | 1 | 3.74 | 7.78 | +107.8% | 0 | 0 |
| 4 | 9 | Parse | 1 | 350.77 | 350.79 | +0.0% | 1400 | 1056 |
| 4 | 9 | ParseAndLookup | 3 | 411.73 | 483.37 | +17.4% | 1400 | 1056 |
| 4 | 16 | Lookup | 1 | 4.10 | 8.41 | +105.1% | 0 | 0 |
| 4 | 16 | Parse | 1 | 554.13 | 841.09 | +51.8% | 1680 | 1336 |
| 4 | 16 | ParseAndLookup | 3 | 632.12 | 1001.47 | +58.4% | 1680 | 1336 |
| 4 | 32 | Lookup | 1 | 4.28 | 13.66 | +218.8% | 0 | 0 |
| 4 | 32 | Parse | 1 | 1228.24 | 2432.33 | +98.0% | 4296 | 3280 |
| 4 | 32 | ParseAndLookup | 3 | 1279.87 | 2601.33 | +103.2% | 4296 | 3280 |
| 4 | 33 | Lookup | 1 | 4.16 | 4.20 | +1.1% | 0 | 0 |
| 4 | 33 | Parse | 1 | 1331.48 | 2202.02 | +65.4% | 4336 | 7136 |
| 4 | 33 | ParseAndLookup | 1 | 1352.22 | 2227.43 | +64.7% | 4336 | 7136 |
| 4 | 64 | Lookup | 1 | 3.81 | 4.23 | +10.9% | 0 | 0 |
| 4 | 64 | Parse | 1 | 2475.93 | 3118.47 | +26.0% | 10256 | 8376 |
| 4 | 64 | ParseAndLookup | 1 | 2551.76 | 3214.83 | +26.0% | 10256 | 8376 |
| 64 | 9 | ParseAndLookup | 3 | 400.88 | 454.79 | +13.4% | 1400 | 1056 |
| 64 | 16 | Lookup | 1 | 4.47 | 9.79 | +119.0% | 0 | 0 |
| 64 | 16 | Parse | 1 | 536.01 | 809.20 | +51.0% | 1680 | 1336 |
| 64 | 16 | ParseAndLookup | 3 | 604.69 | 914.47 | +51.2% | 1680 | 1336 |
| 64 | 32 | ParseAndLookup | 3 | 1227.65 | 2277.68 | +85.5% | 4296 | 3280 |

Follow-up build verification during the shared-layout investigation caught a stale binary after source restoration: preserving the old source timestamps had allowed incremental compilation to retain the candidate DLL. The original 100-test source-restored run is therefore not evidence of binary restoration. A forced recompilation now produces Core SHA-256 `698305941afad42c05f26b4e4edd3ed28b6e094760d04db6259de6c708246f5a`, identical to the frozen dictionary control. All 11,812 method bodies match, and 143 focused tests pass against that verified DLL (including the previous 100 cases). Original/restored production source digests also match. The frozen control/candidate measurements above remain valid; their separate manifests correctly identified both binaries. See `restoration.json`, `restored-core-il-verification.json` and the corrected test log. Future restoration must invalidate incremental build timestamps and verify the resulting binary.

The follow-up [shared-layout investigation](observation-values-shared-layout.md) finds gains by sharing schema metadata and prebinding ordinals rather than retaining arbitrary-name vectors.
