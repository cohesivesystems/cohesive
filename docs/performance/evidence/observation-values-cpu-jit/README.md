# CPU/JIT evidence index

Raw generated reports, traces and rejected patches are preserved byte-for-byte in [raw-reports.tar.gz](raw-reports.tar.gz); [the index](raw-reports-index.json) records each member size/SHA-256. Extract with `tar -xzf raw-reports.tar.gz` in this directory to restore the filenames described below. Summary data, scripts and provenance remain directly readable.

See [the report](../../observation-values-cpu-jit.md) for interpretation and limitations.

- `warmup.csv`: three fresh processes per library/warmup condition; five-second untraced loops.
- `qualification-*`: three paired plain-parser matrices and additional tagged, escaped and isolated-flat qualification for the **discarded** cache-outlining candidate. Each frozen closure differs only in Core.
- `ablation-*`: isolated-flat diagnostics bypassing cache lookup or also bypassing root cleanup. These libraries are **not retained** in production.
- `jit-flat-only.txt`, `jit-matrix.txt`: actual current-library BenchmarkDotNet generated code under different workload ordering. The accompanying reports record the measured cases.
- `exploratory-*`: initial outlining trials, kept distinct from repeated qualification.
- `inline-flat.json`, `inline-mixed.json`, `inline-candidate.json`: relevant CLR inlining successes/failures. The candidate rejects prepared-cache inlining but still rejects dictionary insertion inlining.
- `*-profile.json`: sample counts and native method-relative offsets. Raw traces are compressed as `.nettrace.gz`; decompress before running the analyzer. `raw-trace-manifest.json` hashes the original uncompressed files.

Trace mapping:

| Artifact | Library / warmup | Closure manifest |
|---|---|---|
| `flat-current` | Accepted baseline / flat | `manifest.json`, current |
| `flat-reference` | Before token reuse / flat | `manifest.json`, before-reuse |
| `mixed-current`, `inline-flat`, `inline-mixed` | Accepted baseline / indicated workload | `warmup-manifest.json`, current |
| `mixed-reference` | Before token reuse / collection | `warmup-manifest.json`, before-reuse |
| `inline-candidate` | Rejected cache-outlining annotation / collection | `final-manifest.json`, candidate |

Current-flat and both mixed captures include same-process JIT disassembly. Reference-flat has
method-version offsets without a matching disassembly capture. Native addresses are per-process;
only compare offsets against the corresponding method version, not an address from another run.

`pgo-off.csv` and `tiering-off.csv` are single-process runtime-switch probes, not repeated
performance estimates. `runtime-environment.json` records relevant inherited runtime settings;
probe overrides appear in their scripts. The scripts record exact local orchestration and assume
the named frozen folders already exist; they are not a self-contained historical build archive.

`restoration.json` verifies 11,812 restored method signatures/bodies/implementation flags,
including locals and exception regions, against the accepted baseline despite a newer assembly
informational revision stamp. `candidate-il.json` identifies the diagnostic candidate's one
implementation-flag difference. `Compare.csproj` / `compare-il.cs` reproduce this SDK-only
comparison when given the two DLL paths. `final-tests.log` and `cli-checks.json` record final
semantic/allocation checks and profiling-tool argument/checksum checks.
