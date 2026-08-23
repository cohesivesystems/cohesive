# Real-container materialization harness

This harness is the local infrastructure boundary for ARI-399 and its materialization slices. It starts pinned PostgreSQL, Azure Cosmos DB emulator, and Elasticsearch containers together with pgAdmin, Cosmos Data Explorer, and Kibana. It projects one deterministic freight scenario journal into both source databases, executes one canonical Cohesive relation over either replica, and atomically promotes the equivalent results into provider-specific Elasticsearch generations.

The journal at `scenarios/freight-baseline.json` is the only seed-data authority. The .NET seed projection validates tenant-local references, aggregate ownership, and cardinality before replacing the harness PostgreSQL schema and Cosmos database. The default seed path sends independent Customer and Location states through `GenericRepositorySeedDataService` for both providers and sends the whole Order aggregate through `CosmosEntityOutboxRepository`. PostgreSQL currently projects the Order root and its owned stop rows through one explicit adapter-backed transaction because the scalar entity repository does not yet expose an aggregate writer. A separate direct path retains raw Npgsql and Cosmos SDK writes as an independent oracle. Elasticsearch starts empty after a fresh reset; `materialize` creates candidate generations and promotes their read aliases.

Common materialization conformance orchestration consumes an open catalog of explicit replica fixtures. The runner owns deterministic replica ordering, semantic-fingerprint fencing, and canonical document equality; it has no PostgreSQL/Cosmos switch. Each fixture owns its physical Relations dialect, source construction, capability preflight, and provider diagnostics. Elasticsearch remains an explicit materialization-target adapter, while raw source seeding and verification remain independent provider oracles. This follows the Cohesive.Storage semantic/adapter model without introducing a lowest-common-denominator datastore facade. `FreightMaterializationInfrastructure` is the canonical Cohesive.Infra authority for the local service graph, environment policies, effective settings, health/readiness, and harness operations. `Cohesive.Adapters.DockerCompose` and `Cohesive.Adapters.Aspire` independently project that same exact local realization; generated Compose and the fingerprinted Aspire projection are derived artifacts rather than parallel topology authorities. The prior handwritten Compose file remains only a temporary parity oracle.

## Prerequisites

- Docker with Compose support.
- .NET SDK 10.
- The .NET 10 `dnx` command; the wrapper acquires the pinned Aspire CLI 13.5.2 package on first use.
- At least 4 GB of memory available to Docker; more is useful while the Cosmos emulator initializes.

## Commands

Run these from the repository root:

```bash
eng/materialization-harness/harness.sh up
eng/materialization-harness/harness.sh infra-check
eng/materialization-harness/harness.sh infra-generate
eng/materialization-harness/harness.sh infra-parity
eng/materialization-harness/harness.sh aspire-up interactive
eng/materialization-harness/harness.sh aspire-status
eng/materialization-harness/harness.sh aspire-seed
eng/materialization-harness/harness.sh aspire-verify
eng/materialization-harness/harness.sh aspire-materialize
eng/materialization-harness/harness.sh aspire-logs
eng/materialization-harness/harness.sh aspire-stop
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
eng/materialization-harness/harness.sh process-evidence postgres
eng/materialization-harness/harness.sh failure-test postgres AfterTargetBatch
eng/materialization-harness/harness.sh control-equivalence-test postgres
eng/materialization-harness/harness.sh source-matrix-test all
eng/materialization-harness/harness.sh elastic-failure-test postgres
eng/materialization-harness/harness.sh compatibility-drift-test all
eng/materialization-harness/harness.sh matrix-test
eng/materialization-harness/harness.sh verify-index
eng/materialization-harness/harness.sh test
eng/materialization-harness/harness.sh status
eng/materialization-harness/harness.sh logs
eng/materialization-harness/harness.sh down
eng/materialization-harness/harness.sh reset
```

