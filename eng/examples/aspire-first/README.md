For state-authoritative versus event-authoritative PostgreSQL execution, see [State authority](STATE-AUTHORITY.md).

# One fulfillment model, two storage compositions

Declare orders, reservations, inventory, their queries and transitions once. Attach native storage and
Aspire resources to choose how the same HTTP API executes.

| Run | Orders and reservations | Inventory | Joined reads |
| --- | --- | --- | --- |
| `AppHost` | PostgreSQL | Same PostgreSQL database | One SQL statement |
| `PostgresCosmosAppHost` | PostgreSQL | Cosmos DB | Bounded composed reads; availability pushes the order/reservation join into SQL |

The first is the default and stays PostgreSQL-only. The second is a separate executable composition,
not a placement switch scattered through domain code. The domain, query definitions, transitions,
process and API are compiled once in `Orders.Model` and referenced by both workers.

## Declare → attach → bind → run

1. **Declare:** `Orders.Model/FulfillmentDomain.cs` colocates entities, transitions and the process as separate types.
   `FulfillmentDomain` owns `OrderTransitions` and `InventoryTransitions`, each authored once from its
   canonical entity handle. API bindings and the independently registered `FulfillmentProcess` consume
   these domain-owned authorities; transitions are not separately registered application components.
   `FulfillmentQueries.cs` defines their joined read views. The generated process sequences stock lookup,
   reservation, order submission and compensation. `OrderApi.cs` colocates the API contract and `OrderEndpoints` bindings, including fulfillment admission
   and linked result polling alongside entity/query endpoints.
2. **Attach:** `Orders.Model/Storage/FulfillmentStorage.cs` groups reusable native mappings, shared local partition
   policy and PostgreSQL-only placement as separate types. `FulfillmentStorage` places all three entities in one database.
   `PostgresCosmosWorker/PostgresCosmosStorage` selects only order/reservation mappings for PostgreSQL;
   inventory uses the existing Cosmos canonical entity envelope, fixed partition and source reader.
3. **Bind:** both workers activate their composition through `ModelActivator`, then use the same
   `OrderEndpoints : ApiBindingModel`. Its constructor contributes entity, query and process bindings together; HTTP fixtures prepare this same model before host construction. PostgreSQL uses `ForServices`; the mixed composition uses
   `ServicePersistenceBuilder` to select Cosmos inventory and composed query readers. It does not
   redefine routes, processes or query logic.
4. **Run:** `FulfillmentInfrastructure` and `PostgresCosmosFulfillmentInfrastructure` each supply its worker's connection references and readiness dependencies.
   Native resource options remain ordinary Aspire calls.

The process reads inventory directly:

```csharp
var stock = await process.PointRead(domain.Inventory, input.Sku,
    policy: new ProcessReadPolicy(timeout: TimeSpan.FromSeconds(2)));
if (stock.Entity == null)
    return new FulfillmentResult("Rejected", "Inventory does not exist.");
```

The entity declaration supplies the read contract and generated dependency. The result envelope makes
absence explicit; subsequent transitions remain responsible for concurrency and stock validation.
The optional deadline is cooperative and covers the whole read. Classified-failure retries can also
be selected with `ProcessReadPolicy`; this host-memory example does not enable adapter exception classification or
durable retry progress. The Durable Task interpreter supports an explicit `ProcessReadProgress.Durable`
demand; selecting it with the ephemeral executor fails at setup. See [process policy semantics](../../../src/Cohesive.Processes/README.md).

A `StorageModel` can own multiple providers. `StorageModelBuilder` is available directly; the base
class is optional authoring convenience. Binding recipes never open connections during activation.
The storage models explicitly initialize local schemas during async preparation, before publishing runtime API bindings.

`FulfillmentStoragePolicy` owns the fixed demo partition and orders connection name. Queries still compile
that fixed partition as a literal; invocation-scoped partition selection is not introduced by this file cleanup.
The order-registration helper is test-only. Storage mappings and native schema additions live with their
storage models; executable startup only registers native clients and configures the host.

## Run either composition

Requires .NET 10, Aspire prerequisites and a Docker-compatible engine. Run one host at a time:

```sh
# Default: PostgreSQL only
dotnet run --project eng/examples/aspire-first/AppHost

# Separate composition: PostgreSQL + Cosmos preview emulator
dotnet run --project eng/examples/aspire-first/PostgresCosmosAppHost
```

