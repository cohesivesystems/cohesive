# SQLite entity repositories

`SqliteEntityRepository` implements `IEntityRepository` over one normalized scalar table. Its immutable mapping retains the canonical `EntityDefinition`, complete field-to-column map, naming origins, batch capabilities, and initial migration. The [shared runtime](README.md) owns connection policy, commands, scalar encodings, and migration execution.

## Persist ordinary records

```csharp
using Cohesive.Adapters.SQLite;
using Cohesive.Prelude;
using Cohesive.Storage;
using Cohesive.Transitions.Authoring;

var definition = ObjectEntityDefinition.For<RunRecord>(new("ito.run"));
var mapping = new SqliteEntityRepositoryMapping(definition,
    identityField: nameof(RunRecord.Id),
    partitionField: nameof(RunRecord.Tenant),
    tableName: "runs");
var database = new SqliteDatabase(new SqliteDatabaseOptions("/data/ito.db"));

// Explicit startup action. Existing tables require a reviewed adoption plan.
new SqliteSchema("ito/runs", [mapping.InitialMigration]).Apply(database);
var native = new SqliteEntityRepository(database, mapping);
IEntityRepository<RunRecord> runs = new TypedEntityRepository<RunRecord>(native);
var context = OperationContext.Create();

var initial = await runs.Upsert(context, new RunRecord("run/1", "tenant-a", 0, 123.4500m));
var updated = await runs.Upsert(context, new RunRecord("run/1", "tenant-a", 1, 130m),
    expectedConcurrencyToken: initial.ConcurrencyToken);
var value = await runs.TryGetEntity(context, "run/1", new EntityReadOptions(partitionKey: "tenant-a"));
var batch = await runs.UpsertBatch(context,
    [new RunRecord("run/2", "tenant-a", 0, 10m), new RunRecord("run/3", "tenant-b", 0, 20m)],
    EntityBatchAtomicity.AllOrNothing);

public sealed record RunRecord(string Id, string Tenant, long Version, decimal Notional);
```

Typed facades reuse existing observation mapping and materialization. Custom identity/version selectors and materializer configuration remain available. Identity and partition mappings refer to **canonical serialized field names**, including `JsonPropertyName`, rather than CLR member names when those differ.

## Mapping authority and representation

- Table names default to the logical entity name. Columns default to canonical field names, ordered ordinally for deterministic SQL and migration fingerprints. `columnNames` supplies snapshotted overrides; `ConventionSuppliedSettings` explains which decisions were inferred. Physical names never rename semantic entities or fields.
- Identity is an explicitly selected required non-null textual field whose value must equal the snapshot's `EntityId`. Partition defaults to that field or can be selected separately. The primary key is `(partition, identity)` when distinct, otherwise `identity`. String, GUID, enum, and entity-reference contracts can supply textual keys. Values are not trimmed or case-folded.
- Every field must be present, single-valued, and supported by `SqliteScalarCodec`. Nullable fields store explicit null as SQL NULL; required presence does not imply non-nullability. Optional/missing fields, nested objects, arrays, named structural types, and other unsupported representations fail during mapping construction. CLR discovery currently maps nullable properties to optional fields, so persisting those properties requires an explicit required/nullable canonical definition until an optional-field realization is added.
- The scalar catalog supplies exact column types and parameters. Decimal TEXT preserves canonical observation values, including integral decimals normalized by `ObservationValue`, without REAL conversion or a SQL numeric-ordering claim. Temporal and byte encodings follow the shared profile.
- Reserved columns `__cohesive_version`, `__cohesive_token`, `__cohesive_graph`, and `__cohesive_shape` retain separate semantic version, storage token, graph revision, and shape identity. Column collisions, including case differences, and reserved table prefixes are rejected.
- `InitialMigration` creates a STRICT table with primary key and metadata checks. It excludes `IF NOT EXISTS`: an existing table needs a reviewed adoption plan. Apply it in an application-owned `SqliteSchema`; the repository performs no migration or arbitrary schema-drift detection.