`infra-generate` refreshes the checked-in default YAML and manifest. `infra-check` proves those bytes are current and then runs Docker Compose's semantic parity comparison against the handwritten oracle; health command implementations are allowed to differ, while their canonical endpoints, expected statuses, timings, and readiness semantics are covered by compiler tests. `infra-parity` runs only that independent comparison. Ordinary lifecycle commands compile an ignored `.runtime/compose.yaml` and manifest from a fixed whitelist of exported harness settings, preserving `.env` overrides without making ambient configuration part of canonical Infra IR. Secret values are never configuration candidates or manifest content.

`aspire-up` starts the same canonical topology through the pinned Aspire CLI and writes the exact fingerprinted projection to `.runtime/aspire.manifest.json`. `interactive` uses stable named volumes and `aspire-stop` is non-destructive. `isolated` uses anonymous volumes and the canonical 30-minute maximum lifetime; use worktree-specific port overrides when multiple fixed-port environments run concurrently. Aspire owns container lifecycle, the dashboard, resource logs, OpenTelemetry, endpoint observation, and health display. `aspire-status` displays live resource identities, endpoints, readiness, and dashboard links. `aspire-seed`, `aspire-materialize`, and `aspire-verify` execute canonical host operations through the `materialization-workflow` resource command surface, which is available to both the dashboard and CLI/API clients. Environment mutations remain lifecycle-controlled and are not launched as nested Compose commands.

Stable Aspire 13.5.2 APIs represent HTTP probes and `WaitFor` dependencies directly. PostgreSQL's canonical `pg_isready` command probe uses an exact, source-referenced TCP readiness override because stable Aspire has no command-probe API. Canonical health timing remains inspectable in the projection and is declared as constrained because stable Aspire does not expose per-resource polling cadence. DCP uses an ephemeral self-signed TLS identity so headless AppHost startup never exports host private-key material; the resource service and dashboard remain HTTPS-only. These differences appear under `decisions` in the runtime manifest; they are not hidden conventions.

`seed` uses Cohesive.Storage repositories and is the normal path. `seed-direct` performs the same baseline projection with raw provider clients, keeping seed verification independent from the repository implementation being tested. The direct Cosmos envelope timestamp is journal-derived; repository-managed persistence metadata remains adapter evidence rather than canonical freight state. `mutate` applies the journal's ordered incremental suffix to both real replicas, and `verify-final` checks their exact final entity state and mutation evidence without rewriting either source. Replaying `mutate` is an explicit idempotency check. `test` seeds, verifies, and materializes the direct path first, then replaces it with the repository path, repeats the same baseline checks, applies and replays the mutation suffix, and runs the focused compiler, inverse-impact, adapter, Process, repository, and persistence tests. `down` preserves database, checkpoint, and index volumes. After `down` and `up`, `verify` proves both source databases still match the journal's exact baseline logical state without rewriting them. `materialize` creates and promotes a new generation for each replica. `verify-index` is read-only and displays the active aliases and their document counts. `reset` is intentionally destructive: it removes only this Compose project's volumes, starts fresh services, and replays the canonical scenario baseline through Cohesive.Storage.

### Incremental scenario authority

The versioned journal declares a baseline cut and then a deterministic mutation suffix. The suffix covers direct root create/update/delete, shared customer and location updates, stop creation and deletion, stop reordering, location movement, a two-operation atomic stop-type exchange, and an atomic contributor cleanup. Authored stop operations resolve into one whole owning Order transition per source transaction, so component lifetime, versioning, and atomicity are aggregate-owned in every provider. Resolution produces exact before/after semantic images, monotonic aggregate/entity versions, stable delivery identities, source-transaction groups, journal-derived occurrence times, and SHA-256 transition fingerprints before provider I/O begins.

