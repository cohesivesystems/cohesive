# Real-container materialization harness

This harness is the local infrastructure boundary for ARI-399. It starts pinned PostgreSQL, Azure Cosmos DB emulator, and Elasticsearch containers, then projects one deterministic freight scenario journal into both source databases.

The journal at `scenarios/freight-baseline.json` is the only seed-data authority. The .NET seed projection validates tenant-local references and cardinality before replacing the harness PostgreSQL schema and Cosmos database. Elasticsearch starts empty because candidate generations are outputs of materialization, not seed data.

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
eng/materialization-harness/harness.sh test
eng/materialization-harness/harness.sh status
eng/materialization-harness/harness.sh logs
eng/materialization-harness/harness.sh down
eng/materialization-harness/harness.sh reset
```

`down` preserves database, checkpoint, and index volumes. After `down` and `up`, `verify` proves both source databases still match the journal without rewriting them. `reset` is intentionally destructive: it removes only this Compose project's volumes, starts fresh services, and replays the canonical scenario journal.

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
| Cosmos gateway | `https://localhost:58081/` |
| Cosmos readiness | `http://localhost:58080/ready` |
| Elasticsearch | `http://localhost:59200` |

Use `harness.sh env` to inspect the effective project identity and endpoints without displaying connection secrets.

## Pinned service capabilities

- PostgreSQL `17.10-alpine3.24` starts with `wal_level=logical`, ten replication slots, and ten WAL senders.
- Cosmos emulator `vnext-EN20260810` runs in HTTPS gateway mode and uses its documented readiness endpoint. The local .NET seeder accepts the emulator certificate only for loopback requests.
- Elasticsearch `8.19.13` matches the adapter client's minor line and runs as an unauthenticated single node bound only to loopback.

The emulator proves local NoSQL gateway behavior. It does not establish production equivalence for unsupported full-fidelity change-feed or continuous-backup capabilities; those remain explicit adapter capability differences.

## Seeded freight surface

Each provider receives separate tenant-partitioned Orders, CustomerAccounts, OrderStops, and Locations. PostgreSQL uses composite tenant/entity keys, and Cosmos uses `/tenantId` as every container's partition key. The baseline contains two tenants, shared customers, shared locations, six orders, and enough stops to cross the harness's intended two-item paging and lookup boundaries.

The materialized `OrderSearchDocument` and Elasticsearch generation lifecycle are intentionally not implemented by the seed program. They belong to ARI-402 and must consume the same journal and canonical relation rather than introducing provider-specific derivation logic.
