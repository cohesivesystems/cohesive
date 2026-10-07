# Aspire-first fulfillment domain

Start with a native Aspire AppHost; attach Cohesive requirements to existing objects.
The example keeps PostgreSQL options, project metadata, connection references, readiness
waits and volume configuration in native Aspire. It adds no second deployment inventory.

## Read the flow

1. `AppHost/OrdersApp.cs` declares two canonical nodes: order storage requires relational
   persistence; the order application requires application execution and storage readiness.
2. `Configure` creates the ordinary native AppHost: `AddPostgres().WithDataVolume()`,
   `AddDatabase`, `AddProject<Projects.OrderWorker>`, `WithReference` and `WaitFor`.
3. `AspireInfrastructureAssociation.Attach` associates those **same** database/project
   objects with the canonical nodes and explicit implementation evidence. Native names
   supply physical identities; there is no repeated physical-name table. The PostgreSQL
   server, its password parameter and an illustrative Redis container remain Aspire-only.
4. The existing target-deployment compiler matches requirements against evidence and
   checks canonical coverage. The AppHost refuses to start if the plan is incomplete.
5. Once the user starts the AppHost, ordinary Aspire runs the database and application.
   `OrderWorker/FulfillmentDomain.cs` declares `Order`, `InventoryItem` and `Reservation`
   once with `DomainModelBuilder.Entity<T>()`. Typed handles retain those exact definitions
   for relationships, storage and queries. `FulfillmentStorage` attaches
   fluent PostgreSQL mappings (`For(entity).Table().Identity().Partition().Build()`)
   and bind the Aspire-supplied data source through
   `PostgresNpgsqlRuntimeBinding`. The endpoints use `IEntityRepository.Upsert/TryGet`,
   implemented by the existing `Cohesive.Adapters.Postgres` repository.
   `JsonPropertyName` preserves the canonical `id`/`partition` names; writes use the
   record directly through `CreateState`. PostgreSQL mappings add physical details only.
6. `AddRequestOperationContext` registers the shared factory and request-scoped context.
   `UseRequestOperationContext` initializes it from the HTTP principal, activity and
   request-aborted token; minimal API handlers receive it through DI and pass it to
   storage. If authentication is added, place context middleware after authentication
   so it captures the established principal.
7. Schema lifecycle stays explicit: startup executes the embedded `schema.sql` once.
   Npgsql supplies the native data source and schema bootstrap; the shared repository
   owns entity validation/writes and the shared native query reader executes the compiled join. The runtime binding is
   caller-attested affinity, not independent proof of database identity.

The example demonstrates persistence, one guarded domain transition and a joined query across three entities.
It is not yet a CQRS/ES implementation.
It does not claim sequential execution, event history, audit logging or orchestration.
Those require additional explicit contracts, implementations and execution evidence.

## Build and run

Prerequisites: .NET 10, a running Docker-compatible container engine, and a trusted
ASP.NET Core development certificate. Build/test does not start containers:

```sh
dotnet build eng/examples/aspire-first/AppHost/AppHost.csproj -c Release
dotnet test src/Cohesive.Adapters.Aspire.Tests -c Release
```

To run locally (starts PostgreSQL, Redis and the application):

```sh
dotnet run --project eng/examples/aspire-first/AppHost/AppHost.csproj
```

Open the worker HTTP endpoint from the Aspire dashboard. Using that URL:

```sh
curl -X POST "$WORKER_URL/orders"
# Set ORDER_ID to the id returned by creation.
curl "$WORKER_URL/orders/$ORDER_ID"
curl -X POST "$WORKER_URL/orders/$ORDER_ID/submit"
# A second submission returns HTTP 409 without another write.
curl -X POST "$WORKER_URL/orders/$ORDER_ID/submit"
```

