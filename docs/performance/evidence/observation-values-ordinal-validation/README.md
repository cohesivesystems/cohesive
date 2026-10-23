# Ordinal validation evidence

See the [decision report](../../observation-values-ordinal-validation.md). Binding and parser selection remain experimental and explicit, while minimal support lives in the existing Core validator/plan classes.

- `manifest.json`: base revision, exact final source hashes and frozen runtime dependency hashes.
- `profile-manifest.json`: separate initial/final profile dependency closures and initial profiling source hashes. Initial harness sources are archived under `profiles/before-source/`.
- `summary.csv`: 138 main timing processes. Each exact benchmark has three repeats: 84 packed validation/parse, 24 fixed-vector controls, 12 combined, 12 preparation and six case-variant fallback runs.
- `guard-summary.csv`, `guard-manifest.json`: 12 matched ordinary-parser timing processes, using the initial profile harness in both arms and swapping only Core.
- `retained.txt`: 14 separate heap/allocation diagnostics completed before the main timings. Complete-tree retained bytes exclude prepared metadata; preparation allocation is reported independently.
- `profile-summary.json`: selected sample and leaf dictionary/hash/poll counts, plus leading nonpoll leaf frames. Counts are not cycle attribution or an allocation measure.
- `tested-core-il.json`: method-body/flags/local-signature/exception-region comparison between the full-test Core and frozen benchmark Core, not between the previous and final source versions.
- `verification.json`: test/conformance/process counts and exact frozen/tested code comparison.
- `raw-reports.tar.gz` and `raw-reports-index.json`: SHA-256 verified raw BDN outputs/logs, seven EventPipe captures, matching JIT text, analyzer source, verification logs, source snapshots and report packaging templates. Derived ETLX caches are omitted and can be regenerated from the retained traces.

## Reproduce

Use the recorded base revision and archived final `source/` files. Build Core tests and Relations benchmarks in Release with `-m:1 -p:UseSharedCompilation=false`. Run Core tests with collections serialized, Relations tests and `--nested-layout-conformance`, then freeze the complete benchmark output directory. Adjust the scripts' explicit paths and use empty output directories.

`profile-before.py` captures the initial baseline before applying Core changes. Its source overlay is archived separately. `profile-after.py` captures the final matched reference/bound routes. Both use the already-available `dotnet-trace` tool with `Microsoft-DotNETCore-SampleProfiler:0x0:5,Microsoft-Windows-DotNETRuntime:0x20018:5`. The included analyzer uses existing TraceEvent libraries; adjust its local reference paths. No repository dependency was added.

Run `measure-guards.py` after profiles and before `measure.py`. The guard script copies the initial profile harness twice and replaces only Core in the after arm, asserting that it is the only changed runtime DLL. `measure.py` performs all heap diagnostics first, then the 138 timing processes. Do not overlap timings with builds, tests, tracing, heap diagnostics or compression. `summarize.py` prints medians and process ranges. `package.py` preserves raw bytes and verifies hashes; local paths/templates require adjustment.

Examples:

```text
dotnet Cohesive.Relations.Benchmarks.dll --nested-layout-profile validate-bound-packed 8 256 128 array 6
dotnet Cohesive.Relations.Benchmarks.dll --nested-layout-retained 16 128 array bound-packed 15 25
dotnet Cohesive.Relations.Benchmarks.dll --filter '*ObservationNestedLayoutBenchmarks.ValidateBoundCompactOrdinal(Width: 16, Count: 128, Scenario: "array", PresentPercent: 25)' --inProcess --warmupCount 4 --iterationCount 6 --iterationTime 200
```
