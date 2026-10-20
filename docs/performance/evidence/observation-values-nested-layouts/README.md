# Nested shared-layout experiment evidence

The candidate is benchmark-only; production Core/Relations remain bytecode-equivalent to the previously tested performance implementation. The base revision is the integrated PR #461 performance branch, `d1c0ad8c`.

## Reproduce

Build `src/Cohesive.Relations.Benchmarks/Cohesive.Relations.Benchmarks.csproj` in Release with `-m:1 -p:UseSharedCompilation=false`. Before timing, execute:

```text
dotnet Cohesive.Relations.Benchmarks.dll --nested-layout-conformance
dotnet Cohesive.Relations.Benchmarks.dll --nested-layout-retained 16 128 array shared 15
```

Freeze the complete build directory. Set the source/frozen/output folder paths in `measure.py`, then run it. It captures binary/source hashes, runs all retained diagnostics first, and selects one exact BDN case per fresh process. Every result is required to contain exactly one successful benchmark. There are 159 timing processes: 20 parse pairs, nine retained-read routes and four combined routes, each repeated three times with counterbalanced order. `summarize.py RUN_DIRECTORY` emits median-of-process-mean tables; `summary.csv` retains each observation.

`qualify-fallback.py` records the longer counterbalanced follow-up for a measured case-variant fallback regression; it uses the same frozen binaries and runs only after the main matrix completes. Its results are separate from the primary table.

`retained.txt` records allocation/live-heap counters for 22 fresh diagnostic processes; counts are whole trees. `preparation_allocated_bytes` is cold candidate construction allocation (including transient/cold initialization), not live metadata size. Timing, allocation and approximate retained-heap metrics must not be conflated.

`manifest.json` records the exact DLL/config closure and source hashes. `verification.json` records conformance and checks. `tested-core-il.json` verifies production Core method bodies against the fully tested performance build. All raw reports and logs are retained byte-for-byte in `raw-reports.tar.gz`; `raw-reports-index.json` records archive and per-member hashes. The [decision report](../../observation-values-nested-layouts.md) explains semantics, restrictions and tradeoffs.
