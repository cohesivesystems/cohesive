# Field storage comparison evidence

Raw generated reports, traces and rejected patches are preserved byte-for-byte in [raw-reports.tar.gz](raw-reports.tar.gz); [the index](raw-reports-index.json) records each member size/SHA-256. Extract with `tar -xzf raw-reports.tar.gz` in this directory to restore the filenames described below. Summary data, scripts and provenance remain directly readable.

See [the report](../../observation-values-field-storage.md) for interpretation.

- `N-W-F-METHOD-SIDE-*`: BenchmarkDotNet full JSON, CSV and Markdown for repeat N, training width W and target width F. SIDE is control (dictionary promotion at nine fields) or candidate (contiguous vector through thirty-two).
- `summary.csv`: every process mean and allocation count. `Lookup` is normalized per query; `ParseAndLookup` includes sixteen queries per parsed object.
- `retained.csv`: allocation and surviving heap deltas for 20,000 simultaneously retained parsed objects, three fresh-process pairs per width. Divide totals by count for approximate bytes/object; the outer array and fixtures are excluded.
- `manifest.json`: SHA-256 digests for frozen timing closures; only `Cohesive.dll` differs. `retained-manifest.json` records the corrected diagnostic closures separately.
- `candidate.patch`: experimental production change relative to the pre-experiment source. This patch is retained as evidence if the candidate is rejected.
- `measure.py`: initial narrow-training screen, one process per method/width/side.
- `qualify.py`: three pairs of combined construction/lookup at 9, 16 and 32 fields under both training shapes, reusing the first screening pair for narrow training. Also measures sixteen-field parsing and lookup separately after wide training once.
- `retained.py`: surviving-memory diagnostic runner. Run sequentially after timing.
- `summarize.py`: emits median-of-process-means summaries with repeat counts and allocation.
- `unreliable-get-total-memory.csv` is invalid heap evidence, retained only to document why the diagnostic changed. `heap-accounting-probe.cs` and its JSON/text output show the cross-check.
- Test logs and restoration metadata record conformance checks and final source status.

Reproduction: build the benchmark project in Release (`-m:1 -p:UseSharedCompilation=false`), freeze its output as `/private/tmp/cohesive-vector-control`, apply the candidate patch, rebuild, copy the control closure as `/private/tmp/cohesive-vector-candidate` and replace only Cohesive.dll/PDB with the candidate build. Run the measurement scripts sequentially, then summarize. For `retained.py`, use the corrected diagnostic harness in `/private/tmp/cohesive-vector-retained-control` and `/private/tmp/cohesive-vector-retained-candidate`; the same dependency/Core closures apply. Only heap diagnostic code changed after timing. Do not overlap with builds, tests, tracing or other timed work.

.NET SDK 10.0.402, .NET 10.0.13, BenchmarkDotNet 0.15.8, macOS 27.0.1 ARM64. The host could not elevate process priority. All fixtures are generated local numeric objects; query strings are separately allocated. Results are workload- and JIT-history-dependent, not portable timing guarantees.
