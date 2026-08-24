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
