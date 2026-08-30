---
kind: decision
status: implemented
authority: cohesive.relations.indexed-observation-physical-interpretation
owners: [cohesive-relations]
applies_to: [cohesive, cohesive-relations]
last_verified: 2026-08-29
supersedes: []
---

# Interpret indexed observations as relation occurrences

## Context

`Cohesive.Relations.Model.Observation` historically combined five concerns: semantic field values, a local shape id,
stable source identity, source version, derived-field lineage, and an ordinal physical buffer. Core now owns the
canonical, identity-free `Cohesive.Model.Observation`, and the preceding identity decision established that relation
participation is an evaluation-scoped occurrence rather than an entity snapshot.

Relations still needs ordinal layouts, dense value slots, packed presence bits, and lineage for allocation-sensitive
execution. Moving those structures into core would make one physical strategy part of the semantic contract. Keeping
the legacy row as the authority would instead preserve ambiguous local shape identity and duplicate validation and CLR
conversion.

## Decision

`Cohesive.Relations.Physical.IndexedObservationOccurrence` is the explicit physical interpretation. It composes the
existing `RelationQueryObservationOccurrence` with an `ObservationLayout`, `ObservationBuffer`, and
`ObservationLineage`. Its occurrence supplies exact `QualifiedShapeId`, evaluation-local occurrence identity, binding,
and optional stable source identity. It adds no entity identity or source version.

Construction follows two boundaries:

- `FromObservation` interprets an already validated core observation, snapshots layout and lineage, and requires exact
  qualified-shape agreement. A caller that supplies an explicit layout must also supply the graph so every layout field
  can be proven to belong to the shape.
- `Create` accepts ordinal buffers with exact `GraphShapeId`, validates layout identities, buffer lengths, out-of-range
  presence bits, and all present field values through core `ObservationValidator`, then snapshots mutable inputs.
- `CreateBuilder` is the single-owner ingestion boundary for adapters and database readers. It allocates the ordinal
  storage, accepts fields by identity or ordinal, validates through the same core authority on `Build`, and transfers
  its buffers without copying. A successful build consumes the builder; a semantic validation failure leaves it
  available for completion and retry.

`ToObservation` reconstructs and validates the complete semantic value against the exact supplied graph. Projection is
lossless for qualified shape identity, present versus absent fields, nulls, nested values, numbers, and every portable
`ObservationValue` kind. Occurrence identity and lineage intentionally remain outside the projected semantic value.

Core exposes `IObservationFieldReader` as a narrow execution boundary. `ObservationMaterializer<T>` compiles against
that reader, checks exact qualified shape identity, and can therefore read either a core observation or the indexed
Relations occurrence without reconstructing a dictionary. The interface is not another semantic authority: physical
implementations must be validated at their construction boundary, and consumers requiring validation or portable JSON
must project to core `Observation`.

ARI-504 removed the legacy local-shape row and mapper. Supplied roots now retain stable source identity beside exact
qualified shape and portable fields; evaluation validates those fields against the persisted compilation graph before
execution. Entity versions remain on `EntityObservationSnapshot` and are not copied into relation roots.

## Invariants

- Core `Observation` and `ObservationValue` are the sole semantic value and validation authorities.
- A physical occurrence always carries the exact qualified shape from `RelationQueryObservationOccurrence`.
- Layout and buffer objects never enter Cohesive core contracts.
- Public raw buffer storage is not exposed. Physical factories snapshot caller-owned lists and buffers before
  returning; the builder may transfer only storage that it exclusively allocated and consumes.
- Layouts of at most 64 fields retain presence in the occurrence itself. Larger layouts allocate the exact required
  external presence-word array.
- Presence bits cannot address ordinals outside the layout.
- Lineage belongs to the derived occurrence and is not projected into an identity-free observation or entity snapshot.
- A compiled CLR materializer rejects a reader governed by another qualified shape, even when local shape ids match.

## Performance and verification

The indexed path retains direct ordinal reads, dense buffers, and packed presence masks. The warm DTO benchmark
compares the shared core compiler over semantic observations and the indexed reader adapter, while
`IndexedObservationOccurrenceTests` cover direct materialization and ordinal access. Relations semantic, executor,
hydration, query, mapping, portable serialization, and benchmark-project builds remain regression gates.

A focused Apple M5 Max / .NET 10 ShortRun over sixteen flat `Int64` fields measured the compact storage and ownership
boundaries as follows. The owned builder is the appropriate end-to-end adapter comparison: it avoids the caller's
fresh value and presence buffers being copied into a second retained set. `Create` remains slightly faster when its
caller already has reusable populated buffers, while preserving its defensive-copy contract.

| Physical ingestion operation | Mean | Allocated |
| --- | ---: | ---: |
| Snapshot already-populated caller buffers | 172.8 ns | 608 B |
| Populate fresh caller buffers, then snapshot | 198.8 ns | 1,176 B |
| Populate and transfer through owned builder | 184.6 ns | 688 B |
| Shape-bound JSON hydration into indexed storage | 677.0 ns | 608 B |

The builder reduced end-to-end ordinal-ingestion allocation by 41.5% and elapsed time by 7.1% versus producing fresh
caller buffers and then snapshotting them. Inline presence removed the separate heap bitmap for the common ≤64-field
case; the remaining 608 B JSON allocation is the retained sixteen-value array and occurrence. These figures are a
representative regression signal rather than a release performance guarantee.

An informational short run on the same Apple M5 Max / .NET 10 environment as the checked-in baseline measured the
new `SharedCoreIndexedSimple` path as follows. Allocations were unchanged from the legacy indexed compiler; the shared
core plan added 2.1–8.1% elapsed time, with the percentage falling as row count increased.

| Rows | Legacy indexed baseline | Shared core indexed | Elapsed delta | Allocation |
| ---: | ---: | ---: | ---: | ---: |
| 1 | 264.42 ns | 285.74 ns | +8.1% | 1,640 B, unchanged |
| 32 | 9.43 μs | 9.70 μs | +2.9% | 51,928 B, unchanged |
| 1,024 | 319.22 μs | 325.92 μs | +2.1% | 1,660,952 B, unchanged |

These figures are a representative regression signal rather than a release performance guarantee. The benchmark is
retained so later materializer or indexed-reader changes can compare the same semantic fixture.

## Consequences

Relations has a named physical occurrence without making layout an application-domain concept. New code can move
between validated semantic values and indexed execution explicitly, and one compiled CLR plan governs both paths.
Storage, Transitions, Identity, adapters, APIs, fixtures, and supplied-root authoring now use their explicit core
snapshot, acquisition, or occurrence contracts.
