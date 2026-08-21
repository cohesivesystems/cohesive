# Real-container materialization harness

This harness is the local infrastructure boundary for ARI-399 and its materialization slices. It starts pinned PostgreSQL, Azure Cosmos DB emulator, and Elasticsearch containers together with pgAdmin, Cosmos Data Explorer, and Kibana. It projects one deterministic freight scenario journal into both source databases, executes one canonical Cohesive relation over either replica, and atomically promotes the equivalent results into provider-specific Elasticsearch generations.

The journal at `scenarios/freight-baseline.json` is the only seed-data authority. The .NET seed projection validates tenant-local references and cardinality before replacing the harness PostgreSQL schema and Cosmos database. The default seed path creates canonical entity states and sends them through `GenericRepositorySeedDataService`, `PostgresEntityRepository`, and `CosmosEntityOutboxRepository`. A separate direct path retains raw Npgsql and Cosmos SDK writes as an independent oracle. Elasticsearch starts empty after a fresh reset; `materialize` creates candidate generations and promotes their read aliases.

Common materialization conformance orchestration consumes an open catalog of explicit replica fixtures. The runner owns deterministic replica ordering, semantic-fingerprint fencing, and canonical document equality; it has no PostgreSQL/Cosmos switch. Each fixture owns its physical Relations dialect, source construction, capability preflight, and provider diagnostics. Elasticsearch remains an explicit materialization-target adapter, while raw source seeding and verification remain independent provider oracles. This follows the Cohesive.Storage semantic/adapter model without introducing a lowest-common-denominator datastore facade. Compose owns only local resource lifecycle and can later be replaced by a Cohesive.Infra interpretation without changing the conformance workflow.

## Prerequisites

- Docker with Compose support.
- .NET SDK 10.
- At least 4 GB of memory available to Docker; more is useful while the Cosmos emulator initializes.

## Commands

Run these from the repository root:

```bash
eng/materialization-harness/harness.sh up
eng/materialization-harness/harness.sh validate
eng/materialization-harness/harness.sh seed
eng/materialization-harness/harness.sh seed-direct
eng/materialization-harness/harness.sh verify
eng/materialization-harness/harness.sh mutate
eng/materialization-harness/harness.sh verify-final
eng/materialization-harness/harness.sh materialize
eng/materialization-harness/harness.sh process-start all
eng/materialization-harness/harness.sh host
eng/materialization-harness/harness.sh process-inspect postgres
eng/materialization-harness/harness.sh process-explain cosmos
eng/materialization-harness/harness.sh process-traces all
eng/materialization-harness/harness.sh process-pause postgres
eng/materialization-harness/harness.sh process-continue postgres
eng/materialization-harness/harness.sh process-restart cosmos
eng/materialization-harness/harness.sh process-cancel cosmos
eng/materialization-harness/harness.sh process-limits postgres 8
eng/materialization-harness/harness.sh verify-index
eng/materialization-harness/harness.sh test
eng/materialization-harness/harness.sh status
eng/materialization-harness/harness.sh logs
eng/materialization-harness/harness.sh down
eng/materialization-harness/harness.sh reset
```

`seed` uses Cohesive.Storage repositories and is the normal path. `seed-direct` performs the same baseline projection with raw provider clients, keeping seed verification independent from the repository implementation being tested. The direct Cosmos envelope timestamp is journal-derived; repository-managed persistence metadata remains adapter evidence rather than canonical freight state. `mutate` applies the journal's ordered incremental suffix to both real replicas, and `verify-final` checks their exact final entity state and mutation evidence without rewriting either source. Replaying `mutate` is an explicit idempotency check. `test` seeds, verifies, and materializes the direct path first, then replaces it with the repository path, repeats the same baseline checks, applies and replays the mutation suffix, and runs the focused compiler, inverse-impact, adapter, Process, repository, and persistence tests. `down` preserves database, checkpoint, and index volumes. After `down` and `up`, `verify` proves both source databases still match the journal's exact baseline logical state without rewriting them. `materialize` creates and promotes a new generation for each replica. `verify-index` is read-only and displays the active aliases and their document counts. `reset` is intentionally destructive: it removes only this Compose project's volumes, starts fresh services, and replays the canonical scenario baseline through Cohesive.Storage.

### Incremental scenario authority