The mixed host uses Aspire's experimental cross-platform Cosmos preview emulator. Its worker uses the
shared `CosmosSystemTextJsonSerializer`, Gateway transport and the referenced account endpoint. Before
binding, it reads container metadata and requires `/partitionKey`. No certificate validation bypass is
installed. This is local example setup, not a production deployment recipe.

Both expose:

- `POST /orders`, `GET /orders/{id}`, `POST /orders/{id}/submit`
- `GET /orders/{id}/details`, `GET /orders/{id}/availability`
- `POST /fulfillment` → `202 Accepted` with a receipt and linked result URL
- `GET /fulfillment/results/{instanceId}` → pending or terminal result

Example fulfillment body (reuse these IDs only to retry the same intent):

```json
{"commandId":"demo-1","idempotencyKey":"demo-1","instanceId":"demo-1","attemptId":"1","input":{"orderId":"<created order ID>","sku":"book","quantity":2}}
```

An empty store has no inventory: fulfillment reports missing inventory. The integration fixture supplies
synthetic inventory through the canonical repository; fulfillment creates the reservations consumed by the queries. Tests launch the actual worker, exercise HTTP and remove their
fixture data. There is no automatic product-data seed or training workflow.

## What changes with placement?

The single-database query uses one PostgreSQL statement snapshot. In the mixed composition, availability
infers the closed reservation-demand boundary from storage placement, executes that prefix in PostgreSQL, then joins its rows with bounded Cosmos
inventory acquisition. Details uses the shared composed interpreter over both stores, preserving nested
result assembly. It does not yet optimize its SQL traversal into one native prefix.

The logical query fingerprint is unchanged. Source identity, placement, physical capabilities and execution
evidence change. Remote partition mismatch, missing or ambiguous mappings and invalid source selectors fail
at setup. Result row and decoded-scalar byte bounds are checked before returning a complete result.

**There is no distributed snapshot or cross-database transaction.** Inventory may change between reads.
The mixed SQL schema retains the reservation-to-order foreign key, but cannot enforce its inventory
reference in Cosmos. The domain relationship is still canonical. Each entity transition atomically commits
its own state and operation receipt in its own backend; the process provides sequencing and compensation.
Host-owned accepted execution remains in-memory, not crash durable. See [process guarantees](FULFILLMENT.md).

The shared endpoint bindings explicitly permit `RelationQueryConsistency.IndependentSources`. This is
an application consistency choice, not a consequence inferred from placement. The PostgreSQL-only
reader supplies the stronger statement snapshot. Selecting `SingleSnapshot` for a mixed reader fails at
setup, including when that reader was already cached for an independent-source request.

`PostgresCosmosStorage` declares inventory once with `CosmosEntityStorageBinding`: its entity, canonical
identity, native container, fixed partition and acquisition limits supply both the repository and query
source. `Storage.Compose(...)` retains the selected preparation; its DI binding resolves every query selected
by the endpoint plan; adding an endpoint query does not require another registration in `Bind`. Readers
are shared within that persistence instance, while inputs and results remain invocation-scoped. A foreign
query, changed bound or stronger consistency request is rejected even after a reader has been cached.
The PostgreSQL runtime must come from the exact prepared mappings. The fixed-partition helper intentionally
uses the standard Cosmos observation envelope; broader/custom source declarations remain available through
the query adapter, without inventing repository guarantees.

Both AppHosts derive infrastructure evidence from prepared bindings. The mixed storage model prepares
its endpoint-selected queries offline, with no PostgreSQL connection or Cosmos SDK client. PostgreSQL
acquisition evidence attaches to the order database, Cosmos source-profile evidence to the inventory
container, and bounded physical-evaluation evidence to the worker. Resource dependencies are explicit.
Runtime binding consumes the same preparation and checks source descriptors, selectors, partition,
query identity and result bounds. There is no aggregate snapshot capability in this graph. These are
preparation/association proofs, not evidence of deployed schemas, credentials, live reads or readiness.

## Validate

```sh
dotnet test src/Cohesive.Adapters.Aspire.Tests -c Release
```

Offline tests verify semantic equivalence, exclusive placement, partition rejection, native resource wiring
and cached service query selection. Live tests are opt-in:

- `COHESIVE_ORDER_EXAMPLE_TEST_CONNECTION_STRING`: disposable default PostgreSQL database.
- `COHESIVE_ORDER_EXAMPLE_INVENTORY_CONNECTION_STRING`: a second disposable PostgreSQL database for the
  retained adapter regression; that alternate composition is test-only.
