# SQLite and POCO adoption proof for Ito

COH-89 proves an incremental route from an immutable CLR control record to canonical Transition
execution, atomic SQLite persistence, restart, and exact decision replay. It also exercises a
specialized three-table repository using the shared SQLite runtime. The proof owns no trading,
price-correction, historical-universe, or simulation semantics.

## Semantic authorities and adoption steps

1. Declare an ordinary immutable record. The executable
   [RunControl fixture](../../Cohesive.ExecutionKernel.TestFixtures/Storage/RunControlFixture.cs)
   has identity, tenant, status, attempt, eligibility, decimal limit, scheduled instant, and byte digest.
   `ObjectEntityDefinition.For<RunControl>` produces its canonical entity definition; there is no
   `Entity<T>` inheritance. Mutable byte arrays at the CLR boundary are copied into observations.
2. Author `TransitionAuthoring.Create<RunControl, StartRun, string>` with typed guards, updates, an event emission,
   and outcome. Materialize and retain its canonical document, the referenced event contract, and
   its Process caller document. Builders produce IR; persisted documents govern replay.
3. Create one `SqliteDatabase`, declare the scalar entity mapping, and explicitly apply the entity
   migration and the outbox's complete `Migrations`. Use stable application module names. See
   [runtime configuration](README.md) and [atomic outbox contracts](OUTBOX.md).
4. Before submitting the state commit, durably retain the **prior** snapshot/token, canonical input,
   exact operation occurrence and authority scope, definition references, and canonical decision.
   The fixture uses a bounded SQLite BLOB/hash store. This is an application-owned test journal,
   not a framework definition registry. Interrupted staging can leave unused proof records;
   successful state commits in this sequence always have their inputs staged first.
5. Evaluate with `TransitionReferenceInterpreter`, project the resulting state, and lower emission
   intents with `TransitionEmissionEnvelopeLowerer` against the exact event catalog. Stable emission
   IDs derive from the saved request and source node. Direct commits publish through the entity
   outbox; Process commits retain handoff evidence for the Process outbox.
6. On restart, load the retained documents through `ExecutionDefinitionJsonSerializer` and
   `ExecutionDefinitionDocumentCatalog`. Validate the exact ID, revision, fingerprint, and dependency
   links; do not choose the newest revision. Compile the saved Transition and validate its Process
   link, then re-evaluate with the saved prior state and input. Compare canonical patches, outcome,
   and emission intents against the saved decision, and reconstruct the candidate and stable envelopes.
7. Reconcile an ambiguous commit through its original identities. The tests commit, lose the caller
   acknowledgment, advance the entity independently, reopen repositories, and retry. The original
   snapshot/token is returned while current state remains advanced. The Process path publishes no
   entity-outbox entry; the direct path retains one entry. Decision replay only evaluates IR and
   builds evidence: it neither dispatches external effects nor updates current state.

The [lifecycle tests](../../Cohesive.Adapters.SQLite.Tests/SqlitePocoLifecycleReplayTests.cs) also reject
missing Transition, Process, or event documents, wrong revisions and fingerprints, tampered semantic
content, and changed prior state. A later authored revision coexists without changing the pinned
decision. Document content fingerprints are integrity and exact-version evidence, not signatures
or proof that a malicious party could not rewrite all retained evidence.

The fixture's dependency traversal is intentionally bounded to this flat Transition and its known
Process/event links. A production application needs its own retention and dependency staging policy;
this change does not introduce a universal registry, arbitrary dependency crawler, or SQLite Process engine.
The entity shape and physical mapping remain fixed in this fixture. Applications must retain the shape
contract needed to read saved observations and plan explicit mapping migrations when that contract changes;
the new-revision test proves pinned Transition selection, not arbitrary entity-schema migration.

## Capability-to-test matrix