The versioned journal declares a baseline cut and then a deterministic mutation suffix. The suffix covers direct root create/update/delete, shared customer and location updates, stop creation and deletion, stop reordering, location movement, a two-row atomic stop-type exchange, and an atomic contributor cleanup. Resolution produces exact before/after semantic images, monotonic entity versions, stable delivery identities, source-transaction groups, journal-derived occurrence times, and SHA-256 transition fingerprints before provider I/O begins.

PostgreSQL applies each source transaction and its evidence row in one database transaction. Its DML is compiled through the official adapter's shared injection-safe insert, update, delete, and select builders; only harness schema DDL remains explicit SQL. The freight entity tables remain the logical-replication publication authority; the separate `scenario_mutations` table is replay and verification evidence, not a second published change source. Cosmos applies each entity mutation and emulator-compatible change envelope in one transactional batch scoped to the entity container and tenant partition. A run interrupted between providers resumes safely because each provider independently recognizes an exact prior delivery, rejects a conflicting delivery identity, and rejects partial atomic transactions. Final verification compares both entity projections and all persisted scalar and before/after evidence with the resolved journal.

After a Process rebuild promotes a generation, the host continuously applies real provider changes to that active generation. It creates one change feed for every canonical acquisition input and tenant partition. PostgreSQL feeds use the official adapter's dedicated `pgoutput` slots over the published freight tables; the adapter retains the physical tenant column, filters before assigning logical-partition evidence, and settles WAL only after the synchronization checkpoint commits. Cosmos feeds page the explicitly seeded, tenant-partitioned scenario envelopes because the local emulator cannot provide the production full-fidelity feed contract. Both feeds enforce hard item and byte bounds and retain authenticated positions.

The feed catalog and inverse routes are compiled from the same canonical relation. A provider-neutral impact executor maps changed orders, customers, stops, and locations back to affected order roots, using official Relations readers for bounded inverse lookups. It deduplicates roots per source transaction, re-runs the canonical join for each affected order, and then upserts or deletes the active Elasticsearch entry. Checkpoint, settlement, and generation affinity make retries idempotent; no provider-specific freight projection is allowed to become a second semantic authority.

`process-start` exercises the canonical execution-control SDK dispatcher without starting an HTTP server. It accepts `postgres`, `cosmos`, or `all` (the default). `host` runs the same dispatcher behind the ASP.NET projection and drives admitted rebuilds in a background worker. The remaining `process-*` commands are direct SDK clients over the same durable PostgreSQL authority and accept the same optional provider selector. Run them while the foreground host is stopped; a running host owns the dedicated replication slots, so live control should use the equivalent HTTP routes on that host. Pause interrupts bounded work and is retained before further source I/O; Continue preserves the attempt, generation, and source continuation; RestartAttempt abandons the old candidate and creates a fresh attempt/generation; Cancel is terminal and abandons every non-active candidate. `process-limits` targets one provider because a limit update is bound to an exact control epoch.

Each provider compiles a canonical single-leaf rebuild plan set with two tenant shards, complete dependency-feed catalogs, exact provider source profiles, one Elastic target, and deterministic placement evidence. The host executes that plan set through its parent coordinator, leaf coordinator, shard worker, promotion worker, durable operation adapters, and storage-owned lifecycle. Initialization, bounded scans and joins, synchronization, readiness, promotion, finalization, limit updates, and retained traces are therefore accounted for by canonical Process checkpoints rather than a parallel harness lifecycle. Once both providers are active, the host compares logical documents after each complete synchronization cycle. A transient difference schedules another cycle; the same retained mismatch across two complete cycles fails loudly.

The shorter acceptance entrypoint is:

```bash
eng/test-materialization-harness.sh
```

## Parallel worktrees and ports

The command wrapper derives a Compose project name from the absolute worktree path, so named volumes and services do not collide. Host ports are fixed by default and can be overridden in `eng/materialization-harness/.env`; copy `.env.example` as a starting point. Override `COHESIVE_HARNESS_PROJECT_NAME` when a stable external name is required.

Default endpoints:

| Service | Endpoint |
| --- | --- |
| PostgreSQL | `localhost:55432`, database `cohesive_materialization` |
| pgAdmin | `http://localhost:55050/` |
| Cosmos gateway | `https://localhost:58081/` |
| Cosmos readiness | `http://localhost:58080/ready` |
| Cosmos Data Explorer | `http://localhost:58082/` |
| Elasticsearch | `http://localhost:59200` |
| Kibana | `http://localhost:55601/` |
| Process/API host | `http://localhost:59399/` |

