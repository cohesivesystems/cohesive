# Cohesive.Adapters.Cosmos

Azure Cosmos DB adapters for Cohesive storage, canonical relation/query compilation, aggregation, outbox records, and vector storage.

## Install

```bash
dotnet add package Cohesive.Adapters.Cosmos
```

## Use When

- You want Cohesive entity and observation storage backed by Azure Cosmos DB.
- You need an exact supported slice of canonical relation/query IR compiled to parameterized Cosmos SQL.
- You want a safe standalone builder for hand-crafted Cosmos SQL without constructing canonical relation/query IR.
- You want Cosmos-backed vector storage or process outbox persistence.

## Standalone Cosmos SQL Construction

`CosmosSqlBuilder` is an adapter-local, independently usable construction layer. It renders only validated
aliases, escaped property paths, allow-listed operators and functions, and deterministic parameter slots.
Captured values and runtime values are normalized recursively before they reach the Cosmos SDK.

```csharp
using Cohesive.Adapters.Cosmos;
using Cohesive.Model;
using Microsoft.Azure.Cosmos;

var id = CosmosSqlExpression.Property("c", FieldPath.FromField("Id"));
var status = CosmosSqlExpression.Property("c", FieldPath.FromField("Status"));

var template = new CosmosSqlBuilder("c")
    .Select(id, "id")
    .Select(status, "status")
    .Where(CosmosSqlExpression.Binary(
        CosmosSqlBinaryOperator.Equal,
        status,
        CosmosSqlExpression.RuntimeParameter("status")))
    .OrderBy(id)
    .OffsetLimit(offset: 0, limit: 100)
    .BuildTemplate();

CosmosSqlStatement statement = template.Bind(
    new Dictionary<string, object?>
    {
        ["status"] = "open"
    });
QueryDefinition query = statement.ToQueryDefinition();
```

The same builder supports aliased or `SELECT VALUE` projection, object construction, in-document array
expansion, predicates, `DISTINCT`, grouping, aggregation, ordering, and offset/limit paging. It deliberately
does not accept raw SQL fragments or arbitrary identifiers. `CosmosSqlCommandTemplate` can be bound repeatedly;
`CosmosSqlStatement` and its parameter values are immutable snapshots.

Direct builder use expresses Cosmos SQL semantics. It does not create canonical Cohesive semantics, prove that
a hand-crafted statement is equivalent to a relation/query definition, or manufacture canonical plan and
realization provenance. Use the canonical compiler when those guarantees are required.

## Canonical Relation/Query Compilation

Canonical compilation starts after static compilation, capability realization, and source placement. The
Cosmos adapter supplies a conservative target profile and policy. The realization must explicitly request
value results without contributor-occurrence lineage because Cosmos SQL v1 does not reconstruct source
occurrence identities:

```csharp
using Cohesive.Adapters.Cosmos;
using Cohesive.Model;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Realization;

var realization = RelationQueryRealizationCompiler.Compile(
    plan,
    CosmosRelationQueryTargetProfile.Default,
    CosmosRelationQueryTargetProfile.Policy,
    RelationQueryResultObservability.NotRequested);

var request = new RelationQueryNativeCompilationRequest(plan, realization, placement);
var placedSource = placement.Bindings.Single();
var storageBinding = CosmosRelationQueryStorageBinding.FromSemanticPathConvention(
    new("cosmos-binding:loads/v1"),
    placedSource,
    CosmosRelationQueryTargetProfile.Target,
    CosmosRelationQueryTargetProfile.ProfileId,
    containerName: "loads",
    identityPath: FieldPath.FromField("Id"),
    stableUniqueOrderingPaths: [FieldPath.FromField("Id")],
    exactOrderingPaths: [FieldPath.FromField("Id")],
    maximumInputRows: 10_000);

var result = new CosmosRelationQueryCompiler().Compile(request, storageBinding);
if (!result.IsSuccessful)
    throw new InvalidOperationException(string.Join(Environment.NewLine, result.Diagnostics));

CosmosRelationQueryCompiledArtifact artifact = result.Artifacts.Single();
CosmosSqlStatement statement = artifact.Bind(parameterValues);
QueryDefinition query = statement.ToQueryDefinition();
```