PostgreSQL applies each source transaction and its evidence row in one database transaction; an Order mutation replaces the canonical root and its owned `order_stops` rows atomically. Its DML is compiled through the official adapter's shared injection-safe insert, update, delete, and select builders; only harness schema DDL remains explicit SQL. The freight entity tables remain the logical-replication publication authority; the separate `scenario_mutations` table is replay and verification evidence, not a second published change source. Cosmos applies each whole entity or Order-aggregate mutation and emulator-compatible change envelope in one transactional batch scoped to the entity container and tenant partition. A run interrupted between providers resumes safely because each provider independently recognizes an exact prior delivery, rejects a conflicting delivery identity, and rejects partial atomic transactions. Final verification compares both aggregate/entity projections and all persisted scalar and before/after evidence with the resolved journal.

After a Process rebuild promotes a generation, the host continuously applies real provider changes to that active generation. It creates one change feed for every canonical acquisition input and tenant partition. PostgreSQL feeds use the official adapter's dedicated `pgoutput` slots over the published freight tables; the adapter retains the physical tenant column, filters before assigning logical-partition evidence, and settles WAL only after the synchronization checkpoint commits. Cosmos feeds page the explicitly seeded, tenant-partitioned scenario envelopes because the local emulator cannot provide the production full-fidelity feed contract. Both feeds enforce hard item and byte bounds and retain authenticated positions.

The feed catalog and impact routes are compiled from the same canonical relation. A provider-neutral impact executor handles direct Order changes, maps Customer changes through a bounded inverse lookup, and maps Location changes through an exact owned-stop occurrence predicate. It deduplicates roots per source transaction, re-runs the canonical join for each affected Order, and then upserts or deletes the active Elasticsearch entry. Checkpoint, settlement, and generation affinity make retries idempotent; no provider-specific freight projection is allowed to become a second semantic authority.

`process-start` exercises the canonical execution-control SDK dispatcher without starting an HTTP server. It accepts `postgres`, `cosmos`, or `all` (the default). `host` runs the same dispatcher behind the ASP.NET projection and drives admitted rebuilds in a background worker. The remaining `process-*` commands are direct SDK clients over the same durable PostgreSQL authority and accept the same optional provider selector. Run them while the foreground host is stopped; a running host owns the dedicated replication slots, so live control should use the equivalent HTTP routes on that host. Pause interrupts bounded work and is retained before further source I/O; Continue preserves the attempt, generation, and source continuation; RestartAttempt abandons the old candidate and creates a fresh attempt/generation; Cancel is terminal and abandons every non-active candidate. `process-limits` targets one provider because a limit update is bound to an exact control epoch.

`failure-test` is the destructive, one-command black-box recovery acceptance entry point. It removes only this Compose project's volumes, starts and seeds the real services, builds the host and supervisor, then runs two isolated provider Process instances. The first host is killed after the selected canonical lifecycle boundary and must resume the same attempt-derived generation. The second is killed at the same cut, receives an SDK RestartAttempt while stopped, and must complete through a fresh generation while the interrupted candidate is retired and never routed for reads. The default cell uses PostgreSQL at `AfterTargetBatch`; a provider and any `MaterializationExecutionBoundaryPoint` may be supplied explicitly.

`control-equivalence-test` is the destructive differential acceptance entry point for execution control. It clean-resets and seeds the stack, runs start, pause, inspect, explain, traces, Continue, limit update, RestartAttempt, and Cancel through the direct SDK dispatcher, then repeats the same scenario through the real ASP.NET host. The HTTP client first asks the host to project each canonical command from retained state and posts that body verbatim, so it carries no parallel Process-command construction logic. The supervisor compares dispositions, revision movement, attempt/generation relationships, exact Control-epoch binding, terminal state, and target routing after removing only transport-local identities. Every request, response, result, host log, and divergence report is capped at 256 KiB and represented in the artifact manifest.