POST creates a fresh server-generated ID in `Draft`; GET returns the ID and status, or 404.
Creation no longer accepts a caller-selected ID, preventing the old upsert route from resetting a
submitted order. Creation retries create distinct orders; this example does not promise idempotent creation.
`OrderTransitions.Submit` uses conventional node IDs and provenance, keeping only the contract ID
and revision explicit. Inserting or reordering implicit steps changes their IDs; explicit IDs remain
available when editing stability is required. It declares the POCO-authored `Draft → Submitted` rule. `OrderEndpoints`
constructs `SubmitOrder(OrderId)` from the route and receives canonical `SubmitOrderResult(Status, Reason)`
outcomes. The transition validates that the command targets the loaded order. It
references that exact declaration in the canonical API and passes it to the shared ASP.NET binding.
The binding compiles once during registration and retains the plan for requests; invalid declarations
fail registration with a `TransitionApiPreparationException` retaining structured compiler diagnostics. No global compilation cache or per-request compilation is added.
All four routes (create, get, submit and details) are declared as portable handles in `OrderApi`
under one `Entity<Order>()` builder in a static constructor,
and bound in `OrderEndpoints`: entity operations use `app.MapEntityApi<Order>(..., endpoints => ...)`,
while the joined read uses `app.MapApiQuery(OrderApi.Details, details).FromRoute<Guid>(...).OkOrNotFound()`. Callbacks receive
typed order state and transition outcomes; `.Input(request => ...)` uses the existing request context
and its `RequiredEntityId`. The separate declarations contain no handlers; the example's HTTP error body
is explicitly the native `ProblemDetails` type. Creation declares 201, lookup/submission declare 404,
and submission declares the common 409 body.
Combined declaration/binding is also supported by the same shared surface; `Program` only configures startup and middleware.
The transition binding loads the order, evaluates the rule, and commits with the captured PostgreSQL concurrency
token. Repeated submission returns 409 without a write; a stale concurrent write also returns 409 rather
than retrying the domain decision. Both cases use `application/problem+json`: domain rejection has
code `orders.submit.rejected`, while a lost conditional commit has the shared code
`services.concurrency.conflict` and a trace ID. The transition binding returns its concurrency result
directly; `AddCohesiveExceptionHandling` plus native `UseExceptionHandler` provide the same sanitized
fallback for storage conflicts outside that binding. Neither exposes backend identities or tokens.
An unknown ID returns 404. There is no authentication;
this sample is for a local developer environment. Stop with Ctrl+C. `WithDataVolume`
retains PostgreSQL data across runs; deleting that volume is a separate deliberate action.
No cloud provider, deployment credentials or Ari environment is involved.

The explicit bootstrap creates `public.cohesive_orders`. Any `public.orders` table from
the earlier direct-Npgsql example remains untouched; its rows are not migrated or read.
The next local bootstrap migration adds required `status` with `Draft` as the default for existing
`cohesive_orders` rows. This is an explicit demo migration, not a general schema migration system.

## Validation and limits

The executable AppHost is referenced by the adapter test project, so tests exercise its
actual generated project metadata and native PostgreSQL resource model. Tests verify
object identity, unchanged native annotations/resource counts, native-only coexistence,
canonical fingerprint equivalence, missing associations, incompatible evidence, wrong
node kind, foreign-model objects, duplicate associations and frozen authoring. Storage
unit tests verify the real PostgreSQL adapter binding, canonical state validation and
cancellation before opening a connection. They do not substitute a fake repository.

The compiler validates **declared evidence**, not PostgreSQL configuration or a running
service. A PostgreSQL resource type does not automatically assert a transaction, audit,
ordering or durability guarantee. Native `WithReference`/`WaitFor` remain the wiring
and startup authority; association does not synthesize or certify those annotations.
The model tests build but never start Aspire. The opt-in `OrderStorageIntegrationTests` ran against a disposable local PostgreSQL 17 database:
it used the example's embedded schema and binding, verified create/load and token-guarded writes,
rejected a stale token and reloaded the winning version. Tests remove only their randomly identified rows.
Set `COHESIVE_ORDER_EXAMPLE_TEST_CONNECTION_STRING` to a disposable database to run these tests;
without it the database tests are explicitly skipped. CI supplies a disposable PostgreSQL 17 service
and runs these tests, including the actual worker entry point and the chained join. This is storage evidence, not an Aspire startup or cloud check.

Portable authority is the ordinary Cohesive definition, manifest and compiler result.
The native map is an immutable dictionary pointing at Aspire-owned mutable objects;
it is not a durable artifact or ownership transfer. Reattach and revalidate after changing
requirements or implementation selections. No new wire schema, compiler, traversal,
provider-options wrapper or native runtime readiness collector is introduced.

## Transition validation

