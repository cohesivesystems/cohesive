# From implied creation to a multi-entity process

`FulfillmentDomain` remains the state authority. Native Aspire owns resources; native PostgreSQL mappings
attach to the same domain definitions. `FulfillmentProcess.Run` is authoring syntax compiled into canonical
Process IR, not an async application coordinator. Its generated definition can be inspected and fingerprinted.
`FulfillmentService` uses the existing `Service.Define` builder to select canonical transitions, a typed stock lookup
and relational queries once. `Definition.Bind(process, orders, inventory).Build(...)` prepares exact links, catalogs
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

### Operator failure details

`FulfillmentService.Definition.Bind(...).Build(...)` returns the prepared `HostedServiceProcess` directly; `Program` maps
its single route through the native ASP.NET extension and opts into `AddCohesiveTransitionFailureLogging(fulfillment)`; route mapping has no
subscription or lifetime wiring. The generic-host integration in `Cohesive.Host` subscribes at native host startup and releases the
subscription on stop or disposal, including disposal without stopping. An unstarted host never subscribes.
Repeated registration subscribes once per native host. It logs full native diagnostics at Debug level; enable
that level only for a protected operator sink because IDs and concurrency tokens may be present. Public
process responses remain sanitized. Other hosts and process-wide diagnostic listeners cannot subscribe to
these failures without a reference to this host or adapter instance.

### Closed order contracts

The order submission step takes no application input. Its Process binding supplies canonical Unit to
the subject selected by `input.OrderId`. It branches on `SubmissionOutcome.Submitted`; neither a duplicated command ID nor display
text participates in the domain rule. `OrderStatus` has explicit `Draft` and `Submitted` wire members.
The process revision is 2 because the referenced transition's input/outcome contracts changed. Existing
revision-1 receipts are not reinterpreted as revision-2 evidence.

### Authoring and binding boundaries

Start transitions at their entity: `FulfillmentDomain.Orders.Transition<SubmitOrderResult>(...)`
uses the exact declared order shape and the shared Unit input contract. Payload-bearing transitions use
`Inventory.Transition<FulfillOrder, StockResult>(...)`. Both delegate to `TransitionAuthoring`; they do
not introduce a second transition model or compile on invocation.

`Service.Define(...).Operation(...).Run(...).Build(...)` retains its typed process and selected operation.
`app.MapServiceEphemeralProcess(fulfillment, http: ...)` projects those retained contracts; callers do not
repeat the operation ID, process, declaration and runtime. The declaration-plus-deferred-resolver overload
remains appropriate when a host intentionally postpones preparation.

`AddFixedRequestIdentity(FulfillmentDemoIdentity.Context)` supplies reusable request-context plumbing.
The example still owns its synthetic actor, local scope and grants. This is not authentication and must
not be used as a substitute for authenticated per-user identities.

Some explicit declarations carry meaning:

- `FulfillmentStorage.Bind` places the full model in one database. `BindInventory` places only inventory
  in the remote database for composed execution. Sharing the mapping objects avoids schema duplication;
  registering all entities in the remote database would falsely describe its authority.
- `ReservationDemandProjection` identifies the optional closed prefix used by composed availability
  execution. Details currently has no composed placement, so it needs no corresponding cut. The cut is
  placement advice, not a second query. A future named-subplan authoring seam should retain it with its
  exact query rather than infer it from node order or CLR result types.
- `StockPointRead` is a native hosted observation. Its binding performs `TryGetEntity` for one SKU in
  the receipt partition. Its name documents that implementation; the hosted-query IR currently declares
  observation semantics, not a portable keyed-read operator. Removing the declaration would hide a
  process dependency. The subsequent reserve transition remains the authority on stock availability.
- `Provenance` attributes generated process/query/service artifacts to their producer and source. It is
  not runtime configuration. Convention-derived provenance can reduce authoring, but must still be
  materialized in those artifacts rather than dropped.

Aspire association selection sources can default to a deterministic reference, but this example retains
its real `Source` on both associations. Native project metadata supplies only .NET project execution.

`OrdersApp.StorageQueries` is derived by `Bindings.PrepareService(FulfillmentService.Definition, ...)`.
The selected query identities, revisions, bounds and consistency live on the service registration, not in a second
AppHost list. The optional `Cohesive.Adapters.Postgres.Services` integration uses the same offline mappings and native compiler
as the worker; the returned capabilities drive Infra requirements. Exact required, provenance and binding
plans must agree. Compiler changes alter evidence fingerprints, not public requirement identities.
The optional Aspire/Postgres association rejects evidence for a different actual database, independently
of its Aspire resource label. No database connection is opened during preparation. Each query gets its
own statement snapshot, not a common snapshot across queries or the composed two-database path.

This evidence does not certify schema installation, privileges, live readiness, conditional entity writes
or atomic transition receipts. Those require their own prepared and/or runtime evidence; a database name
or query artifact is insufficient. Native schema and receipt setup remain explicit in the worker.

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

This automatic binding profile hosts one ephemeral, terminal process operation. Multiple operations,
durable execution, effects and child-process hosting still need their explicit bindings. The relational query
selections are host composition roots for the existing HTTP readers, not newly inferred portable service
query operations. The composed two-database example remains an explicit placement alternative;
`PrepareService` rejects independent-source selections instead of advertising a native snapshot for them.
