# Guarded discriminator linking and Transition admission

This fixture measures the entity integration, independently of the standalone machine benchmark.
`GuardedMachineTransitionBenchmarks` varies sibling edges sharing one source state/action (1, 8, 32)
and unrelated edges with distinct actions (0, 128). Guards select a different integer choice from entity facts;
exactly the last sibling is enabled. Every edge leads from ready to done.

`Link` constructs the typed discriminator binding and all linked evidence. Machine source lowering
and canonical document construction are excluded. `Admit` executes a precompiled Transition through
`TransitionReferenceInterpreter.DecideFullState`, including its configuration assignment, target check,
patch and decision materialization. Compilation and observation/input preparation are excluded.
Setup checks that the compiled operation succeeds. No I/O, storage or entity commit is measured.

## Reproduce

Run sequentially on the same host, without concurrent builds or benchmarks:

```sh
bash docs/performance/machine-admission/compare-revision.sh 06990cca /tmp/guarded-before GuardedMachineTransitionBenchmarks
bash docs/performance/machine-admission/compare-revision.sh HEAD /tmp/guarded-after GuardedMachineTransitionBenchmarks
```

The script runs actual production code at each revision with the current shared public-API fixture,
recording the full revision and fixture. For uncommitted changes, use:

```sh
dotnet run -c Release --project src/Cohesive.Relations.Benchmarks -- --filter '*GuardedMachineTransitionBenchmarks*' --artifacts /tmp/guarded-after
```

## Results

Apple M5 Max / .NET 10.0.13, sequential runs with no overlapping build/test work.
Values are means; KB means 1,024 bytes. [Raw reports](guarded-transitions-results.md) include dispersion and GC data.

| Siblings | Unrelated | Link before → after (µs) | Link allocation before → after (KB) | Admit before → after (µs) | Admit allocation before → after (KB) |
| ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | 0 | 8.323 → 7.924 | 25.04 → 25.31 | 1.501 → 1.502 | 7.10 → 7.10 |
| 1 | 128 | 99.919 → 42.326 | 107.49 → 131.58 | 1.862 → 1.518 | 7.10 → 7.10 |
| 8 | 0 | 15.247 → 9.140 | 43.84 → 32.21 | 3.626 → 4.494 | 11.08 → 12.13 |
| 8 | 128 | 84.179 → 45.733 | 126.77 → 139.12 | 3.622 → 4.461 | 11.08 → 12.13 |
| 32 | 0 | 55.265 → 15.807 | 276.28 → 53.92 | 10.516 → 12.463 | 25.01 → 25.49 |
| 32 | 128 | 143.583 → 50.991 | 358.30 → 159.91 | 10.930 → 12.343 | 25.01 → 25.49 |

The tradeoff is explicit: grouping substantially reduces linking work for larger catalogs and repeated
siblings, but adds temporary grouping allocations for singleton-heavy catalogs. Precise ambiguity counting
adds runtime cost: the 32-sibling/no-unrelated case increased from 10.5 to 12.5 µs, with a small allocation
increase. A single sibling retains its old execution cost. Unrelated edges affect linking, while admission
works with the compiled movement's sibling evidence. The shared interpreter still dominates execution and
allocates; these results are not the standalone admission numbers or a zero-allocation claim.

## Single-pass follow-up

The follow-up stores ordered guard expressions once per linked edge slice and evaluates each at most once,
using a single evaluation context per movement. Runtime stops at the second enabled guard. The selected
edge result is retained from that pass; no numeric counting expression runs in the interpreter. The compiler
still derives balanced expressions for static analysis, exposed as compiler methods so they are not serialized. Older expression-count link evidence is rejected
by strict JSON member validation and must be regenerated from source.

| Siblings | Unrelated | Link mean (µs) | Link allocation (KB) | Admit mean (µs) | Admit allocation (KB) |
| ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | 0 | 8.861 | 25.40 | 1.451 | 7.03 |
| 1 | 128 | 43.488 | 132.68 | 1.478 | 7.03 |
| 8 | 0 | 9.203 | 30.81 | 3.031 | 10.03 |
| 8 | 128 | 45.610 | 138.72 | 3.007 | 10.03 |
| 32 | 0 | 18.744 | 46.69 | 8.240 | 20.58 |
| 32 | 128 | 74.192 | 153.75 | 8.327 | 20.58 |

At 32 siblings without unrelated edges, admission improves from the previous 12.463 µs / 25.49 KB to
8.240 µs / 20.58 KB. Link-time diagnostics and grouping still allocate, notably for singleton-heavy catalogs.
Some linking timing error bars remain wide: the tables are evidence, not a promise of a uniform speedup.
The shared guard array is reused in memory, but independent edge serialization still repeats sibling guards.
A catalog-level reference table would change the persisted evidence format and is intentionally deferred;
the redundant derived count and selected-guard expression are no longer serialized.

## Interpretation and bounds

Before is commit `06990ccae27f3e7d8ff6c653e6e0d1d54af7a113`. After is the production source committed
with this report. These ShortRun results are indicative measurements on one host, not latency guarantees.
The fixture covers successful selection of one sibling; false guards, wrong source states, ambiguous
selection and predicate-backed configurations are correctness tests rather than benchmark workloads.

Linking now groups edges once by source state/action, lowers each guard once, and shares an ordered
guard array per group. It no longer scans the entire edge catalog for each linked edge.
Catalog normalization still sorts the final edge list. Shared immutable expression references reduce
construction work; serializing every link independently may still repeat the group's evidence.

Admission checks the source state's ambiguity predicate before normal admission. Multiple enabled siblings
produce InvalidDefinition with transitions.execution.machine.admissionAmbiguous, including when the
requested edge itself is disabled. A false guard remains an ordinary AdmissionRejected result. Static
compilation analyzes the ambiguity predicate separately so its input dependencies and failure condition
remain visible to sparse analysis. Neither failure produces commit artifacts.

The runtime evaluates guards once and retains whether the selected edge is enabled, stopping at the second
enabled sibling. This remains O(sibling count × guard cost) for unique admission, plus normal interpreter work. Link-time equality proofs suppress warnings but do not eliminate runtime sibling evaluation.
A single-edge group has no ambiguity expression. Guard contracts, outcome precision and absence of side
effects take priority over suppressing the cost of this diagnostic check.

## Scoped diagnostics and equality proof

The review follow-up adds a conservative pairwise equality proof at registration. Its cost is
quadratic in siblings (not the whole catalog), with constraints collected once per guard.
Runtime evaluation and its guarantees are unchanged. The fixture has distinct `Choice == i`
constraints, so these groups now produce no disjointness warnings.

Compared with the previously recorded single-pass baseline (32 siblings, no unrelated edges),
linking is 21.40 µs / 54.40 KB versus 18.74 µs / 46.69 KB. For one sibling it is
8.84 µs / 25.56 KB versus 8.86 µs / 25.40 KB. Registration does more analysis; this is not a
runtime speedup claim. Short-run error bars remain wide. A trial using nested capturing LINQ
allocated 144.64 KB at 32 siblings; direct pairwise loops avoid that temporary work.

Raw final output: [equality-proof-results.md](equality-proof-results.md). Reproduce with
`dotnet run -c Release --project src/Cohesive.Relations.Benchmarks -- --filter '*GuardedMachineTransitionBenchmarks.Link*'`.