The opt-in PostgreSQL test also starts the actual shared HTTP transition binding on a local ephemeral
port with request-context middleware. It verifies HTTP creation (201), lookup (Draft), missing-order
lookup (404), submit, reload as Submitted, repeated-submit 409 with
an unchanged token, missing-order 404 and rejection of a pre-submit stale write. Pure transition tests
run without PostgreSQL and verify admission and immutable input semantics. This establishes conditional
state mutation, not sequential execution, event sourcing, audit history or orchestration.

A deterministic HTTP race lets two submissions read the same PostgreSQL token before either writes.
Exactly one returns 200 and one returns sanitized 409, with one persisted state-version increment and
no retries. This test deliberately registers no exception middleware. A separate smoke test launches
the built `OrderWorker` entry point and exercises schema bootstrap, request context, create/get/submit,
and the domain Problem Details response. Child processes and randomly identified rows are cleaned up.
These checks do not start the Aspire orchestrator or qualify a cloud deployment.

## Compose the domain, storage and query

The declaration begins with entity authority, then connects entities:

```csharp
var domain = new DomainModelBuilder().Version("1");
var orders = domain.Entity<Order>("example/order");
var inventory = domain.Entity<InventoryItem>("example/inventory");
var reservations = domain.Entity<Reservation>("example/reservation");
var reservationOrder = reservations.References(reservation => reservation.OrderId, orders);
var reservationItem = reservations.References(reservation => reservation.Sku, inventory);
var definition = domain.Build();
```

`References` denotes the target observation identity. The domain owns these reusable relationship
handles; the existing query author captures a relationship in its catalog when traversed. No separate
query schema is inferred: `entity.QueryShape(author)` imports the entity's exact canonical state graph.
The domain model itself remains the entity catalog, not a new orchestration or persistence engine.

`FulfillmentQueries.OrderDetails` selects an order by a bound parameter and the local partition, traverses the
inverse reservation/order relationship, then traverses reservation/inventory. Both traversals are
left joins, so an order without reservations remains present. The declaration exposes
`RelationQuery<string, OrderDetails?>` and uses `SingleOrDefault<OrderDetails>()` to map each parent and
reservation field once. The author derives flat SQL slots and a portable `NestedQueryResultAssembly`
from that declaration; no `OrderDetailRow` DTO or handwritten result callback is maintained.

Query authoring accepts domain entities and bound nodes directly:

```csharp
var orders = query.Source(FulfillmentDomain.Orders).Where(
    order => order.Id == orderId.Value && order.Partition == FulfillmentDemo.LocalPartition);
// After the declared relationship traversals:
query.SingleOrDefault<OrderDetails>()
    .From(inventory, orders, order => order.Id)
    .Identity(result => result.Id)
    .Field(result => result.Status, orders, order => order.Status);
```

The result builder consumes each node's focused binding, preserving the same session/visibility checks
through the shared `IRelationQueryBinding<T>` contract. Nodes and explicit bindings implement that
contract; `From`, `Field` and `Collection` each have one signature. Parent and child `.Identity(...)` map the already-selected key to
a public property; they reuse its projection slot rather than repeat the key expression. The example
therefore projects six scalar slots instead of eight. This is a representation reduction, not a claim
of measured database latency improvement. Join direction, complete joined branch and partition filters
remain explicit. `SingleOrDefault<T>()` exposes only `From`; the returned mapping stage exposes
`Identity`, `Field`, `Collection` and `Build`. Calling a mapping method before `From` is a compile error.
Completed-builder reuse and foreign-session/visibility checks remain runtime authoring checks.

`FulfillmentStorage.Bind` attaches each entity and its native mapping once. The host uses that same
registration for repositories and queries; there is no separate per-query infrastructure catalog:

```csharp
var persistence = FulfillmentStorage.Bind(database);
var orders = persistence.Repository(FulfillmentDomain.Orders);
var details = persistence.Query(
    FulfillmentQueries.OrderDetails, maximumRows: 1000, maximumBytes: 1_000_000);
```

Query preparation discovers consumed source and traversal shapes from the canonical plan and resolves
only those mappings. An order/reservation subplan does not require inventory IO or an inventory table
binding in its SQL artifact, even though the persistence registration also knows inventory. Connections,
physical columns, scope and bounds remain explicit. Native versus composed placement is still a host choice.

Relationship handles created by `DomainEntity.References` retain exact endpoint documents. Traversing
one imports its endpoints automatically, so `FulfillmentQueries.OrderDetails` starts with the order shape and needs no
standalone reservation/inventory `QueryShape` calls. Explicit imports remain supported; conflicting
registrations fail rather than silently inferring another CLR schema.

