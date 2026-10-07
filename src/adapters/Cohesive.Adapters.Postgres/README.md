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

## Reuse repository mappings for native queries

`PostgresEntityRepositoryMapping.For(domainEntity)` accepts a typed domain handle. Once a canonical
query is compiled and its inputs placed, `.Table(placedInput, repositoryMapping)` projects the existing
physical table and demanded columns into its native binding. No second column catalog is required.
Text columns use explicit C-collation equality; this projection does not assert ordinal ordering,
global identity uniqueness, foreign keys or partition isolation. Those remain explicit query/schema
obligations. The [fulfillment example](../../../eng/examples/aspire-first/README.md) demonstrates the full pipeline.

`PostgresQueryRowsReader` implements the shared `IRelationQueryRowsReader` complete-result contract and executes one compiled, unpaged native `QueryRows` branch against an explicitly
attested `PostgresNpgsqlRuntimeBinding`. Prepare it once; concurrent calls own separate commands,
parameter values and result budgets. The caller retains ownership of the data source. Presence markers
reconstruct outer-join absence independently of SQL null. Positive row and decoded-scalar-byte bounds
are mandatory; one overflow row detects truncation and fails the call rather than returning partial data.
Cancellation/provider failures propagate, no retries occur, and ambient transactions are rejected.
Native command timeout still bounds command duration; the result budget is not a database-work budget.

This reader deliberately does not expose a complete canonical evaluation outcome, supplied-root
execution, relation invariants, paging or temporal semantics. Such artifacts/values are rejected;
use the existing source-acquisition path with explicit policies where those contracts are required.

## Register a typed query at host composition

`PostgresQueryRegistration(runtime).Entity(domainEntity, mapping).Register(query, maximumRows, maximumBytes)`
places all demanded entity sources/traversals in the explicitly selected database, projects the registered
repository mappings and invokes the existing static/placement/feasibility/native compilers. It opens no
connection. Missing mappings and invalid plans fail at registration; static/native compilation failures
retain their result objects in `PostgresQueryPreparationException`, while placement/binding failures retain
the existing artifact-authoring diagnostics. This convenience covers entity-backed inputs on one database;
use the lower-level APIs for custom placement, acquisition or physical bindings.

Keep the returned `PostgresQueryReader<TInput,TResult>` for the host lifetime. HTTP code consumes its
`IRelationQueryReader<TInput,TResult>` contract and exact `Definition`, while native composition can inspect
its PostgreSQL artifact. `ReadAsync` performs no
compilation; it binds the input, reads bounded native rows and invokes the query's local typed result
projection. Its `Artifact` remains inspectable. The builder is registration-local and mutable, while readers
retain immutable prepared artifacts and require concurrency-safe projection callbacks. Native data-source
ownership remains with the caller. PostgreSQL configuration stays outside the semantic query declaration.
