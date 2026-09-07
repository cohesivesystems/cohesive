# Cohesive.Adapters.Cosmos

`Cohesive.Adapters.Cosmos` provides Azure Cosmos DB interpretations for Cohesive entity storage, Relations,
materialization sources, Process Transition receipts, domain-event inboxes, outbox records, and vector storage.

## Install

```bash
dotnet add package Cohesive.Adapters.Cosmos
```

## Build a safe Cosmos query

The standalone builder validates property paths and operators, creates deterministic parameters, and never accepts
raw SQL fragments:

```csharp
var id = CosmosSqlExpression.Property("c", FieldPath.FromField("Id"));
var status = CosmosSqlExpression.Property("c", FieldPath.FromField("Status"));

var template = new CosmosSqlBuilder("c")
    .Select(id, "id")
    .Select(status, "status")
    .Where(CosmosSqlExpression.Binary(
        CosmosSqlBinaryOperator.Equal,
        status,
        CosmosSqlExpression.RuntimeParameter("status")))
    .OrderBy(id)
    .OffsetLimit(offset: 0, limit: 100)
    .BuildTemplate();

var statement = template.Bind(new Dictionary<string, object?>
{
    ["status"] = "open"
});
```

Use the canonical compiler when the query must retain Relation semantics, plan affinity, capability evidence, and
provenance. Placement and the Cosmos storage binding remain explicit persisted interpretations of that plan.

## Implemented interpretations

- Parameterized Cosmos SQL compilation for the supported canonical Relation/query slice.
- Bounded Cosmos SDK source acquisition and materialization change sources.
- Entity repository and embedded aggregate storage realization.
- Atomic Process Transition execution with exact partition-local replay receipts.
- A durable, target-deduplicating canonical domain-event inbox.
- Safe standalone Cosmos SQL construction.
- Outbox persistence and vector storage integrations.

## Important boundaries

Cosmos `JOIN` expands arrays within one document; it is not a cross-document join. Cross-container relationships use
bounded reads and local correlation when the physical plan can preserve the requested semantics.

Process Transition receipt lookup requires exact point-read partition placement. The adapter fails with structured
capability evidence when that placement cannot be resolved and never substitutes a cross-partition scan for atomic
subject authority.

Missing values, `null`, ordering, paging, aggregation, partition scope, and continuation behavior are represented
explicitly. Unsupported combinations fail with structured diagnostics rather than inheriting SDK coercions.

## Continue

- [Internals](INTERNALS.md) contains Process Transition receipts, the domain-event inbox, full SQL builder, canonical
  compilation, semantic envelope, acquisition, materialization, query authority, and storage realization details.
- [Relations execution and adapters](../../Cohesive.Relations/docs/EXECUTION_AND_ADAPTERS.md) explains composed
  PostgreSQL/Cosmos reads.
- [Relations capability reference](../../Cohesive.Relations/docs/CAPABILITIES.md) records the generated profile.
- [`Cohesive.Storage`](../../Cohesive.Storage/README.md) owns the provider-neutral storage contracts.

## Experimental atomic storage commits

Create `CosmosStorageCommitExecutor` with `CreateAsync(client, databaseId, containerId, target)`.
It verifies a single observed writable region, `/partitionKey` container partitioning and disabled
TTL, then realizes conditional writes plus a receipt in one transactional batch. Other targets or
partitions are rejected; native limits include the receipt. The conservative serialized document
budget is 1 MiB. It owns a dedicated document namespace and uses a lossless stream codec independent
of the client's serializer. Recreate the executor after topology or consistency changes.

Query-dependent commits require the explicit all-writers guard protocol and a Strong account/read
profile. The local vNext emulator's Eventual profile cannot qualify these reads; it still exercises
native batch, ETag and exact receipt behavior. Run those integration checks by setting
`COSMOS_STORAGE_COMMIT_CONNECTION_STRING` and filtering `CosmosStorageCommitTests`.
See the [commit decision and deferred adoption work](../../../docs/decisions/declarative-storage-commits.md).

For commit profiles below Strong, an item conflict or token mismatch followed by an invisible receipt
returns `Unknown`, including Session profiles after restart. Reconcile the exact intent when visibility
catches up; do not treat this as a definitive precondition failure. `executor.Validate(intent)` measures
native documents and the receipt before any I/O, using the same encoding and byte budget as execution.