The adapter invokes the existing static, placement, feasibility and native PostgreSQL compilers during
host registration, retaining the native artifact for inspection. The query contains no PostgreSQL imports,
placement or compiler calls. This is host infrastructure composition; it does not make the Aspire AppHost
or Cohesive.Infra responsible for implementing query compilation. Registration reuses repository mappings
for physical tables/columns, fails before IO for missing mappings, and retains structured compiler failures.
There is no global cache: retain the returned reader at host lifetime and invoke it per request.

`OrderApi.Details` is an `ApiEndpoint<OrderDetails>`; its primary response is visible in C# while
NotFound remains a separate outcome. The endpoint receives `IRelationQueryReader<string, OrderDetails?>` and binds the reader object directly
to the independently declared API. `Definition` retains the exact canonical query; no PostgreSQL type
or extracted `ReadAsync` delegate crosses the HTTP boundary:

```csharp
app.MapApiQuery(OrderApi.Details, details)
    .FromRoute<Guid>("id", id => id.ToString("D"))
    .OkOrNotFound();
```

Response type and query/body contracts are checked during registration. Invalid route input returns 400
without executing the query, null maps to the declared 404, and request cancellation reaches PostgreSQL.
There are no joins, observation decoding or result nesting in the endpoint.

Preparation occurs once at registration, and each request executes one parameterized SQL statement.
`PostgresQueryRowsReader` implements `IRelationQueryRowsReader` and reconstructs canonical observations from native result aliases and presence
markers. A missing joined row omits its fields. The shared canonical assembly nests reservation rows and sorts their IDs; it does not perform the joins. The reader rejects overflow rather than silently
truncating: this example allows 1,000 rows and 1,000,000 decoded scalar bytes, with cancellation and
native command timeout. It has no paging or retry policy, and is not a full canonical evaluation-outcome
API. The current reader accepts non-temporal scalar results/parameters only.

`GET /orders/{id}/details` returns `{ id, status, reservations: [...] }`, or 404. Each reservation includes
its SKU, quantity and current available stock in the same PostgreSQL statement snapshot. These are
current-state reads, not a materialized read model or event-sourced history. There is no stock reservation
command yet: the next Process increment can orchestrate transitions using this domain foundation.

Scope is explicit: the root query filters `partition = local`; inventory/reservation tables constrain
rows to that partition, and foreign keys prevent orphan references. Their IDs are globally unique in
this local demo; repository upserts additionally address `(partition, identity)`. This is not a general
multi-tenant relationship policy or authorization mechanism. The mapping projection does not invent
uniqueness, foreign-key or tenant guarantees. Native schema remains the authority for those constraints.

### Remaining scope integration

The example's explicit local-partition predicate remains required. Composed PostgreSQL source readers
already support `PostgresRelationQuerySourcePolicy.PartitionScope`; native query compilation currently
rejects any selected placement with a partition selector. Neither a registration helper nor a logical
scope label closes that native capability gap.

The next coherent scope change must attach an explicit scope once to persistence registration and
propagate it to every source and relationship acquisition. Native compilation must filter acquired
rowsets before joins (including optional outer-join sides), while composed readers enforce the same
scope. Required-but-missing scope must fail before IO. Tests must include foreign-partition roots and
related rows, matching keys across partitions, empty outer joins, and native/composed differential
results. Only then can this example remove its handwritten predicate. Scope selection must remain
explicit and must not be presented as authentication or authorization by itself.

### One result declaration, two execution phases

`SingleOrDefault<OrderDetails>().From(...).Field(...).Collection(...).Build(...)` declares parent identity,
scalar fields and child collections. Flat row projection and nested assembly remain distinct execution
phases, but both derive from one canonical mapping. The assembly is serialized and fingerprinted with the
query; changing its mapping changes the query revision. Static compilation validates output coverage and
scalar type compatibility against the result shape.

Zero rows produce null. Missing/null child identities produce empty collections. Repeated identical children
are deduplicated and sorted by ordinal string identity; conflicting copies or multiple parents fail closed.
The initial contract supports one parent with one level of child collections, direct scalar fields and string
identities. It does not infer aggregates, arbitrary CLR callbacks or cross-source snapshot consistency.

