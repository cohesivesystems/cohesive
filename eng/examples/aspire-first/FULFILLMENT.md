> Current entry point: `model.Endpoints` composes partial entity/query/process implementations;
> `Build(builder.Services, persistence.ForServices(receipts))` resolves their storage and logging;
> `app.MapServices(prepared)` maps the complete API. The standalone binding APIs below remain available.

# From implied creation to a multi-entity process

`FulfillmentDomain` remains the semantic authority for entities and transitions. Native Aspire owns resources; native PostgreSQL mappings
attach to the same domain definitions. `FulfillmentProcess.Execute` is authoring syntax compiled into canonical
Process IR, not an async application coordinator. Its class-level `[Process]` declaration generates an instance-owned, cached definition that can be inspected and fingerprinted.
`FulfillmentApplication` supplies shared instances of the domain, queries, transitions and process.
`OrderApi` colocates service policy with its process endpoint pair. Generated process dependencies supply
the transitions and stock lookup; HTTP relations remain separate API query bindings. `ServiceApiBindingsBuilder.Build(..., persistence.ForServices(receipts))` prepares exact links, catalogs
and the shared runtime at startup, matching repositories against the declared entity contracts. Receipt schema must be bound and validated before constructing receipt-capable repositories.
The binding phase retains canonical compiler diagnostics;
applications bind domain operations to repositories without recreating adapters and dispatch catalogs.

## What this slice guarantees

- `Create(..., EntityCreationPolicy.IfAbsent, ...)` turns the initializer's materialized state into canonical creation input, executes the
  implied creation decision, validates its candidate, and inserts only if identity plus partition is absent.
  Repetition with an existing ID returns sanitized 409 without replacing state. `ReplaceExisting` is the explicit alternative when replacement is intended.
- The hosted stock query is an explicit native point read. It is advisory; the guarded reserve transition
  checks current stock and its storage token. The joined reservation-availability relation is separately
  exposed by the running program through its existing prepared native reader.
- A Process sequences stock query → reserve → submit → create reservation. Domain rejection by submission leads to a release
  transition. Ordering comes from the Process graph, not duplicated endpoint control flow.
- Each accepted entity operation commits state and its immutable process receipt in one PostgreSQL transaction.
  The receipt includes the canonical request, decision evidence and original result. It is not an authoritative
  event stream, an audit-retention policy, or an outbox publisher.
- Repeating an exact entity operation occurrence replays its original result. Tests reconstruct the runtime and repeat
  an invocation, proving stock is neither decremented nor released twice, and reservation creation is not repeated. Changed input cannot reuse that receipt.

## Concrete example

A Draft order requests two items from stock of five. Stock becomes three, the order becomes Submitted,
and a declared creation transition inserts a reservation. `OrderDetails` and `ReservationAvailability`
now read that fact rather than depending on fixture-only demand. This one-item workflow uses the order ID
as its reservation ID, scoped to the repository's trusted partition.

`FulfillmentResult` reports closed `FulfillmentStatus` and `FulfillmentOutcome` values; stock operations
accept only `StockQuantity` and return a closed `StockOutcome`. The process binding selects the SKU.
If the order is already Submitted, stock is released and the result is `Rejected / OrderRejected`.
Invalid quantities and insufficient stock reject without creating a reservation.

Tests run the same sequence under both state and event authority using the optional Marten attachment.
They also fail reservation insertion after stock and order have committed, remove the injected fault,
and reconstruct the process host before replaying the exact occurrence. With stock of two and a request
for two, replay completes the missing reservation while stock stays zero. Replaying completed success
or compensation leaves the entity snapshots unchanged. Both joined queries consume the created reservation.
These are disposable PostgreSQL tests, not cloud deployment evidence.

## Local walkthrough

The SQL seed below is for **state-authoritative mode**. Event-authoritative mode must seed through
the configured repository so the first action is retained; see [State authority](STATE-AUTHORITY.md).

Start the AppHost as described in README. Create an order with `POST /orders`. In the disposable PostgreSQL
instance shown in the Aspire dashboard, seed a synthetic SKU using native SQL:

```sql
INSERT INTO public.cohesive_inventory (sku, partition_key, available, observation_version)
VALUES ('demo-book', 'local', 5, 0);
```

Use the returned order ID:

```sh
curl -i -H 'Content-Type: application/json' -d '{"commandId":"demo-1","idempotencyKey":"demo-1","instanceId":"demo-1","attemptId":"1","input":{"orderId":"<order-id>","sku":"demo-book","quantity":2}}' "$WORKER_URL/fulfillment"
# Follow the Location returned above; keep the original request unchanged for deliberate retry.
curl "$WORKER_URL/fulfillment/results/ZGVtby0x"
curl "$WORKER_URL/orders/<order-id>"
curl "$WORKER_URL/orders/<order-id>/availability"
```

