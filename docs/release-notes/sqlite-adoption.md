# SQLite adoption — unreleased

## Ordered representative semantics and SQL construction

Relations adds `SelectRepresentativeQueryNode` and structural/typed `SelectRepresentative` authoring. It selects
one uniquely best occurrence per explicit key partition, keeps only the winner's provenance and rejects tied
winners (`REL3212`). A later filter never causes fallback to a discarded row. Existing keyed `Distinct` semantics
are unchanged. See the [semantic contract](../../src/Cohesive.Relations/docs/internals/RELATIONS_AND_QUERIES.md#ordered-representative-selection).

This adds a `selectRepresentative` node to the version-one document union and generated TypeScript contracts;
update exhaustive consumers. The in-memory profile advances to `realization-v3`, so regenerate retained
realization artifacts against the new profile. Definition fingerprints for documents without the new node are
unchanged. Shared SQL adds capability-gated `SqlExpression.RowNumber` and public `SqlOrdering`, supported by the
SQLite and PostgreSQL dialects. Native Relations compilation and application query migration remain follow-up
work in COH-96; constructing a window alone does not establish canonical representative semantics.

## Explicit SQLite pooling and reusable binding plans

`SqliteDatabaseOptions` accepts an optional `pooling` policy. `SqliteConnectionPooling.Enabled` reuses provider native
handles while restoring and verifying the connection profile on every checkout. Disabled remains the convention.
Logical connections and transactions retain caller ownership; pooling is not a general native session reset.
`SqliteDatabase.ClearPool()` releases idle handles and retires active ones on return. Applications must finish operations
and clear enabled pools before replacing or deleting the database file. Recompile consumers for the extended options
constructor signature. See the [ownership contract](../../src/adapters/Cohesive.Adapters.SQLite/README.md) and
[measured tradeoffs](../../src/adapters/Cohesive.Adapters.SQLite/PERFORMANCE.md).

`SqliteCommandTemplate` wraps a shared `SqlCommandTemplate` with cached binding lookup. `SqliteDatabase.CreateCommand`
binds encoded values directly into fresh provider parameters without materializing a bound intermediate statement.
Runtime byte arrays are borrowed for the command lifetime; captured constants remain isolated. The shared SQL builder
adds capability-gated `SqlExpression.ScalarSubquery`, requiring one projected column and an explicit limit of one.
Postgres and SQLite support this construction; other dialects must explicitly accept it.

## Operation-scoped SQLite command reuse

`SqliteCommandScope` is an additive execution option for repeated immutable templates within one caller-owned
connection and active transaction. It retains private native commands, preparation and parameters, requires complete
bindings on each execution, and permits one active reader. Disposing it closes its commands and reader without
committing, rolling back or disposing borrowed resources. Existing one-shot commands remain independent. See the
[scope contract](../../src/adapters/Cohesive.Adapters.SQLite/README.md#repeated-commands-within-a-transaction) and
[benchmarks](../../src/adapters/Cohesive.Adapters.SQLite/PERFORMANCE.md#prepared-command-reuse).

## Intentional breaking SQL API changes

`SqlFunction.ClockTimestamp` has intentionally left the shared SQL function enum. PostgreSQL wall-clock behavior
belongs to its adapter and now uses the public dialect intrinsic extension:

```csharp
// Before
SqlExpression.Function(SqlFunction.ClockTimestamp)

// After
SqlExpression.Intrinsic(PostgresSqlDialect.ClockTimestampIntrinsic)
```

The emitted `CLOCK_TIMESTAMP()` SQL and its evaluation behavior are unchanged. Other dialects reject the intrinsic
unless they explicitly implement its contract. This is not a portable clock function or a request to substitute
transaction-start or statement-start time.

Former `PostgresSql*` construction types move to `Cohesive.Adapters.Sql` as `Sql*`; rendering and binding require an
explicit dialect. Official adapters consume the public API with no production friend-assembly grants. Postgres
compiled Relation artifacts advance to schema v4/compiler profile v2; regenerate older artifacts from canonical
Relations IR.

## Repository and capability behavior

- Typed batches accept `EntityWriteRequest<TEntity>` candidates with per-write opaque CAS tokens, retaining custom
  identity/version selectors through typed and typed-outbox facades.
- SQLite `None` batches commit each write independently. Later failure retains prior successful writes.
  `SamePartition` and `AllOrNothing` keep one atomic transaction.
- A conditional SQLite write that finds a different stored shape revision reports that mismatch separately from
  stale or missing concurrency targets.
- Shared SQL explicitly gates `IS [NOT] DISTINCT FROM`, right outer joins, and full outer joins. Postgres and the
  adapter's required modern SQLite profile advertise them; dialects without these facilities reject construction.

## Atomic SQLite outbox and Transition receipts

`SqliteEntityOutboxRepository` adds atomic state/envelope commits and Process operation receipts over the existing
entity mapping. Exact retries recover the original committed snapshot and token after later entity mutations.
Direct envelopes are exposed through a bounded commit cursor; Process envelopes remain handoff evidence for the
Process outbox. Auxiliary migrations must be applied explicitly. Automatic dispatch, delivery acknowledgment, and
retention pruning are not included. See the [outbox contract](../../src/adapters/Cohesive.Adapters.SQLite/OUTBOX.md).

## Lossless retained evidence — breaking encoding revision

Repository conformance found that plain receipt JSON rejected byte fields and erased detached
temporal/numeric kinds. `EntityStorageJson` format 2 reuses the PortableValue tagged codec and
preserves every observation kind. Entity operation commit fingerprints advance to `sha256-entity-v2`;
request/intent fingerprints and operation/emission identities do not change.

Apply SQLite outbox `repository.Migrations`, including migration 2. It leaves migration 1 intact,
adds `format`, and labels retained rows as version 1. Version 1 and unknown versions fail explicitly
on lookup, replay, or delivery reads; they cannot become missing operations or authorize another
write. Existing evidence needs an explicit migration using its original shape and execution inputs.
There is no automatic migration from lossy plain JSON.

Cosmos compressed Transition commit evidence advances to `br+base64/canonical-json;v=2` with the same
profile. Unversioned and compressed v1 evidence are rejected. Migrate retained evidence explicitly
before enabling retries against upgraded repositories. External transports or stores of
`EntityTransitionOperationCommit` must also select `EntityStorageJson.CreateOptions()` when retaining
detached entity state; the default JSON profile remains unchanged.

Default POCO materialization now handles native byte values directly, returning an owned mutable
array. Custom serializer contracts keep their explicit behavior. PostgreSQL `None` batches now use
individual commits, retaining the successful prefix and distinct `xmin` fences for repeated writes.
Cosmos direct-outbox retries now reconcile retained emission evidence before rejecting the caller's
original CAS token. Its direct replay still requires the current entity to match the candidate; only
the Process receipt path retains historical snapshots across later mutations.

See the [adoption recipe and evidence matrix](../../src/adapters/Cohesive.Adapters.SQLite/ADOPTION.md).
