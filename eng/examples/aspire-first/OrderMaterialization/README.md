# OrderDetails shadow materialization

The live HTTP endpoint still reads PostgreSQL. This optional command consumes the
same rooted `OrderDetailsRelation`, hydrates its nested result through
Cohesive.Relations, and maintains a separate Elasticsearch generation. It never
promotes an alias or changes endpoint bindings.

## Run locally

Enable `Shadow:Enabled` on the existing AppHost to add Elasticsearch and the
OrderMaterialization project. The latter defaults to `--help`. PostgreSQL starts with
logical replication enabled. This is a local development configuration, including
an Elasticsearch node without authentication; do not expose it publicly.

Alternatively run OrderMaterialization against dedicated local PostgreSQL (logical WAL
required) and Elasticsearch instances. Configure:

- `ConnectionStrings__orders`: the same native database used by the worker.
- `Shadow__Elasticsearch`: Elasticsearch HTTP endpoint.
- `Shadow__PositionKey`: a persistent base64 authentication key of at least 32 bytes.
- `Shadow__SlotGeneration`: an explicit replication-slot incarnation, retained on restart.
- `EntityState__Authority` and `EntityState__Reconstitution`: the worker's authority policy.

From the repository root:

```sh
dotnet run --project eng/examples/aspire-first/OrderMaterialization -- initialize
dotnet run --project eng/examples/aspire-first/OrderMaterialization -- rebuild \
  --attempt demo-1 --started 2026-10-26T00:00:00Z --invocation baseline-1
dotnet run --project eng/examples/aspire-first/OrderMaterialization -- catch-up \
  --attempt demo-1 --started 2026-10-26T00:00:00Z --invocation changes-1
```

Initialization is explicit: it prepares product tables, sets FULL replica
identity, creates an insert/update/delete publication and per-input pgoutput
slots, and installs the dedicated target template and checkpoint table. It does
not change existing template declarations. Native preflight validates publication,
slot, table and replica-identity assumptions before reads begin. Missing slots
with existing progress require operator recovery, not silent recreation.

`rebuild` captures source positions, enumerates bounded root pages, hydrates each
root and writes its stable target identity. `catch-up` consumes retained pgoutput
changes through a captured cut; compiled inverse-impact routes find affected
orders, including both sides of reservation reparenting. Root deletions create
tombstones. The same prepared result assembly is used for live and shadow reads.

Retain attempt, start timestamp, key, slot incarnation and invocation ID when
resuming an interrupted activation. Use a new invocation ID for a new catch-up
cut. Durable prepared work and checkpoints live in PostgreSQL. Elasticsearch
version fencing makes replay of an already-written batch safe. Each command takes
a PostgreSQL session advisory lock, permitting one cooperating shadow writer.
There is no unattended scheduler or automatic retry loop in this example.

## Guarantees and limits

- This is baseline plus retained-feed catch-up with **current-state enrichment**,
  not a historical snapshot spanning every read. Convergence is demonstrated at
  a bounded source cut after writes settle.
- Query semantics, storage mappings and impact routes are reused, not redeclared
  as a second search query. Read plans separately authorize baseline enumeration
  and inverse-impact lookups. Evidence comes from the actual native readers.
- The target remains shadow-only; `orders-shadow` is never promoted. The live API
  keeps its existing PostgreSQL consistency and response behavior.
- WAL retention is operational state. Monitor slot lag and disk use. A lost slot,
  changed incarnation, incompatible plan or mismatched template is a recovery
  boundary. Do not delete slots while a candidate depends on them.
- Disposable tests own their source rows and generations. Cleanup of persistent
  generations, slots and volumes is an explicit operator action.
- Native PostgreSQL `C` text ordering is used consistently for opaque baseline
  continuation keys; it is not advertised as CLR ordinal query ordering.

The native integration test requires dedicated `COHESIVE_SHADOW_POSTGRES` and
`COHESIVE_SHADOW_ELASTIC` resources. It interrupts after a target write, reconstructs
the runtime, compares nested output with live reads, then exercises contributor
updates, reparenting, replay and deletions. It checks that no live alias appears.

The PostgreSQL ledger now uses canonical JSON fingerprints: `jsonb` property
reordering must not corrupt prepared work. Legacy hashes are accepted only when
their original check succeeds, then upgraded on a successful write. A legacy
mismatch remains an integrity error; it is never silently re-signed.

Native construction is owned by `PostgresMaterializationRelation.BindAsync`: the
example supplies its borrowed pool, attested runtime, bounds and slot factory.
`PostgresLogicalReplicationPreparation` owns explicit publication/slot setup and
asks the configured state store about progress. `PostgresSessionLease` owns the
operator lock lifetime. The shared rebuild compiler accepts caller provenance and
capacity evidence and uses the declared scheduling limits.

The auxiliary impact plan is a bounded read envelope over declared inputs,
including optional current-state enrichment, rather than a minimal list of only
operations taken by one impact route. Every actual read remains constrained by
semantic-plan affinity, native mapping, partition and configured limits.

