# Width benchmark evidence

Raw generated reports, traces and rejected patches are preserved byte-for-byte in [raw-reports.tar.gz](raw-reports.tar.gz); [the index](raw-reports-index.json) records each member size/SHA-256. Extract with `tar -xzf raw-reports.tar.gz` in this directory to restore the filenames described below. Summary data, scripts and provenance remain directly readable.

See [the report](../../observation-values-width.md) for interpretation and constraints.

- `N-W-F-SIDE-*`: BenchmarkDotNet full JSON, CSV and Markdown; repeat N, training width W, measured fields F.
- `summary.csv`: every process mean and bytes/op; `summarize.py` computes median-of-means tables.
- `manifest.json`: runtime/dependency SHA-256 digests; only Cohesive.dll differs.
- `candidate.patch`: the rejected production experiment, relative to accepted builder source.
- `measure.py`: sequential runner, alternating order over three repeats. It expects frozen dependency closures in `/private/tmp/cohesive-width-control` and `/private/tmp/cohesive-width-candidate`.
- Test logs record focused parser conformance, string ownership, tagged decoding and value semantics on the candidate and restored sources.

To reproduce, build the benchmark project in Release (`-m:1 -p:UseSharedCompilation=false`), copy its output to the control directory, apply `candidate.patch`, rebuild, copy the control closure into the candidate directory, and replace only Cohesive.dll/PDB with the candidate build. Run `measure.py`, then `summarize.py`. Do not overlap timing with builds, tests, traces or other benchmark processes. Each CLI filter selects exactly one width/training combination in a fresh process.

.NET SDK 10.0.402, .NET 10.0.13, BenchmarkDotNet 0.15.8, macOS 27.0.1 ARM64. BenchmarkDotNet could not elevate process priority. All inputs are generated numeric objects; these are synthetic local measurements.
