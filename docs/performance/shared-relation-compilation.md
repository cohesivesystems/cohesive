# Request-scoped Relation compilation across evaluators

Hosts commonly create one `RelationQueryEvaluator` per tenant or placement over a single immutable
`RelationQueryCompilationRequest`. The evaluator previously stored static compilation in its own
preparation entry, so each new evaluator recompiled the request and produced a new plan instance.
The default in-memory interpreter caches realization weakly per plan instance, so realization
(requirement projection, matching and fingerprinting) was also repeated for every evaluator.

Static compilation depends only on the exact request. It now lives in a process-wide weak table keyed
by the request instance; every evaluator over that request observes the same compilation result and
plan, and therefore the same realization report. Placement and physical planning still depend on the
evaluator's placement callback, source readers and policy, and remain evaluator-scoped. Retention is
bounded by the request lifetime. A failed compilation is not stored, so a later evaluation retries.

## Evidence

Measured in Ari's `ProtocolCompilationAllocationTests` (warm X12 004010 204 protocol-spec
compilation; Linux container, Release, cumulative `GC.GetTotalAllocatedBytes(precise: true)` for one
case), whose catalog acquisition creates one evaluator per query context over a shared request:

| Configuration | Allocated |
|---|---|
| Ari supplied-only bridge already sharing its evaluator and request | 109 MB |
| With shared request-scoped compilation | 98 MB |

The removed work was realization of the acquisition queries (sampled with `GCAllocationTick` stacks
under `RelationQueryInMemoryInterpreter.CompileRealization`). This is an allocation measurement of one
end-to-end test case, not a latency claim.

`RelationQueryEvaluatorTests.EvaluateAsync_SharesRequestScopedCompilationAndRealizationAcrossEvaluators`
asserts that two evaluators over one request return the same compilation and realization instances
while invoking placement independently.
