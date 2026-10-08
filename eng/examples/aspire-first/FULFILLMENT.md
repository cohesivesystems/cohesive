# From implied creation to a multi-entity process

`FulfillmentDomain` remains the state authority. Native Aspire owns resources; native PostgreSQL mappings
attach to the same domain definitions. `FulfillmentProcess.Run` is authoring syntax compiled into canonical
Process IR, not an async application coordinator. Its generated definition can be inspected and fingerprinted.
`FulfillmentProcessBindings` uses `Service.Define(...).Operation(...).Run(process, bindings => ...).Build(...)` to prepare exact
links, catalogs and the shared runtime at startup. Receipt schema must be bound and validated before constructing receipt-capable repositories.
The binding phase retains canonical compiler diagnostics;
applications bind domain operations to repositories without recreating adapters and dispatch catalogs.

## What this slice guarantees

- `Create(..., EntityCreationPolicy.IfAbsent, ...)` turns the initializer's materialized state into canonical creation input, executes the
  implied creation decision, validates its candidate, and inserts only if identity plus partition is absent.
  Repetition with an existing ID returns sanitized 409 without replacing state. `ReplaceExisting` is the explicit alternative when replacement is intended.
- The hosted stock query is an explicit native point read. It is advisory; the guarded reserve transition
  checks current stock and its storage token. The joined reservation-availability relation is separately
  exposed by the running program through its existing prepared native reader.
- A Process sequences stock query → reserve → submit. Domain rejection by submission leads to a release
  transition. Ordering comes from the Process graph, not duplicated endpoint control flow.
- Each accepted entity operation commits state and its immutable process receipt in one PostgreSQL transaction.
  The receipt includes the canonical request, decision evidence and original result. It is not an authoritative
  event stream, an audit-retention policy, or an outbox publisher.
- Repeating an exact entity operation occurrence replays its original result. Tests reconstruct the runtime and repeat
  an invocation, proving stock is neither decremented nor released twice. Changed input cannot reuse that receipt.

## Concrete example

A Draft order requests two items from stock of five. Stock becomes three and the order becomes Submitted.
Re-executing the same invocation identity returns the retained results and keeps stock at three.
If the order is already Submitted, reservation is compensated and stock returns to five. If stock is one,
the guarded reservation rejects; the order is not submitted.

These scenarios are tested against a disposable native PostgreSQL database. The actual application startup,
create/read/submit routes and query preparation are also exercised. No cloud deployment is part of these tests.

The Process does not insert a `Reservation` row: this slice demonstrates stock mutation plus order submission.
The existing `Reservation` relation demonstrates joined demand using separately supplied fixture rows. Adding
an authoritative reservation lifecycle should be a declared transition, not an implicit extra SQL write.

## Local walkthrough

Start the AppHost as described in README. Create an order with `POST /orders`. In the disposable PostgreSQL
instance shown in the Aspire dashboard, seed a synthetic SKU using native SQL:

```sql
INSERT INTO public.cohesive_inventory (sku, partition_key, available, observation_version)
VALUES ('demo-book', 'local', 5, 0);
```

Use the returned order ID:

```sh
curl -H 'Content-Type: application/json' -d '{"orderId":"<order-id>","sku":"demo-book","quantity":2}' "$WORKER_URL/fulfillment"
curl "$WORKER_URL/orders/<order-id>"
curl "$WORKER_URL/orders/<order-id>/availability"
```

The sample attaches a synthetic local identity explicitly through `FulfillmentDemoIdentity`. It is **not
production authentication**. Replace the enricher with authenticated identity/grant resolution before exposing
this application. The receipt options select the fixed local demo partition. Transition bindings inherit it, and the stock-read
handler uses that same repository evidence. The hosted query declares the inventory entity authority rather
than duplicating physical placement in portable query configuration.

## Failure and recovery boundary

Execution is ephemeral with a 15-second budget. There is no durable Process ledger or background recovery worker.
A physical failure after reservation can leave stock reserved; compensation only runs when the graph reaches its
explicit domain-rejection branch. Cancellation or commit acknowledgement loss is an uncertain-effect result,
not proof that nothing happened. Resolve the exact retained operation before retrying; never blindly compensate.
The HTTP projection currently derives a new invocation identity per request. Repeating a POST is a new process,
not client idempotency. Query observations and terminal process results are not persisted: re-executing an
ephemeral process can observe a changed world even when its completed entity operations replay. Runtime tests use an explicit retained identity; this does not establish HTTP retry safety.

Receipts are retained indefinitely in this demo. Schema bootstrap is explicit, caller-owned and idempotent; it
is not a migration engine. Production must choose schema migration, receipt retention, reconciliation and
client identity policies before claiming recovery guarantees. No global transaction spans order and inventory.

Next composition: a durable execution binding and then independently selectable authoritative event history,
reconstitution, state checkpoints and read projections. Those are not inferred from these receipts.
