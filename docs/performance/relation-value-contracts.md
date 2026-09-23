# Explicit relation value operations

Measured on 2026-09-22 with BenchmarkDotNet 0.15.8, macOS Arm64 / Apple M5 Max,
.NET SDK 10.0.201 and .NET runtime 10.0.5. Short job, one warmup and three measured
iterations. These are warm isolated operation measurements, not application latency claims.

The benchmark prepares immutable inputs and expressions once. Each measured invocation includes
canonical evaluator dispatch and returns one value. `single` retains the input element without
copying nested payloads; `parseDecimal` retains only its Decimal result and uses the existing exact
core parser after bounded lexical normalization. No cache or process-wide retained state is added.

| Workload | Mean | Managed allocation/op |
| --- | ---: | ---: |
| Decimal `0012.50` | 92.56 ns | 256 B |
| Decimal maximum coefficient (29 digits) | 562.01 ns | 1,856 B |
| Single flat scalar | 25.40 ns | 88 B |
| Single nested object | 25.39 ns | 88 B |
| Single collection payload (4,096 values) | 26.32 ns | 88 B |

This establishes the cost of new operations; there is no previous implementation to compare.
Exact parsing intentionally reuses the authoritative BigInteger-backed core routine rather than
introducing another decimal implementation. Its temporary allocation is bounded by the Decimal
coefficient/scale limits after scanning and trimming zero padding. The long-padding test uses
200,004 characters and enforces a 2,048-byte allocation ceiling. Selection's allocation ceiling
is 128 bytes per invocation, independent of payload size. Graph-owned named enum validation reuses
observation admission and has a deterministic zero-allocation success-path regression.

Expression analysis and draft validation occur during preparation, not per row. Contextual branch
typing adds no second traversal: existing branch analysis receives the target expectation and
validates every branch before joining. Named literals are resolved in invocation-owned graph state.

Reproduce:

```sh
dotnet run --project src/Cohesive.Relations.Benchmarks -c Release -- \
  --filter '*ExpressionValueContractBenchmarks*' --job short --warmupCount 1 --iterationCount 3
```

Executable invariants live in `RelationQueryExpressionEvaluatorTests`, `ObservationValidatorTests`
and `ExprAnalysisTests`. Timing thresholds are deliberately excluded from CI.