The sample attaches a synthetic local identity explicitly through `FulfillmentDemoIdentity`. It is **not
production authentication**. Replace the enricher with authenticated identity/grant resolution before exposing
this application. The receipt options select the fixed local demo partition. Transition bindings inherit it, and the stock-read
handler uses that same repository evidence. The hosted query declares the inventory entity authority rather
than duplicating physical placement in portable query configuration.

## Failure and recovery boundary

Execution is host-owned and independent of the HTTP request. Start returns 202 with a native admission receipt;
the process continues in the generic-host worker. Caller cancellation and bounded response waiting do not
cancel admitted execution. The same explicit command/idempotency/instance/attempt/input replays admission
without scheduling another execution. Conflicting intent is rejected.

There is no crash recovery: in-memory execution receipts, deduplication and results disappear on host exit.
At most 1,000 instances are retained, including completed ones. Capacity rejects new work without eviction.
The single worker supports finite process graphs, has no retry/timer/distributed scheduler, and reports missing
terminal evidence safely if physical execution fails. A physical failure after reservation may leave stock
reserved; compensation runs only along the graph's explicit domain-rejection branch. Inspect the exact
PostgreSQL transition receipt before deciding how to recover; do not blindly repeat or compensate.

Receipts are retained indefinitely in this demo. Schema bootstrap is explicit, caller-owned and idempotent; it
is not a migration engine. Production must choose schema migration, receipt retention, reconciliation and
client identity policies before claiming recovery guarantees. No global transaction spans order, inventory and reservation. A failed reservation insert can leave a
Submitted order with reserved stock but no reservation until the exact occurrence is recovered. A new
command identity is not a retry. The tests demonstrate receipt-backed replay, not automatic process recovery.

State/event authority and reconstitution are independently selectable through the existing
[Marten attachment](STATE-AUTHORITY.md). Next composition: durable process recovery and explicit read-projection
lifecycles. Neither follows merely from storing transition receipts.

### Operator failure details

`BindHosted(..., logPrivateFailures: true)` opts into the existing generic-host scoped transition failure
logger. It subscribes to the prepared process at startup and releases on stop/disposal. Disabled Debug
logging does not build diagnostic arguments. The logger is operator-only; native IDs, tokens and provider
details must remain in protected sinks. Public polling responses contain only the service's safe diagnostics.

## Service declarations and host attachments

The portable service and Process documents remain authoritative for operation identity, execution policy,
timeout and dependency references. `ServiceDependencies` attaches existing transition definitions and native
lookup delegates; it does not synthesize domain behavior from names. Only exact dependencies referenced by
the process resolve repositories. Missing or ambiguous repositories fail during binding, and the lookup
uses the repository's fixed partition. Authorization remains explicit when the host is built.

Separately authored services use the same preparation path:

```csharp
var hosted = Service.Bind(serviceDocument, process, dependencies, orders, inventory, reservations)
    .Build(authority: "example", authorization: authorization);
```

The standalone `Service.Bind` profile above hosts one ephemeral, terminal process operation; the running demo uses `BindHosted` for admission and polling. Multiple operations,
durable execution, effects and child-process hosting still need their explicit bindings. The relational query
selections are host composition roots for the existing HTTP readers, not newly inferred portable service
query operations. The composed two-database example remains an explicit placement alternative;
`PrepareService` rejects independent-source selections instead of advertising a native snapshot for them.


## Separate PostgreSQL + Cosmos composition

`PostgresCosmosAppHost` runs the same `Orders.Model` API/process with PostgreSQL orders and Cosmos inventory.
`ServicePersistenceBuilder` selects the inventory repository by its exact observation contract; generated
process dependencies therefore resolve to the intended provider without a second process declaration.
`EntityPartitionKeyPolicy.Fixed` exposes the same trusted partition to Cosmos reads, writes and service
lookup admission. The explicit `sku` identity field supports typed lookup without an Id/Key convention.
The Cosmos client uses the existing canonical System.Text.Json serializer, not the SDK's default serializer.

Cosmos uses an atomic transactional batch for stock plus its receipt; PostgreSQL uses its native transaction
for order state plus its receipt. There is no cross-store transaction. The same host-owned process compensates
when order submission is domain-rejected; infrastructure failure may leave an uncertain effect for recovery.
HTTP 202 and result polling do not imply crash-durable execution. Joined reads have no distributed snapshot.
The two-database PostgreSQL split is retained solely as an adapter regression, not as production example setup.
