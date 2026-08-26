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
| Former identity-bearing Relations row | Indexed physical shaped row with stable source identity, source version, and optional derived field lineage | Removed by ARI-504 after its responsibilities moved to explicit contracts |
| `RelationQuerySourceReadObservation` | Stable source-row identity plus graph-qualified shape and partial field outcomes | Source-acquisition evidence; not a versioned entity snapshot |
| `RelationQueryObservationOccurrence` | Identity unique within one evaluation, binding participation, qualified shape, and optional stable source identity | Existing canonical relation-evaluation occurrence; no version belongs here |
| `Cohesive.Storage.EntitySnapshot` | Repository result containing a core entity snapshot, partition, concurrency token, and loaded-field completeness | Storage interpretation around `EntityObservationSnapshot` |
| `Transitions.Model.EntityState` | Identified and versioned entity state backed by the core entity snapshot | Semantic wrapper around `EntityObservationSnapshot` plus Transition lineage |
| `Transitions.Authoring.EntitySnapshot<TEntity>` | Typed authoring view over `EntityState` | Ergonomic view, not another snapshot authority |
| Process node “occurrence” counters | Position in token or operation history | Unrelated execution identity; no observation abstraction applies |
| Identity directory query results | Canonical relation output rows materialized into Identity records | Core materializer consumer; no distinct Identity-domain observation semantics |

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
occurrence or its derivation evidence, not to the identity-free value or an entity snapshot.

## Consequences and migration

- New entity-state APIs can require `EntityObservationSnapshot` without weakening identity or version invariants.
- Relation evaluation continues using its existing occurrence identity, which cannot be confused with `EntityId`.
- Storage concurrency and placement remain adapter concerns around the semantic snapshot.
- ARI-503 introduced `IndexedObservationOccurrence`, which composes indexed storage and lineage with the existing
  relation occurrence and projects losslessly to core observations.
- ARI-504 migrated Transitions, Storage, Identity, APIs, adapters, fixtures, and supplied-root authoring and removed
  the legacy identity/version authority from Relations.
