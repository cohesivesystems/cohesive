---
kind: decision
status: implemented
authority: cohesive.model.observation-identity-semantics
owners: [cohesive-core]
applies_to: [cohesive, cohesive-relations, cohesive-storage, cohesive-transitions, cohesive-processes, cohesive-identity]
last_verified: 2026-08-25
supersedes: []
---

# Keep entity snapshots distinct from relation occurrences

## Context

The legacy `Cohesive.Relations.Model.Observation` combines a shaped value, stable source identity, source version,
derived field lineage, and indexed physical layout. Downstream packages consequently use “observation” for several
states with different identity, lifecycle, and versioning rules.

The identity-free `Cohesive.Model.Observation` is now the authority for what was observed. Identity-bearing wrappers
must model who or which occurrence was observed without adding nullable identity or moving unrelated physical state
into the core value.

## Audit

| Existing concept | Actual invariant | Decision |
| --- | --- | --- |
| `Cohesive.Relations.Model.Observation` | Indexed physical shaped row with stable source identity, source version, and optional derived field lineage | Compatibility representation until ARI-503 adapts or renames it as a physical occurrence |
| `RelationQuerySourceReadObservation` | Stable source-row identity plus graph-qualified shape and partial field outcomes | Source-acquisition evidence; not a versioned entity snapshot |
| `RelationQueryObservationOccurrence` | Identity unique within one evaluation, binding participation, qualified shape, and optional stable source identity | Existing canonical relation-evaluation occurrence; no version belongs here |
| `Cohesive.Storage.EntitySnapshot` | Repository result containing a legacy row, partition, concurrency token, and loaded-field completeness | Storage interpretation that will compose the core entity snapshot during ARI-504 |
| `Transitions.Model.EntityState` | Identified and versioned entity state backed by the legacy Relations row | Semantic consumer to migrate to the core entity snapshot during ARI-504 |
| `Transitions.Authoring.EntitySnapshot<TEntity>` | Typed authoring view over `EntityState` | Ergonomic view, not another snapshot authority |
| Process node “occurrence” counters | Position in token or operation history | Unrelated execution identity; no observation abstraction applies |
| Identity directory temporary observations | Adapter rows created to invoke the Relations CLR mapper | Compatibility use only; no distinct Identity-domain observation semantics |

## Decision

Core defines `EntityObservationSnapshot` as the smallest entity-state identity wrapper. It contains exactly:

- a non-default `EntityId`;
- a non-negative entity-state version; and
- one validated, identity-free core `Observation`.

It does not contain storage partition, concurrency token, loaded-field completeness, relation binding, relation
occurrence identity, or lineage. Those facts have different owners and lifecycles.

No generic `IdentifiedObservation`, nullable identity union, or new core occurrence hierarchy is introduced.
`RelationQueryObservationOccurrence` already expresses evaluation-scoped occurrence identity and deliberately has no
version. One stable source row can participate through multiple bindings and therefore produce multiple occurrences.

Relations field lineage describes how a derived relation result was produced. It belongs to that derived physical
occurrence or its derivation evidence, not to the identity-free value or an entity snapshot. The lineage remains on
the legacy Relations observation only as a staged compatibility path until ARI-503 moves it with the physical
occurrence representation.

## Consequences and migration

- New entity-state APIs can require `EntityObservationSnapshot` without weakening identity or version invariants.
- Relation evaluation continues using its existing occurrence identity, which cannot be confused with `EntityId`.
- Storage concurrency and placement remain adapter concerns around the semantic snapshot.
- ARI-503 must adapt the indexed Relations representation and attach lineage to the derived occurrence it describes.
- ARI-504 must migrate Transitions, Storage, Identity, APIs, adapters, and fixtures, then remove the legacy
  identity/version authority from the Relations row.

The existing Relations, Storage, and Transitions APIs remain available until those dependency-ordered migrations.
They are compatibility paths with ARI-503 and ARI-504 as their concrete removal condition, not parallel semantic
authorities.
