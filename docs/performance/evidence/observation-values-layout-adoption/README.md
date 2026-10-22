# Strict layout ingestion adoption evidence

Raw generated reports, traces and rejected patches are preserved byte-for-byte in [raw-reports.tar.gz](raw-reports.tar.gz); [the index](raw-reports-index.json) records each member size/SHA-256. Extract with `tar -xzf raw-reports.tar.gz` in this directory to restore the filenames described below. Summary data, scripts and provenance remain directly readable.

See [the report](../../observation-values-layout-adoption.md) for APIs, semantic guarantees and results.

- `adoption-N-WORKLOAD-METHOD-*`: full JSON/CSV/Markdown end-to-end results, three routes on the same current closure.
- `guard-N-WIDTH-SIDE-*`: old frozen benchmark harness with control/current Core and Relations libraries; every other DLL and runtime config is identical.
- `manifest.json`: SHA-256 digests for all three closures. The guard must differ only in Core and Relations.
- `summary.csv`, `measure.py`, `summarize.py`: per-process means/allocations, sequential runner and median summaries. Each process selects one benchmark; no timed work overlaps builds/tests.
- Full Core/Relations test logs and tested/final IL comparison record semantic and binary verification.

Reproduction directories are `/private/tmp/cohesive-adoption-frozen` (current end-to-end harness), `/private/tmp/cohesive-adoption-control` (pre-adoption frozen harness/closure), and `/private/tmp/cohesive-adoption-current` (copy of control with only current Core/Relations DLL/PDB replaced). Build Release with `-m:1 -p:UseSharedCompilation=false`, freeze outputs, verify manifests, then run the scripts. Preserve source/binary freshness; do not restore old source timestamps over newer experimental binaries.

.NET SDK 10.0.402, .NET 10.0.13, BenchmarkDotNet 0.15.8, macOS 27.0.1 ARM64. Runtime priority elevation was unavailable. Synthetic local data; no cold-start, network or full-application latency claim.

`qualify-N-large-METHOD-*` and `qualify-large.py` record the longer counterbalanced large-input follow-up. These use a separate six-warmup/ten-iteration/300-ms regime and are not mixed into the initial medians. `verification.json` records case counts, tests and the tested/final IL comparison.
