# OrderDetails shadow materialization

The live HTTP endpoint still reads PostgreSQL. This optional command consumes the
same rooted `OrderDetailsRelation`, hydrates its nested result through
Cohesive.Relations, and maintains a separate Elasticsearch generation. It never
promotes an alias or changes endpoint bindings.

## Run locally

Enable `Shadow:Enabled` on the existing AppHost to add Elasticsearch and the
OrderShadow project. The latter defaults to `--help`. PostgreSQL starts with
logical replication enabled. This is a local development configuration, including
an Elasticsearch node without authentication; do not expose it publicly.

Alternatively run OrderShadow against dedicated local PostgreSQL (logical WAL
required) and Elasticsearch instances. Configure:

- `ConnectionStrings__orders`: the same native database used by the worker.
- `Shadow__Elasticsearch`: Elasticsearch HTTP endpoint.
- `Shadow__PositionKey`: a persistent base64 authentication key of at least 32 bytes.
- `Shadow__SlotGeneration`: an explicit replication-slot incarnation, retained on restart.
- `EntityState__Authority` and `EntityState__Reconstitution`: the worker's authority policy.

From the repository root:

```sh
dotnet run --project eng/examples/aspire-first/OrderShadow -- initialize
dotnet run --project eng/examples/aspire-first/OrderShadow -- rebuild \
  --attempt demo-1 --started 2026-10-26T00:00:00Z --invocation baseline-1
dotnet run --project eng/examples/aspire-first/OrderShadow -- catch-up \
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
