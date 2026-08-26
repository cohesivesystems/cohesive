---
kind: decision
status: implemented
authority: cohesive.observation.consumer-boundaries
owners: [cohesive-core, cohesive-relations, cohesive-storage, cohesive-transitions]
applies_to: [cohesive, cohesive-relations, cohesive-storage, cohesive-transitions, cohesive-identity]
last_verified: 2026-08-25
supersedes: []
---

# Migrate observation consumers to explicit semantic and identity boundaries

## Decision

All supported consumers use one identity-free semantic value and add identity, version, storage, or execution facts
only at the boundary that owns them.

| Meaning | Authority |
| --- | --- |
| Qualified semantic shape and complete field value | `Cohesive.Model.Observation` |
| CLR materialization policy | immutable `ObservationMaterializer<T>` |
| Entity identity and entity-state version | `EntityObservationSnapshot` |
| Transition state plus Transition derivation lineage | `EntityState` |
| Partition, concurrency token, and selected-field metadata | `Cohesive.Storage.EntitySnapshot` |
| Relation occurrence identity, indexed layout, and derived-field lineage | `IndexedObservationOccurrence` |
| Stable source-row identity and partial requested-field outcomes | Relations acquisition contracts |
| Portable directly supplied root identity and values | `RelationQuerySuppliedRoot` validated through the evaluation's exact graph |

Repository field selection is physical access metadata. A repository still returns a complete, validated semantic
observation; `LoadedFields` reports what the requested physical selection was. It does not create a semantically
invalid partial core observation. Relations source readers may project partial or inconclusive requested-field
evidence because acquisition outcomes have different completeness semantics.

Inline entity shapes receive a deterministic graph identity derived from canonical shape bytes. Graph-backed entity
definitions retain their declared graph revision. Entity repositories and Transition state creation therefore compare
exact `QualifiedShapeId` values rather than local shape names.

Directly supplied roots do not inherit entity versions or relation lineage. CLR roots are first projected to portable
object values and validated against the exact root graph; core observations and entity snapshots have overloads that
preserve the same boundary explicitly.

## Cohesive fit

The core observation, materializer, entity snapshot, Relations occurrence, and source-evidence contracts already own
the required reusable semantics. No Ari-specific or parallel model is needed. The migration removes the mutable
Relations mapping context and identity-bearing row rather than wrapping them.

## Verification

Core tests cover deterministic inline shape identity, exact graph mismatch, materialization, and invalid values.
Relations tests cover supplied-root portability, graph validation, fingerprints, acquisition projection, and indexed
occurrences. Storage, adapter, API, Identity, and Transition tests cover complete snapshots, physical selection
metadata, concurrency, partitioning, outbox commits, creation, and replay.
