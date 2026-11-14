# Input constraint admission (#529)

Measured on 2026-11-14 for the PR #548 review follow-up to `f0bbbaae`, based on
`52091f09`. [BenchmarkDotNet report](benchmarks.md) retains runtime, hardware,
uncertainty, GC, and allocations. This replaces the initial three-iteration run
with three warmup iterations and ten measured iterations per case (one launch).

```sh
dotnet run -c Release --project src/Cohesive.Relations.Benchmarks -- \
  --filter '*InputConstraintBenchmarks*' --job short --warmupCount 3 --iterationCount 10
```

The control uses the same current implementation with attribute projection
disabled. The treatment projects Required/MaxLength. The 24 cases cover a flat
record, one nested record, 16 records, and 1,024 records:

- `WarmAdmission`: validates an already projected value against a reused graph.
- `TypedAdmission`: calls `ValidateInput<T>` and projects the record on **every**
  invocation, against a reused graph. Projection allocation is included.
- `FreshGraphAdmission`: rebuilds the graph and authoring metadata on each call,
  then validates the preprojected value. This is not a cold-process/JIT measurement.

No validation result is retained between calls. All eight preprojected warm cases
report **0 B/op**. That assertion does **not** apply to the typed API.

| Constrained input | Typed mean | Typed allocation/call | Preprojected mean |
| --- | ---: | ---: | ---: |
| Flat | 185 ns | 352 B | 67 ns |
| Nested | 268 ns | 528 B | 98 ns |
| 16 records | 1.885 microseconds | 3,768 B | 623 ns |
| 1,024 records | 128.560 microseconds | 213,432 B | 35.424 microseconds |

For 1,024 items the unconstrained typed control measured 126.793 microseconds,
with the same allocation. These are single-machine engineering measurements,
not application throughput or Cohesive-versus-conventional claims. Some constrained
means are below their controls; do not infer a speedup or stable overhead ratio.
The initial run's 26% preprojected difference is not an established regression
estimate. Compare the full report's uncertainty and reproduce on the consuming
application before making a performance claim.

`InputConstraintTests.WarmSuccessfulConstraintValidationAllocatesNothing` checks
portable and ordinal successful validation over repeated 1,000-call windows.
The minimum of three windows excludes one-time runtime setup without tolerating
per-call allocations. The typed API owns its projected payload cost separately.

Validation: the complete Release test project passed **5,652** tests, with 72
environment-dependent skips. Review regressions cover case handling, dictionary
projection rejection, idempotent shape resolution and retained collection-item
rules, and message-dependent fingerprints. Application submission integration is
still pending in cohesive-evals#42; this benchmark does not qualify that consumer.
