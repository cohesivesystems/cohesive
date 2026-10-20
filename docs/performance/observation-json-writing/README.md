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
