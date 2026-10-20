---
kind: decision
status: implemented
authority: cohesive.model.identity-free-observation-materialization
owners: [cohesive-core]
applies_to: [cohesive, cohesive-relations]
last_verified: 2026-10-20
supersedes: []
---

# Materialize identity-free observations without physical layout authority

## Context

`Cohesive.Model.Observation` is the semantic authority for an identity-free value governed by one exact,
graph-qualified shape. Cohesive also needs an efficient CLR interpretation for application code, including mutable
POCO property binding, immutable-record constructor binding, explicit semantic field mappings, converters, and
configurable missing-field behavior.

The existing `Cohesive.Relations.Mapping.ObservationObjectMapper` interprets the indexed physical Relations
observation. Its plan identity depends on `ObservationLayout` ordinals and its defaults come from a mutable
`ShapeMappingContext`. Moving that implementation unchanged would make physical layout and ambient configuration
part of core semantic materialization.

## Decision

Core owns an immutable, one-way `ObservationMaterializer<T>` compiled by
`ObservationMaterializerBuilder<T>`. A compiled materializer:

- accepts exactly one `QualifiedShapeId` and rejects observations from another graph or shape;
- binds semantic fields by canonical identity, optionally lowering their reads to prebound ordinals for an explicit shared layout;
- fixes field mappings, converters, missing-field behavior, and a cloned read-only serializer contract at compile
  time;
- compiles constructor and property binding into one reusable delegate; and
- uses reflection metadata cached centrally by CLR type.

`Observation.Materialize<T>()` uses a deterministic default plan. That plan is cached by CLR type and complete
`QualifiedShapeId`; the default mapping and conversion policy is fixed, so no additional mutable policy key is
required. Explicitly configured materializers are reusable immutable plan instances rather than entries in a
process-wide cache.

When CLR naming metadata contributes semantic field identities, the caller supplies the immutable
`ClrShapeGraphBuildResult` that authored the graph. The builder verifies that the result contains `T` as the exact
root shape in the exact graph, then resolves implicit mappings through `ResolveMemberPath`. The build result is the
single authority for the effective provider composition; materialization does not rerun providers or implement a
parallel metadata precedence algorithm.

## Alternatives considered

- **Reuse `ShapeMappingContext`.** Rejected because it is mutable ambient configuration and keys the current mapper
  through a physical Relations layout.
- **Infer JSON or attribute names again during materialization.** Rejected because this would duplicate
  `ClrShapeGraphBuilder` provider precedence and could disagree with the graph's authored field identities.
- **Cache by local `ShapeId` or layout.** Rejected because the same local shape id can have different meaning across
  graph revisions, and layout is not part of an identity-free observation.
- **Replace the Relations mapper immediately.** Rejected for this increment because indexed path migration requires
  the identity-bearing snapshot and boundary work tracked by ARI-502 through ARI-504.

## Compatibility outcome

ARI-504 migrated Relations, Transitions, Storage, Identity, adapters, APIs, fixtures, and benchmarks to the core
materializer. The mutable `ShapeMappingContext`, legacy observation/object mappers, and their separate missing-field
policy were removed. Indexed execution implements `IObservationFieldReader`, so both semantic and indexed reads use
one compiled materialization contract.

## Strict shape-aware JSON interpretation

A plan compiled with `Compile(layout)` can ingest one complete plain JSON record through
`MaterializeJson(utf8Json)`. The exact bound layout supplies its graph-qualified shape and compiled field
ordinals. Core's existing JSON reader restores shaped values and validates the whole record before the compiled
CLR projection executes, including unmapped fields. Root names are case-sensitive, unknown and decoded duplicate
root names are rejected, and trailing non-whitespace content is rejected. This explicit boundary does not alter
general JSON conversion or already-validated-reader materialization.

The operation retains the existing converter, constructor-default and missing-member policies. It borrows input
bytes for the call, owns the parsed values, and returns independent default mutable CLR projections. A name-only
plan must be recompiled with an explicit layout before using strict JSON ingestion; no schema is inferred from
payloads and no global tenant-name cache is added.

See [the adoption evidence](../performance/observation-values-layout-adoption.md) for measurements, the public API
example and the limits of the performance claims.
