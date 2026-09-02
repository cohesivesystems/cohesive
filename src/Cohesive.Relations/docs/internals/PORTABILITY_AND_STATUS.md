# Cohesive.Relations internals: portability and status

## Portable Documents

Canonical relationships are persisted independently in `relationship-catalog/v1`; relation and
query definitions reference them by stable `RelationshipId` from `relation-query/v1`. A catalog
can therefore serve many definitions and evolve as its own explicitly versioned semantic model.

The format provides:

- Closed relationship-target, relation, query, node, result, and paging discriminators.
- Stable semantic identifiers.
- Graph-qualified relationship and query shapes.
- Explicit forward and inverse relationship traversal.
- Binding-qualified field references.
- Strict JSON parsing.
- Structured semantic validation.
- Deterministic catalog and definition fingerprints.
- Host-language contract projection.

Document metadata and physical plans do not participate in the semantic definition fingerprint.

The current definition fingerprint profile is `relation-query/v1-c14n/v4`. Canonical query parameter
documents explicitly emit `defaultKind` (`None` or `Value`) so an absent fallback cannot collide with
an explicit null fallback. The v1 reader remains compatible with legacy parameters that omit the
discriminator: a concrete `defaultValue` implies `Value`, while an omitted or JSON-null value implies
`None`. Legacy JSON null was ambiguous and therefore cannot be recovered as an explicit null default;
producers that intend an explicit null must emit `defaultKind: "Value"`. Documents produced with the
prior fingerprint profile must be regenerated or migrated before validation under this profile.

The prototype ambient `relatedField(...)` expression has been removed. Related values must be introduced
through an explicit canonical relationship traversal that references the persisted relationship catalog;
expressions then read fields from the traversal's visible binding.

## Canonical Query Authority

`relation-query/v1` is the sole semantic model for relations and queries. Authoring surfaces lower into that
canonical IR; hosts invoke it through `RelationQueryEvaluation`; and interpreters, source readers, physical
planners, and attributable native artifacts realize it. Storage integrations, API endpoints, Identity, and
`Cohesive.Processes` consume those canonical contracts rather than maintaining parallel query models.

New query semantics must be introduced in the canonical IR, its capability profiles, and the adapters that
interpret it. Backend-specific construction utilities may remain independently useful without becoming another
semantic authority. For example, the standalone Cosmos SQL construction layer is an adapter utility whose
statements may be hand-authored or emitted from canonical relation/query compilation.

The former parallel query model and compatibility facade were removed as an intentional breaking change. There
is no automatic translation shim; consumers migrate by authoring canonical definitions and invoking the canonical
evaluation or target-compilation surfaces described above.

## Adjacent Execution Workstreams

Relations owns portable relational meaning, static requirements, lineage, dependency manifests,
realization decisions, physical-plan contracts, reference interpretation, and target compilation.

The Storage/materialization workstream will own durable projections and indexes: rebuild lifecycle, change
capture, affected-root resolution, checkpoints, target generations, writes, and convergence between rebuild
and real-time updates. The planned `Cohesive.Control` workstream will own reusable industrial execution policy such as
batching, parallelism, throttling, backpressure, retry, and cancellation. These workstreams consume attributable
Relations artifacts; their operational state and policies do not belong in canonical relation/query IR.

## Current Status

`Cohesive.Relations` is in early R&D. API stability is not yet a goal; validating the correct semantic model is.

The current foundation includes:

- A shared canonical relation/query IR.
- A portable, fingerprinted canonical evaluation document and one target-neutral host evaluator.
- Explicit value bindings and directional relationship traversal.
- Canonical relationship catalogs and deterministic relationship IDs.
- Standalone typed/semantic relationship authoring and entity-reference compilation.
- Versioned persisted relationship-catalog and relation/query documents.
- Structural and semantic diagnostics.
- Deterministic definition fingerprints.
- Demand-driven static compilation into input contracts, lineage, dependency manifests, and explicit logical pruning.
- Explicit demand-scoped execution slices containing canonical nodes, bindings, assignments, expression sites, and terminals.
- Capability-driven realization with explicit result observability and a target-neutral native-compilation handoff.
- Plan-attributed runtime evidence, causal requirement-gap analysis, and explicit missing-data policy decisions.
- A deterministic physical planner, bounded source-reader contracts, and canonical physical executor.
- Deterministic Storage source registration and an in-memory entity source reader for production-style canonical
  host evaluation.
- Affinity-validated Cosmos SDK artifact execution plus bounded Cosmos entity-source acquisition with explicit
  partition, cross-partition, selector, envelope, chunk, and buffering policy.
- Npgsql-backed PostgreSQL bounded enumeration, batched point/predicate acquisition, and rebuild/reconciliation
  materialization paging over the exact canonical storage binding.
- A canonical in-memory relation/query reference interpreter over supplied evidence.
- Same-element structured collection existentials with direct-field reference execution and exact Elasticsearch
  nested-query lowering when physical correlation evidence is available.
- Object/observation mapping and runtime-compiled DTO kernels for supported canonical relation terminals.
- Structural and typed expression-based C# authoring with deterministic CLR shape snapshots.
- Plan-bound typed and structural source-placement authoring with deterministic conventions, per-setting
  configuration provenance, and adapter-ready placed-input handles.
- Contract projection for other host languages.

Active areas of development include:

- Broader Cosmos SQL, Elasticsearch, and PostgreSQL compiler coverage; Gremlin is deferred.
- Broader nested collection traversal, additional scoped collection operators, and target lowering.
- Broader cross-source placement optimization, batching policies, and native/local join strategy selection.
- Backend differential and reference-interpreter conformance testing.
- JSON Schema generation and compatibility tooling.

## Installation

```bash
dotnet add package Cohesive.Relations
```

## Related Packages

- `Cohesive` provides the core shape, expression, observation, and type models.
- `Cohesive.Relations.Contracts` exposes relation/query contracts for canonical JSON wire projection and other code generation.
- `Cohesive.Storage` provides generic storage abstractions.
- `Cohesive.Transitions` defines entity transitions and invariants that can participate in relationship and dependency analysis.
- `Cohesive.Adapters.CSharp` projects canonical catalogs into deterministic, collision-checked relationship identifiers.
- `Cohesive.Adapters.Cosmos` provides Cosmos-oriented interpretations.
- `Cohesive.Adapters.Elastic` provides search-oriented interpretations.
- `Cohesive.Adapters.Postgres` provides provider-neutral PostgreSQL SQL compilation plus Npgsql-backed bounded
  Relations acquisition and rebuild/reconciliation materialization sources.
- `Cohesive.Adapters.TypeScript` projects semantic contracts into TypeScript.

## Direction

The long-term goal is for relational definitions to be authored once and interpreted across storage engines, application memory, APIs, search systems, frontend runtimes, and operational tooling without losing their semantic meaning.

A fast DTO mapper, a SQL query, a non-relational batch plan, a dependency manifest consumed by an
index synchronizer, and a missing-input explanation should be understood as interpretations or downstream
uses of the same relational program.
