# PostgreSQL service preparation

Optional integration of `Cohesive.Api.Execution` service selections with PostgreSQL runtime readers and
`Cohesive.Infra` evidence. The base PostgreSQL adapter does not depend on service hosting.

For infrastructure authoring, use
`infra.Resource(store).Persistent().Postgres(bindings, endpoints.Plan)`.
This prepares the selected queries once, contributes provider-neutral requirements and retains the exact
immutable PostgreSQL evidence on that resource. The default implementation ID is
`postgres/{node.Value}/implementation`; pass `implementation:` to override it.
The application still selects the database bindings and endpoint plan explicitly, so multiple databases
cannot be silently conflated. The portable definition contains requirements; native preparation is an
in-memory authoring attachment, not a serialized second authority.

Declare relational queries through API implementation Bind calls (or explicit `ServiceDependencies.Query` for separately authored services).
`bindings.PrepareService(endpoints.Plan, implementationId)` validates endpoint coverage and derives
provider-neutral snapshot requirements and native PostgreSQL evidence directly from the portable
implementation plan. No application-level query forwarding catalog is needed. Separately declared
services and explicit dependency selections remain supported by the same preparation path. `persistence.Query(service, query)` checks the exact
semantic inputs and prepares a reader using the selected row and byte bounds. Both paths run at setup,
without database IO; schema installation, credentials, privileges and runtime readiness remain separate.

A native statement snapshot satisfies either `SingleSnapshot` or the weaker `IndependentSources`
requirement. It never implies a snapshot across query invocations. `ServiceQueryConsistency` has moved
to the provider-neutral `RelationQueryConsistency` in Relations. Endpoint `Bind` and runtime persistence
resolution require this choice explicitly; every reader declares its actual guarantee.
The service selections remain host attachments to canonical query IR, not a second query definition.

Tests in `PostgresServiceSelectionTests` cover exact selection, bounds, immutable registration, unselected
queries and consistency rejection. The Aspire-first integration tests exercise the resulting readers and
process bindings against real PostgreSQL, including receipt replay and cross-partition isolation.

`persistence.ForServices(validatedReceipts)` attaches the same native mappings to `IServicePersistence`.
Process observation contracts select a unique registered entity mapping; no entity/repository list is repeated
at each service binding. Repositories are shared within this resolver's host lifetime. Native query preparation
still receives explicit complete-result bounds. The resolver owns no data sources and performs no schema IO.


`ForServices` shares repository instances by observation contract and query readers by exact query instance
and row/byte bounds within one resolver. Preparation is synchronized and successful readers are retained
for that host scope; failed preparation is not cached. Inputs and results remain invocation-scoped.

## Heterogeneous preparation

`bindings.PrepareComposedService(endpointPlan, offlineSources, policy)` prepares the actual native prefix
(where available), acquisition placement and bounded physical evaluation without clients or IO. It
rejects a `SingleSnapshot` selection. Runtime `Bind` consumes that exact preparation and checks source,
partition, selector, query and result-bound affinity before constructing readers. Native prefix physical
preparation is reused, not compiled again at reader construction.

`preparation.Bind(postgres, receipts, remoteRegistrations)` creates the host-scoped `IServicePersistence`
for the entire selected query set, without a second list of `.Query(...)` registrations. Each
`EntityStorageRegistration` pairs an adapter-declared repository with its query acquisition source.
Local repositories use the PostgreSQL fallback; unselected queries do not fall back to another placement.
Readers are cached by exact query authority within this resolver; requested bounds and consistency are
checked on every resolution, including cached readers. Failed preparation is not cached. Native PostgreSQL
bindings must be the same immutable declaration used by preparation.

Infrastructure inference decomposes the preparation into source acquisition requirements and workload
evaluation requirements. Evidence retains canonical query, placement, physical-plan and native SQL
fingerprints plus result bounds. Capability identities are based on semantic query identity/revision;
a compiler change updates evidence, not the capability identity. Cosmos source evidence comes from its
registered adapter capability profile and bounded partition policy. No leaf or aggregate claims a shared
snapshot across reads. The graph must associate each source with its actual native resource and connect
workload dependencies. Offline evidence does not validate deployment, credentials or runtime readiness.

The SQL prefix must enforce the application's partition predicate; this API does not invent filters or
prove that an arbitrary author-written predicate implements the intended tenancy policy. Remote acquisition
is checked against the explicit logical partition and its selected native source policy.

## Model-owned policy and DI

Configure `.TransitionReceipts(new PostgresTransitionReceiptOptions(...))` on offline
`PostgresPersistenceBindings`. `PostgresStorageComposition` validates that table when binding the data source
registered under `Bindings.Database.Value`. `ComposeServices(plan, nativeSources, policy)` provides the equivalent
mixed-source preparation and exposes the same infrastructure evidence. Both implement `IServiceStorageComposition`.

Native Npgsql and Cosmos clients remain owned by their DI registrations or external callers. Schema creation is
explicit; composition binding never migrates a database. Optional Marten history and direct outbox attachments
remain separate from receipt policy and must use the same data source. Existing explicit `Bind`/`ForServices`
paths remain available for adoption without a storage model or DI.

Native compilation is shared by offline evidence and runtime readers through an owner-scoped weak preparation
slot on the exact immutable mapping instance and compilation request. Successful artifacts are reused; failed
preparation is not published. Parameters, reads and results are never cached. Different mapping instances do
not share native artifacts.
