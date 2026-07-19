# Cohesive.Adapters.Postgres

`Cohesive.Adapters.Postgres` provides an injection-safe standalone PostgreSQL `SELECT` builder and exact,
persistable storage bindings for Cohesive.Relations plans. The builder can be used without Cohesive.Relations query
compilation; the storage binding records how a particular compiled plan and placement map to PostgreSQL tables.

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
offset paging, and null-aware structural keyset predicates.

Captured constants remain portable when a compiled artifact is serialized, and runtime bindings accept the same
closed provider-neutral CLR domain. The supported values are
`null`, `bool`, `int`, `long`, `decimal`, `string`, `Guid`, `DateOnly`, `DateTime` with
`DateTimeKind.Unspecified`, `DateTimeOffset`, and `byte[]`. Other CLR types are rejected instead of being serialized
with ambiguous provider-specific behavior. Runtime parameter values are not persisted; callers supply them to `Bind`.

The canonical v1 compiler intentionally advertises only the expression closure for which it has exact lowering
evidence: comparisons, Boolean logic, conditionals, ordinal prefix/suffix/substring search, and the documented
aggregates. General arithmetic remains fail-closed even though the standalone SQL builder can express arithmetic;
checked numeric-domain evidence is required before the relation compiler can claim canonical overflow and rounding
semantics. Whole-row distinctness is supported within exact physical equality domains, while keyed representative-row
selection and interval-overlap joins are not advertised by the v1 target profile.

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
`cohesive.adapters.postgres.sql/database-semantics/utf8-standard-identifiers/v1` profile. PostgreSQL SQL v1 therefore
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
    PostgresRelationQueryTargetProfile.Default,
    executionDomain);
var customerSource = placementAuthor.Source(
    "postgres/customers",
    PostgresRelationQueryTargetProfile.Default,
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

`Cohesive.Adapters.Postgres` deliberately has no Npgsql dependency. `PostgresRelationQueryCompiler` returns a
provider-neutral `PostgresSqlStatement` containing quoted SQL text and ordered CLR parameter values. It does not create
Npgsql or ADO.NET parameter objects. The caller owns the final driver mapping for each value, including the PostgreSQL
type assigned to `null`, and executes the statement through its chosen driver or data-access layer. A supplied root is
likewise an explicit plan input, not an implicit table scan: only its demanded fields are bound, and acquired inputs
still use the persisted storage binding.

Conformance tests compile representative rows, aggregation, relationship traversal, explicit join, temporal join,
text-search, paging, and distinct plans against the exact advertised profile, including structured fail-closed cases.
True backend differential execution is deliberately deferred: this package has no approved Npgsql dependency, so a
future driver integration or conformance harness must compare PostgreSQL results with the in-memory reference
interpreter without moving provider types into this adapter's public surface.
