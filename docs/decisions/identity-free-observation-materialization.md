---
kind: decision
status: implemented
authority: cohesive.model.identity-free-observation-materialization
owners: [cohesive-core]
applies_to: [cohesive, cohesive-relations]
last_verified: 2026-08-25
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
- reads semantic fields by canonical identity rather than Relations layout ordinal;
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

## Compatibility and removal condition

`Cohesive.Relations.Mapping.ObservationObjectMapper` and its legacy missing-field policy remain available while the
indexed physical observation is still used by Relations consumers. They are a staged compatibility path, not a
second semantic authority. ARI-503 and ARI-504 must migrate those consumers and can remove or reduce the duplicated
physical-path mapping implementation once no supported caller requires it.