Use `harness.sh env` to inspect the effective project identity and endpoints without displaying connection secrets.

### Browser UI access

`harness.sh up` waits for all three browser UIs to become healthy. Cosmos Data Explorer is attached directly to the local emulator. Kibana is preconfigured for the harness Elasticsearch node. pgAdmin is preconfigured with a server named `Cohesive materialization harness` whose connection settings are derived from the same Postgres environment variables used by the database container.

The default pgAdmin login is `harness@cohesivesystems.com` with password `cohesive-local-only`. Both values are local-only defaults and can be changed through `.env`. The database password is acquired by the preconfigured server at runtime and is not copied into pgAdmin's persisted configuration database.

Kibana's Dev Tools console can spot-check the materialized data with:

```http
GET /_cat/aliases/freight-order-search-*?v
GET /_cat/indices/cohesive-freight-*?v
GET /freight-order-search-postgres/_search?pretty
GET /freight-order-search-cosmos/_search?pretty
```

The stable read aliases are `freight-order-search-postgres` and `freight-order-search-cosmos`. Generation index names are content-derived and are printed by `materialize`; callers should read through the aliases.

### Process and API surface

The host persists canonical Process checkpoints, materialization page progress, synchronization work, routing state, and index-sync Control state in PostgreSQL. Postgres and Cosmos Process graphs use independent durability authorities. Within each authority, every Process instance has an independently fenced root and shares immutable content-addressed evidence pages, so one provider's parent/child traffic does not rewrite or lock a single authority-wide document. The adapter reconstructs and verifies the exact canonical Process aggregate before interpretation; no harness-specific aggregate-size exemption is required, while every physical page remains bounded. Routing authority rows are additionally namespaced by the canonical backend-pool definition fingerprint, preventing a revised semantic model from reusing incompatible routing state. Each tenant/provider progress key retains the exact physical source scope, relation read fingerprint, generation, worker fence, batch page ordinal, and opaque continuation. A crash after an Elasticsearch batch but before its progress checkpoint is safe because the stable batch identity replays before the checkpoint advances. A host restart reconstructs execution bindings from the durable parent operation boundary that logically allocated each child attempt and uses a new physical worker-incarnation identity so an orphaned in-flight claim cannot be mistaken for work still owned by the restarted host. The local profile's one-minute worker lease bounds aggregate ownership recovery without making lease maintenance dominate the test workload. An explicit RestartAttempt allocates a new attempt-derived generation and abandons the prior candidate through the lifecycle.

The HTTP projection uses only canonical Cohesive.Api.Execution contracts:

| Method | Route | Purpose |
| --- | --- | --- |
| `POST` | `/execution-control/processes/start` | Admit a stable Process attempt. |
| `GET` | `/execution-control/processes/{processInstanceId}` | Inspect durable status. |
| `GET` | `/execution-control/processes/{processInstanceId}/explain` | Project canonical definition, status, and evidence. |
| `GET` | `/execution-control/processes/{processInstanceId}/traces` | Read retained canonical traces or an explicit availability result. |
| `POST` | `/execution-control/processes/pause` | Pause at a page boundary. |
| `POST` | `/execution-control/processes/continue` | Continue the current attempt. |
| `POST` | `/execution-control/processes/restart-attempt` | Replace the attempt and abandon its candidate. |
| `POST` | `/execution-control/processes/cancel` | Cancel terminally and abandon its candidate. |
| `POST` | `/execution-control/processes/update-limits` | Update a bound index-sync Control epoch. |

The local host intentionally exposes two fixed Process instances—`process/materialization-harness/freight-rebuild/postgres` and `process/materialization-harness/freight-rebuild/cosmos`—under one trusted local authority scope. `COHESIVE_MATERIALIZATION_PROCESS_INSTANCE_ID` overrides their common prefix. The SDK commands construct optimistic revision/attempt expectations from the retained checkpoint. HTTP callers supply the canonical command request; the host derives authorization and invocation evidence server-side. `harness.sh env` prints the effective host URL and other endpoints.

