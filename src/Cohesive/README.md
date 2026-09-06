# Cohesive

`Cohesive` contains the portable values, shape model, expression IR, execution contracts, and provenance primitives
shared by every Cohesive block and adapter.

## Install

```bash
dotnet add package Cohesive
```

## Start with a shape

Ordinary CLR types can produce a deterministic shape graph without hand-authoring fields or node identities:

<!-- docs-sync:core-shape:start -->
```csharp
using Cohesive.Model;

[ShapeDefinition("shape.shipment", ShapeRoles.Transport)]
public sealed record Shipment(string Id, IReadOnlyList<Stop> Stops);

[ShapeType("type.stop")]
public sealed record Stop(string City, string State);

var graph = new ClrShapeGraphBuilder()
    .AddShape<Shipment>()
    .Build(new("shipping"));
```
<!-- docs-sync:core-shape:end -->

The graph records the semantic types, fields, cardinalities, roles, and CLR provenance used by higher-level blocks.
It can be persisted, validated, generated into another host language, or interpreted by a target adapter.

## What this package provides

- Graph-qualified shapes, fields, paths, scalar types, cardinality, and nullability.
- Immutable `ObservationValue` and `Observation` values with exact shape evidence.
- Explicit `EntityObservationSnapshot` values when identity and version apply.
- Portable `Expr` definitions and expression-site analysis shared by compilers and interpreters.
- Canonical execution-definition, interaction, control, trace, explain, provenance, and compatibility contracts.
- Common typed quantities, identifiers, codes, paths, diagnostics, and deterministic serialization helpers.

The package does not define Relations, Transitions, Processes, APIs, presentation, storage, or provider behavior.
Those blocks depend on these shared semantic contracts.

## Observations and snapshots

An `Observation` describes an immutable shaped value. Entity identity and version remain explicit rather than being
silently attached to every value:

```csharp
Shipment shipment = observation.Materialize<Shipment>();

var snapshot = new EntityObservationSnapshot(
    new EntityId("shipment-42"),
    version: 3,
    observation);
```

Physical layouts, source placement, relation occurrences, and storage concurrency tokens belong to the interpreting
block or adapter.

## Go deeper

- [Core internals](INTERNALS.md) covers observations, portable JSON values, execution catalogs, interactions,
  durable requests, Process control, and expression analysis.
- [System-wide documentation](../../docs/index.md) explains how the blocks fit together.
- [Semantic model](../../docs/concepts/semantic-model.md) introduces the shared vocabulary.
- [Observation identity decision](../../docs/decisions/observation-identity-snapshot-and-occurrence-semantics.md)
  records the ownership boundary between values, snapshots, and occurrences.

Related application blocks include
[`Cohesive.Relations`](../Cohesive.Relations/README.md),
[`Cohesive.Transitions`](../Cohesive.Transitions/README.md), and
[`Cohesive.Processes`](../Cohesive.Processes/README.md).

### Ordinal observation construction

`Observation.Create(shape, layout, immutableValues)` retains an `ImmutableArray<ObservationValue>` in a shared
`ObservationLayout`; the span overload snapshots caller-owned values. Layouts belong to the exact graph instance and
shape from which they were compiled. Each slot is one canonical field: `Undefined` means absent, while `Null` remains
present. Full shape validation still runs. Name-based `Fields` is an immutable view over the same vector, and canonical
serialization, equality, and fingerprints are independent of physical field order. The field view also implements
`IOrdinalObservationFieldReader`, allowing a materializer compiled against that layout to read directly by ordinal.

Default CLR materialization converts native byte observations directly to an independently owned `byte[]`, including
inside conventional nested records and arrays. This copies mutable output once; it does not route bytes through JSON
text. Explicit serializer customizations retain their chosen conversion contract. Durable detached observation values
can opt into `PortableValueJsonConverter.TaggedObservationValues`, which reuses the PortableValue node encoding to
preserve byte, temporal, numeric, and undefined kinds. Opting in changes the wire format and requires a versioned profile;
ordinary observation JSON remains unchanged. Entity receipts use the explicit `EntityStorageJson` profile in Storage.
