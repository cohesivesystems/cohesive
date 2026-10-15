# Verified physical preflight

`RelationQueryPhysicalExecutor` verifies, before any source I/O, that a request's semantic plan,
realization report, placement, policy and physical plan agree. It recomputes the realization,
placement and physical-plan fingerprints and re-runs physical planning. Evaluators already cache
those immutable inputs per compilation request, so every evaluation repeated identical
verification of the same instances.

The executor now remembers a successful verification in a weak table keyed by the
`CompiledRelationQueryPhysicalPlan` instance, together with the exact semantic plan, realization
report and interpreter instances it was verified against. A request skips re-verification only when
all four are the same instances. Any other instance, including an equal deserialized copy, is
verified afresh; failures are never remembered. All inputs are immutable. Supplied-source,
partition, capability and reachability checks still run on every execution.

## Evidence

Ari `ProtocolCompilationAllocationTests` workload (warm X12 004010 204 protocol-spec compilation),
local Linux container, Release, Cohesive `main` packed locally, cumulative allocated bytes for one
warm case: 60.0 MB before, 57.6 MB after. Sampled stacks attributed the removed work to
`RelationQueryRealizationFingerprinter.Compute` and `RelationQueryPhysicalPlanner.Compile` under
`RelationQueryPhysicalExecutor.Preflight`. This is an allocation measurement, not a latency claim.

`RelationQueryPhysicalExecutorTests.ExecuteAsync_VerifiesPlanIntegrityOncePerExactPreparedInputs`
asserts one physical planning pass across two executions;
`ExecuteAsync_PreviouslyVerifiedPhysicalPlanStillRejectsStaleRealization` asserts a verified plan
still rejects a mismatched realization before any I/O.
