# Relation preparation reuse

Four changes remove repeated preparation work in `Cohesive.Relations`. Each keeps results, fingerprints and
validation identical; they only avoid recomputing values that are already fixed for an immutable input.

| Change | Repeated work removed |
|---|---|
| `HostedQueryDefinition` implements `IImmutableExecutionDefinition` | Every `HostedQuery.Definition`, `InputContract` or `ResultContract` read decoded the whole definition document again. Like Process, Transition and Relation definitions, the decoded projection is now shared per document instance. |
| `SequenceKeyBuilder` for nested ordering keys | Realization trace, proof and validated-proof keys, and static-compilation trace keys, allocated every inner key and every `ToString("D4")` ordinal as a separate string before copying it into the outer key. The builder writes the identical text directly. `NormalizeTraces` now computes each trace key once and skips work for zero or one trace. |
| Shared `RelationQueryTargetCapabilityProfileAnalysis` | Realization, placement and physical planning analyzed the same immutable target profile repeatedly. Analyses are now weakly cached per profile instance. |
| Report fingerprint computed once | The realization compiler computed a report's fingerprint and the report constructor computed it again to validate it. The compiler now lets the constructor compute it once from the normalized content, and physical planning no longer recomputes it, because the report's only public constructor already rejects a mismatched fingerprint. |

## Invariants

- Ordering keys are byte-for-byte unchanged, so realization fingerprints and decision ordering are unchanged.
  `RealizationAllocationTests` compares the builder with the original concatenation for nulls, empty
  strings, non-ASCII text, values larger than the stack buffer, negative ordinals and nesting. The existing
  golden realization fingerprint tests pass unchanged.
- Shared projections and analyses are keyed by instance identity with weak keys, so an equal but
  different instance is still decoded or analyzed independently, and retention follows the owner.
- `HostedQueryDefinition`'s reachable graph passes the existing `ExecutionImmutabilityContractTests`
  reflection audit for marked definitions.
- The public `RelationQueryRealizationReport` constructor still rejects a supplied fingerprint that differs
  from the normalized content.

## Evidence

Ari `ProtocolCompilationAllocationTests` workload (X12 004010 204 protocol-spec preview plus durable
compilation), local Linux container, Release, Cohesive `main` (`341a949`) packed locally and consumed by
Ari `main`. Cumulative allocated bytes per case; warm is the steady third/fourth case in one process.

| Configuration | First case | Warm case |
|---|---:|---:|
| Cohesive `main` | 251.9 MB | 53.5 MB |
| + shared hosted-query projection | 246.2 MB | 51.3 MB |
| + nested key builder | 238.6 MB | 51.2 MB |
| + shared profile analysis, no planner fingerprint recomputation | 234.5 MB | 50.1 MB |
| + single report fingerprint | 233.2 MB | 50.1 MB |

These are allocation measurements of one end-to-end workload, not latency claims.

## Remaining first-use cost

The largest remaining first-case cost is durable commit fingerprinting (`ProcessDurableCommitFingerprinter`),
which serializes the entire checkpoint, including large payloads, for every commit. Canonical hashing needs
the full document to order properties, so removing it requires composing the commit fingerprint from
existing per-value content fingerprints. That changes the durable fingerprint format and needs a versioned
profile, golden digests and a payload-size scaling benchmark first.