Set `COHESIVE_MATERIALIZATION_PAGE_DELAY_MS` to a non-negative value up to `60000` when the six-order fixture completes too quickly for manual pause or crash testing. The delay uses the canonical executor's boundary-observation hook after bounded materialization operations and honors the active operation's cancellation token. It defaults to zero.

## Pinned service capabilities

- PostgreSQL `17.10-alpine3.24` starts with `wal_level=logical`, twenty replication slots, twenty WAL senders, and a one-second sender keepalive timeout so a quiet local feed can prove its global WAL cut within the bounded read policy.
- pgAdmin `9.17` persists its UI metadata independently of the Postgres data volume and reloads its declarative server definition on startup.
- Cosmos emulator `vnext-EN20260810` runs in HTTPS gateway mode with its built-in HTTP Data Explorer and uses its documented readiness endpoint. The local .NET seeder accepts the emulator certificate only for loopback requests.
- Elasticsearch `8.19.13` matches the adapter client's minor line and runs as an unauthenticated single node bound only to loopback.
- Kibana `8.19.13` matches the Elasticsearch node exactly and runs without external telemetry or authentication for this loopback-only harness.

The vNext emulator proves local NoSQL gateway behavior but reports an Eventual account consistency level and does not support the production full-fidelity change-feed/continuous-backup contract. Rebuild reads still use the real Cosmos relation reader. Incremental reads use a harness-only interpretation of the deterministic scenario envelopes written transactionally beside each entity mutation; those envelopes preserve before images, source-transaction boundaries, and journal time while exercising the same retained-change and settlement contracts as a production adapter. This is not a claim that the emulator itself supplies full-fidelity change-feed semantics. The production Cosmos interpretation remains bound to the stricter provider capability contract.

## Seeded freight surface

Each provider receives separate tenant-partitioned Orders, CustomerAccounts, OrderStops, and Locations. The relation model's inferred source shapes are projected once into canonical entity definitions; both repository seed realizations consume those definitions rather than maintaining a second field schema. PostgreSQL uses composite tenant/entity keys, an explicit semantic observation-version column, and `xmin` only as its opaque concurrency token. Cosmos stores canonical Cohesive observation envelopes partitioned by `/partitionKey`. Physical schema/container creation remains an explicit harness lifecycle step rather than a hidden repository side effect. The baseline contains two tenants, shared customers, shared locations, six orders, and enough stops to cross the harness's two-item paging and lookup boundaries. Its order IDs are globally unique because the current one-output-per-root materialization contract deliberately uses the root identity as the stable index item identity; tenant partition evidence still fences every source and join read.

The canonical relation joins each order to its customer, traverses the inverse `Order -> OrderStop` relationship, selects the first pickup and last drop by `(orderId, sequence, id)`, and then joins each selected stop to its location before projecting an `OrderSearchDocument`. Orders retain no precomputed stop or endpoint-location identities. Provider-specific code supplies only physical placements, selectors, and readers; the compiled relation and materialization definition fingerprint remain identical.

Some location reads are conservatively over-acquired because their traversals follow filter/order/distinct semantics. This is an explicit bounded physical-plan choice (`REL2113`), not a second endpoint-selection implementation: every candidate remains tenant-fenced and subject to fan-out and row limits, while the canonical interpreter alone decides which ordered stop is logically reachable. The canonical fixture's behavioral tests cover stop reorder, location move, and selected-stop deletion.

For every provider, materialization:

1. compiles and links a canonical one-target rebuild plan set with one shard per tenant;
2. creates an isolated generation;
3. reads each tenant in deterministic pages;
4. executes the canonical relation to hydrate customer, ordered-stop, and location contributors;
5. bulk-upserts the projected documents;
6. seals and validates the expected document count;
7. promotes the candidate through a fenced alias update; and
8. reads both aliases back and rejects any canonical difference.

After promotion, active-generation maintenance pages each tenant/feed position, computes inverse impact, rehydrates affected roots through the same relation, flushes bounded mutations to Elasticsearch, commits synchronization evidence, and only then settles provider progress. A fresh `materialize` run against the final source state is the differential oracle for the incremental result.

When run through the Process host, each applied page also commits a PostgreSQL progress checkpoint. A stopped host therefore resumes from the last durable continuation. An exact retry uses the same attempt-derived generation and idempotency identities; an explicit RestartAttempt uses a new generation and leaves durable abandonment evidence that prevents a delayed old worker from promoting its candidate.
