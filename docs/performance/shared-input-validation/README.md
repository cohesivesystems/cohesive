# Shared input validator cache

Measured 2026-11-15 on the refinement based on SDK `142a4018`.
[Full BenchmarkDotNet report](benchmarks.md) includes uncertainty and allocation.
Twelve cases cover flat, nested, 16-item and 1,024-item inputs, with three warmups
and ten measured iterations (one launch).

```sh
dotnet run -c Release --project src/Cohesive.Relations.Benchmarks -- \
  --filter '*SharedInputValidatorBenchmarks*' --job short --warmupCount 3 --iterationCount 10
```

- `DirectTyped`: the existing typed API with a prebuilt shape graph.
- `SharedTyped`: the shared service with a populated per-type cache.
- `ColdCache`: a fresh configuration/cache plus first validator preparation and typed validation on every invocation. This is not cold process/JIT measurement.

Both warm paths project input on every call. A shared cache eliminates repeated
shape authoring, not request projection. No requests or validation results are
retained. The allocation regression separately tests warm cache lookup plus
portable-value validation at zero bytes, and mutable-input/weak-reference tests
check that results and caller objects are not retained.

These are single-machine component measurements, not an application performance
or Cohesive-versus-conventional claim. Interpret small timing differences with
the report's uncertainty; the API refinement is about configuration and reuse.

Validation: 5,685 Release tests passed, 72 environment-dependent skips. Twelve
new cases cover required/Unicode semantics, fresh mutable inputs, concurrent
first use, policy isolation, eager/lazy authoring failure, DI sharing and warm
allocations. Trimming and NativeAOT remain outside this qualification.
