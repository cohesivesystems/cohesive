# Cohesive.Adapters.Postgres

`Cohesive.Adapters.Postgres` realizes Cohesive Relations, Storage, materialization, Process durability, and logical
replication semantics on PostgreSQL through explicit bindings and Npgsql-backed runtime ports.

## Install

```bash
dotnet add package Cohesive.Adapters.Postgres
```

## Start with safe SQL construction

The standalone builder quotes identifiers and parameterizes values. It is useful on its own and is also shared by
the canonical compiler:

```csharp
var template = new SqlSelectBuilder(
        new SqlQualifiedTable("transport", "loads"),
        "l")
    .Select(SqlExpression.Column("l", "id"), "id")
    .Where(SqlExpression.Binary(
        SqlBinaryOperator.Equal,
        SqlExpression.Column("l", "status"),
        SqlExpression.RuntimeParameter("status")))
    .OrderBy(SqlExpression.Column("l", "id"))
    .Limit(100)
    .BuildTemplate(PostgresSqlDialect.Instance);

var statement = template.Bind(PostgresSqlDialect.Instance, new Dictionary<string, object?>
{
    ["status"] = "Open"
});
```

For canonical Relations, author the relation first, compile its exact demand, place acquired inputs in a PostgreSQL
execution domain, then bind semantic fields to tables and columns. Semantic-path conventions handle ordinary names;
explicit mappings and evidence are required where the physical model differs.

## Implemented interpretations

- Parameterized native SQL compilation for the declared PostgreSQL target profile.
- Npgsql source acquisition for bounded enumeration, identity batches, and predicate batches.
- Exact Relation storage-binding authoring and capability evidence.
- Entity repositories and aggregate storage realization.
- Durable Process aggregate persistence and distribution ledgers.
- Materialization state, generation routing, rebuild, and reconciliation sources.
- Logical replication with exact slot, baseline, checkpoint, and settlement evidence.

## Current boundary

The adapter preserves only semantics proven by its target profiles and binding evidence. Text equality and ordering,
temporal intervals, isolation, pagination, and change-feed assumptions are distinct claims. Missing evidence produces
structured diagnostics before native compilation or execution.

The standalone SQL builder expresses PostgreSQL behavior; using it does not manufacture a canonical Relation plan or
prove equivalence with one.

## Continue

- [Internals](INTERNALS.md) contains Process storage, materialization, SQL construction, exact bindings, acquisition,
  logical replication, end-to-end compilation, and repository details.
- [Relations execution and adapters](../../Cohesive.Relations/docs/EXECUTION_AND_ADAPTERS.md) compares native joins
  with composed reads.
- [Relations capability reference](../../Cohesive.Relations/docs/CAPABILITIES.md) is generated from the target
  profiles.
- [`Cohesive.Storage`](../../Cohesive.Storage/README.md) owns the provider-neutral storage semantics.

## POCO-first entity mappings

Use the canonical entity definition as the field authority and attach physical names with selectors:

```csharp
var mapping = PostgresEntityRepositoryMapping.For<Order>(entity)
    .Table("public", "cohesive_orders")
    .Identity(order => order.Id, "order_id")
    .Partition(order => order.Partition, "partition_key")
    .Build();
```

`Column` maps additional fields. Selectors follow the shared `FieldPath` conventions, including
`JsonPropertyName`; nested paths and computed expressions are rejected. Scalar encodings come from
the supplied canonical definition, not a second CLR type catalog. Identity and partition must be
required non-null text. `Build` reuses repository validation for complete coverage, required fields,
scalar compatibility and key semantics; the mapping constructor rejects duplicate fields/columns.
An immutable mapping snapshot is independent of subsequent builder edits. The builder is invocation-local
and not thread-safe. Custom version columns and batch bounds are optional `Build` arguments.
The existing constructor remains available; neither API owns schema creation or migration.
