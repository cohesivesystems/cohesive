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

`PostgresPersistenceRegistration(runtime).Entity(domainEntity, mapping).Query(query, maximumRows, maximumBytes)`
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

The persistence registration is shared: `.Repository(entity)` uses the same attached entity definition,
mapping and runtime as `.Query(...)`. Query dependencies are resolved from the compiled input contract;
unconsumed attachments do not create SQL sources. Missing/duplicate attachments fail before IO. Configure
the mutable registration on one thread during host setup, then retain its prepared readers/repositories;
there is no global result cache or inferred tenant scope. The earlier `PostgresQueryRegistration.Register`
name is replaced by `PostgresPersistenceRegistration.Query` in this unreleased surface.

## Prepared native and composed queries

`PostgresPersistenceRegistration` attaches canonical entities to native repository mappings once.
`Query` prepares a native typed reader. `QueryComposed(query, projection, remote, policy)` prepares a
closed native prefix and the remaining query over one remote PostgreSQL registration, reusing those
same attachments. The policy declares acquisition bounds and a partition scope once. Remaining source
and traversal mappings are selected from the remote registration; missing mappings or mismatched partition
selectors fail during preparation. The native prefix must explicitly enforce the same authorized scope.
This initial recipe uses sequential bounded acquisition, fails on overflow, and makes no distributed-snapshot
claim. It opens no connection during preparation; caller-owned data sources must outlive the readers.

Storage-binding schema v4 includes `SourceScope` in its fingerprint and convention-derived identity.
Null means the full placement, while `ForSource` means exactly the declared source. Reader and compiler
admission check that declared scope; table coverage cannot silently narrow it. Persisted v3 bindings are
rejected and must be regenerated from their declarations. Semantic/native preparation errors retain their
compiler results; cut/physical errors retain `RelationQueryPreparationException` evidence.

`Repository(entity)` preserves the `DomainEntity<T>` type as `IEntityRepository<T>`, using the existing
`TypedEntityRepository<T>` over the native repository. Canonical writes, batching and concurrency fences
continue to delegate unchanged. At registration, the default typed-write identity selector is compiled from
`mapping.IdentityField` using the same `FieldPath.Capture` member naming rules as mapping authoring (including
`JsonPropertyName`). An absent or ambiguous readable property fails before request execution;
there is no Id/Key fallback for a declared field, and warm identity extraction performs no reflection
(see the scoped allocation test in the Storage README). Explicit `selectEntityId` remains an escape for custom
CLR mappings. Semantic versions retain existing Version/zero conventions, or an explicit `selectVersion`.
The registration remains PostgreSQL-specific; application consumers depend on typed repository/read interfaces.


This shared CLR identity conversion does not broaden native key encodings: the PostgreSQL repository
still requires required, non-null TEXT identity and partition mappings. Inferred UUID/native non-text
keys remain rejected by mapping validation, as before this change.

For composed execution, construct `PostgresRelationQueryComposedPolicy(physicalPlanningPolicy, ...)`.
It retains that required planning policy and derives `SourcePolicy.MaximumBatchKeys` from
`MaximumBatchSize`; there is no second batch-size input. `QueryComposed` accepts only this composed
policy type. Standalone readers retain `PostgresRelationQuerySourcePolicy` and its integer batch bound.
The composed policy rejects a null partition scope at construction. Missing remote mappings or
mismatched partition scopes raise `RelationQueryPreparationException`
with a `postgres.composed.*` code and semantic compilation evidence. Identity caching and allocation
boundaries are documented once in the [Storage README](../../Cohesive.Storage/README.md).

## Atomic entity process receipts

Opt in with `PostgresTransitionReceiptOptions(schema, table, partitionKey)` on the repository or
`persistence.Repository(entity, transitionReceipts: storage)`. Execute `options.SchemaSql` explicitly in your
schema lifecycle, then obtain `storage = await options.BindAsync(dataSource)`. The repository requires this
validated handle and rejects a handle from another data-source instance; unchecked options cannot be passed.
The check rejects incompatible columns or missing immediate primary/creation uniqueness fences without
migrating the table. PostgreSQL 14 and 17 are exercised, including invalid-schema rejection; the optional
PostgreSQL 15+ null-equality metadata is read through a version-tolerant catalog projection. Rebind after
external schema changes: startup validation does not lock out later DDL. Column order is immaterial: the shared SQL builder emits an explicit insert column list. Without options the repository advertises no atomic transition-receipt support.

State and canonical receipt commit in one native transaction. Advisory transaction locks serialize the same
occurrence/subject; the conditional SQL write still fences external writers. Exact replay returns original
evidence even after later entity updates. Receipt size, format, hash, canonical encoding and identity are checked
on read. Oversized receipt writes roll back entity state. No retry is hidden; an ambiguous COMMIT requires exact
receipt resolution. Options choose a trusted physical partition, not an authorization grant. Application schema,
retention and recovery remain explicit. Receipts do not imply an outbox or event-authoritative state.

`Create(..., EntityCreationPolicy.IfAbsent)` uses `INSERT ... ON CONFLICT DO NOTHING` on the configured identity/partition key, independently
of receipt configuration. `Upsert` retains its existing replacement behavior. Typed wrappers forward both the
capability and the operation.

The shared Storage receipt protocol resolves replay only after a conditional native conflict; PostgreSQL supplies the locked native
transaction and conditional SQL writes. Fixed receipt partition evidence is inherited by process bindings.
Receipt validation intentionally retains canonical reserialization and hashing: the warm cost is measured in
`src/Cohesive.Relations.Benchmarks/RESULTS.md`, separately from SQL and network latency.
