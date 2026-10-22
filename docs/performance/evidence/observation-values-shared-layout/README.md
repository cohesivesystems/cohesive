# Shared-layout benchmark evidence

Raw generated reports, traces and rejected patches are preserved byte-for-byte in [raw-reports.tar.gz](raw-reports.tar.gz); [the index](raw-reports-index.json) records each member size/SHA-256. Extract with `tar -xzf raw-reports.tar.gz` in this directory to restore the filenames described below. Summary data, scripts and provenance remain directly readable.

See [the report](../../observation-values-shared-layout.md) for semantic boundaries and results.

- `N-W-R-METHOD-*`: BenchmarkDotNet full JSON, CSV and Markdown for repeat N, field width W and reads R.
- `summary.csv`: every process mean and allocation count. Parse methods are per record; retained-read methods are per sixteen-query batch.
- `retained.csv`: three fresh-process measurements per width/route, keeping 20,000 objects alive. Heap deltas exclude outer arrays and shared graph/layout. `layout_setup_allocated_bytes` includes first factory/cache allocation, not graph/shape construction or retained size.
- `manifest.json`: SHA-256 digests of the one frozen dependency closure used by every route. `core-il-verification.json`, `baseline-allocation-smoke.txt` and `verification.json` verify the dictionary baseline and case counts.
- `measure.py`: sequential timing runner; each CLI filter selects exactly one benchmark in a fresh process.
- `retained.py`: sequential heap diagnostic, run after timing.
- `summarize.py`: medians of process means, allocation summaries and per-query lookup summaries.
- `cli-checks.json` records invalid-argument checks.
- Test logs exercise strict versus generic policies, graph qualification, presence, canonical identity, ordinal access and allocation bounds.

To reproduce, build `src/Cohesive.Relations.Benchmarks` in Release (`-m:1 -p:UseSharedCompilation=false`), copy its output to `/private/tmp/cohesive-layout-frozen`, run `measure.py`, then `retained.py`, then `summarize.py`. Never overlap timing with builds, tests, traces or memory diagnostics.

.NET SDK 10.0.402, .NET 10.0.13, BenchmarkDotNet 0.15.8, macOS 27.0.1 ARM64. Synthetic numeric records and mixed hits/misses; no production traffic or general-purpose JSON substitution is claimed.

The runner includes a fixed accepted-Core hash and allocation guard for this evidence revision. Verify method equivalence and deliberately update the guard when reproducing against another revision; do not bypass it merely to accept a stale binary. Preliminary misfiltered and stale-vector runs are excluded from this directory and all summaries.
