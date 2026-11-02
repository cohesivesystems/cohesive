# Guarded machine admission

The canonical machine definition remains the semantic authority. Construction derives immutable-use
state/action membership and candidate indices from its canonical edges; these caches do not enter
serialization or fingerprints. Admission shares the existing stateless portable expression evaluator,
while facts, input and expression evaluation context remain invocation-local. No compiled guard cache
or additional expression interpreter is introduced.

## Measurement

Run from the repository root:

```sh
dotnet run -c Release --project src/Cohesive.Relations.Benchmarks -- --filter '*MachineAdmissionBenchmarks*'
```

`MachineAdmissionBenchmarks` compares the prior successful guarded-admission strategy (linear scans,
candidate array, new capability profile and evaluator per guard) with `MachineDefinition.Evaluate`.
The fixture has one state and 1, 128 or 1,024 guarded self-loop edges with distinct actions; admission
selects the last-added action. Both paths check input contracts and evaluate the same portable predicate.
Facts and input are projected before timing. Setup checks matching decision kinds.

| Edges | Before mean | After mean | Before bytes/op | After bytes/op |
| ---: | ---: | ---: | ---: | ---: |
| 1 | 3,947 ns | 295 ns | 7,432 | 496 |
| 128 | 5,159 ns | 247 ns | 7,432 | 496 |
| 1,024 | 5,941 ns | 226 ns | 7,432 | 496 |

See [raw BenchmarkDotNet results](results.md) for environment, dispersion and GC measurements.
This is a short run on Apple M5 Max / .NET 10.0.13; unrelated test compilation overlapped part of
the run. Timing differences are indicative, not an SLA or evidence that larger catalogs are faster.
The stable allocation reduction is approximately 93%. The regression test bounds bytes per admission
and checks that adding unrelated actions does not increase allocations.

## Guarantees and remaining costs

Candidate lookup no longer scans the entire catalog. Cost still scales with the number of candidate
edges for the requested state/action and the complexity of their guards and input contracts. The
shared reference evaluator still walks Expr on every admission; invocation contexts, closures and
result objects still allocate. This change does not claim compiled predicates or zero allocation.
Unknown action, illegal, disabled, ambiguous and indeterminate outcomes retain their prior meaning.

Index construction adds definition-lifetime memory and build work, amortized across admissions.
These measurements exclude build, persistence, observation projection, entity transition execution,
relationship analysis and the unguarded Enabled/Decide convenience methods. Those methods retain
their existing implementation. Specialization or compiled guard plans are deferred until representative
workloads justify extending the shared expression execution infrastructure.
