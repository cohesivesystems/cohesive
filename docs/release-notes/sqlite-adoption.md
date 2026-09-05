# SQLite adoption — unreleased

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
