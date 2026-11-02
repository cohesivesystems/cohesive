# Machine admission

The canonical machine definition remains the semantic authority. Construction derives state/action
membership, candidate indices, guard-presence sets and sorted offered actions from canonical edges.
These private caches live with the definition and do not enter serialization or fingerprints. Admission
shares the existing stateless portable expression evaluator; facts, input and expression context remain
invocation-local. No compiled guard cache or additional expression interpreter is introduced.

## Reproduction against actual revisions

Run from the repository root, supplying absolute result paths:

```sh
bash docs/performance/machine-admission/compare-revision.sh 536fde87 /tmp/machines-before
bash docs/performance/machine-admission/compare-revision.sh HEAD /tmp/machines-after
```

The script archives the selected Git revision into a temporary checkout, copies the current benchmark
fixture into it, and runs that revision's actual production code. It records the full production revision
and fixture alongside the results. Run sequentially without other compilation or benchmark work.
The old code imports typed guards from Relations.Authoring, so the shared fixture includes that import
for compatibility. No predecessor evaluator or admission implementation is copied into the fixture.
For uncommitted changes, run the fixture directly in the working tree instead:

```sh
dotnet run -c Release --project src/Cohesive.Relations.Benchmarks -- --filter '*MachineAdmissionBenchmarks*' --artifacts /tmp/machines-after
```

The guarded fixture has one state and 1, 128 or 1,024 guarded self-loop edges with distinct actions;
admission selects the last-added action. Facts/input projection and machine construction happen outside
timing. The unguarded fixture measures Decide and Enabled over the same catalog sizes. Both fixtures
call public production APIs exclusively. Runtime semantics are checked separately by conformance tests.

## Results

Before is actual production commit `536fde87280fde36f1005a5b564816970d894609`; after is the production
source committed with this report, measured in the working tree. Runs were sequential on Apple M5 Max /
.NET 10.0.13 without overlapping test or build work. See [raw reports](results.md) for environment and dispersion.

| Operation | Edges | Before mean | After mean | Before bytes/op | After bytes/op |
| --- | ---: | ---: | ---: | ---: | ---: |
| Guarded admission | 1 | 3,841 ns | 228 ns | 7,520 | 496 |
| Guarded admission | 128 | 4,248 ns | 224 ns | 7,520 | 496 |
| Guarded admission | 1,024 | 5,989 ns | 229 ns | 7,520 | 496 |
| Decide | 1 | 8.7 ns | 29.8 ns | 32 | 0 |
| Decide | 128 | 304 ns | 29.0 ns | 32 | 0 |
| Decide | 1,024 | 2,699 ns | 32.5 ns | 32 | 0 |
| Enabled | 1 | 68 ns | 8.1 ns | 552 | 0 |
| Enabled | 128 | 3,986 ns | 7.9 ns | 10,472 | 0 |
| Enabled | 1,024 | 47,514 ns | 7.3 ns | 94,168 | 0 |

Guarded allocation is about 93% lower. The actual old implementation allocates 7,520 B, correcting the
former reconstructed baseline's 7,432 B. The raw report rounds this to 7.34 KB; its GC measurements were
1,971,322,880 bytes / 262,144 operations and 985,661,440 bytes / 131,072 operations.

There is an explicit small-catalog tradeoff: Decide is slower for the single-edge fixture, while avoiding
allocations and catalog-size-dependent scans. The indexed selection path favors repeated admissions over
larger catalogs and one semantic selection authority; a special single-edge fast path is not added.

Short-run timings remain indicative, not an SLA; consult raw dispersion before drawing timing
conclusions. The allocation regression test bounds each guarded admission at 1 KB for both catalog
sizes and allows a 256-byte difference rather than requiring runtime-specific exact byte equality.

## Guarantees and remaining costs

Evaluate is the canonical selection operation for both guarded and unguarded definitions. Decide
preserves the convenience contract (throw on unknown or guarded actions, null for illegal actions)
and shares the indexed selection operation with Evaluate, without allocating an intermediate decision
when only the edge is needed. Enabled returns an immutable, cached, ordinally sorted action list; it refuses guarded actions
from the current state. Guard requirements retain their existing scope: Decide checks the action across
the machine, while Enabled checks the current state. Neither convenience method scans the edge catalog.

Admission cost still scales with candidates for the requested state/action and with guard/input-contract
complexity. The shared reference evaluator still walks Expr on every admission; invocation contexts,
closures and result objects still allocate. This change does not claim compiled predicates or zero
allocation admission. Unknown action, illegal, disabled, ambiguous and indeterminate retain their meaning.

Index construction adds definition-lifetime memory and build work, amortized across calls. Measurements
exclude construction, persistence, observation projection, entity transitions and relationship analysis.
Compiled guard plans are deferred until representative workloads justify extending the shared expression
execution infrastructure. The former reconstructed-baseline results are superseded by the actual-revision
measurements here.