`source-matrix-test` is the destructive real-source acceptance entry point for incremental replay, ordering, and generation fencing. For each requested provider it establishes an active baseline, applies the canonical mutation suffix, kills the host immediately after an Elasticsearch target batch, and verifies that exact pending source page and item version remain durable. A conflicting opaque cursor under the same preparation identity must fail closed with `IdentityConflict` and `RestartAttempt` guidance without changing the retained work. A replacement host must replay the exact target request in the same generation, commit the coupled checkpoint once, and converge to a fresh catalog-complete receipt. The final probes submit an old worker fence and a lower item version; Elasticsearch must return `StaleFence` and `VersionConflict` while the logical alias documents remain unchanged. `all` runs PostgreSQL and Cosmos from separate clean resets and byte-compares their final canonical documents. Convergence is defined by the canonical logical receipt and exact documents, not equality between provider-opaque head tokens whose physical values may advance independently.

`elastic-failure-test` is the destructive real-target acceptance entry point for Elasticsearch rejection and ambiguous-promotion recovery. It clean-resets the stack for each of three cells. The retryable cell applies one real bulk subset, returns an attributable 429 for the unresolved subset, and proves that only those rejected item identities are retried in the same generation. The permanent cell applies all but one item, returns an attributable mapping failure for the remaining item, and proves that the Process requires `RestartAttempt` while the incomplete physical generation is never attached to the read alias. The ambiguous-promotion cell applies the real alias transaction and loses its successful response; the target must reconcile the resulting alias state and complete exactly one published generation. All cells use the real Elastic adapter and SDK transport, retain bounded wire-identity evidence, and fail if an incomplete generation becomes readable.

`compatibility-drift-test` establishes real retained Process, progress, synchronization-work, source, and Elasticsearch authorities, then submits incompatible plan fingerprint, physical source binding, cursor schema, generation, and cursor-value evidence. Every cell must return its catalogued `NotFound` or `IdentityConflict` disposition with `RestartAttempt` guidance while canonical revisions, fences, Process evidence, target state, aliases, and documents remain unchanged. PostgreSQL and Cosmos use the same provider-neutral probe catalog.

`matrix-test` is the complete destructive acceptance entry point. It obtains source providers and Elasticsearch failure cells from the shared matrix catalog, runs the source, target, and compatibility slices, and aggregates their evidence in deterministic cell-identity order. Every retained artifact hash is validated before writing one bounded top-level `manifest.json`. Cell manifests distinguish success, success-with-recovery, and expected-failure outcomes; missing, duplicate, incomplete, or hash-divergent cells prevent the aggregate manifest from being published.

The source-boundary armed host receives an attributable fault plan through environment variables, writes one atomic JSON marker into a run-specific artifact directory, and blocks without committing additional lifecycle state. The external supervisor verifies that marker against the host PID before killing the actual process tree. Replacement hosts start without the fault plan and reconstruct only from canonical Process, progress, settlement, target, and provider authorities. The Elasticsearch failure plan is likewise observation evidence at the real SDK transport boundary; the target remains the sole lifecycle authority.

Each run writes under `eng/materialization-harness/artifacts/`, which is ignored by Git. Individual host logs, SDK results, HTTP status/explain/trace responses, progress and settlement summaries, target generation state, and Elasticsearch alias/index responses are capped at 256 KiB. `manifest.json` records observed and retained sizes, truncation, and SHA-256 hashes. `process-evidence` exposes the same bounded Process/checkpoint/target projection for manual diagnosis without dumping canonical durable aggregates.

Each provider compiles a canonical single-leaf rebuild plan set with two tenant shards, complete dependency-feed catalogs, exact provider source profiles, one Elastic target, and deterministic placement evidence. The host executes that plan set through its parent coordinator, leaf coordinator, shard worker, promotion worker, durable operation adapters, and storage-owned lifecycle. Initialization, bounded scans and joins, synchronization, readiness, promotion, finalization, limit updates, and retained traces are therefore accounted for by canonical Process checkpoints rather than a parallel harness lifecycle. Once both providers are active, the host compares logical documents after each complete synchronization cycle. A transient difference schedules another cycle; the same retained mismatch across two complete cycles fails loudly.

The shorter acceptance entrypoint is:

```bash
eng/test-materialization-harness.sh
```

## Parallel worktrees and ports

