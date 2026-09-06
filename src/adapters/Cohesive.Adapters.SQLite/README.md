# Cohesive.Adapters.SQLite

Shared SQLite infrastructure for entity repositories and specialized repositories such as Ito's market-data store. The adapter supplies connections, command binding, exact scalar encodings, module migrations, [entity repositories with optimistic concurrency and atomic batches](ENTITY_REPOSITORIES.md), and [atomic outbox commits and Transition receipts](OUTBOX.md). Relation compilation remains a subsequent increment.

The adapter uses `Microsoft.Data.Sqlite`. Core `ValueContract`, `ObservationValue`, and storage realization contracts remain the semantic authorities. SQLite-specific policy and native connection/transaction types stay in this adapter.

## Open a database and apply a module

```csharp
using Cohesive.Adapters.SQLite;

var database = new SqliteDatabase(new SqliteDatabaseOptions(
    path: "/data/ito.db",
    durability: SqliteDurability.Full,
    busyTimeoutSeconds: 5));

var schema = new SqliteSchema("ito/market-data", [new SqliteMigration(1,
[
    "CREATE TABLE market_payloads (id TEXT PRIMARY KEY, content BLOB NOT NULL) STRICT;",
    "CREATE TABLE market_prices (payload_id TEXT NOT NULL REFERENCES market_payloads(id), price TEXT NOT NULL) STRICT;"
])]);

schema.Apply(database, cancellationToken);
```

Construction performs no database I/O. The parent directory must exist. `OpenConnection` configures and verifies the native engine version, WAL journal mode, synchronization policy, and foreign-key enforcement. Applying migrations is an explicit application/startup action; opening a connection never applies a schema.

## Effective configuration and operating boundary

`database.Options` exposes the absolute path, resolved settings, convention-supplied setting names, and `StorageRealizationTarget` adapter/profile identity. Explicit arguments override conventions. The two profiles are `sqlite.file-wal-full/v1` (default) and `sqlite.file-wal-normal/v1` (explicit weaker commit durability).

| Property | Supported boundary |
| --- | --- |
| Placement | One persistent database file on a local filesystem, accessed by processes on the same host |
| Atomicity | One native transaction across records/tables in that database |
| Writers | One concurrent writer; contention uses the provider's bounded retry policy |
| Readers | WAL permits readers alongside a writer; long readers can delay checkpoints |
| Durability | FULL by default; NORMAL explicitly permits recent committed writes to be lost on OS/power failure |
| Foreign keys | Enabled and verified on each acquired connection |
| Distributed transactions | Unavailable; no guarantee across independent files, attached databases, or hosts |
| Native engine | `SqliteDatabase.MinimumEngineVersion` or newer; the qualified floor is 3.51.3 |
| Connection pooling | Disabled by convention; opt in with `SqliteConnectionPooling.Enabled`, retaining one owner per logical connection |