The storage binding is a versioned physical interpretation of one exact placed source. Explicit bindings map
compiled input identities to arbitrary Cosmos document paths; `FromSemanticPathConvention` deterministically
maps semantic field paths to matching document paths. Container, document-root, identity, partition, missing/null,
stable-unique ordering, exact-ordering, maximum-input-row, origin, and convention facts participate in the binding
fingerprint. `maximumInputRows` is an asserted deployment fact needed only by plans containing row `COUNT`; it must
be no greater than `CosmosRelationQueryTargetProfile.MaximumExactInteger` (`2^53 - 1`). When a binding is rehydrated,
its persisted schema version must be supported and its supplied fingerprint must match the recomputed fingerprint
of the normalized facts. Stale or modified persisted bindings are rejected before compilation.
Collection-element segments are retained in field selectors and selected-input provenance (for example,
`Items.*.Name`) and are interpreted only through an expansion alias. Identity, partition, and ordering proofs—and
every document path rendered directly into Cosmos SQL—remain property-only.

The compiler consumes the standalone builder but adds semantic validation. Each successful branch artifact
retains selected-input and result-field bindings, canonical parameter contracts, paging evidence, its complete
storage binding, deterministic artifact identity, and provenance back to the exact plan, realization decisions,
placement, target evidence, operating boundaries, compiler profile, and convention set. Each result-field binding
also retains the canonical `ExprValueContract` and its expected physical Cosmos JSON encoding, so a future result
reader can reconstruct values without guessing from SQL aliases or CLR target types.

Capability realization and adapter-native compilation are deliberately separate checks. The generic capability
vocabulary currently advertises operations such as count, minimum, maximum, and distinctness without distinguishing
every structurally significant variant: row count versus value count, grouped versus ungrouped aggregation, or
whole-row versus keyed distinctness. After realization succeeds, the Cosmos compiler therefore inspects the
demand-scoped compiled plan and accepts only the exact native variants described below. A rejected structural
variant produces an attributable `REL22xx` diagnostic; successful capability realization alone is not a promise
that every shape of the operation has a Cosmos-native lowering.

### Exact v1 Semantic Envelope

The default `cohesive.adapters.cosmos.sql/canonical-v1` profile and compiler support only the closure they can
currently prove exact:

- One placed source set bound to one Cosmos container, with no relationship traversal or cross-source stage.
- Named query-row and query-aggregation terminals. Canonical relation terminals are rejected until native
  artifacts can represent root correlation, output identity, cardinality, keys, and invariant evidence.
- Demand-selected source fields, filters, in-document collection expansion, projection, conservatively proven
  whole-row `DISTINCT`, grouping, supported aggregates, ordering, and offset paging in the compiler's validated
  pipeline order.
- Field and nested-field reads, invocation parameters, constants, typed field/literal sites, collection current
  items, boolean negation, supported comparisons and boolean operators, conditionals, and `contains` when their
  compiled value contracts meet the target's exactness constraints.
- Numeric comparison and ordering over known required, non-null `Int32` values. String and date `ORDER BY`
  additionally require the source path in the binding's `ExactOrderingPaths` proof set.
- Row `COUNT` (emitted as `COUNT(1)`) when the storage binding proves `maximumInputRows <= 2^53 - 1`, and grouped
  `MIN`/`MAX` over a known required, non-null `Int32` value.

The v1 compiler rejects unsupported topology or semantics with deterministic `REL22xx` diagnostics. Notable
deferrals include relationship joins, relation-row output, cross-container queries, keyset paging, aggregate
filters, `COUNT(expression)`, `SUM`, ungrouped `MIN`/`MAX`, aggregate ordering or paging, `GROUP BY` combined
with `ORDER BY`, precision-unsafe numeric comparison or ordering, `DateTime`/`Instant` relational comparison or
ordering, string/date `ORDER BY` without physical ordering evidence, and any expression or aggregate outside
the advertised exact type closure. It never falls back to client evaluation or silently substitutes weaker
Cosmos behavior.

### Missing, Null, Distinctness, Ordering, Paging, Parameters, and Aggregation

Cosmos distinguishes an omitted property (undefined) from JSON null. The storage binding declares those
encodings as `OmittedProperty` and `JsonNull`; the compiler requires non-null, present operands wherever the
canonical operation cannot otherwise preserve that distinction. Nullable or potentially missing predicates,
ordering keys, grouping keys, aggregate inputs, and similar unsafe sites fail closed instead of inheriting
Cosmos's undefined propagation implicitly.

`DISTINCT` means Cosmos whole-projection distinctness, so the SQL projection must retain the complete canonical
projected binding, including undemanded fields that still participate in row equality. Every assignment must
have a required, non-null scalar domain whose equality matches Cosmos exactly. Keyed distinctness,
nested/structured distinct shapes, nullable or optional values, and precision-unsafe numeric domains are
deferred; they fail closed rather than changing which source row or distinct value survives.

