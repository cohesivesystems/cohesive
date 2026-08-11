---
kind: decision
status: implemented
authority: cohesive.api.execution-package-boundary
owners: [cohesive-core]
applies_to: [cohesive-api, cohesive-execution-kernel]
last_verified: 2026-08-11
supersedes: []
---

# Separate execution-control API composition from generic API declarations

## Context

The execution-control catalog composes generic API declarations with canonical Process start, control, status,
explain, retained-trace, and Control-limit contracts. Keeping that catalog in `Cohesive.Api` made the generic API
package depend directly on the complete `Cohesive.Processes` language and runtime; its stale Storage reference also
acquired Processes transitively. Consumers that only declare APIs therefore acquired an unrelated Process
dependency.

The retained-trace operation made the mismatch explicit because its response and availability mapping are owned by
`Cohesive.Processes`. Hiding the dependency transitively or copying those contracts would obscure ownership rather
than repair it.

## Decision

Place the first-party execution-control catalog, safe execution API result projections, trusted invocation context,
and in-memory reference integration in a separate `Cohesive.Api.Execution` package.

The dependency graph is:

```text
Cohesive.Api.Execution -> Cohesive.Api
Cohesive.Api.Execution -> Cohesive.Processes
Cohesive.Api.Execution -> Cohesive.Storage
Cohesive.Api.Execution -> Cohesive
Cohesive.Adapters.AspNet -> Cohesive.Api.Execution
```

`Cohesive.Storage` remains the owner of the Control contracts composed by the catalog. `Cohesive.Api` retains only
generic semantic API declarations; its complete project-reference closure does not acquire `Cohesive.Processes`.
Execution-control public types use the `Cohesive.Api.Execution` namespace. Concrete route construction,
authorization-policy binding, and HTTP serialization remain in `Cohesive.Adapters.AspNet`.

This is an ownership and packaging change only. The execution-control semantic authority, v4 schema version,
operation ordering, endpoint identities, authorization requirements, result variants, semantic references, and
canonical artifact bytes do not change.

## Alternatives considered

### Keep execution control in Cohesive.Api

Rejected because it makes optional Process integration part of every generic API consumer's dependency graph.

### Name the package Cohesive.Processes.Api

Rejected because the package composes multiple authorities: generic API declarations, foundation execution and
Control contracts, and Process runtime artifacts. `Cohesive.Processes` owns Process meaning but does not own the
generic API integration boundary.

### Move execution control into Cohesive.Adapters.AspNet

Rejected because the catalog is transport-neutral and is also consumed by CLI, generated-client, in-memory, test,
and future host interpretations. ASP.NET owns only one concrete projection.

### Copy or wrap Process contracts in the API package

Rejected because that would introduce parallel identities, availability states, response models, and serialization
authority.

## Consequences

- Consumers of execution control must add `Cohesive.Api.Execution` and import its namespace.
- Consumers of only `Cohesive.Api` no longer acquire `Cohesive.Processes` through this integration.
- Adapter and test projects name their execution and Process dependencies directly.
- Future execution-control operations belong in the composition package when they bind existing semantic
  authorities; generic API mechanisms continue to belong in `Cohesive.Api`.
- The related ASP.NET extraction can move generic Minimal API projection without relocating the execution catalog
  again.

Assembly dependency tests guard the boundary, and existing catalog, code-generation, adapter, and canonical-byte
tests guard semantic compatibility.
