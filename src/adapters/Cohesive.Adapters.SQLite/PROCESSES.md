# Durable Process aggregates on SQLite

`SqliteProcessDurableStore` implements `IProcessDurableStore` for bounded local Process instances.
Use it with the existing `ProcessDurableRuntime`; it does not introduce another workflow interpreter.

```csharp
var database = new SqliteDatabase(new("processes.db", durability: SqliteDurability.Full));
SqliteProcessDurableStore.Schema.Apply(database, cancellationToken);
var store = new SqliteProcessDurableStore(database, authorityId: "ito/ingestion");
var runtime = new ProcessDurableRuntime(
    store, host, new(workerId: "ito/worker-1", workerLease: TimeSpan.FromMinutes(5)),
    bindingResolver: requestBindings,
    storeMutationExceptionClassifier: SqliteProcessStoreMutationExceptionClassifier.Instance,
    operationAdapterResolver: operationAdapters);
```

Keep the exact compiled Process plan, start evidence, activation input and bound operation definitions
available across restart. Initialize, activate, admit inputs and drive operations through the existing
runtime contracts. Recovery must resupply the same activation or operation identity; a restarted host
must not manufacture a fresh identity to bypass unknown outcomes.

## Authority and guarantees

The existing `InMemoryProcessDurableStore` supplies the semantic reduction, as it does for the Postgres
adapter. SQLite loads one canonical `ProcessDurableStoreDocument`, evaluates the operation, and atomically
stores the successor. No second implementation of checkpoint, lease, inbox, outbox, operation or receipt
rules is introduced. Process-local mutations are values inside this aggregate; they do not enlist arbitrary
market-data repository tables in the transaction.

An immediate transaction acquires SQLite's writer boundary before loading state. Reduction and replacement
remain inside that transaction. Physical lease validation uses the host's current system UTC time under
that boundary, not the caller's historical replay clock. All cooperating processes must use the same
local clock and authority binding. The adapter inherits SQLite's single-writer and local-filesystem scope;
it is not a multi-host distributed lease authority. Lease acquisition/renewal observations remain explicit
runtime inputs and require a trustworthy host clock.

The shared `SqliteStorageCommitExecutor` supplies SQL, parameter binding, conditional replacement, bounded
reads and atomic receipt writes through internal transaction-scoped operations. Its ordinary public
transaction ownership is unchanged. There are no new tables or feature-local SQL commands. The reserved
`cohesive.processes.aggregate/v1` target is owned by this adapter; do not write it through another path.

Each root stores the exact portable document and a token matching its atomic commit receipt. Its content
hash identifies the physical receipt. Reads verify instance identity, size, hash/result and matching token
before interpreting the document. Initialization and logical commit receipts remain inside the Process
aggregate, so an old exact retry returns its original snapshot even after subsequent progress. Physical
receipts verify storage integrity; Process receipts own logical replay semantics. These are distinct layers.

## Bounded profile and costs

The default maximum canonical UTF-8 document size is **4 MiB per instance**, including local state and
retained historical commit snapshots. Configure `maximumAggregateBytes` explicitly to change it. The
capability record exposes that bound. Native text transfer has an encoded-row bound before JSON decoding,
followed by the exact UTF-8 document check. Oversize writes fail before commit. Oversize/corrupt reads fail
closed and do not become an empty instance or successful replay.

This first profile reconstructs and rewrites the whole instance. Work and temporary memory grow with the
bounded aggregate; retained commit snapshots may themselves contain historical evidence. Physical receipts
also accumulate on disk. No automatic truncation, pruning, expiry, paging or unbounded-history performance
claim is made. This is a deliberate small implementation for bounded ingestion workflows, not the paged
Postgres realization. A future paging strategy must preserve canonical reconstruction and exact original
receipts. No third-party dependency or public transaction-enlistment API was added.

Native SQLite I/O and busy waits are synchronous. Cancellation is checked before I/O and commit but cannot
interrupt a native operation already executing. FULL durability is mandatory. Construction does not open
the file or run DDL; apply the shared schema explicitly at bootstrap. A missing parent directory, SQLite
failure, invalid canonical JSON or configured bound failure is an error, never a weakened realization.

Use the SQLite mutation exception classifier with the runtime: native and cancellation exceptions remain
potentially ambiguous and require exact reconciliation; local validation/size failures are not retried as
unknown commits. Read/mutation methods can throw `SqliteException`, `JsonException`, `InvalidDataException`
and cancellation in addition to their interface validation contracts. Preserve the database and WAL and
retain all receipts for the recovery horizon.

## Qualification and next binding

Native tests reuse the canonical Process/Request fixtures. They compare reopened state with the reference
store, preserve inbox/outbox/durable-operation evidence and local values, race workers, reject old fences,
check cancellation and bounds, reject corrupted receipt evidence, and verify replay after later progress.
The executable test worker is killed after runtime activation commit or during an uncommitted mutation;
reopening proves original evidence survives and an exact activation replay performs no host work.

This supplies durable Process storage, not a scheduler or a source-specific reconciliation policy. The next
Ito binding must connect retained acquisition/preparation references to durable Request adapters, prove
ambiguous external acquisition remains unresolved rather than reissued, and then replace the local replay
bridge. Destination coverage, source progress and Process checkpoints remain separate authorities.