The `shadow-materialization` GitHub workflow starts disposable PostgreSQL and
Elasticsearch instances and runs this recovery test on pull requests, including
stacked branches. It also verifies native ledger and Unicode keyset contracts.

### Ledger representation cost

A Release/.NET 10 warm allocation check measures 32 iterations of serializing a
nested JSON ledger and hashing it, excluding database I/O and ledger replay. For
1 KiB / 64 KiB / 256 KiB payloads on the local macOS test host, legacy hashing
allocated 3,424 / 196,960 / 786,968 bytes per operation; canonical hashing allocated
2,912 / 131,936 / 525,166. Approximate timings were 1.4 / 46 / 491 µs legacy versus
3.2 / 67 / 425 µs canonical. These are diagnostic samples, not throughput claims.
The canonical path still parses serialized JSON; it avoids the legacy full UTF-8
hash input allocation by streaming canonical output. The regression bounds
allocation growth, preserves array order and checks decimal/object equivalence.

### Review compatibility notes

The public partition binding uses `partition`/`Partition` instead of `tenant`/`Tenant`; named arguments and property accesses must be updated. Persisted shard and placement identifiers retain the historical `tenant/` prefix so the terminology cleanup does not rename saved progress.

Baseline root reads now use `PostgresRelationQueryReadPolicy.MaximumRowsPerRead` rather than a hidden limit of 1000. Configure that policy explicitly to retain the previous bound. CDC source binding performs scoped deployment inspection and retains no open connection; stream enumeration owns and disposes its native connection. Existing publications must cover every declared input table; initialization rejects missing coverage instead of silently omitting changes.

## Declaration, preparation and command ownership

`FulfillmentMaterializations : MaterializationModel` owns the example's materialization policies and calls
`Build().Prepare(impactPolicy)`. `PreparedMaterialization` retains the admitted
declaration and impact plan; PostgreSQL preparation consumes that same object.
The target and runtime share one declaration instance per activation.

`Cohesive.Application.Cli.AddMaterializationShadowCommands` owns initialize/rebuild/catch-up
command parsing, stable retry inputs, cancellation and convergence exit status. Help
does not initialize models or resources. `MaterializationShadowExecution` invokes
the existing rebuild and synchronization interpreters; it has no promotion step.
The example owns native client lifetimes, the PostgreSQL writer lease, resource
names, and explicit provider policy. These commands are bounded activations, not
a continuously running service. The namespace and project are now
`AspireFirst.Materialization` / `OrderMaterialization`; durable identities,
`Shadow:*` configuration, and live PostgreSQL endpoint bindings are unchanged.

### Model activation and runtime binding

`OrderMaterializationApplication` activates the live `FulfillmentApplication` and
`FulfillmentMaterializations` in one graph. Constructor dependencies resolve the
same `FulfillmentQueries` instance. `IAuthoringModel.Complete` freezes the ordered
materialization catalog before activation returns, without opening native clients.

The base class is optional. Direct consumers can call
`new MaterializationModelBuilder().Include(builder.Build().Prepare(impactPolicy))`
and `Build()` to freeze the catalog, or keep using an individual prepared declaration.
Duplicate semantic IDs are rejected; frozen catalogs cannot be mutated.

`FulfillmentMaterializationStorage : StorageModel` declares `OrderDetails` through
`Storage.Define` using `PostgresMaterializationStorage`. Its named options own
partition/read/rebuild policies, publication, stable slot naming, checkpoint
schema/authority, capacity evidence and provenance. The adapter derives source
tables, reader sets and rebuild composition. `FulfillmentMaterializations.OrderDetails`
is the prepared declaration; there are no model-wide Document/Impact forwarding properties.
The native binding returns Cohesive's `BoundMaterialization`, which borrows the
source/target/progress/work ports and owns no connections. The command host owns
disposal. `MaterializationShadowExecution.RunAsync` consumes this shared binding;
there is no application-specific runtime wrapper. Durable resource identities,
shadow-only execution, and live PostgreSQL reads are unchanged.

Source initialization is explicit through `model.Storage.OrderDetails.InitializeSourcesAsync`;
it derives publication tables from the prepared source mappings, rather than a
hand-maintained entity list. Binding derives database identity and authority from the declaration before native
source admission and performs no DDL. Elasticsearch template policy stays
native and the host supplies an admitted target; the PostgreSQL adapter does not
acquire an Elasticsearch dependency. Slot recipes are evaluated once at declaration,
and invalid or duplicate native names fail before IO. Existing publication, slot,
authority and durable progress identities are retained.

The source's `Reads.PartitionScope.CanonicalValue` also determines the physical
placement subject; there is no independent Partition option to drift. Database
identity and checkpoint authority are likewise supplied once by the declaration.
The public native binding seam is `PostgresMaterializationStorage.BindAsync(pool,
replicationFactory, target, key, slotGeneration, ct)`; relation-level reader binding
is internal. Native preflight still validates actual connection/replication affinity.
