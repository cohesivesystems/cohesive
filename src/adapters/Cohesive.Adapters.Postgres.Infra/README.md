# Cohesive.Adapters.Postgres.Infra

Optional infrastructure evidence derived from native PostgreSQL query preparation. The base Postgres
adapter has no Infra dependency. No connections, resources or migrations are created here.

```csharp
var queries = PostgresInfrastructure.Prepare(new("orders/postgres"), bindings,
    new QuerySnapshotRequirement("orders/details", "1", query.CompilationRequest));
foreach (var capability in queries.Requirements) storage.Requires(capability);
// In an Aspire AppHost, import Cohesive.Adapters.Aspire.Postgres:
associations.Resource(storeNode, nativeDatabase, queries, lifecycleAuthority);
```

`QuerySnapshotRequirement` belongs to provider-neutral `Cohesive.Relations.Infra`. The author owns its
stable identity and explicit semantic revision. Neither a compiler upgrade nor a physical table rename
changes that identity. Bump the requirement revision when changing its semantic contract. The example
also bumps its infrastructure definition revision to 3 for this migration. There is no compatibility
alias for the unreleased `PostgresInfrastructure.QuerySnapshot` fingerprint-derived identity.

Preparation consumes the requirement's canonical request. Evidence validates equality of the required
plan, artifact provenance plan and storage binding's compiled-plan fingerprint. Compiler and native
mapping changes remain visible in artifact/binding evidence fingerprints rather than silently renaming
the requirement. Other adapters can implement the same requirement after validating its exact plan.
Explicit manual Infra evidence remains an author-attested escape hatch; it does not acquire these checks.

Declare the query list once: preparation exposes the capabilities for Infra requirements and retains
implementation evidence bound to the logical database. The optional Aspire/Postgres association checks
`PostgresDatabaseResource.DatabaseName`, not the native resource label, before associating evidence.
Other native hosts must use `ForDatabase` with independent native database metadata.

Each artifact must be a complete, fully placed, unpaged query-row artifact on the same database.
Empty/duplicate sets, mixed databases, mismatched plans, source-scoped bindings and paging are rejected.
A successful preparation establishes an independent statement snapshot per query, never a distributed
snapshot or a common transaction across calls. Schema installation, privileges, runtime availability,
conditional writes and transition receipt atomicity are not certified.

Mappings have one authoring path: `PostgresPersistenceBindings.Define(database, configure).Bind(runtime)`. Define freezes
mappings; runtime registration shares the immutable result. The runtime-first constructor/Entity path
was removed. Standalone and composed queries share mapping lookup and input placement; semantic failures
use `RelationQueryPreparationException`, while native lowering failures retain `PostgresQueryPreparationException`.

Tests cover public artifact rehydration, exact-plan rejection, unsupported paging/source scope, database
affinity, stable versioned identities, shared semantic diagnostics and offline/runtime preparation equality.
