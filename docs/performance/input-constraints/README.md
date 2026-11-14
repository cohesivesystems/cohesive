# Input constraint admission (#529)

Measured on 2026-11-14 against this change, based on `52091f09`.
[BenchmarkDotNet report](benchmarks.md) records runtime, hardware, uncertainty,
GC, and allocation results. These are short-run engineering measurements, not
application throughput or Cohesive-versus-conventional performance claims.

```sh
dotnet run -c Release --project src/Cohesive.Relations.Benchmarks -- \
  --filter '*InputConstraintBenchmarks*' --job short --warmupCount 1 --iterationCount 3
```

The control is the same current implementation with attribute projection disabled.
The treatment projects Required/MaxLength. Fixtures cover a flat record, one nested
record, 16 records, and 1,024 records. Values are projected once outside the timed
operation. `WarmAdmission` reuses the graph and checks the input each time;
`FreshGraphAdmission` rebuilds the graph and its reflection metadata for each call.
Fresh graph creation is not a cold-process/JIT measurement. No validation result
is retained between calls.

All eight warm cases report **0 B/op**. Warm constrained means were approximately
58 ns flat, 104 ns nested, 675 ns for 16 records, and 43.7 microseconds for 1,024.
Fresh constrained graphs allocate about 11.7–18.1 KB per invocation. Short-run
cold timing is noisy; the report does not establish a speedup or a precise
regression threshold. Author once and reuse the graph at application admission.

`InputConstraintTests.WarmSuccessfulConstraintValidationAllocatesNothing` checks
portable and ordinal successful validation over repeated 1,000-call windows.
The minimum of three windows excludes one-time runtime setup without tolerating
per-call allocations. Record-to-observation projection has its existing retained
payload cost and is not part of that zero-allocation assertion.

Validation: the full Release SDK suite passed 5,644 tests, with 72 environment-
dependent tests skipped. The 28 added cases include DataAnnotations parity,
UTF-16 boundaries, field/shape targets, prepared/interpreted equivalence, contract
round trips, and unsupported declarations. The earlier Debug control on unchanged
main had four allocation-ceiling failures; Release is the clean final result.
