# Compact ordinal evidence

See the [decision report](../../observation-values-compact-ordinals.md). This adds an internal Core physical representation and two integration cases, but selection remains benchmark-only.

- `manifest.json`: base revision, exact hashes of every changed implementation/test/benchmark source, and the frozen runtime dependency closure.
- `summary.csv`: 168 fresh-process timing rows: 120 parse/admission, 24 read-only, 24 combined. Each exact benchmark has three repeats. Four warmups and six 200 ms iterations use normal inherited runtime settings.
- `retained.txt`: 44 separate fresh-process retained-tree/allocation diagnostics, completed before timing. Preparation allocation is reported separately; it is not included in live-tree deltas or warm timings.
- `guard-manifest.json`, `guard-summary.csv`: 12 fresh-process matched Core integration controls. Both use the previous density-study harness/dependency closure; only Core differs. The old harness is intentional so its benchmark type discovery has no dependency on the new internal storage type.
- `tested-core-il.json`: method-body/flags/local-signature/exception-region equality between the tested Core and the final frozen benchmark Core. This is **not** an assertion that Core is unchanged from the previous experiment.
- `verification.json`: test, conformance and process counts.
- `runtime-environment.json`: host platform and inherited runtime overrides. The raw BenchmarkDotNet headers include runtime/SDK and hardware information.
- `raw-reports.tar.gz`: unmodified BDN JSON/CSV/Markdown/HTML, complete logs, verification logs and source snapshots. The archive and each member are SHA-256 indexed in `raw-reports-index.json`.

## Reproduction

Use the recorded base revision plus the archived `source/` files. Build Core tests and Relations benchmarks in Release with `-m:1 -p:UseSharedCompilation=false`. Run Core tests (collections serialized), Relations tests and `--nested-layout-conformance`, then freeze the complete benchmark output directory. Adjust explicit worktree/frozen/output paths in the scripts to your environment and use empty output directories.

`measure.py` first completes all 44 retained diagnostics, then runs the 168 timings. `measure-guards.py` runs afterward using the prior study's frozen closure as its before harness and a copy with the newly built Core as its after harness. Assert that Core is the only differing runtime DLL. No build, test, trace, compression or retained-heap diagnostic should overlap timing. `summarize.py` prints medians and min/max process means. `package.py` is the local evidence packaging recipe; its absolute paths and report template paths require adjustment.

Examples:

```text
dotnet Cohesive.Relations.Benchmarks.dll --nested-layout-conformance
dotnet Cohesive.Relations.Benchmarks.dll --nested-layout-retained 256 128 array packed 15 6
dotnet Cohesive.Relations.Benchmarks.dll --filter '*ObservationNestedLayoutBenchmarks.ReadCompactOrdinal(Width: 256, Count: 128, Scenario: "sparse-array", PresentPercent: 25)' --inProcess --warmupCount 4 --iterationCount 6 --iterationTime 200
```

The width-16 read fixture is contiguous quarter-full. The width-256 read fixture is strided quarter-full so the midpoint hits a later bitmap word. Do not compare their absolute times as though they had identical hit/miss patterns. All four routes within each fixture have the same values, queries and checksum. Timed preparation is excluded; each fresh setup verifies all candidate interpretations before measuring one exact method.