Native complete rows and complete composed outcomes use the same assembly. `typedQuery.Project(outcome)`
requires the exact compilation request, successful execution, an unsuppressed terminal and no unresolved
row gaps. The original outcome remains available with its provenance and source traces. Raw interpreter
outputs still carry the flat row shape; assembly does not relabel them as nested observations.

### Existing cross-source execution

These joins use the existing Relations authoring, compilation and native realization machinery. Cohesive
also already supports cross-source joins: `IRelationQueryEvaluator` uses bounded
`IRelationQuerySourceReader` acquisition, batched related reads and local correlation. PostgreSQL and
Cosmos source readers implement that acquisition contract. This preserves phase evidence, completeness,
requirement gaps and source traces; separate reads do not imply an atomic cross-source snapshot.

The complete-result reader contracts added for this example do not replace that evaluator or automatically
select federation. The example explicitly selects native PostgreSQL at registration. The existing
`MapRelationQueryApiDefinition` binding exposes full evaluator outcomes when that richer contract is needed.
See [execution and adapters](../../../src/Cohesive.Relations/docs/EXECUTION_AND_ADAPTERS.md) for the
composed join example and its no-N+1 regression. Its deterministic readers prove planning/correlation;
the example here also compares live PostgreSQL native execution with bounded composed acquisition
and the reference interpreter, using the same declaration and shared nested assembly.

### Native joins inside composed execution

The companion `FulfillmentQueries.ReservationAvailability` declares a semantic `ReservationDemand` projection between
order/reservation matching and inventory enrichment. `ReservationAvailabilityInfrastructure` can bind that
**same graph** in either of two ways:

```csharp
var orders = FulfillmentStorage.Bind(ordersDatabase);
var inventory = FulfillmentStorage.Bind(inventoryDatabase, databaseName: "inventory");
var native = ReservationAvailabilityInfrastructure.BindNative(orders);
var composed = ReservationAvailabilityInfrastructure.BindComposed(orders, inventory);
var availability = await composed.ReadAsync(orderId, cancellationToken);
```

`BindComposed` delegates to the library's `PostgresPersistenceRegistration.QueryComposed`:
the host supplies the query, cut, remote entity registration and one named acquisition policy with
its partition scope. Placement construction, mapping selection and remaining-reader preparation belong
to the adapter. Native and composed paths reuse the same entity attachments.

The native path executes one SQL statement. The composed path compiles the closed demand projection into
one PostgreSQL statement, then feeds its complete rowset into the existing physical executor for the
remaining inventory join. Orders and reservations are not acquired or joined again. The host selects the
cut; the query does not declare a PostgreSQL implementation. The ordinary HTTP details endpoint remains
all-native, and this companion binding is exercised by the two-database integration test.

`RelationQuerySubplan.Compile` derives both plans from the original snapshot and retains their provenance.
Prefix and remainder have distinct deterministic query identities, with the original request retained.
`RelationQueryPreparationException` retains semantic/physical diagnostics and stable cut-rejection codes.
It accepts a nonterminal closed projection whose ancestors are sources, filters, joins, relationship
traversals or projections. It rejects interior branches escaping the projection, unsupported operators,
multiple results and partial output demand. Existing canonical validation prevents hidden input bindings
from leaking through a projection. This is an explicit view boundary, not automatic discovery of arbitrary
SQL join islands. No separately authored join definition or application compilation is needed.

`RelationQuerySubplanReader.EvaluateAsync` retains the prefix rows, derived-plan relationship and full
remaining evaluation. The single parameter contract, consumed parameter inputs, capability evidence and
prefix row bound are prepared once; invocation values are validated once and remain local. Row occurrences retain bag multiplicity; they do not acquire invented entity
identities. Missing fields remain distinct from null. Prefix errors/overflow stop before remaining IO;
incomplete remaining acquisition cannot become a successful typed result. Preparation and native readers
are retained at host lifetime, while rows, parameters and evidence are invocation-local. Source readers
must match the remaining plan's source/domain/profile and declared logical partition. PostgreSQL binding
schema v4 fingerprints the explicit `ForSource` scope; full-placement coverage is never inferred from
which tables happen to be present. Persisted v3 bindings must be regenerated. Host registration
is responsible for using the same authorized scope in the native prefix; the scope label does not prove
backend authorization. There is no distributed snapshot across the two databases.

