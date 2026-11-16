# Validation context integration into main — 2026-11-15

This is a focused integration of the context-reuse change from `cfc0ab24`, plus its required pooled
location-cursor storage, onto main at `ae546131`. The historical isolated baseline `24a07634` is not
merged: unrelated transition-kernel, authoring and other framework changes remain isolated.

All current-main validation bodies are retained, including value/contract constraints and the shared
cached input validators. Only workspace creation/disposal and location storage change. Each synchronous
validation exclusively leases a context. A thread retains at most one cleared idle owner; nested leases
remain independent. Disposal returns and clears pooled segments and releases graph, diagnostics and
active-type references while resetting counters and projection policy. Returned diagnostics keep their
own paths and immutable arrays. Cold threads and nested simultaneous leases can still allocate.

Release execution and model checks: **3,095 passed, 13 skipped**, including the current constraint tests
and added context ownership, graph lifetime, exception cleanup, policy reset, retained-path and concurrent
reuse tests. Builds passed with existing dependency-audit warnings; no dependency versions changed.

The integration was measured against current main, not the older optimized runtime. Main still allocated
a location list and backing array on every validation, so its allocation baseline differs from the prior
isolated report. BenchmarkDotNet 0.15.8 / .NET 10.0.13 / Apple M5 Max, Release; eleven 250 ms iterations,
five warmups, two launches. Values and contracts are preconstructed and warmed. No separate builds or
tests overlapped timed execution.

| Workload | Before B/op | After B/op | Before mean | After mean |
|---|---:|---:|---:|---:|
| collection | 256 | 0 | 1,362.08 ns | 1,367.51 ns |
| flat | 256 | 0 | 60.32 ns | 55.57 ns |
| large-collection | 256 | 0 | 81,693.42 ns | 72,692.80 ns |
| nested | 536 | 0 | 367.36 ns | 274.44 ns |
| scalar | 256 | 0 | 33.33 ns | 30.77 ns |

No clear latency regression was observed: scalar, flat, nested and large-collection means decreased.
The collection result was noisy/multimodal and its small mean difference is within run variability;
it does not establish either a speedup or regression. Zero allocation applies to these warm valid
fixtures, not arbitrary diagnostics, named-type bookkeeping, cold initialization or nested leases.
The original isolated transition-runtime numbers are not claimed for this narrower main integration.

[Raw benchmark results and verification logs](evidence/validation-context-main-integration/).
