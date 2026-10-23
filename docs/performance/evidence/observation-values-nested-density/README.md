# Fallback profiling and nested density evidence

See the [decision report](../../observation-values-nested-density.md). All candidates remain benchmark-only.

## Two frozen phases

`profile-manifest.json` identifies the original parser plus forced-fallback/prefix probes. Its source snapshot is archived under `profile/source/`. `manifest.json` identifies the final build with separated fallback and density selection. It records all DLL/config hashes and exact benchmark source hashes. No runtime libraries differ semantically from the previously tested performance build; `tested-core-il.json` verifies Core.

- `ablation-summary.csv`: three process repeats each of direct, old combined, forced fallback and speculative prefix. Native profiles use this same initial profiling closure.
- `summary.csv`: 156 main timing processes. Standard matrix: four warmups, six 200 ms iterations. Case-variant fallback qualification: six warmups, ten 300 ms iterations. One exact benchmark per fresh process; three counterbalanced repeats.
- `tiering-summary.csv`: six bounded diagnostic processes with `DOTNET_TieredCompilation=0`. Absolute results must not be mixed with the normal-runtime matrix.
- `retained.txt`: 42 fresh-process diagnostics, measuring complete retained trees and allocation separately. Candidate preparation allocation is excluded from warm timing and live-tree deltas.
- `runtime-environment.json`: inherited tiering/PGO/ReadyToRun/server-GC overrides, plus the explicit control override in the driver.
- `verification.json`: semantic, ownership, allocation, binary/source and process-count checks.
- `profile-summary.json`: per-route selected thread samples and relevant native method tiers/sizes. GC polling samples are not active CPU cycles.

## Reproduction

Build the Relations benchmark project in Release with `-m:1 -p:UseSharedCompilation=false`. Run `--nested-layout-conformance`, then freeze the full output directory before timing. Update explicit worktree/frozen/output paths in the scripts to your environment.

`profile-fallback.py` captures the initial existing/shared/forced routes using `dotnet-trace` and the included TraceEvent analyzer. Providers are `Microsoft-DotNETCore-SampleProfiler:0x0:5,Microsoft-Windows-DotNETRuntime:0x20018:5`. `analyzer/` uses already-available TraceEvent libraries; adjust local hint paths. No new repository package dependency was added.

`measure-ablation.py` runs the first-phase four-route decomposition. `measure.py` records final hashes, captures/analyzes the separated route, completes heap diagnostics, then starts the main timing matrix. `measure-tiering.py` runs only after that matrix finishes. `summarize.py RUN_DIRECTORY` reports medians of process means; inspect individual rows as well, especially the bimodal compact-storage case.

Final CLI examples:

```text
dotnet Cohesive.Relations.Benchmarks.dll --nested-layout-profile separated 8
dotnet Cohesive.Relations.Benchmarks.dll --nested-layout-retained 32 128 array density 15 25
```

Raw BDN outputs, native code, full EventPipe captures, initial profile source and logs are archived byte-for-byte in `raw-reports.tar.gz`. `raw-reports-index.json` records the archive and every member's SHA-256. Derived ETLX caches are omitted because the included analyzer regenerates them from the retained traces. No timing overlaps builds, tests, tracing, compression or analysis.
