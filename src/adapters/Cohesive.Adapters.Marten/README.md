# Cohesive.Adapters.Marten

Marten realizes Cohesive entity history on PostgreSQL. Cohesive owns the canonical entity,
transition decision, replacement action, reconstitution and operation receipt contracts;
Marten owns stream storage, versions and event schema. The ordinary PostgreSQL adapter has
no Marten dependency. Applications opt into this package when attaching event history.

## Attach to native storage

```csharp
using var store = DocumentStore.For(options =>
{
    options.Connection(database); // The same caller-owned NpgsqlDataSource as relational storage.
    options.DatabaseSchemaName = "cohesive_events";
    options.AutoCreateSchemaObjects = JasperFx.AutoCreate.None;
    options.UseCohesiveEntityHistory();
});
// Explicit development migration; production deployments own their migration lifecycle.
await store.Storage.ApplyAllConfiguredChangesToDatabaseAsync();
var history = await MartenEntityHistoryStorage.BindAsync(
    database, store, EntityStateAuthority.Events);
var services = persistence.ForServices(receipts, history, outbox);
```

`BindAsync` validates Marten's schema before traffic; it performs no migration. Keep the
native data source and store alive for the attachment lifetime. Configure `AutoCreate.None`
to prevent implicit native migrations. Native Marten configuration remains available.

## One commit boundary

The PostgreSQL repository owns a connection and transaction. Each Marten session enlists
through `SessionOptions.ForTransaction(transaction, shouldAutoCommit: false)`. Saving a
session flushes the event append but does not commit the transaction. Relational state,
Marten events and the operation receipt commit together. For direct entity emissions,
relational state, Marten events and the existing outbox commit together instead. Process-owned
emissions remain in the operation receipt; they are not also published by the direct outbox.

The adapter introduces no generic transaction coordinator or second receipt model. A
subject advisory lock coordinates relational projection rebuilds with writes; Marten also
checks the expected stream version on append. Canonical semantic versions remain separate
from stream positions and PostgreSQL row tokens. No implicit retries occur. A connection
failure during commit is ambiguous: resolve the original operation/emission identities
before retrying.

## Authority and replay

State authority reads current relational rows and retains events as audit evidence. Event
authority reads Marten streams; relational rows are synchronous query projections. The event
is a complete canonical replacement action, not an invented business event. Its envelope
retains the authority, lossless codec version and payload integrity hash. Exact entity,
partition, shape and position validation precede acceptance. Changing authority on a retained
stream is rejected when that stream is acquired for a write/read; it is not a database-wide
startup scan.

Full replay validates the complete bounded stream. Checkpoint-and-tail starts at an exact
retained replacement event at the configured cadence. Marten's event index supplies that
position; there is no custom head/action/checkpoint table or duplicate snapshot store. This
first slice does not use Marten's aggregate projection/reducer model or async daemon.

Action-count admission occurs before fetching the selected stream range. Payload-byte
limits apply to canonical payload decoding and appending; Marten buffers the fetched event
objects, so these are not hard limits on network acquisition or peak memory in a maliciously
modified database. No throughput or allocation improvement is claimed.

## Evidence and limits

The PostgreSQL conformance suite runs against real PostgreSQL and covers replay, concurrent
writers, projection rebuilding, missing/corrupt events, setup schema rejection and late
receipt/outbox failures. Deferred-trigger tests reject COMMIT after event, relational and
evidence writes, then check all participants from fresh connections. The Aspire HTTP test
uses the same OrderApi and transition under both authority modes.

There is no external delivery, arbitrary domain-event reducer, tombstone, retention policy,
cross-database atomicity or asynchronous CQRS projection in this slice. Native SQL and direct
Marten writes bypass the Cohesive protocol. See the [example walkthrough](../../../eng/examples/aspire-first/STATE-AUTHORITY.md).

The envelope stores the canonical action as a JSON object in `jsonb`, not base64; native projections can
inspect that payload, although this adapter does not configure Marten projections. Binding checks the
registered event type as well as stream identity and exact data source. This unreleased envelope revision
requires explicit migration of earlier byte-array events (including uppercase hashes).

Replay limits are write admission limits too: a full-replay stream cannot append past its configured action
or byte budget. Checkpoint cadence must fit the action budget. Cumulative canonical byte counts are retained
independently of replay mode; appends acquire the previous event and, when needed, the checkpoint predecessor
to check the selected range without decoding the entire stream. This adds bounded metadata acquisition;
no write-throughput improvement is claimed. Read-side integrity and range bounds remain independently checked.
Outbox byte-limited reads return a partial page with its last included sequence; an individually oversized
or corrupt commit still fails rather than being silently skipped.
