# Cohesive.Storage

`Cohesive.Storage` defines provider-neutral durability, source acquisition, aggregate storage realization, and
relation-derived materialization contracts for Cohesive systems.

## Install

```bash
dotnet add package Cohesive.Storage
```

## Register a Relation source

Storage can attach an entity repository to the canonical Relations acquisition port. Shape, source identity, limits,
and version projection are derived or declared once:

<!-- docs-sync:storage-relation-source:start -->
```csharp
var source = EntityRelationQuerySourceRegistration.InMemory(
    loadShape,
    repository,
    logicalPartition: RelationQueryLogicalPartitionIdentity.WholeSource,
    observationVersionSemanticPath: FieldPath.FromField("SourceEntityVersion"),
    limits: new(
        maximumBatchSize: 100,
        maximumBufferedRows: 10_000,
        maximumFanOut: 100,
        maximumConcurrency: 4));

var catalog = new EntityRelationQuerySourceCatalog([source]);
IRelationQueryEvaluator evaluator = catalog.CreateEvaluator(physicalPlanningPolicy);
var outcome = await evaluator.EvaluateAsync(evaluation, cancellationToken);
```
<!-- docs-sync:storage-relation-source:end -->

Relations remains authoritative for filters, joins, projections, aggregation, and paging. Storage contributes bounded
physical acquisition and exact source evidence.

## What this package owns

- Entity repository and observation-stream ports.
- The atomic durable Process aggregate, store contract, and provider-neutral reference runtime.
- Canonical Relation source registration and evaluator composition.
- Aggregate storage structures and target realization documents.
- Relation-derived materialization definitions, impact planning, rebuilds, and incremental synchronization.
- Generation allocation, sealing, validation, promotion, cleanup, routing, and progress evidence.
- Query-authority and lifecycle-control contracts used by storage adapters.

## Important boundaries

`Cohesive.Storage` does not define another query language, Transition model, or Process model. It consumes exact
semantic documents and compiled evidence from those owning blocks. Provider SDKs and physical schemas remain in
adapter packages.

`InMemoryProcessDurableStore` is the copy-on-write reference implementation and semantic test oracle. It is not a
production durability provider and does not claim physical exactly-once publication.

Materialization uses the Relations dependency manifest and lineage rather than copying their edges into a second
model. When an incremental route cannot be proven within configured limits, planning fails closed or requires an
explicit rebuild policy.

## Continue

- [Internals](INTERNALS.md) covers the durable Process aggregate, source contracts, storage realizations,
  materialization lifecycle, routing, and query authority in detail.
- [Index synchronization runbook](../../docs/INDEX_SYNC_RUNBOOK.md) covers the operational path.
- [`Cohesive.Relations`](../Cohesive.Relations/README.md) owns relation/query semantics and dependency evidence.
- [`Cohesive.Adapters.Postgres`](../adapters/Cohesive.Adapters.Postgres/README.md),
  [`Cohesive.Adapters.Cosmos`](../adapters/Cohesive.Adapters.Cosmos/README.md), and
  [`Cohesive.Adapters.Elastic`](../adapters/Cohesive.Adapters.Elastic/README.md) provide physical interpretations.
