# Canonical Process closure compilation

The reusable capability is `ProcessStaticCompiler.CompileClosure`. Applications select exact roots;
canonical Process nodes own child dependencies. The existing exact document catalog resolves them.
Compilation derives dependencies using the same semantic projection as validated Process links and
prepares each reachable child once, bottom-up. Iterative traversal avoids host call-stack growth.
Successful projections are invocation-scoped; no global retention or independently persisted graph exists.

The compiler retains full canonical validation for every document. Discovery projects typed IR once;
that same immutable projection is reused after admission validation rather than projected a third time.
Parent contexts retain only reachable child links. Complete transitive evidence is still required by
the existing validator; this change does not remove its integrity or recursion checks.

## Qualification and measurement

`ProcessClosureCompilationTests` exercises a four-definition diamond, duplicate roots and invocations,
unselected documents, exact fingerprint mismatch, missing children, malformed semantic definitions,
wrong document kinds, competing external Process links, and equivalence with explicit bottom-up plans.
The generated-authoring and existing semantic/link tests also pass (80 focused tests).

A temporary .NET 10.0.201 Release harness on macOS used the test's exact four-definition diamond.
Documents, interaction contracts, and the document catalog were prepared outside the measurement.
The manual baseline compiled the four documents bottom-up, sharing the leaf link and supplying only
reachable links to each parent. The closure path selected the root and derived those links. Both paths
ran ten warmups followed by 100 synchronous calls. Allocations used
`GC.GetAllocatedBytesForCurrentThread`; elapsed time used `Stopwatch`. This measures compilation only,
not startup, physical realization, database I/O, Ari test-suite duration, or deployment admission.

| Path | Allocated bytes per call | Milliseconds per call |
| --- | ---: | ---: |
| Explicit bottom-up preparation | 402,729 | 0.774 |
| Graph-derived closure | 409,682 | 0.879 |

The derived path adds approximately 7 KB (1.7%) for graph resolution and traversal on this tiny workload.
The timing difference is roughly 0.1 ms and is not an end-to-end speed improvement claim. First-use
samples were deliberately not compared because sequential execution shares serializer/JIT initialization.
An initial implementation projected each payload once more during discovery; reusing the discovered
projection reduced the measured warm closure allocation from about 445 KB to 410 KB.

## Ownership and adoption

This extends existing Cohesive Process compilation rather than introducing an Ari graph coordinator,
parallel identifier catalog, or persisted dependency type. `ExecutionDefinitionDocumentCatalog`, canonical
Process validation, and validated `ProcessDefinitionLink` evidence remain their respective authorities.
The closure result establishes all-or-nothing target-independent admission: failures expose no partial
plans and retain the failed exact reference alongside diagnostics. Physical admission remains separate.

Ari adoption must remove both its explicit bottom-up deployment sequence and recursive child-link wiring
in validation-context helpers. Product-selected roots, non-Process relation/transition evidence,
interaction contracts, and fresh physical capability checks remain Ari policy. This capability must be
published and consumed before replacing that code; copying Cohesive implementation into Ari is excluded.
