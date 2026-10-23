# Current-main integration qualification

This directory qualifies the integrated performance branch against main `39c490ed`, retaining main's preparation, immutable interpretation, structural evidence and weak-cache publication work. Earlier experiment directories record historical decisions, not the baseline for this comparison.

## Reproduce

1. Build main and the performance branch's Relations benchmark project in Release, using the same .NET SDK and `-m:1 -p:UseSharedCompilation=false`. Build sequentially and finish all tests before timing.
2. Freeze main's complete `bin/Release/net10.0` closure. Replace only its `Cohesive.Relations.Benchmarks` DLL, PDB, deps and runtimeconfig files with the branch harness. This is the control closure.
3. Copy that closure and replace only `Cohesive.dll`, `Cohesive.Relations.dll`, and `Cohesive.Storage.dll` (and their PDBs) with branch builds. `manifest.json` records all runtime DLL/config hashes and the runner asserts exactly these three differences.
4. Set the two frozen folder paths in `measure.py`, then run it. Run no builds, tests, traces or other timed work concurrently. The runner uses one selected benchmark per fresh process, three repeats, alternating order, four warmups and six measured 200 ms iterations. It verifies each result contains exactly one successful benchmark.
5. Run `summarize.py RUN_DIRECTORY`. Results use medians of the three process means, not pooled iteration samples. `summary.csv` preserves each process mean and allocation count; allocation medians on large payloads can be nonintegral because BDN includes amortized runtime costs and the traversal's randomized string table varies collisions.

`ObservationMainResponseQualificationBenchmarks` selects the new prepared API through reflection during setup only, so the identical benchmark DLL can run compatibility mode on main, which lacks that API. Timed reads invoke a typed delegate. Current-only strict JSON comparisons use the same branch closure for both indexed-name and new ordinal routes. Every benchmark validates its complete fixture outside timing.

`verification.json` records post-rebase tests and canonical wire identity. `tested-core-il.json` compares all method bodies between the fully tested Core build and the frozen final benchmark Core. Raw BDN reports and execution logs are retained byte-for-byte in `raw-reports.tar.gz`; `raw-reports-index.json` lists hashes for the archive and every member.

The report at `../../observation-values-main-qualification.md` is the integration decision. These are bounded warm microbenchmarks, not a production throughput or tail-latency claim. Cold registration cost remains outside measurement.
