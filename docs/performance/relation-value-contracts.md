# Explicit relation value operations

Measured on 2026-09-22 with BenchmarkDotNet 0.15.8, macOS Arm64 / Apple M5 Max,
.NET SDK 10.0.201 and .NET runtime 10.0.5. Short job, one warmup and three measured
iterations. These are warm isolated operation measurements, not application latency claims.

The benchmark prepares immutable inputs and expressions once. Each measured invocation includes
canonical evaluator dispatch and returns one value. `single` retains the input element without
copying nested payloads; `parseDecimal` retains only its Decimal result and delegates to the shared exact
core parser. JSON numbers use the same scanner with exponent notation enabled. No cache or process-wide retained state is added.

The before measurement is commit `2dfe6e4`; the after measurement uses the consolidated parser
with the same benchmark setup, machine, SDK and job. Short-run timing has broad confidence intervals;
allocation is the stronger result. No end-to-end latency claim is made.

| Workload | Before mean | After mean | Before allocation/op | After allocation/op |
| --- | ---: | ---: | ---: | ---: |
| Decimal `0012.50` | 92.56 ns | 46.88 ns | 256 B | 80 B |
| Decimal maximum coefficient (29 digits) | 562.01 ns | 91.23 ns | 1,856 B | 80 B |
| Single flat scalar | 25.40 ns | 26.17 ns | 88 B | 88 B |
| Single nested object | 25.39 ns | 30.10 ns | 88 B | 88 B |
| Single collection payload (4,096 values) | 26.32 ns | 26.03 ns | 88 B | 88 B |

The shared parser delegates coefficient parsing to BCL `UInt128.TryParse` after a lexical scan
identifies nonzero digit bounds and exponent-adjusted scale. It needs at most 29 stack characters
and rejects coefficients outside Decimal's 96-bit domain instead of letting `decimal.TryParse`
round them. The core helper allocates zero managed bytes, including 200,004-character padded
inputs and 100,000-digit rejected coefficients. Evaluator dispatch accounts for the remaining
80 B/op. The previous general BigInteger accumulation is removed from JSON parsing as well;
large exponent-normalized values cannot cause coefficient-sized arithmetic allocation.

`JsonElement` still materializes a token string once; its regression bounds that allocation by
input length plus 2,048 B for a 100,009-character exponent-normalized token. The shared exact
arithmetic adds no growing work storage. Streaming JSON retains its existing stack/rented input
buffer. Selection's allocation ceiling remains 128 bytes per invocation, independent of payload
size. Graph-owned named enum validation retains its zero-allocation success-path regression.

Expression analysis and draft validation occur during preparation, not per row. Contextual branch
typing adds no second traversal: existing branch analysis receives the target expectation and
validates every branch before joining. Named literals are resolved in invocation-owned graph state.

Reproduce:

```sh
dotnet run --project src/Cohesive.Relations.Benchmarks -c Release -- \
  --filter '*ExpressionValueContractBenchmarks*' --job short --warmupCount 1 --iterationCount 3
```

Executable invariants live in `RelationQueryExpressionEvaluatorTests`, `ObservationValidatorTests`
`ObservationValueTests` and `ExprAnalysisTests`. Timing thresholds are deliberately excluded from CI.

## Integer parser extension (2026-09-23)

The same warm evaluator benchmark now includes Int32 `"002"`, Int64 maximum and an Int32 input
with 4,096 leading zeroes. On Apple M5 Max, macOS arm64, .NET SDK 10.0.201/runtime 10.0.5,
Release, BenchmarkDotNet ShortRun (one warmup, three measured iterations), observed means were
28.44 ns, 41.08 ns and 1.172 μs respectively, with **72 B/evaluation** for all three.
Run: `dotnet run --project src/Cohesive.Relations.Benchmarks -c Release -- --filter '*ExpressionValueContractBenchmarks*' --job short --warmupCount 1 --iterationCount 3`.

Inputs/expressions are prepared once in GlobalSetup; measurement includes normal evaluator dispatch
and returning the scalar, but excludes compilation, observation lookup and full relation execution.
The existing decimal cases measured 41.57/87.64 ns and 80 B; these are different semantic workloads,
not a before/after speedup claim. Other validation jobs were active, so these short-run timings are
exploratory; no end-to-end latency or controlled timing guarantee is claimed. The 100,000-zero input
regression independently checks bounded allocation. There is no per-input cache or payload copy.

## Required-value assertion (2026-09-23)

The new `requireValue` assertion evaluates its argument once and returns the original
`ObservationValue`; it does not traverse or copy its payload. The warm benchmark prepares an
immutable expression and flat text, nested object, or 4,096-element array outside measurement.
On Apple M5 Max/macOS arm64, .NET SDK 10.0.201/runtime 10.0.5, Release, one warmup and three
measured iterations, observed means were 29.03 ns (flat), 26.83 ns (nested), and 31.44 ns
(collection), with **80 B/evaluation** in every case. This includes existing generic evaluator
dispatch allocation; it is not an allocation-free claim. Compilation, observation validation and
full relation execution are excluded. Concurrent validation and short runs limit timing precision;
these are exploratory measurements, not end-to-end latency claims.

Reproduce with the command above and filter `*ExpressionValueContractBenchmarks*required*`.
The payload-storage identity and 10,000-evaluation allocation-scaling tests protect the mechanism.
`AsPresentNonNull` runs during preparation, reuses already-refined contracts and otherwise creates
one immutable contract; it adds no runtime cache or per-row contract analysis.
