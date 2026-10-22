# Compose state authority without replacing the order model

The PostgreSQL example uses `Cohesive.Adapters.Marten` and runs the same `FulfillmentDomain`, `OrderApi`, transitions,
queries and process with either current-state or event authority. Select the storage policy;
do not introduce another order type, transition evaluator or endpoint implementation.

From the repository root:

```sh
# Current rows are authoritative; history provides retained audit evidence.
EntityState__Authority=State dotnet run --project eng/examples/aspire-first/AppHost

# Retained replacement actions are authoritative; rows are derived query projections.
EntityState__Authority=Events dotnet run --project eng/examples/aspire-first/AppHost

# Same authority, different acquisition strategy.
EntityState__Authority=Events EntityState__Reconstitution=FullReplay \
  dotnet run --project eng/examples/aspire-first/AppHost
```

The native Aspire connection name stays `orders`. The event configuration uses a separate
physical database, `orders_events`, so selecting it does not silently reinterpret existing
state-only rows. Switching authority on a retained stream is rejected when it is acquired; migration is a
separate operation. Both modes use the same PostgreSQL mappings and canonical entity model.

## Concrete proof

Use the worker URL shown by Aspire:

```sh
curl -i -X POST "$WORKER_URL/orders"
# Use the returned ID in the following requests.
curl -i -X POST "$WORKER_URL/orders/<id>/submit"
curl "$WORKER_URL/orders/<id>"
```

Both configurations return a Submitted order. In the disposable example database, deleting
that order's mapped row has different consequences: state authority returns 404; event
authority still reads Submitted from history. `RebuildStateProjection(context, id, partition)`
explicitly restores the event-owned query row under the same stream fence, without another
action, receipt or emission. Ordinary reads never perform repair writes. Joined SQL queries
use mapped rows, so they require the projection to exist; they do not secretly replay streams.

The HTTP integration test uses the actual `OrderEndpoints` model for both modes. Repository
tests cover the rebuild, stale writers, concurrent writers, exact receipt replay, checkpoint
and full replay, corruption, and transaction rollback.

## What is authoritative?

| Piece | Meaning |
|---|---|
| Entity definition and transition | Valid state and pure decision semantics |
| State mode | Current mapped row owns state; retained actions are audit evidence |
| Events mode | Ordered committed replacement actions own state; mapped rows are derived |
| Stream position | Conditional append fence; distinct from semantic entity version |
| Process receipt | Exact operation request, decision evidence and result for idempotent handoff |
| Direct-transition outbox | Atomic delivery intent for authored durable envelopes |

`TransitionStateProjector` still prepares candidates. `EntityTransitionCommitProtocol` still
resolves exact process retries. A borrowed Marten session in the PostgreSQL transaction retains the state action
when history is attached. A receipt failure rolls back the row **and** history. Process-owned
emissions stay in process receipts; they are not republished through the direct entity outbox.

`PostgresEntityOutboxRepository` implements the existing `IEntityOutboxRepository` contract.
Its state write, optional history append and direct envelopes share one native transaction.
Exact envelope retries return the original committed result. Outbox storage is an independent
attachment: `persistence.ForServices(receipts, history, outbox)`. Omit history for ordinary
state storage with an outbox, or omit outbox for history without delivery intents. The example
configures both, but its Submit transition currently emits no explicit domain event, so
submitting does not fabricate an outbox message. The direct-emission regression uses the
existing RunControl transition fixture to prove atomic retention and replay.

## Deliberate first event contract

The initial `EntityStateAction` contract is a **complete replacement action** carrying a canonical
`EntityObservationSnapshot`, not a named business event and not an interaction envelope.
It covers implied CRUD changes as well as explicit transitions without pretending every
change already has a declared domain-event reducer. Reconstitution never reruns decisions,
authorization, queries, clocks or effects.

Every successful repository write appends an action, even if the semantic state/version is
unchanged. A rejected transition that does not commit appends nothing. Full replay validates
the ordered stream. Checkpoint-plus-tail starts at an exact retained replacement action
(default every 100 actions), then validates the tail. Because replacement actions already
contain full state, a checkpoint is an indexed position rather than a second serialized copy.
No retention or compaction runs automatically.

Appends acquire only the latest action; they do not replay a growing stream on each write.
Reads admit an action-count bound before fetching and check canonical payload byte bounds
before decoding. Marten buffers fetched event objects: these are not hard network/peak-memory
limits against a database modified outside this protocol. Bounds fail rather than truncate. Each selected action is decoded once. No throughput or allocation improvement is
claimed: full replacement payloads trade space for a small, explicit initial action contract.

## Boundaries

- All writers must use this repository protocol. Native SQL seeding is suitable only for
  state mode; event mode requires repository creation. Existing state-only rows are not
  silently adopted into an event stream. Direct SQL mutation can bypass every guarantee.
- Event-owned updates require the exact stream token. A derived row's `xmin` is not that token.
- Outbox writes serialize through commit per outbox table to make its cursor safe to tail.
  This bounds write throughput. Delivery, acknowledgements, retention and external exactly-once
  effects remain separate responsibilities.
- Hashes detect accidental payload corruption, not malicious database administration.
- This does not add arbitrary domain-event reducers, asynchronous CQRS projections, stream
  migration/upcasting, tombstones, a distributed snapshot, or crash-durable process execution.
  Those remain subsequent composition work. PostgreSQL-only policy does not change the
  Postgres+Cosmos example's explicitly independent-source consistency.

## Native Marten realization

AppHost and worker both select `FulfillmentHistoryPolicy.FromConfiguration` and register it
explicitly before model activation. The convenience method binds the immutable policy directly
from the `EntityState` section with the standard .NET configuration binder; missing selections
use constructor defaults, and enum validity remains enforced by the constructor. Event authority selects `orders_events` in both the prepared
storage model and native Aspire database; state authority selects `orders`. The logical Aspire
connection reference remains `orders`. Invalid values fail before constructing native resources.

The storage policy declares the `cohesive_events` schema and create-only initialization. The
worker registers a DI-owned `DocumentStore`, applying `policy.History.Configure(options)` after
its connection. Storage preparation checks schema names, disabled automatic migrations, string
stream identity and event registration before database access. Initialization uses the existing
storage preparation path; it is not performed by Program. Library configuration defaults to
validation-only; this demo explicitly chooses creation. The
PostgreSQL package remains independently usable. Marten sessions borrow the repository's
transaction with `shouldAutoCommit: false`; no event flush can commit a receipt or row early.
Deferred-trigger tests fail the final database COMMIT and verify that relational state,
events and the applicable receipt/outbox evidence all roll back.

Custom history head/action tables from the initial PR have been removed. Existing local
databases from that unreleased implementation need a fresh database or explicit migration;
there is no automatic conversion. Native Marten projection infrastructure is available for
subsequent CQRS work but is not activated by this example.