The [shared assertions](../../Cohesive.Tests/Storage/Conformance/EntityRepositoryConformance.cs) consume
the same POCO, canonical state, Transition, event contract, and Process operation across providers.
SQLite links the assertion source into its dedicated assembly; the reusable fixture library has no
test-framework dependency. Provider setup owns physical schema and operating boundaries only.

| Invariant | SQLite | In-memory | PostgreSQL | Cosmos |
| --- | --- | --- | --- | --- |
| Exact flat scalar/shape/version/partition/token round trip, including bytes and UTC instant | Shared | Shared | Shared, live opt-in | Shared, explicit tagged storage profile |
| Stale and missing CAS targets leave state unchanged | Shared | Shared | Shared | Shared |
| Ordered `None` writes and retained prefix after stale later write | Shared | Shared | Shared; individual commits rotate `xmin` | Shared |
| Late-failure same-partition rollback | Shared | Shared | Shared | Shared |
| Cross-partition all-or-nothing | Shared | Shared | Shared | Explicit unsupported capability, no writes |
| Direct canonical envelope duplicate/conflict and stale first commit | Shared | Shared | No outbox implementation | Shared |
| Process original receipt survives later state; changed input conflicts | Shared | Shared | Explicit insufficient capability | Shared |
| Direct historical receipt survives later mutation | SQLite outbox tests | Not asserted by shared fixture | N/A | Not asserted by shared fixture |
| Concurrent duplicates, creation identity, late index failure, cancellation, bounded pages, corrupt evidence | SQLite outbox tests | Existing provider tests | Existing provider tests | Existing provider tests |
| Abrupt writer death and WAL recovery | SQLite child-process test | N/A | Provider recovery boundary | Provider recovery boundary |
| Persisted exact-definition decision replay after lost acknowledgment | SQLite lifecycle tests | Interpreter used as reference | N/A | N/A |
| Payload/publication/checkpoint rollback and reader snapshot visibility | SQLite publication fixture | N/A | N/A | N/A |

The shared scalar fixture stays inside PostgreSQL's UTC/microsecond timestamp domain. SQLite supports
its larger documented text-encoded scalar domain. Capabilities do not claim every provider has identical
limits or physical guarantees. SQLite remains one local database file, explicit FULL/NORMAL durability,
bounded lock retry, and cooperative cancellation around synchronous native operations. None of these
tests establish cross-file atomicity, external exactly-once effects, or a general database crash guarantee.