Reads validate the stored graph and shape identities. Unconditional upserts also refuse to overwrite an existing row belonging to another shape revision. Shape evolution requires explicit migration, not reinterpretation through a changed mapping. Named graph identities must identify immutable revisions, as elsewhere in Cohesive.

## Reads and optimistic concurrency

`TryGet` returns null for a missing identity or explicit identity/partition pair. An unscoped identity in multiple partitions throws `InvalidOperationException`. Expected semantic version and expected storage token are independent read preconditions; mismatches throw `ObservationConcurrencyConflictException`.

Every successful write generates a fresh opaque UUID token, even when state and semantic version are unchanged. Retain the whole token; do not parse it or derive it from the semantic version. Without an expected token, `Upsert` inserts or replaces a row of the same shape. With a token, SQL conditionally replaces the exact identity/partition/shape/token; stale or missing targets fail without inserting. There is no insert-only/create-if-absent operation. A different partition identifies another row, not a move of the original entity.

Field selections are validated and retained in `LoadedFields`, including an explicitly empty selection. The repository loads and validates the complete observation before returning it. This follows the existing materializer convention; it does not promise reduced column I/O or physically sparse state. Projection metadata is snapshotted into immutable storage.

## Batches, cancellation, and failure

Native capabilities cover same-partition and cross-partition all-or-nothing batches within one repository table/database, with a default maximum of 1,000 writes. Cross-file/distributed transactions are unsupported. `SamePartition` rejects multiple logical partitions; oversize batches are rejected instead of split. Empty batches perform no I/O.

Every batch runs in input order under one immediate transaction, including `None` requests, which receive a stronger physical guarantee. Results retain the requested atomicity and input order. Repeated identities execute sequentially with distinct tokens; a later conditional write must match the token current at its position. A late stale token, SQL failure, shape mismatch, encoding failure, or observed cancellation rolls back all pending writes. Snapshots are returned only after commit.

Candidate/partition validation precedes acquisition; scalar encoding additionally validates bound values. Caller-owned list entries are snapshotted before acquisition and must not be mutated concurrently during that snapshot. Each operation owns its connection and write transaction. Native operations are synchronous, and task APIs wrap completed results without hidden scheduling, nested commits, or retries. Cancellation is checked before acquisition, during batch preparation, between writes, and before commit. It cannot interrupt native execution or busy retries; cancellation after successful commit does not turn it into a cancellation result.

Typed and typed-outbox facades preserve native capabilities, limits, ordering, and atomicity. Typed batches now use the same identity/version selectors as single writes and invoke one native batch instead of five concurrent single writes. For per-candidate tokens, pass canonical `EntityBatchWriteRequest` values through the raw overload.

## Verification and follow-up work

Real-file tests cover competing CAS writers, stale/missing targets, repeated identities, late batch failure, partition ambiguity, reopen persistence, scalar validation, physical naming, shape revisions, and native dispatch through typed facades. Shared regressions cover required nullable values versus absence in flat, nested, and collection observations, including warm validation allocation checks.

Atomic outbox/receipt commits remain COH-88. Optional/structured field realization, relation compilation, and Ito's temporal market-data publication/query rules remain separate work. Specialized code can share transactions through `SqliteDatabase`; this repository owns its transactions and does not expose uncommitted snapshots as committed results.

## SQL construction and ordinal observations

Repository select, upsert, and conditional-update commands are compiled once through `Cohesive.Adapters.Sql` with
`SqliteSqlDialect`. Shape guards remain part of the upsert's conflict-update predicate. All values are still encoded
by `SqliteScalarCodec`; connection ownership, immediate transactions, batch order, and storage tokens are unchanged.

`Mapping.Layout` aligns the selected columns with canonical field identities. Reads decode directly into one immutable
value vector and retain it in `Observation`, with cached identity/partition ordinals. Name-based field access is a view
using the shared layout; no per-row field-name dictionary is built. Postgres uses the same core construction path.
