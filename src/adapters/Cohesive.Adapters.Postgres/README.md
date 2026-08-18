# Cohesive.Adapters.Postgres

`Cohesive.Adapters.Postgres` is the single PostgreSQL adapter package. It provides injection-safe standalone SQL
construction, canonical Relations compilation, exact persistable storage bindings, and Npgsql-backed bounded
Relations, rebuild, reconciliation, transaction-aligned logical-replication sources, and a durable competing-consumer
ledger for `Cohesive.Processes.Distribution`. The builder can be used
without Cohesive.Relations query compilation; the storage binding remains the shared physical authority for
compilation and runtime source execution.

## Process distribution ledger

`PostgresProcessDistributionStore` is the first durable reference realization of the portable
`IProcessDistributionStore` contract. It persists one complete, versioned distribution ledger per authority row and
performs each placement or lifecycle decision under a serializable transaction, row lock, provider clock, and revision
compare-and-swap. Work execution occurs outside the transaction, so multiple processes can compete for claims and run
them concurrently without a singleton coordinator.

Create and validate `PostgresProcessDistributionStoreOptions`, construct the store with a caller-owned
`NpgsqlDataSource`, and run `EnsureCreatedAsync` explicitly during deployment or bootstrap. Ordinary distribution
operations never execute DDL. See the
[`Cohesive.Processes.Distribution` guide](../../Cohesive.Processes.Distribution/README.md) for the authority boundary,
worker configuration, recovery guarantees, target profiles, observability, and current atomic-composition limitation.