The application is responsible for attesting that the path uses an appropriate local filesystem. Path normalization cannot establish filesystem or hardware guarantees. In-memory databases and URI connection paths are excluded from this profile. The engine floor includes the upstream [WAL-reset fix](https://sqlite.org/wal.html#walresetbug); older branches with selective backports are outside this initial qualification. Actual native version is checked at connection acquisition, including when an application overrides the bundled provider.

The timeout is the provider's lock retry limit in whole seconds (default 5, permitted range 1–300), **not a query execution deadline**. `Microsoft.Data.Sqlite` [executes database operations synchronously](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/async). Cancellation is checked before acquisition, between configuration/migration operations, and before migration commit. It cannot interrupt an executing native operation or the provider's busy retry loop. An operation that finishes committing is committed even if cancellation arrives immediately afterward. The adapter provides no automatic transaction retries or background work.

## Borrow one transaction across repository operations

`SqliteDatabase` is immutable and reusable across threads. A connection, transaction, command, and reader each have a single caller owner and must not be used concurrently. Commands borrow their connection and optional transaction. Disposing a command never commits or disposes that transaction.

```csharp
using Cohesive.Model;
using Microsoft.Data.Sqlite;

using var connection = database.OpenConnection(cancellationToken);
using var transaction = connection.BeginTransaction(deferred: false);

using (var payload = database.CreateCommand(connection, transaction,
    "INSERT INTO market_payloads VALUES ($id, $content);",
    new SqliteParameter("$id", "payload/42"),
    SqliteScalarCodec.CreateParameter("$content",
        new ValueContract(new ScalarTypeRef(ScalarTypeKind.Bytes)),
        ObservationValue.FromBytes(payloadBytes))))
    payload.ExecuteNonQuery();

using (var price = database.CreateCommand(connection, transaction,
    "INSERT INTO market_prices VALUES ($payload, $price);",
    new SqliteParameter("$payload", "payload/42"),
    SqliteScalarCodec.CreateParameter("$price",
        new ValueContract(new ScalarTypeRef(ScalarTypeKind.Decimal)),
        ObservationValue.FromDecimal(123.4500m))))
    price.ExecuteNonQuery();

// A checkpoint/revision command can borrow this same connection and transaction.
cancellationToken.ThrowIfCancellationRequested();
transaction.Commit();
```

Uncommitted transactions roll back on disposal. Prefer an immediate transaction for a known write unit so lock acquisition precedes its read/write decisions. On a busy/locked error, a caller that chooses to retry must reason about the entire transaction and its idempotency; the adapter does not replay a partial command sequence. See the provider's [transaction semantics](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/transactions).

Use fresh parameters for data and `SqliteDatabase.QuoteIdentifier` for dynamic identifier components. `CreateCommand` checks transaction affinity and supplies the configured timeout. It is also the shared construction boundary for future repository implementations. Native access is an explicit adapter escape hatch: code borrowing a foreign connection or changing PRAGMAs assumes responsibility for maintaining the declared profile.

## Reuse native connections and compiled commands

For repeated operations, opt into provider pooling explicitly:

```csharp
var database = new SqliteDatabase(new SqliteDatabaseOptions(
    path: "/data/market.db",
    pooling: SqliteConnectionPooling.Enabled));
```

Each operation still owns and disposes its logical connection, transaction and commands. Disposal returns the native
handle to the provider pool. The provider rolls back its active transaction on connection disposal. Every checkout
restores foreign keys and writable mode and applies/verifies the requested WAL and synchronization profile, including
when FULL and NORMAL runtimes share a pool. Pooling does not introduce an ambient transaction or automatic retries.

Matching connection strings share a process-wide provider pool. Pooling is **not a general session-state reset**:
callers using native access must clean up temporary tables, attached databases, custom functions/collations and other
connection-local changes. Keep pooling disabled when native-handle isolation is required. Profile-acquisition failure
clears the matching pool so an unsuitable handle is retired. No other pools are cleared.

After all application operations finish, call `database.ClearPool()` before deleting or replacing its database file.
It closes idle handles and retires active handles on return; it neither interrupts current owners nor prevents future
opens. A connection's disposal alone does not release every pooled native handle. This lifecycle responsibility belongs
to the application owning the database, not to each repository operation.

Build shared SQL templates once and create a SQLite binding plan once:

```csharp
using Cohesive.Adapters.Sql;

var read = new SqliteCommandTemplate(new SqlSelectBuilder(new SqlQualifiedTable("market_payloads"), "p")
    .Select(SqlExpression.Column("p", "content"), "content")
    .Where(SqlExpression.Binary(SqlBinaryOperator.Equal,
        SqlExpression.Column("p", "id"), SqlExpression.RuntimeParameter("id")))
    .BuildTemplate(SqliteSqlDialect.Instance));

using var connection = database.OpenConnection();
using var command = database.CreateCommand(connection, transaction: null, read, ("id", "payload/42"));
var content = (byte[]?)command.ExecuteScalar();
```

`SqliteCommandTemplate.Template` retains the authoritative shared artifact. The provider binding plan caches slot
lookup and creates fresh native parameters for every invocation, avoiding intermediate `SqlStatement` materialization.
Supply each runtime binding exactly once, in any order; repeated SQL references share a slot. Missing, duplicate,
unknown and non-encoded values fail before execution. Values must already be encoded as `int`, `long`, valid Unicode
text, bytes or null; use the scalar codec when starting from semantic observations. Runtime byte arrays are borrowed
until the command is disposed and must not be mutated during use. Captured constant bytes are isolated from callers.
Templates are immutable and reusable concurrently; returned commands are not.

See [connection-reuse measurements](PERFORMANCE.md) for timing, allocation evidence and reproduction instructions.

### Repeated commands within a transaction

Use `SqliteCommandScope` when an operation executes the same templates repeatedly. The scope borrows an open
connection and its active transaction, and owns one native command per template instance. Native preparation and
parameter objects are reused after the first execution; SQL construction remains owned by the immutable template.

```csharp
using var transaction = connection.BeginTransaction(deferred: false);
using (var commands = new SqliteCommandScope(database, connection, transaction))
{
    foreach (var id in ids)
    {
        using var reader = commands.ExecuteReader(read, cancellationToken, ("id", id));
        while (reader.Read()) { /* consume this row before advancing */ }
    }
}
transaction.Commit();
```

Use a finite set of shared template instances and one scope per operation. The scope is not thread-safe and allows
one active reader; dispose that reader before executing another scope command. Disposing the scope also closes its
reader and commands, without completing or disposing the borrowed transaction or connection. Execution after the
transaction ends is rejected. Independent scopes can concurrently share templates, never native commands.

Each execution requires a complete binding, including explicit nulls that replace prior values. Invalid binding
fails before any cached parameter changes or SQL executes. Runtime byte arrays remain borrowed until their command
is rebound or the scope is disposed. Native execution failures propagate to the caller; the scope neither retries
nor selects a transaction recovery policy. Execution is synchronous, with cancellation checked before binding and
execution rather than interrupting native I/O. See [prepared-command measurements](PERFORMANCE.md#prepared-command-reuse)
for the measured benefit on repeated rows and the small overhead for a single execution.

## Exact scalar encodings

`SqliteScalarCodec` is the single mapping catalog for column storage classes, parameters, encoding, decoding, and supported scalar kinds. `Encode` validates the full value contract before provider binding. `Decode` validates the native storage class and resulting observation. Use matching `STRICT` column types and `BINARY` text collation. This is scalar storage, not a relation/query capability declaration.

| Semantic value | SQLite storage | Preserved semantics and limits |
| --- | --- | --- |
| Bool | INTEGER | Only 0 and 1 |
| Int32 / Int64 | INTEGER | Exact signed integer; declared range checked |
| Decimal / decimal quantity | TEXT | Invariant decimal representation; never REAL/binary floating point |
| String / Guid / enum / entity reference | TEXT | Exact valid Unicode; GUID shape and enum membership follow the core contract |
| Date | TEXT | Exact round-trip `DateOnly` format |
| DateTime / Instant | TEXT | Round-trip `DateTimeOffset` format, retaining ticks and offset |
| Bytes | BLOB | Exact bytes, defensively copied at each ownership boundary |
| Permitted explicit null | NULL | Distinguished from Undefined/absent observations, which cannot be encoded as SQL NULL |

Integral decimal inputs fitting Int64 have already normalized to Int64 in `ObservationValue`; the Decimal contract encodes these as decimal TEXT too. Fractional decimal scale retained by the observation is preserved. Binary floating-point observations and lossy/over-precision text are rejected. Unknown, named, structural, array, and graph-qualified contracts have no implicit scalar realization.

Decimal TEXT does **not** supply SQL numeric ordering, aggregation, or arithmetic. Temporal TEXT with differing offsets does **not** supply chronological ordering. Text equality reflects the stored representation; it does not automatically implement numeric or instant equality across representations. Such query semantics require an explicit lowering in a later relation interpreter. See the provider's [SQLite type mapping](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/types).

## Immutable module migrations

Each stable module ID owns its ordered migration plan and table names. Versions start at one and must remain contiguous. `SqliteMigration` retains exact ordered statements and a deterministic SHA-256 fingerprint over a versioned JSON array of version and SQL source. Whitespace and comments are revision content. Review tools can inspect `Module`, `Migrations`, `Statements`, and `Fingerprint` without opening a database; application code currently constructs this adapter plan directly, with no separate source importer.

`Apply` obtains an immediate write transaction, validates the durable prefix, executes the unapplied suffix, and commits schema/data/history together. Repeating the same plan is idempotent, and concurrent initializers serialize through SQLite's writer lock. Failure or observed cancellation before commit rolls back the whole suffix. A first-migration failure also rolls back creation of the history table. Earlier committed versions remain intact.

History is kept in `__cohesive_schema_migrations_v1` keyed by module and version. `PRAGMA user_version` is never read or overwritten. `SqliteSchemaException` exposes the module, version, and a structured `AheadOfPlan`, `ChangedMigration`, or `InvalidHistory` classification. An older application cannot silently downgrade a newer module, and an edited applied migration cannot silently replace its durable revision. Add a new migration rather than rewriting an applied one. This is history verification, not a general schema-drift detector.

Each entry supports one top-level CREATE, ALTER, DROP, INSERT, UPDATE, or DELETE statement. Quoted semicolons and comments are allowed; batch scripts, transaction control, PRAGMAs, ATTACH, and multi-statement trigger bodies are excluded. SQLite validates the remaining SQL grammar. Migrations are trusted, module-owned SQL, **not a security sandbox**: they must not alter another module's tables or the reserved history table. This initial facility does not coordinate multi-module dependency ordering, destructive migration approval, or automatic down migrations.

For Ito adoption, keep existing `user_version` ownership with Ito. Cohesive modules can initialize independently in the same database. Bringing existing market-data tables under a new module requires an explicit, reviewed baseline that validates the existing schema; blindly treating `CREATE TABLE IF NOT EXISTS` as evidence of compatibility is insufficient. Publication ordering, raw versus normalized data, data revisions, and as-of semantics remain Ito's responsibility. The transaction boundary above permits those operations to commit together.

## Operations and recovery

1. **Integrity:** during a maintenance window, run `PRAGMA integrity_check;` and require the single result `ok`. Also run `PRAGMA foreign_key_check;` and require no rows. Record native engine version and module history alongside the result. These are operator actions, not work performed on every connection.
2. **WAL:** keep the database, WAL, and shared-memory files on the same local filesystem. Monitor WAL growth and long-lived readers. A maintenance connection can inspect `PRAGMA wal_checkpoint(PASSIVE);`; busy/incomplete results require addressing readers before a stronger checkpoint. Do not delete an active WAL or force a checkpoint after every repository operation. See [SQLite WAL operations](https://sqlite.org/wal.html).
3. **Backup:** use the provider's `sourceConnection.BackupDatabase(destinationConnection)` (SQLite's [online backup API](https://sqlite.org/backup.html)) into a separate database file, then verify integrity, foreign keys, and expected module versions on that snapshot. Avoid copying only the main file while WAL connections remain active. Retain a tested backup before schema changes.
4. **Restore:** stop every process using the target database, dispose all connections and clear any enabled pool. Preserve the original database **and its sidecar files** as a recovery set. Restore the verified standalone snapshot under a fresh path, open it with this runtime, verify integrity and module fingerprints, and point the application to it before resuming work. Rehearse this procedure against a disposable database; a successful backup call alone is not a restore test.

## Validation and extension boundary

`Cohesive.Adapters.SQLite.Tests` uses disposable real files to cover exact scalar boundary round trips, explicit storage classes, enum/range/null validation, transaction composition, rollback, concurrent initialization, history mismatch diagnostics, lock contention, cancellation before acquisition, and backup restoration. Run:

```sh
dotnet test src/Cohesive.Adapters.SQLite.Tests/Cohesive.Adapters.SQLite.Tests.csproj
```

The implementation deliberately retains native transaction ownership instead of introducing another unit-of-work interface. Each repository should reuse connection/command configuration and the scalar catalog; its schema and contract-specific CAS/batch semantics belong in that repository realization. Each acquisition verifies the profile with either pooling policy; callers should batch a unit of work on one connection. No throughput or latency guarantee is asserted.

The required native engine profile supports `IS [NOT] DISTINCT FROM` and right/full outer joins. The shared builder
checks each facility through `SqlFeature` before rendering; these were added in [SQLite 3.39](https://www.sqlite.org/releaselog/3_39_0.html).