- `COHESIVE_MIXED_POSTGRES_CONNECTION_STRING` and `COHESIVE_MIXED_COSMOS_CONNECTION_STRING`: **dedicated
  disposable** stores for the mixed HTTP fixture. It creates/deletes the `inventory/stock` Cosmos container
  and writes the local SQL schema. Never point these variables at shared or production data.

The mixed test verifies joined HTTP results, fulfillment admission/polling, stock decrement and replay
without a second decrement. Separate store consistency remains a limit even when these checks pass.

The query catalog exposes only logical queries. Composed storage preparation infers supported closed native prefixes; an explicit projection ID remains available as an advanced placement override. Inference runs at preparation, not per request, and does not imply a distributed snapshot.

`FulfillmentQueries` uses `Queries.WithInput<string>("orderId")` to author and register each query in one
place. Array recipes return their final projection; nested recipes receive a typed output source and return keyed field/collection mappings, so
`SingleOrDefault<OrderDetails>` is declared only once.
Neither repeats a terminal query ID/parameter registration. Nested `.From(rows).Key(source, sourceKey,
resultKey)` and the keyed collection mapping preserve the existing portable assembly and query fingerprints.

`FulfillmentApplication` and `PostgresCosmosApplication` expose their components as public required
init-only properties, including the independently registered process. `ModelActivator`
resolves those through the same graph as constructor dependencies, shares each component instance, and
populates the root before returning it. Domain, query, API and binding models keep constructor injection
for declaration authoring in their constructors. Required-init injection is also available to those models
when dependent authoring runs during completion; no property attributes, partial classes or `null!`
initializers are needed.


`FulfillmentInfrastructure` contributes PostgreSQL requirements through
`infra.Resource(Store).Persistent().Postgres(model.Storage.Bindings, model.Endpoints.Plan)`.
The adapter derives both requirements and evidence from the same selected query bindings; no
`StorageQueries` forwarding property or separate implementation ID is needed.
`builder.AttachCohesive(model: this, ...)` then associates the existing database and project.
Database association reuses that preparation and checks the actual native database name.
This is offline evidence, not database or runtime qualification; native-only resources remain supported.

`PostgresCosmosFulfillmentInfrastructure` uses the same attachment entry point for both resources and
the worker. `PostgresCosmosStorage` owns the SQL mappings, offline Cosmos container binding and selected
physical preparation. Native account/database/container names are checked when associated; the worker
gets its connection from the corresponding native Aspire reference. Its `Bind` supplies runtime clients
to those plans without rediscovering the split or recompiling the physical remainder.

## Storage preparation and native resource ownership

`StorageModel` owns native mappings, receipt/history/outbox configuration and a single offline composition. The PostgreSQL
example declares `PostgresStorageComposition` from entity mappings; the mixed example uses
`ComposeServices(nativeSources, policy)` with its declared Cosmos source. Neither storage constructor
depends on endpoint models. Application admission routes queries from all modules by entity placement
and prepares their declared consistency before native initialization. Infrastructure uses the same
offline `PrepareEvidence` operation on the selected queries; no application-owned mixed-store `Bind` recipe is needed.

The workers register keyed native singleton clients using the **existing Aspire connection names** (`orders`
and `inventory-cosmos`). No additional resource catalog is introduced. A PostgreSQL data source owns its pool;
a Cosmos client owns its connections/caches. Repositories and readers borrow these resources. The Marten store
uses the exact DI data source selected for the relational repositories.

```csharp
// For a database whose schema is already migrated:
builder.Services.AddCohesiveServices(model.Endpoints, model.Storage);
await using var app = builder.Build();
await app.MapServicesAsync<OrderEndpoints>();
await app.RunAsync();
```

Both Programs register native clients and call `AddCohesiveServices(model.Endpoints, model.Storage)`.
Product columns, scalar types, nullability, partitioned identity and selected foreign keys are projected
from the canonical entities and PostgreSQL mappings through `Schema(...)`. Validation-only is the library
default; the demos explicitly select `CreateIfMissing`. `Bindings.ProductSchema.CreateSql` exposes the
initial DDL for inspection. Creation contains no ALTER/backfill, and validation rejects drift before admission.
Relationship enforcement is selected explicitly with `ForeignKey(domain.ReservationOrder)`; the mixed model
never declares an inventory FK across PostgreSQL and Cosmos.

Local partition and positive/nonnegative quantity rules use typed `schema.Check(...)` declarations, reading
`FulfillmentStoragePolicy.LocalPartition` rather than repeating it in SQL. Shared order/reservation schema
configuration lives with `FulfillmentPostgresMappings`; order keys and reservation references both use `C`
collation. Existing installations using default-collated order keys need a reviewed migration.