For convention-first C# authoring, begin with the
[`Cohesive.Relations` quick start](https://github.com/cohesivesystems/cohesive/blob/main/src/Cohesive.Relations/docs/GETTING_STARTED.md).
The focused
[PostgreSQL native join versus Cosmos composed reads](https://github.com/cohesivesystems/cohesive/blob/main/src/Cohesive.Relations/docs/EXECUTION_AND_ADAPTERS.md#postgresql-native-join-versus-cosmos-composed-reads)
example authors one Load-to-Customer query, then shows why co-located PostgreSQL tables compile to one inline
`LEFT JOIN` while separately stored Cosmos documents require bounded composed acquisition.

## Standalone SQL construction

Identifiers are always quoted, and values become positional parameters. Runtime parameters can be rebound without
rebuilding the SQL tree.

```csharp
using Cohesive.Adapters.Postgres;

var template = new PostgresSqlSelectBuilder(
        new PostgresSqlQualifiedTable("transport", "loads"),
        "l")
    .Select(PostgresSqlExpression.Column("l", "id"), "id")
    .Where(PostgresSqlExpression.Binary(
        PostgresSqlBinaryOperator.Equal,
        PostgresSqlExpression.Column("l", "status"),
        PostgresSqlExpression.RuntimeParameter("status")))
    .OrderBy(PostgresSqlExpression.Column("l", "id"))
    .Limit(100)
    .BuildTemplate();

var statement = template.Bind(new Dictionary<string, object?>
{
    ["status"] = "Open"
});

// statement.Text:
// SELECT "l"."id" AS "id" FROM "transport"."loads" AS "l"
// WHERE ("l"."status" = $1) ORDER BY "l"."id" ASC NULLS LAST LIMIT 100
// statement.Parameters[0].Value: "Open"
```

`PostgresSqlSelectBuilder` also composes derived-table joins, aggregate `FILTER` clauses, explicit null placement,
offset paging, and null-aware structural keyset predicates. `PostgresSqlInsertBuilder` supports parameterized inserts
and `ON CONFLICT DO UPDATE` from `EXCLUDED` values, while `PostgresSqlUpdateBuilder` requires at least one predicate so
an unrestricted update cannot be produced accidentally. Both mutation builders use the same safe identifiers,
expression tree, deterministic positional parameters, and immutable command templates as the select builder.

Captured constants remain portable when a compiled artifact is serialized, and runtime bindings accept the same
closed provider-neutral CLR domain. The supported values are `null`, `bool`, `int`, `long`, `decimal`, `string`,
`Guid`, finite `DateOnly`, finite microsecond-aligned `DateTime` with `DateTimeKind.Unspecified`, finite UTC
microsecond-aligned `DateTimeOffset`, and `byte[]`. Other CLR types are rejected instead of being serialized with
ambiguous provider-specific behavior. Runtime parameter values are not persisted; callers supply them to `Bind`.

The canonical v2 target profile intentionally advertises only the expression closure for which it has exact lowering
evidence: comparisons, Boolean logic, conditionals, ordinal prefix/suffix/substring search, and the documented
aggregates. General arithmetic remains fail-closed even though the standalone SQL builder can express arithmetic;
checked numeric-domain evidence is required before the relation compiler can claim canonical overflow and rounding
semantics. Whole-row distinctness is supported within exact physical equality domains, while keyed representative-row
selection and interval-overlap joins are not advertised by the native-SQL target profile. The separate
`PostgresRelationQuerySourceTargetProfile` declares only the six primitive acquisition facilities implemented by the
Npgsql reader, so physical source planning cannot inherit SQL joins, aggregation, or snapshot guarantees.

## Exact storage-binding authoring

Binding authoring begins from an exact `RelationQueryAuthoredPlacement`. Each acquired placed input must map to one
table. Typed selectors and structural `FieldPath` selectors produce the same canonical artifact.

```csharp
var result = PostgresRelationQueryBinding.For(authoredPlacement)
    .Database(new PostgresRelationQueryDatabaseId("operations-primary"))
    .Table(
        placedLoads,
        "loads",
        table => table
            .Schema("transport")
            .ColumnsExplicitly()
            .Column(load => load.Id, "load_id")
            .Column(load => load.Status, "load_status")
            .Identity(load => load.Id, "load_id"))
    .Build();

if (!result.IsSuccess)
{
    foreach (var diagnostic in result.Diagnostics)
        Console.Error.WriteLine($"{diagnostic.Code}: {diagnostic.Message}");
}

var binding = result.RequireValue();
```

Use `ColumnsBySemanticPath()` (the default) when top-level semantic field names match column names. Use
`ColumnsExplicitly()` when every demanded field and relationship reference must be named. A successful binding retains
configuration provenance, exact compiled-plan and placement affinity, and a deterministic content fingerprint. Serialize
it with `RelationQueryJsonSerializer.CreateOptions()`; rehydration verifies the persisted fingerprint.

The binding also persists the fixed
`cohesive.adapters.postgres.sql/database-semantics/utf8-standard-identifiers/v1` profile. The canonical PostgreSQL compiler therefore
requires a UTF-8 database and the standard 63-byte identifier limit; authoring rejects identifiers that cannot be
represented exactly within that profile. This assumption is included in the binding fingerprint and is inspectable
through `DatabaseSemanticsProfile`.

Text equality and ordering evidence are independent. PostgreSQL UTF-8 `COLLATE "C"` preserves canonical ordinal
equality, but its byte ordering is not CLR UTF-16 ordinal ordering over unrestricted Unicode. To claim ordinal ordering,
provide `PostgresRelationQueryTextOrderingDomainEvidence` naming a trusted check constraint and authority that restrict
the column to seven-bit ASCII. The same persisted domain is enforced for constants and runtime cursors participating in
that ordering; otherwise native compilation or binding fails closed.

Temporal joins over persisted intervals require explicit validity evidence. `ValidInterval` attests that a named,
trusted, validated PostgreSQL check constraint guarantees `lower <= upper` whenever both endpoints are bounded:

```csharp
.Table(
    placedLoadVersions,
    "load_versions",
    table => table
        .Column(version => version.ValidFrom, "valid_from")
        .Column(version => version.ValidTo, "valid_to")
        .ValidInterval(
            version => version.ValidFrom,
            version => version.ValidTo,
            "ck_load_versions_valid_interval",
            lowerNullBehavior: TemporalNullBoundBehavior.Invalid,
            upperNullBehavior: TemporalNullBoundBehavior.Unbounded))
```

This declaration records evidence; it does not create or inspect the database constraint. The exact endpoint paths,
null behavior, and constraint name participate in persistence, configuration provenance, and fingerprinting. Compilation
fails closed with `TemporalJoinUnsupported` when matching evidence is absent or incompatible.

Every demanded PostgreSQL `date`, `timestamp`, or `timestamptz` column also requires persisted
`PostgresRelationQueryTemporalDomainEvidence`. That evidence attests finite canonical CLR-range values and, for
timestamps, microsecond alignment; it is required for ordinary reads as well as temporal joins. Numeric `SUM` and
`AVG` similarly require explicit finite decimal-domain evidence, with `AVG` proving both intermediate range and
rounding behavior.

Before any Npgsql operation in a process that will use temporal acquisition, disable Npgsql's infinity conversions:

```csharp
AppContext.SetSwitch("Npgsql.DisableDateTimeInfinityConversions", true);
```

Npgsql snapshots this switch during provider initialization. The caller must also select
`PostgresNpgsqlTemporalSemantics.InfinityConversionsDisabledBeforeInitialization` in the source policy as explicit
startup evidence. Registration checks that declaration and the current switch, but cannot retroactively prove when an
application initialized Npgsql. The default policy therefore rejects temporal source acquisition. This preserves
finite, microsecond-aligned CLR endpoints as ordinary values and prevents PostgreSQL `infinity` from being conflated
with them.

## Npgsql-backed source acquisition

`PostgresRelationQuerySourceReader` is registered from the full `CompiledRelationQueryPlan`, its exact
`CompiledRelationQueryPhysicalPlan`, a source identity resolved from that plan, storage binding, and caller-owned
`NpgsqlDataSource`. Registration proves that the semantic reference retained by the physical plan matches the supplied
full plan before using its shape snapshots to validate identity semantics. The reader interprets the same exact
physical-plan fingerprint, stage, placement, table, column, scalar-domain, missing/null, and identity evidence used by
the compiler. It implements the canonical `IRelationQuerySourceReader` contract for:

- bounded table enumeration ordered by the bound unique identity;
- identity point reads and batches; and
- parameterized relationship-key predicate batches.

Each logical request becomes one set-oriented, parameterized PostgreSQL statement. Key batches use one typed array
predicate rather than one command per key, so relationship acquisition does not introduce N+1 I/O. Requests name
canonical semantic selectors; the exact storage binding independently resolves those selectors to physical column
names. The reader selects only the requested semantic and correlation fields, validates every request against its
compiled stage and placement affinity, and returns complete, partial, not-found, failed, or inconclusive canonical
evidence. Caller cancellation is propagated; expected provider failures are retained as sanitized evidence rather than
exposing SQL text or values.

Relationship batches expand the typed key array and use one bounded `LATERAL` probe per key inside that single
statement. Both the per-key probe and the global result window are limited, so fan-out evidence does not require an
unbounded partition count before the adapter can return `Inconclusive`.

`PostgresRelationQuerySourcePolicy` places explicit hard bounds on keys per batch, canonical UTF-8 bytes per key, rows
retained per read, page items, and bytes. The byte bound applies both to the provider result retained by one Npgsql
command and to the canonical materialization page. Npgsql executes with sequential access; fixed scalars use
cancellation-aware async reads, while text and `bytea` are streamed through the same cumulative budget before they are
retained. Canonical source-placement limits additionally bound buffering, fan-out, batching, and planner-visible
concurrency. Invalid registration or page bounds are rejected, oversized keys fail before I/O, oversized batch/fan-out
work becomes inconclusive, and a bounded enumeration that discovers a probe row beyond its declared read boundary
returns `Partial` evidence. The adapter does not silently split one canonical read into per-row work or widen its
operating envelope.

The reader borrows a caller-owned, thread-safe, single-host `NpgsqlDataSource`, which must outlive the reader. It never
disposes the data source; each call creates and disposes its own command and data reader. Public registration also
requires a `PostgresNpgsqlRuntimeBinding`: an explicit authority maps the persisted database identity to that exact
data-source instance and a sanitized configuration fingerprint. Passing a different instance or database attestation
fails before I/O. Multi-host data sources and ambient transactions are rejected so replica choice or hidden
transaction state cannot become unattributed consistency evidence. Reader diagnostics and materialization capability
evidence retain the runtime authority and sanitized data-source fingerprint after registration. A runtime that also
serves logical replication supplies an explicit factory for fresh `LogicalReplicationConnection` instances; see
[Logical replication](#logical-replication) below.

```csharp
var runtime = new PostgresNpgsqlRuntimeBinding(
    storage.Database,
    dataSource,
    "operations/deployment/postgres-primary");
var reader = new PostgresRelationQuerySourceReader(
    plan,
    physicalPlan,
    postgresSourceId,
    storage,
    dataSource,
    runtime,
    sourcePolicy);
```

## Rebuild and reconciliation materialization source

`PostgresMaterializationSource` wraps one reader and one exact PostgreSQL table placement as an
`IMaterializationSource`. Its exact physical stage exposes enumeration or point/predicate pages, and every instance
exposes an opaque durable keyset continuation. Paging v2 requires a UUID identity or an ordinal-text identity with
exact ordering evidence, plus at least 32 bytes of caller-managed secret key material. Continuations are canonical,
HMAC-SHA-256 authenticated, and rejected before decoding when they exceed the versioned size bound. Identity and
fan-out state are bounded by the source policy and the exact relationship-key batch. Both item and canonical
encoded-byte requests are checked against capability evidence before I/O and enforced on each returned page; an
indivisible item larger than the byte limit is rejected explicitly. Its capability profile is derived from that exact
physical stage: source-set enumeration, forward-traversal point reads, or inverse-traversal predicate reads are
advertised only when executable; continuation is always present.

Every page runs as a new PostgreSQL statement snapshot. The source therefore advertises stable identity ordering,
request-local completeness, and reconciliation, but it does **not** claim one coordinated MVCC snapshot across pages.
A continuation retains the exclusive identity boundary, exact binding/read affinity, and cumulative
per-correlation-key emitted counts used to enforce fan-out bounds across resumed statements, not a database snapshot.
A caller may persist that opaque continuation across pause/resume and must supply the same authentication key after a
restart while it remains valid. Deliberate key rotation invalidates previously issued continuations. This paged source
does not itself deliver or settle changes, and the package does not yet provide a PostgreSQL materialization write
target. Incremental delivery is instead supplied by the logical-replication source described below.

```csharp
// Resolve this from an application secret store and retain it while issued continuations remain resumable.
ReadOnlySpan<byte> continuationKey = continuationKeyMaterial;
var rebuildSource = new PostgresMaterializationSource(
    reader,
    sourcePlacement,
    continuationKey);
```

## Logical replication

`PostgresLogicalReplicationMaterializationChangeSource` implements the backend-neutral pull-change and explicit
settlement contracts over PostgreSQL's built-in `pgoutput` protocol. It composes the same exact Relations reader,
physical placement, and storage binding used for rebuild reads, so physical column selectors, canonical scalar
decoding, identity evidence, and materialization scope have one authority. Npgsql remains an adapter implementation
detail; durable positions and change pages expose only `Cohesive.Storage.Materialization` contracts.

### PostgreSQL provisioning

The PostgreSQL server must enable logical decoding with `wal_level = logical` and have sufficient
`max_replication_slots` and `max_wal_senders` capacity. These settings require server-level configuration and may
require a restart. The runtime role must be allowed to connect to the bound database, read the published table and
catalog evidence used during validation, and start logical replication; grant the PostgreSQL `REPLICATION` role
attribute where required by the deployment. Keep host-based authentication scoped to that role, database, and
network rather than copying a broad development rule into production.

Provision one publication and one permanent `pgoutput` slot dedicated exclusively to the exact materialization
source placement. At preflight, the v1 adapter requires the publication to include the bound table with all columns,
`INSERT`, `UPDATE`, and `DELETE` enabled, `TRUNCATE` disabled, no row filter, and
`publish_via_partition_root` disabled. A publication may contain other tables; the adapter advances through their WAL
without projecting them as changes for this placement. The slot must exist, be inactive, be non-temporary, use
`pgoutput`, and not enable two-phase decoding. A representative full-before-image setup is:

```sql
ALTER TABLE "transport"."loads" REPLICA IDENTITY FULL;

CREATE PUBLICATION "cohesive_loads_publication"
    FOR TABLE "transport"."loads"
    WITH (publish = 'insert, update, delete');

SELECT *
FROM pg_create_logical_replication_slot(
    'cohesive_loads_slot',
    'pgoutput');
```

Use `REPLICA IDENTITY DEFAULT` when the bound primary key supplies the required mutation identity, or
`REPLICA IDENTITY USING INDEX ...` with an explicitly bound qualifying unique index. Select
`PostgresLogicalReplicationBeforeImageRequirement.Required` only with `REPLICA IDENTITY FULL`; PostgreSQL's key-only
replica identities cannot prove a complete prior row. Publication column lists must retain every replica-identity and
projected column required by the exact storage binding; the v1 full-column preflight rejects a partial column list.
The v1 adapter also requires `FULL` when a projected text, numeric, or `bytea` value may be represented by pgoutput as
an unchanged TOAST marker: key-only identity cannot reconstruct that complete canonical after image. Fixed-width
projections may use `DEFAULT` or `USING INDEX` without claiming a complete before image.
Row filters, partition-root publication, and two-phase decoding require a future explicit capability rather than an
unattributed relaxation of this binding.

Replication slots retain WAL independently of application health. Configure a finite operational retention policy,
monitor `pg_replication_slots`, and alert on retained bytes, inactivity, invalidation, and remaining safe WAL. Dropping
or recreating the slot invalidates its prior durable positions even when its name is reused. PostgreSQL does not expose
a durable slot-incarnation identity, so `PostgresLogicalReplicationBinding.SlotGeneration` is an operator-owned,
non-secret identity that must rotate whenever the physical slot is recreated.

Useful upstream references are PostgreSQL's
[logical-replication publication and replica-identity documentation](https://www.postgresql.org/docs/current/logical-replication-publication.html),
[replication settings](https://www.postgresql.org/docs/current/runtime-config-replication.html), and Npgsql's
[logical-replication guide](https://www.npgsql.org/doc/replication.html).

### Runtime binding

`NpgsqlDataSource` cannot create replication-protocol connections. Register the ordinary data source and an explicit
factory that returns a fresh, unopened `LogicalReplicationConnection` for every operation:

```csharp
using Npgsql;
using Npgsql.Replication;

await using var dataSource = NpgsqlDataSource.Create(connectionString);
var runtime = new PostgresNpgsqlRuntimeBinding(
    database: storage.Database,
    dataSource: dataSource,
    authority: "operations/deployment/postgres-primary",
    logicalReplicationConnectionFactory: () =>
        new LogicalReplicationConnection(connectionString));
```

The runtime binding verifies that both paths name the same single host, port, database, user, TLS, and other
non-secret connection settings. It normalizes only the pooling, enlistment, multiplexing, and keepalive values that
Npgsql necessarily changes for replication connections, and rejects a factory that returns the same connection
object twice. Password, certificate, and authentication callbacks configured on `NpgsqlDataSource` are not inherited
by `LogicalReplicationConnection`; configure equivalent behavior explicitly in the factory without placing secrets in
the runtime authority or adapter evidence.

Bind that runtime to the canonical reader, publication, dedicated slot, operator-owned slot generation, expected
replica identity, and the same caller-managed position-authentication key after every restart:

```csharp
var logicalBinding = new PostgresLogicalReplicationBinding(
    publicationName: "cohesive_loads_publication",
    slotName: "cohesive_loads_slot",
    slotGeneration: "operations/loads-slot@generation-3",
    expectedReplicaIdentity: new(
        kind: PostgresLogicalReplicationReplicaIdentityKind.Full),
    beforeImageRequirement: PostgresLogicalReplicationBeforeImageRequirement.Required);

var changeSource = await PostgresLogicalReplicationMaterializationChangeSource.CreateAsync(
    reader: reader,
    placement: sourcePlacement,
    runtimeBinding: runtime,
    binding: logicalBinding,
    positionAuthenticationKey: positionAuthenticationKey,
    policy: PostgresLogicalReplicationSourcePolicy.Default);
```

Creation inspects the live publication, table, replica identity, slot, output plugin, and server identity before it
advertises capabilities. Configuration drift fails closed instead of silently changing the meaning or coverage of a
materialization feed. The position key must contain at least 32 bytes, remain available for every still-resumable
position, and come from an application secret store. Rotating it deliberately invalidates positions authenticated by
the prior key. `CreateAsync` does not create, replace, or drop the publication or slot.

### Exported-snapshot baseline handoff

For an initial rebuild that must close the gap between a bounded baseline and logical replication,
`PostgresLogicalReplicationBaselineHandoff.CreateAsync` creates the configured permanent slot at a PostgreSQL
consistent point, imports its exported snapshot as the first command of one `REPEATABLE READ` transaction, and
returns three aligned values:

- the handoff itself, which is an `IMaterializationSource` whose baseline pages all use that imported snapshot;
- `ChangeStartPosition`, the exact exclusive WAL cut paired with the snapshot; and
- `ChangeSource`, the retained change source that reads commits after that cut.

The slot must not already exist. Read every baseline page through the handoff instance (or its descriptor's wrapped
Relations reader) while the handoff remains alive, durably checkpoint the completed baseline and
`ChangeStartPosition`, then dispose the handoff to end the snapshot transaction. Disposal never drops or settles the
permanent slot and does not dispose `ChangeSource`.

```csharp
await using var handoff = await PostgresLogicalReplicationBaselineHandoff.CreateAsync(
    context: operationContext,
    reader: reader,
    placement: sourcePlacement,
    runtimeBinding: runtime,
    binding: logicalBinding,
    positionAuthenticationKey: positionAuthenticationKey,
    policy: PostgresLogicalReplicationSourcePolicy.Default);

// Enumerate handoff.ReadPageAsync(...) to completion and durably checkpoint the baseline.
var catchUpAfter = handoff.ChangeStartPosition;
var incrementalSource = handoff.ChangeSource;
```

Creating a permanent slot is an external durable mutation. The handoff deliberately does not retry an indeterminate
slot-creation result and does not remove a slot during failure cleanup. If creation fails after PostgreSQL may have
created the slot, inspect the named slot and either adopt or remove it before retrying. Rotate `SlotGeneration` whenever
the physical slot is recreated, even when its name is unchanged. This bootstrap path is for a new slot; the ordinary
`PostgresMaterializationSource` remains available for reconciliation rebuilds that do not require one MVCC snapshot.

### Delivery, durability, and settlement semantics

The source captures opaque, authenticated WAL positions and returns complete committed PostgreSQL transactions in
source order. It never splits one transaction between pages. `MaterializationChangeReadRequest` item and byte values
are therefore preferred page budgets: the final admitted transaction may cross either budget, but no later
transaction is admitted. `MaximumTransactionChanges` and `MaximumTransactionBytes` are separate hard safety limits;
exceeding either fails the read without advancing application progress or provider settlement. Reads also bound the
number of transactions, reconnect attempts, inactivity, and encoded position size.

Use `CaptureCurrentPositionAsync` to establish an exclusive "start after the currently visible WAL" boundary. Use
`CaptureRetainedStartPositionAsync` only when a recovery or bootstrap plan intentionally wants the existing slot's
earliest safely replayable confirmed boundary. This is not the raw `restart_lsn`, which may precede the logical slot's
replay contract. Neither call reads changes, extends retention, changes the slot, or creates application progress.
Both positions fail closed when the bound server, database, publication, slot, operator-owned slot generation,
physical plan, placement, or authentication key no longer matches.

An empty page may still advance `ThroughPosition` when the source scanned irrelevant WAL. Persist that exact boundary
just as carefully as a page containing deliveries. A caught-up result is bounded to the WAL end captured for that
operation; it is not a promise that no later transaction can arrive. Stable change and delivery identities make
at-least-once retries attributable, while the canonical before/after observations retain the exact Relations shape,
identity, and field selectors.

Reading does not acknowledge WAL. The owning Process must apply target effects, durably save an application checkpoint
covering the page's exact position and delivery identities, and only then call `IMaterializationSettlingSource.SettleAsync`
with that checkpoint evidence. Settlement advances and confirms the dedicated slot position; replaying the same
settlement identity is idempotent, while reusing it for different evidence is rejected. Use
`PostgresLogicalReplicationMaterializationChangeSource.CreateSettlementId` for the conventional deterministic
identity derived from the already-durable checkpoint and exact authenticated position. The required order remains:

```text
apply effects -> commit application checkpoint -> settle PostgreSQL slot -> record settlement receipt
```

A crash before settlement may redeliver an already-applied transaction. A crash after settlement cannot expose
uncommitted target work because the API requires the durable checkpoint evidence first. Pause/continue retains the
same source position, slot generation, and index generation. If the slot is lost, invalidated, recreated, or no longer
retains the requested WAL, the source returns a typed terminal recovery classification rather than guessing a new
starting point. A rebuild or operator-directed recovery must establish a new baseline and rotate the appropriate
generation identities.

`InspectHealthAsync` polls the exact slot and returns provider-neutral health for the source scope.
`IPostgresLogicalReplicationObserver` additionally receives typed operation and slot-health observations without
putting provider objects into core contracts. Observer implementations must be fast, thread-safe, and non-throwing.
Use the health state, retained/pending/safe WAL estimates, inactivity, operation disposition, and stable failure
classifications to drive alerts and Cohesive.Control policy; do not parse human-readable exception text as
operational state.

## End-to-end relation compilation

The following example authors a `Load -> Customer -> LoadSearchDto` relation with C# expressions, compiles it to the
canonical relation plan, realizes and places that plan for PostgreSQL, and produces a native SQL artifact. The `Load`
is supplied by the caller, while `Customer` is acquired from PostgreSQL.

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using Cohesive.Adapters.Postgres;
using Cohesive.Model;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.IR;
using Cohesive.Relations.Model;
using Cohesive.Relations.Physical;
using Cohesive.Relations.Realization;

// Author the semantic relation. Traverse infers Load.CustomerId -> Customer.Id.
var author = RelationQuery.Expression();
var loadShape = author.Clr.Shape<Load>();
var customerShape = author.Clr.Shape<Customer>();
var loads = author.Source(loadShape);
var customers = author.Traverse<Load, Customer>(loads, load => load.CustomerId);
var documents = author.Project(
    customers,
    (Load load, Customer customer) => new LoadSearchDto
    {
        Id = load.Id,
        CustomerId = load.CustomerId,
        CustomerName = customer.Name,
        CustomerType = customer.Type
    });
var relation = documents.BuildRelation((LoadSearchDto document) => document.Id);

if (!relation.Validation.IsValid)
    throw new InvalidOperationException(string.Join(Environment.NewLine, relation.Validation.Diagnostics));

// Compile expressions into the canonical, backend-independent plan.
var staticCompilation = RelationQueryStaticCompiler.Compile(new(
    relation.CreateDocument(),
    author.ShapeDocuments,
    author.CreateRelationshipCatalogDocument()));
if (!staticCompilation.IsSuccessful || staticCompilation.Plan is not CompiledRelationQueryPlan plan)
    throw new InvalidOperationException(string.Join(Environment.NewLine, staticCompilation.Diagnostics));

// Check family-level feasibility before selecting exact PostgreSQL storage facts.
var profileFeasibility = RelationQueryRealizationCompiler.Compile(
    plan,
    PostgresRelationQueryTargetProfile.Default,
    PostgresRelationQueryTargetProfile.Policy,
    RelationQueryResultObservability.NotRequested);
if (!profileFeasibility.IsRealizable)
    throw new InvalidOperationException(string.Join(Environment.NewLine, profileFeasibility.Diagnostics));

// Place the supplied root and acquired traversal in one PostgreSQL execution domain.
var placementAuthor = RelationQueryPlacement.For(plan);
var executionDomain = new RelationQueryExecutionDomainId("operations-primary");
var suppliedSource = placementAuthor.Source(
    "application/supplied-load",
    PostgresRelationQuerySourceTargetProfile.Default,
    executionDomain);
var customerSource = placementAuthor.Source(
    "postgres/customers",
    PostgresRelationQuerySourceTargetProfile.Default,
    executionDomain);
var placedLoad = placementAuthor
    .Place(plan.InputContract.Sources.Single(), suppliedSource, loadShape)
    .FieldsBySemanticPath();
var placedCustomer = placementAuthor
    .Place(plan.InputContract.Traversals.Single(), customerSource, customerShape)
    .Identity(customer => customer.Id)
    .FieldsBySemanticPath();
var placement = placementAuthor.Build().RequireValue();
var loadInput = placement.GetInput(placedLoad);
var customerInput = placement.GetInput(placedCustomer);

// Conventions map Customer.Type to column "type". Only the exceptional physical names are overridden.
var ordinalText = new PostgresRelationQueryTextSemantics(
    "C",
    PostgresRelationQueryTextEqualitySemantics.Ordinal);
var textOptions = new PostgresRelationQueryColumnOptions(
    scalarType: PostgresRelationQueryScalarType.Text,
    textSemantics: ordinalText);
var storage = PostgresRelationQueryBinding.For(
        placement,
        explicitAuthority: "application/postgres-binding/v1")
    .Database(new PostgresRelationQueryDatabaseId("operations-primary"))
    .Table(
        customerInput,
        "customers",
        table => table
            .Schema("transport")
            .Column(customer => customer.Name, "customer_name", textOptions)
            .Identity(customer => customer.Id, "customer_id", textOptions))
    .Build()
    .RequireValue();

// Qualify family-level feasibility against the exact placement and storage evidence first.
var compiler = new PostgresRelationQueryCompiler();
var contextualRequest = new RelationQueryBoundRealizationRequest(
    plan,
    profileFeasibility,
    placement.Placement);
var boundRealization = compiler.Realize(contextualRequest, storage);
if (!boundRealization.IsRealizable)
    throw new InvalidOperationException(string.Join(Environment.NewLine, boundRealization.Diagnostics));

// Only the exact bound realization can authorize PostgreSQL SQL artifacts.
var nativeCompilation = compiler.Compile(
    new RelationQueryNativeCompilationRequest(plan, boundRealization, placement.Placement),
    storage);
if (!nativeCompilation.IsSuccessful)
    throw new InvalidOperationException(string.Join(Environment.NewLine, nativeCompilation.Diagnostics));

var artifact = nativeCompilation.Artifacts.Single();

// Persist the complete, versioned native artifact and validate it when rehydrating.
var artifactJson = PostgresRelationQueryArtifactJsonSerializer.Serialize(artifact);
// Native artifacts contain executable SQL and must come from trusted storage (or be authenticated by the application).
var persistedArtifact = PostgresRelationQueryArtifactJsonSerializer.DeserializeTrusted(artifactJson);

// The root is supplied, so its demanded fields become typed SQL parameters; no loads table is read.
var statement = persistedArtifact.Bind(
    new Dictionary<RelationQueryInputId, ObservationValue>
    {
        [loadInput.GetField(load => load.Id).Input.Id] = ObservationValue.FromString("load-42"),
        [loadInput.GetField(load => load.CustomerId).Input.Id] = ObservationValue.FromString("customer-7")
    },
    new Dictionary<QueryParameterId, ObservationValue>());

Console.WriteLine(statement.Text);
// The generated outer projection uses semantic aliases (the rendered command is a single line):
// SELECT
//   "LoadSearchDto_result"."LoadSearchDto__customerId" AS "customerId",
//   "LoadSearchDto_result"."LoadSearchDto__customerName" AS "customerName",
//   "LoadSearchDto_result"."LoadSearchDto__customerType" AS "customerType",
//   "LoadSearchDto_result"."LoadSearchDto__id" AS "id"
// FROM (...) AS "LoadSearchDto_result"
// statement.Parameters contains "load-42" and "customer-7" in canonical positional slots.

sealed class Load
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("customerId")]
    public required string CustomerId { get; init; }
}

sealed class Customer
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("type")]
    public required string Type { get; init; }
}

sealed class LoadSearchDto
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("customerId")]
    public required string CustomerId { get; init; }

    [JsonPropertyName("customerName")]
    public required string CustomerName { get; init; }

    [JsonPropertyName("customerType")]
    public required string CustomerType { get; init; }
}
```

Compiler-generated aliases are deterministic readability aids derived from semantic shape names, bindings, field
paths, and operation roles. Final result aliases retain canonical output paths (`customerName`), while intermediate
values preserve their context (`Customer__name`) and derived rowsets describe their role (`LoadSearchDto_result`).
Punctuation is normalized safely. Repeated names receive stable suffixes, and names longer than PostgreSQL's standard
63-byte identifier limit are shortened at a Unicode scalar boundary with a semantic digest. Alias text is derived
artifact metadata rather than a semantic identifier; reconstruction continues to use the explicit result bindings in
the persisted artifact.

Artifact JSON includes the schema version, SQL template, tagged captured constants, reconstruction metadata, storage
binding, provenance, and deterministic fingerprints. `DeserializeTrusted` validates the supported schema, nested
binding and artifact fingerprints, and runtime-slot metadata before returning an artifact. The rehydrated artifact can
therefore be bound repeatedly with new supplied fields and query parameters without recompiling the canonical plan.

Native artifact JSON contains executable SQL text, so rehydration is intentionally named `DeserializeTrusted`.
Deterministic fingerprints detect stale or internally inconsistent artifacts; they are not cryptographic signatures.
Store artifacts in a trusted location or authenticate them with an application-owned integrity mechanism before
rehydration. Invocation values remain positional parameters and never become SQL text.

`PostgresRelationQueryCompiler` and the standalone builder continue to return a provider-neutral
`PostgresSqlStatement` containing quoted SQL text and ordered CLR parameter values. The package does not yet provide a
native-artifact executor that automatically dispatches that statement. Its direct Npgsql dependency is instead used by
the bounded canonical source reader and materialization source, where the exact storage binding supplies explicit
PostgreSQL parameter and result types. A supplied relation root remains an explicit plan input, not an implicit table
scan: only its demanded fields are bound, and acquired inputs still use the persisted storage binding.

## Entity repository

`PostgresEntityRepository` realizes `IEntityRepository` over a normalized table. Its
`PostgresEntityRepositoryMapping` declares only physical table/column names, exact scalar encodings, the canonical
identity and partition fields, the semantic observation-version column, and a batch limit. Construction rejects a
missing, extra, duplicated, or type-incompatible field binding, so the supplied `EntityDefinition` remains the sole
semantic field authority.

Reads may be partition-scoped; an unscoped identity that occurs in more than one partition is rejected as ambiguous.
Writes validate the complete observation through the entity definition and require the mapped identity field to equal
the observation identity. PostgreSQL `xmin` is returned only as an opaque optimistic-concurrency token, while the
portable observation version is persisted explicitly. Native batches run inside one database transaction and
advertise both same-partition and cross-partition all-or-nothing support. Schema creation, migrations, constraints,
foreign keys, publications, and replica identity remain explicit deployment/lifecycle responsibilities rather than
repository side effects.

Conformance tests compile representative rows, aggregation, relationship traversal, explicit join, temporal join,
text-search, paging, and distinct plans against the exact advertised profile, including structured fail-closed cases.
Source-reader and materialization conformance tests additionally cover set-oriented point and predicate batches,
bounded enumeration, authenticated keyset resume and forgery rejection, field projection, provider and page byte
boundaries, runtime/database affinity failures, cancellation, and the absence of a cross-page snapshot claim.
Logical-replication contract tests cover deployment affinity, authenticated positions, transaction-aligned budgeting,
key-changing update expansion, before/after images, explicit settlement, typed health and failure observations, and
slot-generation fencing. Npgsql remains confined to the adapter package; Cohesive.Relations and Cohesive.Storage
public contracts do not expose provider types.

Set `COHESIVE_POSTGRES_TEST_CONNECTION_STRING` to run the opt-in local PostgreSQL execution scenario against a database
where the configured user may create and drop a temporary schema:

```bash
COHESIVE_POSTGRES_TEST_CONNECTION_STRING='Host=localhost;Database=postgres;Username=postgres;Password=postgres' \
  ./eng/test-postgres-integration.sh
```

Logical replication is a separate opt-in suite because it requires server-level configuration and creates
database-wide publications and permanent slots during the tests. Point it only at a disposable database with
`wal_level=logical`; the configured role must be able to create and drop schemas, publications, and logical
replication slots. The suite exercises full, default, and explicit-index replica identity (including an index with an
included non-key column), typed insert/update/delete delivery including a primary-key change, explicit settlement
without read-side feedback, and the exported-snapshot baseline handoff. Each scenario waits for its test-owned slot to
become inactive and then removes the slot, publication, and schema.

```bash
COHESIVE_POSTGRES_LOGICAL_REPLICATION_TEST_CONNECTION_STRING='Host=localhost;Database=postgres;Username=postgres;Password=postgres' \
  ./eng/test-postgres-logical-replication-integration.sh
```