The command wrapper derives a Compose project name from the absolute worktree path, so named volumes and services do not collide. Host ports are fixed by default and can be overridden in `eng/materialization-harness/.env`; copy `.env.example` as a starting point. Override `COHESIVE_HARNESS_PROJECT_NAME` when a stable external name is required. Each command projects those effective values into the ignored runtime artifact before invoking Docker, so the generated topology and the connection strings consumed by the .NET tools cannot drift.

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
| `GET` | `/materialization-harness/providers/{provider}/control-requests/{operation}` | Project the next canonical command body and route for a black-box client. |

The local host intentionally exposes two fixed Process instances—`process/materialization-harness/freight-rebuild/postgres` and `process/materialization-harness/freight-rebuild/cosmos`—under one trusted local authority scope. `COHESIVE_MATERIALIZATION_PROCESS_INSTANCE_ID` overrides their common prefix. The SDK commands and request-projection endpoint construct optimistic revision/attempt expectations from the retained checkpoint through one factory. HTTP callers may post that canonical projected request; the host still replaces client authority, issuance, and provenance with trusted invocation evidence. `harness.sh env` prints the effective host URL and other endpoints.

Set `COHESIVE_MATERIALIZATION_PAGE_DELAY_MS` to a non-negative value up to `60000` when the seven-order fixture completes too quickly for manual pause or crash testing. The delay uses the canonical executor's boundary-observation hook after bounded materialization operations and honors the active operation's cancellation token. It defaults to zero.

The black-box supervisor sets `COHESIVE_MATERIALIZATION_FAULT_PROVIDER`, `COHESIVE_MATERIALIZATION_FAULT_BOUNDARY`, `COHESIVE_MATERIALIZATION_FAULT_OCCURRENCE`, `COHESIVE_MATERIALIZATION_FAULT_RUN_ID`, and an absolute `COHESIVE_MATERIALIZATION_FAULT_MARKER_PATH` for the armed host only. Optional scope and operation selectors further narrow a cell. Partial fault configuration fails host startup. `COHESIVE_MATERIALIZATION_SUPERVISOR_TIMEOUT_SECONDS` defaults to 900 seconds and may be set from 1 through 1,800 seconds; this includes durable worker-lease expiry and provider catch-up time after the exact host cut.

## Pinned service capabilities

- PostgreSQL `17.10-alpine3.24` starts with `wal_level=logical`, twenty replication slots, twenty WAL senders, and a one-second sender keepalive timeout so a quiet local feed can prove its global WAL cut within the bounded read policy.
- pgAdmin `9.17` persists its UI metadata independently of the Postgres data volume and reloads its declarative server definition on startup.
- Cosmos emulator `vnext-EN20260810` runs in HTTPS gateway mode with its built-in HTTP Data Explorer and uses its documented readiness endpoint. The local .NET seeder accepts the emulator certificate only for loopback requests.
- Elasticsearch `8.19.13` matches the adapter client's minor line and runs as an unauthenticated single node bound only to loopback.
- Kibana `8.19.13` matches the Elasticsearch node exactly and runs without external telemetry or authentication for this loopback-only harness.

The vNext emulator proves local NoSQL gateway behavior but reports an Eventual account consistency level and does not support the production full-fidelity change-feed/continuous-backup contract. Rebuild reads still use the real Cosmos relation reader. Incremental reads use a harness-only interpretation of the deterministic scenario envelopes written transactionally beside each entity mutation; those envelopes preserve before images, source-transaction boundaries, and journal time while exercising the same retained-change and settlement contracts as a production adapter. This is not a claim that the emulator itself supplies full-fidelity change-feed semantics. The production Cosmos interpretation remains bound to the stricter provider capability contract.

## Seeded freight surface

