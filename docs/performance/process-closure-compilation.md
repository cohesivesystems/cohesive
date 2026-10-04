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

## Reuse the discovered typed projection during validation

Ari's exact alpha127 admission profile attributed about 505 MB of cold allocation to canonical
preparation, including 317 MB inside closure compilation. Across the complete authored Process catalog,
one typed projection pass allocated about 44 MB. Closure discovery had already decoded each reachable
document, but validation decoded it again before discarding that second projection.

The compiler now supplies its strictly decoded, call-scoped projection to an internal shared document
validation path. The JSON document remains authoritative. Envelope/integrity checks, strict canonical
byte equality, block semantics, source-map attribution, and context-dependent diagnostics remain fresh.
The internal path accepts only the projection discovered from that exact immutable document; public
compilation and imported-document admission still use strict decoding. No new cache or public API is
introduced, and no normalized-byte comparison is replaced by digest equality.

Local Ari Release qualification used an isolated checkout and temporary core/Process assembly overrides
for the prototype, compared with exact published alpha127 packages in the same checkout. Catalog
authoring precedes measurement; first admission includes preparation, realization, and physical admission.

| Admission | Published alpha127 allocated bytes | Prepared projection allocated bytes |
| --- | ---: | ---: |
| First | 549,600,016 | 508,074,320 |
| Warm | 42,938,960 | 42,935,632 |

The cold reduction is 41,525,696 bytes (about 7.6%). Warm preparation reuse is preserved. This is a local
source-assembly allocation qualification, not a published-package or CI-latency claim. Concurrent testing
makes the observed elapsed-time samples unsuitable for a timing comparison. Environment: macOS,
.NET SDK 10.0.201, Release; test boundary
`RepeatedAdmissionSharesPreparationButRetainsFreshDeploymentValidation`.

Regression coverage compares direct and closure diagnostic JSON for omitted canonical members and
unknown imported members, preserves exact canonical bytes and child evidence, and checks that 16 KiB
and 64 KiB Process payloads avoid repeated projection allocation after warmup. Full core qualification
passed 4,194 tests with 33 existing skips before adding the two allocation cases; all 11 closure cases
then passed.

Ari's full engine project passed 854 tests with 18 existing scheduler skips using the same local
prototype assemblies. Temporary overrides were removed after qualification and are not part of either
repository's source change.
