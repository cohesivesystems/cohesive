# Compiled observation contracts

This follows parameter metadata preparation at `0cc53483`, on isolated branch
`codex/transition-observation-metadata`. It is based on the current-main lineage with
validation-context pooling; it does not integrate the historical specialized-kernel branch.

## Change and ownership

Code inspection identified a remaining invocation-local dictionary and freshly constructed
value contracts for statically known observation paths. The before/after experiment below
isolates moving that work to compilation.

The compiled plan now derives an immutable contract catalog from the existing semantic
analysis's read and write requirements. It uses the same relative-path resolver as dynamic
navigation, preserving shape constraints, missing-field diagnostics, unsupported collection
navigation, and collection-scope checks. Metadata preparation does not introduce a second
path traversal implementation or a new public type.

Invocation values and decision evidence remain invocation-owned. Every read and supplied
observation still follows the existing validation boundary. Caller-supplied extra/covering
sparse paths retain dynamic resolution and an invocation-local cache, allocated only when
needed. Invalid paths keep the same runtime diagnostic policy; compilation does not eagerly
reject a path that cannot be prepared.

The tradeoff is bounded plan-lifetime storage proportional to distinct declared read/write
paths and extra compilation work. This saves the common-path dictionary and repeated contract
construction. It does not pool returned decisions or memoize validation results.

## Verification and benchmark scope

The benchmark reuses `TransitionParameterMetadataBenchmarks`: the authored 13-field shipment,
a guard, three assignments, and complete `DecideFullState` validation/evidence. Compilation,
initial values, final state projection, storage, and HTTP work are excluded. Tests/builds from
this task do not overlap timed execution. BenchmarkDotNet 0.15.8, .NET 10.0.13, Apple M5 Max,
Release; two launches, five warmups, eleven 250 ms measurement iterations per launch.

Conformance includes full/sparse/nested/covering observations, conflicts, shape-field
constraints, extra sparse fields, invalid paths, concurrent use, and retained decisions.
The fixed-seed test additionally compares complete decisions with both prepared catalogs
omitted, exercising dynamic resolution of the same canonical definition.


## Results

| Decision | Before B/op | After B/op | Before mean | After mean |
|---|---:|---:|---:|---:|
| Applied | 8,098 | 7,570 | 2.714 μs | 2.509 μs |
| Rejected | 3,569 | 3,249 | 1.319 μs | 1.291 μs |

Applied decisions allocate approximately 528 fewer bytes (6.5%) and run 7.6% faster in this
fixture. Rejected decisions allocate 320 fewer bytes (9.0%) in this step, or 408 fewer
bytes (3,657 → 3,249 B/op) across both metadata changes. The approximately 2% rejection
latency difference is too small relative to observed run-to-run noise to claim a speedup.
Neither measured path showed a reproducible latency regression. These warm measurements do not establish production
percentile latency or cover every possible sparse observation pattern.

Execution-kernel/model suite: **3,099 passed, 13 skipped**. The strengthened differential
checks then passed with all **68** interpreter/compiler tests. Existing dependency audit warnings
remain; dependencies were not changed.

[Benchmark summaries and verification logs](evidence/transition-observation-metadata/).

## Review hardening

Parameter preparation now uses the same complete relative-path resolver, including root
cardinality checks. Default parameter arrays normalize to empty; duplicate catalog entries
reuse the canonical field contract. Validation locations carry a debug-only lease generation
checked before the existing cursor-prefix assertions. The context remains internal, and its
nested-lease tests use direct access rather than reflection. Segment-array retention is deferred
pending separate measurement; Release validation hot-path behavior is unchanged by the debug guard.

After these fixes: **3,100 Release tests passed, 13 skipped**, and **47 focused Debug tests
passed**, including location invalidation across disposal and owner reuse. Benchmark figures
above are the prior measurements; no new latency claim is made for these review fixes.
Only CSV/Markdown benchmark summaries and verification logs are retained in the final tree.
