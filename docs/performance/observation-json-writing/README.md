# Avoid boxed common field enumerators in observation JSON writes

The published alpha.153 repeated-X12 traces still sample about 213 KB in boxed field enumeration
under `ObservationValue.WriteTo`. Its object loop used `IReadOnlyDictionary` enumeration even for
framework storage with native struct enumerators. This repeats for every nested object emitted
through protocol JSON, typed interpretation and canonical-fingerprint preparation.

## Ownership and semantic fit

Core owns the observation and its JSON encoding. Keep the original recursive writer and directly
enumerate existing small-vector, owned-dictionary and ordinal-layout storage; read the field view
once per object. Preserve stored order (including noncanonical layout order), scalars, undefined/null,
temporal/bytes policy, validation and errors. Other collection types retain ordinary enumeration
and disposal; this does not claim allocation-free writing for arbitrary immutable collections,
byte-to-base64 strings, output storage or failure diagnostics. No cache, pool, public API or format
change. Canonical sorting, integrity and contextual admission are unchanged.

The existing `ObservationFieldTraversal` was evaluated. A shared generic-visitor prototype removed
boxes but added recursive-call overhead and was rejected. A separate field callback also increased
CPU in a matched X12 pair and was removed. These physical fast paths therefore keep the original
call shape instead of adding another traversal abstraction or changing validator ownership.
The benchmark deliberately covers deep input so a future refactoring can detect that cost.

## Original X12 comparison

Use verified public alpha.153 dependencies and source control at merged #460 `39c490ed`. Both
variants replace only their local Core module; all other DLLs match. Source and binary hashes are
in the [manifest](manifest.json). Ari source is its qualified alpha.153 adoption. This new fix is
not published. The extended harness keeps the same original Ari test and measurement loop; both
variants have identical harness bytes. No package cache is modified.

All **16,384 cases passed**, two alternating process pairs/source, 2,048 cases/process. Report
cases 1537–2048 to distinguish repeated work from long tiered-JIT warm-up. CPU includes JIT and
GC; neither is subtracted. Medians are first within process, then across the two processes.

| X12 | Late allocation before → after | Late median CPU before → after | Late mean CPU before → after |
| --- | --- | --- | --- |
| 004010 | 4.378 → 4.213 MB | 16.532 → 15.957 ms | 16.711 → 16.286 ms |
| 005030 | 4.591 → 4.399 MB | 18.122 → 18.716 ms | 18.068 → 19.055 ms |

CPU point estimates are mixed: 004010 -3.5%; 005030 +3.3%. The allocation reduction is consistent; this remains a draft with no general CPU-speed claim.

These are cumulative allocated bytes, not retained heap. CPU point estimates from two process
pairs are qualification evidence, not a general latency guarantee. [All process windows](case-windows.csv)
retain cold, second, early repeated and late results, including JIT totals and unfavorable runs.
Do not treat early repetitions as steady state or extrapolate the microbenchmarks to suite speed.

## 005030 CPU investigation before merge

The initial +3.3% median CPU result did **not reproduce consistently** with unchanged binaries.
Four additional alternating process pairs passed all **16,384** original 005030 cases, using the
same 2,048-case process length and late window. CPU includes GC/JIT. No owned builds, tests,
traces or other benchmarks overlapped these untraced runs.

| Pair | Baseline median CPU | Candidate median CPU | Change |
| --- | --- | --- | --- |
| 1 (candidate first) | 17.450 ms | 17.394 ms | -0.3% |
| 2 (control first) | 17.283 ms | 16.933 ms | -2.0% |
| 3 (candidate first) | 16.477 ms | 17.079 ms | +3.7% |
| 4 (control first) | 17.401 ms | 16.946 ms | -2.6% |

Across processes, median CPU is **17.342 → 17.012 ms (-1.9%)**, and mean CPU is
**17.374 → 17.322 ms (-0.3%)**. Allocation remains **4.591 → 4.399 MB (-4.2%)**.
Treat the independent process as the replica, not each of its 512 late cases. The small,
mixed CPU differences do not establish equivalence or a general speedup; a small regression
cannot be excluded. Original unfavorable results above remain part of the evidence.
[All new windows](cpu-investigation-windows.csv) retain sparse late JIT work (0–6.1 ms total
per process window), which is included in the CPU measurements.

A separate matched 2,048-case trace pair lost no events or late managed stacks. The changed
writer's inclusive share of late managed samples is **189/6,302 (3.00%) → 164/6,269 (2.62%)**;
its exclusive samples are 83 → 54. External samples are excluded. These diagnostic samples
are not CPU milliseconds and are not a precise timing comparison. They provide no evidence
that the writer caused the earlier full-case slowdown.

A separate default-runtime JIT capture reaches Tier1 in both variants. The writer's code size
is 5,048 → 5,156 bytes and primary stack frame 448 → 496 bytes. The additional concrete loops
do enlarge the method, but the capture does not establish that this causes an end-to-end CPU
regression. No source tuning was applied to chase these mixed point estimates. Existing slower
tiny flat microbenchmark cases remain documented below.

[Investigation identities and trace counts](cpu-investigation.json) identify the unchanged source,
frozen modules, harness, raw counter hash and disassembly hashes. Reproduce the untraced check
with the comparator below, adding `--sources x12-005030-204 --runs 4 --candidate-first` (replace
the existing `--runs 2`). Source and qualification tests are unchanged; the new evidence is
additional original-test execution and diagnostics. Keep the PR unmerged for review.

## Mechanism qualification

[Before](before-benchmark.md) and [after](after-benchmark.md) BenchmarkDotNet reports retain all
12 estimates and intervals: small/owned/ordinal storage; 1/128 objects; depth 1/32. Output buffers,
writer and immutable fixtures are prepared outside the measured loop. The common numeric path
eliminates per-object boxes; occasional few-byte deep-case estimates are retained rather than
rounded down. Some tiny flat timing cases can be slower despite reduced allocation.

Six median-of-seven regression guards cap 128/1024-object writes at 256 B each. The unchanged
baseline fails all six; 128 small objects allocate 4,096 B and owned dictionaries 10,240 B. The
candidate passes. Five storage/order cases preserve exact JSON property order and a nested
byte-policy failure proves subsequent writes remain independent. Other existing JSON scalar,
converter, canonical and failure regressions run in the full suite.

Final validation: **5,012 Core tests  / 60 existing gated skips**, **1,137 Relations tests** and
**999 Ari Engine tests  / 18 existing emulator skips** against the measured candidate module.
Graph fingerprints are byte-identical to the verified public alpha.153 results. The official
Ari adoption separately passed 1,408 .NET tests, 35 browser tests and all nine native recovery combinations.

Reproduce with the existing comparator, specifying the one changed module:

```sh
python3 ../query-identity-preparation/cpu_compare.py --control /path/to/control \
  --candidate /path/to/candidate --output /tmp/observation-json-recheck \
  --changed-module Cohesive.dll --iterations 2048 --runs 2 \
  --ari-program /path/to/ari/eng/runtime-performance/protocol-preparation/Program.cs
```

Run benchmarks separately with `ValueConversion --filter '*ObservationJsonWritingBenchmarks*'`.
Retained-result creation and buffer growth are deliberately outside those microbenchmarks; cold
workflow measurements include first-use construction. Full raw counters remain local at the
manifest path; the checked-in process summaries and hashes identify the reported experiment.
