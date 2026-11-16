# Compiled transition parameter metadata

Based on current main `6db1d3e0`, in branch `codex/transition-parameter-metadata`.
These measurements do not represent the older isolated specialized-kernel runtime.

The static compiler's existing parameter catalog now supplies names to the compiled plan.
The plan prepares each parameter's field path, value contract, and diagnostic node once,
retaining them in an ordinal frozen dictionary. Contract resolution has one implementation,
shared by preparation and dynamic field navigation, including shape-field constraints.
Invocation values are never retained by this dictionary. The interpreter still projects each
value and performs the same validation on every read, then produces independently owned
decisions and evidence. Unresolved metadata retains dynamic resolution and its diagnostics.
The per-invocation input-contract dictionary is removed.

This exchanges bounded, plan-lifetime metadata and compilation work for fewer allocations
and lookups during invocation. It does not cache validation results, change the canonical IR,
weaken validation, pool returned evidence, or introduce a new public type. Other interpreter
allocations and the historical specialized-kernel integration remain separate work.

## Verification

Release execution-kernel and model suite: **3,097 passed, 13 skipped**. The fixed-seed
full/sparse/conflict test additionally compares complete decisions against a plan using dynamic
parameter resolution. Additional tests cover invalid/absent/null/wrong-type inputs, retained
decisions, concurrent shared-plan execution, and preservation of shape-field constraints.
Existing dependency audit warnings remain; no dependency versions changed.

## Benchmark scope

`TransitionParameterMetadataBenchmarks` reuses the existing authored shipment transition:
a 13-field entity, a guard, three assignments, and two parameter reads on the applied path.
It measures `DecideFullState`, including input validation, observation preparation, execution,
and decision evidence. Compilation, initial value construction, final state projection,
storage, and HTTP work are excluded. The rejected case exits at the guard before parameter
reads, but benefits from removal of the empty per-invocation dictionary.

BenchmarkDotNet 0.15.8, .NET 10.0.13, Apple M5 Max, Release; two launches, five warmups,
eleven 250 ms measurement iterations per launch. This task’s tests and builds did not overlap timed
execution. These are warm microbenchmarks; no production tail-latency guarantee is implied.

Reproduce from the repository root:

```sh
dotnet run -c Release --project src/Cohesive.Relations.Benchmarks -- \
  --filter '*TransitionParameterMetadataBenchmarks*' \
  --iterationCount 11 --warmupCount 5 --launchCount 2 --iterationTime 250
```

## Results

| Run | Applied B/op | Applied mean | Rejected B/op | Rejected mean |
|---|---:|---:|---:|---:|
| before | 8,803 | 2.779 μs | 3,657 | 1.336 μs |
| after | 8,098 | 2.724 μs | 3,569 | 1.441 μs |
| after-repeat | 8,098 | 2.733 μs | 3,569 | 1.339 μs |
| before-repeat | 8,803 | 2.763 μs | 3,657 | 1.336 μs |

Applied decisions save approximately 704 B/op (8%); rejected decisions save approximately
88 B/op (2.4%). The one-byte differences between allocation measurements are measurement
variation. Applied latency improved slightly in both comparisons. The first after-change
rejection measurement was noisy/multimodal and slower; repeating in reverse order did not
reproduce that slowdown. These runs show no reproducible latency regression, but do not
establish a rejection-path speedup or a universal latency bound.

[Raw results and verification logs](evidence/transition-parameter-metadata/).