Cosmos's default SDK serializer rejects binary observation bodies and cannot preserve every detached
scalar kind. Its shared fixture explicitly configures `CosmosSystemTextJsonSerializer` with the
`EntityStorageJson` tagged profile, allowing Cosmos-added outer item metadata, for a **new storage-only
container**. Every reader of that container must use the same profile. This
changes physical observation field values to tagged nodes: existing Relation bindings expecting raw
scalar paths are incompatible. The default serializer's binary rejection has a separate executable
test. A query-compatible, shape-aware Cosmos encoding/capability contract remains follow-up work;
these tests do not claim that the default Cosmos profile supports the full fixture's scalar domain.
Cosmos setup also supplies a deterministic physical entity ID selector because semantic IDs may contain
slashes. Shared emission IDs use the Cosmos-supported character domain; the current Cosmos direct
outbox uses emission identities as physical item IDs. These are explicit target operating constraints.
The encoding/query compatibility and physical-ID policy follow-up is
[COH-93](https://linear.app/cohesive-ari/issue/COH-93/declare-lossless-cosmos-entity-encoding-and-query-compatibility).

## Specialized repository composition

[SqlitePublicationConformanceTests](../../Cohesive.Adapters.SQLite.Tests/SqlitePublicationConformanceTests.cs)
owns `payloads`, `publications`, and a version-fenced `checkpoints` table. All values and query identifiers
go through public shared SQL builders and `SqliteDatabase.CreateCommand`. The application borrows one
connection/transaction across the three writes. A duplicate publication or late checkpoint CAS failure
rolls back the payload as well. An independent reader cannot see uncommitted writes; an established WAL
read transaction keeps its old snapshot across writer commit until the reader begins a new snapshot.

Ito can use this runtime composition while keeping its existing market-data repository interface and
domain rules. It does not need to force payload blobs, publication artifacts, and checkpoints into
generic entity snapshots. Apply the outbox recipe separately to control entities where those semantics fit.

## Ito regression acceptance

The following existing Ito tests were inspected at
[`568dc1f`](https://github.com/cohesivesystems/ito/tree/568dc1f22023f5045d1d8ad187f7167007080d5d).
They remain required when Ito adopts the runtime; this Cohesive change does not modify the Ito application
or claim that its financial semantics have been rerun here.

- `SqliteEodMarketDataStoreTests`: corrections and A→B→A observations, gap suppression, no stale adjusted
  fallback for raw-only revisions, normalizer/universe pin coexistence, non-backdated publication clocks,
  ambiguous observation rejection, exact duplicate descriptor recovery, checkpoint CAS and late-artifact
  rollback, concurrent duplicate publishers, fresh-database races, and payload/index tamper detection.
- `EodReplayTests`: frozen canonical bytes, complete cursor/source coverage, exact golden observation
  coverage, normalizer and lineage mismatch diagnostics, no page migration, pinned retrieval-time cutoff,
  content mismatch, and deterministic duplicate reporting.
- `PointInTimeDataSnapshotTests`: exact snapshot pins, semantic digest authority, historical membership,
  mixed-cutoff/policy rejection, and the simulation gate rejecting a later snapshot before factor invocation.

Run from Ito when its adapter changes are ready:

```sh
dotnet test tests/Ito.Infrastructure.Tests/Ito.Infrastructure.Tests.csproj --filter FullyQualifiedName~SqliteEodMarketDataStoreTests
dotnet test tests/Ito.Core.Tests/Ito.Core.Tests.csproj --filter 'FullyQualifiedName~EodReplayTests|FullyQualifiedName~PointInTimeDataSnapshotTests'
```

## Reproduce Cohesive verification

From the Cohesive repository root:

```sh
dotnet test src/Cohesive.Adapters.SQLite.Tests/Cohesive.Adapters.SQLite.Tests.csproj -c Release -m:1 -nr:false -p:UseSharedCompilation=false
dotnet test src/Cohesive.Tests/Cohesive.Tests.csproj -c Release -m:1 -nr:false -p:UseSharedCompilation=false
```

Live sibling tests are skipped explicitly unless `COHESIVE_POSTGRES_TEST_CONNECTION_STRING` or
`COSMOS_ENTITY_TRANSITION_OPERATION_CONNECTION_STRING` is configured. Each test creates and removes
only a uniquely named schema/database it owns. Cosmos setup accepts self-signed certificates only
for loopback test endpoints; remote services require normal trust validation. PostgreSQL needs schema
creation rights; Cosmos needs database/container creation rights. With either or both variables set:

```sh
dotnet test src/Cohesive.Tests/Cohesive.Tests.csproj -c Release --filter FullyQualifiedName~RepositoryConformanceTests
```

Serialization and materialization benchmarks are in the existing BenchmarkDotNet project:

```sh
dotnet run -c Release --project src/Cohesive.Relations.Benchmarks -- --filter '*Adoption*Benchmarks*' --job short --warmupCount 1 --iterationCount 3 --launchCount 1 --artifacts /tmp/coh-89-bench
```

See [performance evidence](ADOPTION-PERFORMANCE.md) for the measured workload, allocations, and tradeoffs.

Validation on 2026-09-05: SQLite 119 passed; core 3,704 passed with 30 explicitly skipped service-dependent
tests; the configured conformance/receipt run passed 19 checks, including live PostgreSQL 17 and Cosmos
emulator coverage. `bash ./eng/api-check.sh` completed successfully. All ten focused benchmarks completed.
Ito's listed acceptance suites remain a consumer-adoption requirement and were not run by this change.