Each provider receives tenant-partitioned Orders, CustomerAccounts, and Locations from the same canonical model. `Order.Stops` is one ordered owned collection: PostgreSQL decomposes it into the `order_stops` component table, while Cosmos embeds it inside the Order observation document and has no separate stop container. Both official storage-realization compilers retain those physical differences as inspectable interpretations of the same canonical `freight/order` structure. Repository entity-state validation binds the graph-qualified Order root to that same immutable shape-graph document and resolves the named stop component directly; it does not maintain an inline copy of the stop fields. PostgreSQL uses composite tenant/entity keys, an explicit semantic observation-version column, and `xmin` only as its opaque concurrency token. Cosmos stores canonical Cohesive observation envelopes partitioned by `/partitionKey`. Physical schema/container creation remains an explicit harness lifecycle step rather than a hidden repository side effect. The baseline contains two tenants, shared customers, shared locations, seven orders, and enough owned stops to cross the harness's two-item paging and lookup boundaries. Its order IDs are globally unique because the current one-output-per-root materialization contract deliberately uses the root identity as the stable index item identity; tenant partition evidence still fences every source and join read.

The canonical relation joins each Order to its Customer, expands the same owned `Order.Stops` collection for pickup and delivery branches, selects the first pickup and last drop by `(orderId, sequence, id)`, and explicitly joins each selected component to a bounded Location source before projecting an `OrderSearchDocument`. Orders retain no precomputed stop or endpoint-location identities. PostgreSQL reconstructs the owned array from root-correlated component rows; Cosmos reads the embedded JSON array. Provider-specific code supplies only physical placements, selectors, readers, and storage bindings; the compiled relation and materialization definition fingerprint remain identical.

Collection expansion now preserves an exact occurrence identity for every ordered stop. The physical executor uses that occurrence to issue bounded Location lookups, while the incremental impact plan inverts the same lineage into an owned-collection predicate: PostgreSQL emits a root-correlated `EXISTS` against `order_stops`, and Cosmos emits an `EXISTS` subquery over the embedded `Stops` array. A Location change therefore identifies only Orders whose stop occurrences reference that Location; the harness does not permit global invalidation. The canonical fixture's behavioral tests cover stop reorder, location move, selected-stop deletion, storage-realization equivalence, and fail-closed partial occurrence evidence.

For every provider, materialization:

1. compiles and links a canonical one-target rebuild plan set with one shard per tenant;
2. creates an isolated generation;
3. reads each tenant in deterministic pages;
4. executes the canonical relation to hydrate Customer and Location contributors while expanding the root-owned stops in memory;
5. bulk-upserts the projected documents;
6. seals and validates the expected document count;
7. promotes the candidate through a fenced alias update; and
8. reads both aliases back and rejects any canonical difference.

After promotion, active-generation maintenance pages each tenant/feed position, computes affected roots through direct, inverse, or bounded-global impact strategies, rehydrates them through the same relation, flushes bounded mutations to Elasticsearch, commits synchronization evidence, and only then settles provider progress. A fresh `materialize` run against the final source state is the differential oracle for the incremental result.

Direct Order changes also retain an explicit current-state strategy in the rebuild plan. Cosmos scenario envelopes carry
the complete embedded Order observation and therefore select `DeliveredChangeImage`. PostgreSQL logical replication
emits the root table row while owned stops live in `order_stops`, so the harness registers the official Relations
identity-read capability and selects `BatchedIdentityRead`. The shared Storage executor deduplicates changed Order
identities, reads complete aggregates in bounded batches, preserves the raw WAL delivery identity, position,
before-image, and source ordering, and exposes the result as `ReconciledLatest` rather than claiming a coordinated WAL
snapshot. The harness wrapper retains only PostgreSQL lifecycle/error translation and capability binding; it no longer
implements its own reconciliation algorithm.

When run through the Process host, each applied page also commits a PostgreSQL progress checkpoint. A stopped host therefore resumes from the last durable continuation. An exact retry uses the same attempt-derived generation and idempotency identities; an explicit RestartAttempt uses a new generation and leaves durable abandonment evidence that prevents a delayed old worker from promoting its candidate.