Ordering is compiled only for supported required, non-null operands. Numeric ordering is exact only for `Int32`
by default. String or date ordering requires an explicit `ExactOrderingPaths` entry in the storage binding,
attributing the proof that physical Cosmos order matches canonical order. Every `ORDER BY`, whether paged or
not, must end in the identity path or another path declared stable and unique because Cosmos cannot reproduce
canonical input-order tie breaking. Offset paging additionally requires a preceding order and a `limit` no
greater than `CosmosRelationQueryTargetProfile.MaximumPageSize` (currently 1,000). The artifact retains the
stable physical proof path and page bounds. Null-placement requests are therefore exact only inside the declared
non-null boundary; the compiler does not claim general cross-type or nullable ordering equivalence.

Canonical parameters are accepted only when their analyzed type has an allow-listed, exact Cosmos parameter
encoding. Optional parameters without defaults cannot represent semantic undefined, operands used by strict
scalar operations must be required and non-null, and runtime values are checked against the compiled value
contract at bind time. Unsupported scalar widths, structures, or invocation values produce structured
diagnostics or binding errors instead of coercion.

Aggregation is intentionally conservative. Canonical v1 supports row count and grouped `MIN`/`MAX` only when
the input analysis proves a required, non-null `Int32` value for `MIN`/`MAX`. Row count additionally requires an
explicit positive `maximumInputRows` storage fact no greater than `9,007,199,254,740,991` (`2^53 - 1`), the largest
integer Cosmos's binary64 JSON-number domain represents exactly. `COUNT(expression)` is distinct from row count;
ungrouped `MIN`/`MAX` are rejected because canonical and Cosmos empty-input results differ. `SUM` is also rejected:
canonical decimal accumulation is not equivalent to Cosmos's binary-number aggregation. Per-aggregate filters and
aggregate result ordering/paging remain deferred. A builder user may still emit those Cosmos SQL aggregates
directly, but that direct statement carries Cosmos semantics rather than a canonical equivalence proof.

The native artifact describes SQL plus enough result metadata to implement exact decoding: every field names its
semantic value contract and physical encoding, including the special exact-integer encoding for row count. Runtime
query execution and the Cosmos result reader are still deferred, so this release does not yet turn returned JSON
into canonical rows or aggregation results. Execution integration must consume the retained metadata and reject an
unexpected physical representation rather than inferring or coercing a result type.

## Legacy Storage and Predicate Compatibility

Existing entity/outbox storage and `EntityPredicate` compilation remain available as compatibility APIs. They
do not consume canonical relation/query IR and should not be treated as canonical compilation provenance.

```csharp
using Cohesive.Adapters.Cosmos;
using Cohesive.Model;
using Cohesive.Relations.Queries;
using Cohesive.Storage;
using Microsoft.Azure.Cosmos;

await using var environment = await CosmosClientFactory.Shared.CreateDatabaseEnvironment(
    new CosmosDatabaseEnvironmentOptions(
        ClientOptions: new()
        {
            Endpoint = configuration["Cosmos:Endpoint"],
            UseDefaultCredential = true
        },
        DatabaseName: "cohesive-dev"),
    containers:
    [
        new ContainerProperties(id: "entities", partitionKeyPath: "/partitionKey"),
        new ContainerProperties(id: "leases", partitionKeyPath: "/id")
    ]);

var entityContainer = environment.ContainersByName["entities"].Item2;
var leaseContainer = environment.ContainersByName["leases"].Item2;

services.RegisterEntityRepository(LoadEntity.Instance, (_, _) =>
    new CosmosEntityOutboxRepository(
        entityDefinition: LoadEntity.Instance.Definition,
        container: entityContainer,
        leaseContainer: leaseContainer,
        partitionKeyPolicy: EntityPartitionKeyPolicy.FromField(nameof(LoadState.TenantId)),
        options: new CosmosObservationOutboxRepositoryOptions
        {
            InstanceName = "dispatch-worker"
        }));

var predicate = new EntityPredicate(new And<FieldPredicate>(
[
    new FieldPredicate(FieldPath.FromField(nameof(LoadState.Status)), new ExactValuePredicate("open")),
    new FieldPredicate(FieldPath.FromField(nameof(LoadState.CustomerId)), new ExactValuePredicate("customer-42"))
]));

CosmosSqlQuery legacySql = new CosmosSqlQueryCompiler().Compile(predicate);
QueryDefinition legacyQuery = legacySql.ToQueryDefinition();
```

## Related Packages

- `Cohesive.Storage` for repository abstractions.
- `Cohesive.Relations` for canonical relation/query IR, static plans, realization, and native-compilation inputs.
- `Cohesive.AI` for vector storage contracts.
