> Current entry point: `model.Endpoints` composes partial entity/query/process implementations;
> `Build(builder.Services, persistence.ForServices(receipts))` resolves their storage and logging;
> `app.MapServices(prepared)` maps the complete API. The standalone binding APIs below remain available.

# From implied creation to a multi-entity process

`FulfillmentDomain` remains the state authority. Native Aspire owns resources; native PostgreSQL mappings
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
client identity policies before claiming recovery guarantees. No global transaction spans order and inventory.

Next composition: a durable execution binding and then independently selectable authoritative event history,
reconstitution, state checkpoints and read projections. Those are not inferred from these receipts.

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
var hosted = Service.Bind(serviceDocument, process, dependencies, orders, inventory)
    .Build(authority: "example", authorization: authorization);
```

The standalone `Service.Bind` profile above hosts one ephemeral, terminal process operation; the running demo uses `BindHosted` for admission and polling. Multiple operations,
durable execution, effects and child-process hosting still need their explicit bindings. The relational query
selections are host composition roots for the existing HTTP readers, not newly inferred portable service
query operations. The composed two-database example remains an explicit placement alternative;
`PrepareService` rejects independent-source selections instead of advertising a native snapshot for them.
