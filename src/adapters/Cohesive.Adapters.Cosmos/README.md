# Cohesive.Adapters.Cosmos

`Cohesive.Adapters.Cosmos` provides Azure Cosmos DB interpretations for Cohesive entity storage, Relations,
materialization sources, domain-event inboxes, outbox records, and vector storage.

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
- A durable, target-deduplicating canonical domain-event inbox.
- Safe standalone Cosmos SQL construction.
- Outbox persistence and vector storage integrations.

## Important boundaries

Cosmos `JOIN` expands arrays within one document; it is not a cross-document join. Cross-container relationships use
bounded reads and local correlation when the physical plan can preserve the requested semantics.

Missing values, `null`, ordering, paging, aggregation, partition scope, and continuation behavior are represented
explicitly. Unsupported combinations fail with structured diagnostics rather than inheriting SDK coercions.

## Continue

- [Internals](INTERNALS.md) contains the domain-event inbox, full SQL builder, canonical compilation, semantic
  envelope, acquisition, materialization, query authority, and storage realization details.
- [Relations execution and adapters](../../Cohesive.Relations/docs/EXECUTION_AND_ADAPTERS.md) explains composed
  PostgreSQL/Cosmos reads.
- [Relations capability reference](../../Cohesive.Relations/docs/CAPABILITIES.md) records the generated profile.
- [`Cohesive.Storage`](../../Cohesive.Storage/README.md) owns the provider-neutral storage contracts.
