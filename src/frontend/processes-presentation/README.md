# @cohesivesystems/processes-presentation

Pure, renderer-neutral projection of one exact canonical Cohesive Process document into an operator-facing semantic
graph.

## Authority Boundary

`ExecutionDefinitionDocument`, `ProcessDefinition`, the generated closed unions, and
`canonicalProcessNodeKinds` from `@cohesivesystems/processes` remain authoritative. This package owns only a derived
view model, an explicit compatibility declaration, and a total executable disposition ledger. It does not compile,
validate, execute, persist, or independently serialize Process semantics.

The projector copies and freezes retained wire evidence. Canonical construct identities—not array positions or
layout coordinates—form semantic graph keys. ReactFlow nodes, layout, viewport state, DOM selectors, navigation,
styling, and product policy belong to downstream consumers such as Ari.

## Runtime Evidence Overlay

`projectCanonicalProcessRuntime` joins an existing `ProcessPresentationGraph` with one exact canonical
`ExecutionStatus` and an optional `ProcessExecutionTraceArtifact`. The generated contracts in
`@cohesivesystems/processes` remain authoritative. The overlay is an immutable, renderer-neutral index over that
evidence; it does not introduce a second status, trace, lifecycle, or lineage model.

The join fails closed when graph projection version, status/trace schema, definition identity and fingerprint,
Process instance, or attempt lineage are incompatible. Compatible evidence is handled totally:

- tokens and waits attach only to exact canonical Process-node identities;
- trace events attach to their exact node and, when disclosed, exact owned branch/clause, Request outcome, and
  definition-reference elements;
- child, partition, and recurrence evidence remains on the canonical trace event, including exact continuation and
  payload-safe occurrence identities supplied by the runtime;
- terminal outcome, active activation, progress, demand, capacity, health, and extensions remain global when their
  canonical contracts do not name a graph element;
- unknown, redacted, unavailable, unsupported, retained-prefix loss, and every unmatched reference produce stable
  structured diagnostics. Missing evidence never implies completion.

Overlay and evidence identities use semantic Process, attempt, activation, token, and canonical element identities;
they do not depend on graph layout or renderer state. Inputs are copied and deeply frozen, output arrays use ordinal
ordering, and equal inputs produce deeply equal output.

## Compatibility and Failure Behavior

`canonicalProcessPresentationCompatibility` declares the exact Process definition kind and execution-document
schema understood by this projector version. A mismatched kind or schema yields structured diagnostics and no
partial interpretation.

The executable disposition ledger is checked against the generated runtime construct inventory on every projection.
Unknown future constructs remain visible as placeholder nodes and produce errors. Missing edges, targets, identities,
and exact definition references also produce stable diagnostics rather than disappearing.

Selected node details retain frozen source values, exact child/contract definition references, portable value
contracts, canonical source paths, and the deepest matching source-map evidence. The graph is a deterministic derived
artifact and should be regenerated from its exact source document rather than stored as another authority.