The remaining `schema.sql` files contain native additions only: global identity indexes and
the Draft SQL default. They run through `SchemaPreparation` after generated-schema validation, followed by another contract check. They are
not inferred from transition rules and are not covered by the generated validator. Their migration and
verification remain operator/native-SQL responsibilities. The validator covers exact columns/types/nullability,
explicit collations, immediate non-partial partition/identity uniqueness, validated selected foreign keys and
the typed checks. Each validation pass uses one metadata batch; the native hook retains before/after validation.
Existing tables with the old inventory FK or missing status column need a reviewed migration; startup no
longer silently adds mapped columns. Cyclic foreign-key graphs require native migrations in this first slice.
Callbacks own transaction policy; failures prevent admission. Coordinate multi-host migrations externally.

The PostgreSQL model declares history authority, reconstitution and outbox options. Async preparation resolves
the keyed DI Marten store and validates its exact shared data source before history initialization. Auxiliary
schema policies default to `ValidateOnly`; the demos opt into `CreateIfMissing`. Marten uses native `CreateOnly`
followed by validation. Receipt/outbox bootstrap requires an existing schema, while Marten owns its configured
event-schema objects. Cosmos metadata remains validation-only. None of these declarations allocate clients.
Registration performs no native IO. Mapping awaits one cached preparation; failure or cancellation prevents
route publication and remains failed for that host. Rebuild the host after correcting configuration.

DI factory registrations own client disposal; registrations of externally supplied instances leave disposal to
the caller. Stop the host before disposing it so admitted workers stop before native clients are released.
Do not build a second service provider, allocate clients in repositories, or put request identity, transactions,
query iterators or results in singleton state. Direct native binding APIs remain available without DI.

Fulfillment treats an existing reservation with the same order, SKU and quantity as an idempotent completed
intent only when the order is already submitted. A conflicting reservation returns `ReservationConflict`
before stock changes. Concurrent competing intents can still race the preflight read; conditional creation
fails closed as an infrastructure conflict and requires reconciliation, rather than inventing compensation
for another intent's reservation. This example does not claim cross-entity transactional serialization.
Stock rejection mapping is an exhaustive typed enum switch: adding a new outcome requires updating the
process definition. Reservation creation and release cannot reject the positive quantity already admitted
by Reserve; physical failures remain interruptions resolved by the original receipts.

### Upgrading an existing demo database

Fresh databases are generated from the storage model. A database created by the earlier demo can require explicit
collation/check migration. Stop the demo, back up its database, review
[`migrations/001-explicit-collations.sql`](migrations/001-explicit-collations.sql), then execute it once against that
database before starting the new host. It keeps rows and native foreign keys, aligns the declared key collations,
and validates the new partition/quantity checks; incompatible existing rows abort the transaction. This historical
migration is tested against seeded legacy tables. It is not run implicitly and is not the fresh-schema authority.
The migration supports both placements (the mixed placement has no local inventory table); unrelated/custom
schemas need their own reviewed migration. Marten history format migrations remain separate.

`FulfillmentStorage` and the environment-only `FulfillmentHistoryPolicy` now live with the PostgreSQL worker.
`Orders.Model` owns only shared mappings/policy and no longer references Marten, so the Cosmos worker does not
inherit that backend. Native clients remain DI-owned. The embedded native additions run through the model's
`SchemaPreparation` declaration in one transaction under the schema advisory lock, including in integration tests. Built-in creation serializes competing host
bootstraps with a transaction advisory lock; custom native-addition callbacks still own their coordination. These callbacks cannot repair projected-schema drift: run reviewed migrations before host preparation.

Authority selection is shared by AppHost and worker through an explicitly registered
`FulfillmentHistoryPolicy`. See [state authority](STATE-AUTHORITY.md) for native schema
expectations and isolated state/event databases. The mixed example uses the optional
`Cohesive.Adapters.Aspire.Cosmos` association to validate native account, database, container
and partition path against its prepared storage source before accepting evidence.

The workers register their activated application with `AddCohesiveServices(model)` and map
it with `MapServicesAsync()`. Public root properties supply API bindings and storage models;
Program does not pair a single endpoint catalog with a single store. Native clients are still
registered explicitly under the existing resource identities. Additional application modules
can expose endpoint and storage models on the root; a new query module over the same entities
requires no edit to the existing storage declaration. Incidental transitive models are excluded.
Preparation follows ordinal root member names. Missing/ambiguous placement and missing process
dependencies fail before native preparation; cross-store queries still need explicit composed policy.
