# Aspire-first order storage

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
   `OrderWorker/OrderStorage.cs` authors an immutable `Order` record,
   derives the canonical definition once with `ObjectEntityDefinition.For<Order>`, and adds
   a fluent PostgreSQL field mapping (`For<Order>().Table().Identity().Partition().Build()`), and binds the Aspire-supplied data source through
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
   Npgsql is used only for the native data source and schema bootstrap; the shared
   repository owns canonical validation and data reads/writes. The runtime binding is
   caller-attested affinity, not independent proof of database identity.

The order endpoint demonstrates persistence and one guarded domain transition, not a CQRS/ES implementation.
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
fail registration with diagnostics. No global compilation cache or per-request compilation is added.
All three routes (create, get and submit) are declared as portable handles in `OrderApi`
and bound fluently in `OrderEndpoints` through `TypedEntityApiBindings<Order>`. Callbacks receive
typed order state and transition outcomes; the separate declarations contain no ASP.NET handlers.
Combined declaration/binding is also supported by the same shared surface; `Program` only configures startup and middleware.
The transition binding loads the order, evaluates the rule, and commits with the captured PostgreSQL concurrency
token. Repeated submission returns 409 without a write; a stale concurrent write also returns 409 rather
than retrying the domain decision. An unknown ID returns 404. There is no authentication;
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
rejected a stale token and reloaded the winning version. It removes only its randomly identified row.
Set `COHESIVE_ORDER_EXAMPLE_TEST_CONNECTION_STRING` to a disposable database to run this test;
without it the test is explicitly skipped. This is storage evidence, not an Aspire startup or cloud check.

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
