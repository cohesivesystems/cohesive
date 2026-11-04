# Transition discovery measurements

`TransitionDiscoveryBenchmarks` inspects 1, 8 or 32 registered operations on one entity, repository
instance and organization scope. All operations require an explicit capability grant and resource
ownership. The fixture either omits commands (RequiresInput) or evaluates complete commands through
the canonical Transition interpreter. Setup, declaration lowering and repository seeding are excluded.
The in-memory repository makes this an allocation/CPU workload, not a network-latency estimate.

The baseline is actual production commit `15cc53b19f53a9e78a2e04749c6b2adf67b7f0d6`, with the exact
current benchmark fixture copied into an archived checkout. The after measurement uses production
source committed with this report. No hand-copied baseline implementation is used.

| Operations | Complete command | Before allocation | After allocation |
| --- | --- | --- | --- |
| 1 | No | 4.63 KB | 3.41 KB |
| 1 | Yes | 10.75 KB | 9.66 KB |
| 8 | No | 33.41 KB | 16.78 KB |
| 8 | Yes | 82.37 KB | 66.74 KB |
| 32 | No | 132.55 KB | 63.05 KB |
| 32 | Yes | 328.57 KB | 263.04 KB |

The same-policy scope/resource checks fall from twice to once per operation. Compatible operations
share one repository read per inspection instead of one read per operation. Scope/resource decisions
are never shared between operations; the result must honor each operation's capabilities and policy.
Tests enforce read/check counts, separate repository/scope boundaries, per-operation resource denial,
no writes, and a fresh read on every inspection call.

The per-call snapshot cache retains one snapshot and observation per repository/scope, including
negative read results, only until inspection completes. Output remains proportional to operations;
full Transition evaluation still runs once per supplied command. Existing state validation and result
payload suppression remain intact. No cross-call permission or snapshot cache is introduced.

Raw BenchmarkDotNet ShortRun output: [before](before.md), [after](after.md). Runs overlapped some build
and benchmark activity on the host, so timing columns are advisory; no controlled latency improvement
is claimed. Allocation and deterministic read/authorization counts are the primary evidence.

Reproduce from the repository root (run sequentially for cleaner timing):

```sh
bash docs/performance/machine-admission/compare-revision.sh 15cc53b1 /tmp/discovery-before TransitionDiscoveryBenchmarks
dotnet run -c Release --project src/Cohesive.Relations.Benchmarks -- --filter '*TransitionDiscoveryBenchmarks*' --artifacts /tmp/discovery-after
```
