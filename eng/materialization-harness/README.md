# Real-container materialization harness

This harness is the local infrastructure boundary for ARI-399 and its materialization slices. It starts pinned PostgreSQL, Azure Cosmos DB emulator, and Elasticsearch containers together with pgAdmin, Cosmos Data Explorer, and Kibana. It projects one deterministic freight scenario journal into both source databases, executes one canonical Cohesive relation over either replica, and atomically promotes the equivalent results into provider-specific Elasticsearch generations.

The journal at `scenarios/freight-baseline.json` is the only seed-data authority. The .NET seed projection validates tenant-local references and cardinality before replacing the harness PostgreSQL schema and Cosmos database. Elasticsearch starts empty after a fresh reset; `materialize` creates candidate generations and promotes their read aliases.

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
eng/materialization-harness/harness.sh verify
eng/materialization-harness/harness.sh materialize
eng/materialization-harness/harness.sh verify-index
eng/materialization-harness/harness.sh test
eng/materialization-harness/harness.sh status
eng/materialization-harness/harness.sh logs
eng/materialization-harness/harness.sh down
eng/materialization-harness/harness.sh reset
```

`down` preserves database, checkpoint, and index volumes. After `down` and `up`, `verify` proves both source databases still match the journal without rewriting them. `materialize` creates and promotes a new generation for each replica. `verify-index` is read-only and displays the active aliases and their document counts. `reset` is intentionally destructive: it removes only this Compose project's volumes, starts fresh services, and replays the canonical scenario journal.

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

## Pinned service capabilities

- PostgreSQL `17.10-alpine3.24` starts with `wal_level=logical`, ten replication slots, and ten WAL senders.
- pgAdmin `9.17` persists its UI metadata independently of the Postgres data volume and reloads its declarative server definition on startup.
- Cosmos emulator `vnext-EN20260810` runs in HTTPS gateway mode with its built-in HTTP Data Explorer and uses its documented readiness endpoint. The local .NET seeder accepts the emulator certificate only for loopback requests.
- Elasticsearch `8.19.13` matches the adapter client's minor line and runs as an unauthenticated single node bound only to loopback.
- Kibana `8.19.13` matches the Elasticsearch node exactly and runs without external telemetry or authentication for this loopback-only harness.

The vNext emulator proves local NoSQL gateway behavior but reports an Eventual account consistency level and does not support the production full-fidelity change-feed/continuous-backup contract. The Cosmos rebuild therefore uses the real Cosmos relation reader plus Cohesive's deterministic in-memory reconciliation pager. It explicitly does not claim a coordinated snapshot or baseline-plus-catch-up. Production incremental indexing remains bound to the stricter `CosmosMaterializationSource` capability contract.

## Seeded freight surface

Each provider receives separate tenant-partitioned Orders, CustomerAccounts, OrderStops, and Locations. PostgreSQL uses composite tenant/entity keys, and Cosmos stores canonical Cohesive observation envelopes partitioned by `/partitionKey`. The baseline contains two tenants, shared customers, shared locations, six orders, and enough stops to cross the harness's two-item paging and lookup boundaries.

The seed projection derives each order's pickup/delivery stop and endpoint location identities from the ordered stop sequence once, then writes those same values to both replicas. The canonical relation joins each order to its customer and both endpoint locations and projects an `OrderSearchDocument`. Provider-specific code supplies only physical placements, field selectors, and readers; the compiled relation and materialization definition fingerprint remain identical.

The current physical lowerer cannot yet branch from a stop traversal back to the order root for a second endpoint traversal. Retaining the derived endpoint identities on the order keeps that limitation visible without introducing provider-specific relation semantics. A later relation-planning slice can move the endpoint selection itself into the canonical query once branching traversals are supported.

For every provider, materialization:

1. creates an isolated generation;
2. reads each tenant in deterministic pages;
3. executes the canonical relation to hydrate customer and location contributors;
4. bulk-upserts the projected documents;
5. seals and validates the expected document count;
6. promotes the candidate through a fenced alias update; and
7. reads both aliases back and rejects any canonical difference.