The live test uses PostgreSQL 17 with two disposable databases. It covers no reservations, two reservations,
missing orders, cancellation and independent inventory: updating only the second database to stock 11
changes the composed result while the all-native result remains stock 8. Set both
`COHESIVE_ORDER_EXAMPLE_TEST_CONNECTION_STRING` and `COHESIVE_ORDER_EXAMPLE_INVENTORY_CONNECTION_STRING`
to disposable databases to run it. Without both, this optional test skips. Deterministic tests additionally
verify one prefix call and one remaining-source call per invocation, concurrent isolation, duplicate rows,
invalid shape, overflow, failed native reads and partial remaining evidence.

### Try a joined response

After starting the example, optionally load synthetic fixtures into its **local** database. Find the
PostgreSQL container in the Aspire dashboard or `docker ps`, set `POSTGRES_CONTAINER` to its name, then:

```sh
docker exec -i "$POSTGRES_CONTAINER" psql -U postgres -d orders -v ON_ERROR_STOP=1 < eng/examples/aspire-first/demo-data.sql
curl "$WORKER_URL/orders/11111111-1111-4111-8111-111111111111/details"
curl "$WORKER_URL/orders/22222222-2222-4222-8222-222222222222/details"
```

The first order has no reservations; the second has two for `demo-book`, quantities 1 and 2, with
available stock 8 on a fresh database. Fixtures run in a transaction and preserve existing rows on
repeat execution. They do not simulate a successful reservation Process. The schema bootstrap adds
the two tables explicitly and leaves earlier order data intact.

The database-backed regression tests cover zero/multiple reservations, an identical order ID in a
different partition, missing IDs, parameterized hostile input, cancellation, affinity mismatch and
row/byte bounds. They execute the chained outer joins: that path exposed and now protects a compiler
bug where presence markers retained an alias from an earlier subquery scope.

Preparation is measured in `FulfillmentDomainTests` separately from execution. It includes native
registration and compilation after domain setup; it is not whole-host startup, retained memory or request
latency. One isolated local .NET 10 Release run measured 455.9 ms and 49,193,688 allocated bytes; 10,000
warm artifact accesses allocated zero bytes (excluding query execution). The returned artifact is retained
for requests. A large query catalog needs a separate scaling
measurement before eager preparation; the example does not claim catalog-wide startup qualification.

The typed convenience currently admits one canonical invocation parameter and one row result. It adds
no second semantic query model: its immutable compilation request retains the existing canonical
query/shape/relationship documents. HostedQuery was considered, but its portable execution-implementation
contract is a different responsibility from this local typed result projection. Anonymous rows use CLR
shape conventions; use explicitly registered stable shapes when independently versioning a persisted row
contract. Constructor and getter behavior must still pass the expression lowerer's direct-storage checks.

Typed branches support source-first filtering: `query.Source(inventoryShape).Where(item => item.Partition == FulfillmentDemo.LocalPartition)`. The extension uses the branch's owning session and the existing filter implementation; chained filters retain the same binding and foreign query parameters remain invalid.

### Composition and ownership

`FulfillmentDomain` owns the entity definitions and POCOs, including `Order`; their canonical JSON names
have no storage dependency. `FulfillmentStorage` owns all native table mappings and returns a concrete
PostgreSQL registration. `FulfillmentDemo` owns the local partition policy. `FulfillmentQueries` exposes
immutable typed declarations directly, without per-query `Definition` wrappers or an inheritance hierarchy.

Bind each database once at host composition. Repositories preserve the POCO contract:
`IEntityRepository<Order> orders = persistence.Repository(FulfillmentDomain.Orders)`.
This reuses `TypedEntityRepository<T>` and preserves native batch/concurrency capabilities. Typed writes
use the existing Id/Key and Version/zero conventions unless selectors are supplied; for inventory use
`selectEntityId: item => item.Sku`. The entity handle remains explicit so a CLR type cannot silently choose
among different canonical definitions.

`Program` registers the repository and prepared details reader as singleton contracts. Query binding helpers
accept existing registrations instead of rebinding databases. No registration or compilation happens per
request, no global result cache is introduced, and the host owns the Npgsql data source lifetime. Tests may
prepare a separate reader deliberately for a different bound (such as overflow testing).

`MapEntityApi` declares the partition once. `Create` passes that value into `initialize: partition => ...`;
it does not rewrite the entity or infer authorization. Both combined and separately declared API bindings
exercise a non-default partition in their HTTP contract tests.
