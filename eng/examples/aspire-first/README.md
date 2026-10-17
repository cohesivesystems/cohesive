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

1. **Declare:** `Orders.Model/FulfillmentDomain.cs` colocates entities, transitions and the process as separate types;
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
The workers explicitly initialize local schemas before preparing runtime API bindings.

`FulfillmentStoragePolicy` owns the fixed demo partition and orders connection name. Queries still compile
that fixed partition as a literal; invocation-scoped partition selection is not introduced by this file cleanup.
The order-registration helper is test-only. Storage mappings live in the shared model assembly; native schema
resources and executable startup remain in their workers.

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
synthetic inventory and reservations through the canonical repositories, launches the actual worker, exercises HTTP and removes its
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

The original AppHost derives native PostgreSQL query capability evidence from prepared bindings. The mixed
AppHost currently associates the .NET workload with Cohesive.Infra; its database/container resources and
readiness wiring are native Aspire. Query placement is validated by the runtime adapters. It deliberately
does not manufacture Cosmos capability evidence or claim that Infra proves heterogeneous query guarantees.

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
