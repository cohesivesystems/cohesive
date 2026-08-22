# Cohesive.Adapters.Cosmos

Azure Cosmos DB adapters for Cohesive storage, canonical relation/query compilation, aggregation, outbox records, and vector storage.

Start with the [`Cohesive.Relations` quick start](https://github.com/cohesivesystems/cohesive/blob/main/src/Cohesive.Relations/docs/GETTING_STARTED.md).
The
[PostgreSQL native join versus Cosmos composed reads](https://github.com/cohesivesystems/cohesive/blob/main/src/Cohesive.Relations/docs/EXECUTION_AND_ADAPTERS.md#postgresql-native-join-versus-cosmos-composed-reads)
comparison explains cross-document Load-to-Customer execution as one Load enumeration plus deduplicated, bounded
Customer batches and a local hash join. Cosmos `JOIN` over nested values in one document is a separate language
feature; it is not a cross-document join.

## Install

```bash
dotnet add package Cohesive.Adapters.Cosmos
```

## Use When

- You want Cohesive entity and observation storage backed by Azure Cosmos DB.
- You need an exact supported slice of canonical relation/query IR compiled to parameterized Cosmos SQL.
- You want a safe standalone builder for hand-crafted Cosmos SQL without constructing canonical relation/query IR.
- You want Cosmos-backed vector storage or process outbox persistence.
- You need a durable target-deduplicating inbox for canonical domain-event publication.

## Canonical Domain-Event Inbox

`CosmosDomainEventInbox` is both an `IDomainEventPublisher` and an addressable `IDomainEventInbox`. It retains the
canonical event envelope unchanged and uses the complete `DomainEventPublicationDeduplicationKey` as publication
identity. The Cosmos container must use `/partitionKey`; one SHA-256 partition projection isolates each exact
authority/tenant scope, while a second exact-key projection identifies the item within that boundary. The original
authority, tenant, contract identity/revision/fingerprint, and idempotency key remain explicit document fields.

```csharp
var inbox = new CosmosDomainEventInbox(
    container,
    interactionContracts,
    [OrderApproved.Contract.Reference]);

await inbox.ValidateAsync(operationContext);

DomainEventPublicationAcknowledgement acknowledgement = await inbox.PublishAsync(
    operationContext,
    DomainEventPublicationInvocation.From(domainEvent));

DomainEventInboxEntry? retained = await inbox.TryReadAsync(
    operationContext,
    DomainEventPublicationDeduplicationKey.From(domainEvent));
```

The first create establishes the acceptance time and stable receipt. Repeating the exact invocation—including
after publisher restart—returns that original receipt. Reusing the same scoped key for different canonical
envelope content fails with `cosmos.domainEventInbox.identity.conflict`; the adapter never overwrites the retained
entry. Configured capability references must resolve as exact domain-event contracts in the supplied canonical
interaction catalog before any I/O.

`ValidateAsync` point-reads the container metadata and rejects an unavailable container, a partition path other
than `/partitionKey`, or a positive default TTL that would erase deduplication evidence. Complete this admission
check before starting publishers or Process workers. A null TTL or `-1` is compatible because inbox documents do
not set an item TTL.

The inbox is a durable handoff and downstream-routing source, not another domain-event definition authority.
Retention, downstream settlement/checkpoint policy, and transport projection remain application deployment policy.

The opt-in emulator test creates and removes an isolated database and proves first publication, exact replay across
publisher restart, point read, conflicting-content rejection, and container admission:

```bash
COSMOS_DOMAIN_EVENT_INBOX_CONNECTION_STRING='AccountEndpoint=https://localhost:8081/;AccountKey=...;' \
  dotnet test src/Cohesive.Tests/Cohesive.Tests.csproj \
  --filter FullyQualifiedName~Cosmos_TargetPersistsExactReplayAcrossPublisherRestartAndRejectsIdentityConflict
```

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
expansion, correlated collection `EXISTS`, predicates, `DISTINCT`, grouping, aggregation, ordering, and
offset/limit paging. It deliberately
does not accept raw SQL fragments or arbitrary identifiers. `CosmosSqlCommandTemplate` can be bound repeatedly;
`CosmosSqlStatement` and its parameter values are immutable snapshots.

Direct builder use expresses Cosmos SQL semantics. It does not create canonical Cohesive semantics, prove that
a hand-crafted statement is equivalent to a relation/query definition, or manufacture canonical plan and
realization provenance. Use the canonical compiler when those guarantees are required.

## Canonical Relation/Query Compilation

Canonical native compilation starts with a statically compiled plan and its typed CLR shape handle. The
illustrative fragment below assumes `plan`, `loadShape`, an SDK `loadsContainer`, canonical `parameterValues`, and
a `cancellationToken`; the authored query requests both a row branch and an aggregation branch. Source placement
and the Cosmos storage binding are separate persisted interpretations of that plan. The Cosmos adapter supplies a
conservative target profile and policy. The realization must explicitly request value results without
contributor-occurrence lineage because Cosmos SQL v2 does not reconstruct source occurrence identities:

Profile feasibility establishes what the Cosmos target family could support. `Realize(...)` then qualifies that
profile against the exact placement, container binding, field evidence, and compiler policy. Only the resulting
bound realization can authorize native compilation; the native request does not accept profile feasibility alone.

```csharp
using Cohesive.Adapters.Cosmos;
using Cohesive.Model;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Realization;
using Microsoft.Azure.Cosmos;

var placementBuilder = RelationQueryPlacement.For(plan);
var source = placementBuilder.Source(
    sourceKey: "loads-read",
    targetProfile: CosmosRelationQueryTargetProfile.Default);
var loads = placementBuilder.PlaceSource(source, loadShape)
    .Identity(load => load.Id)
    .FieldsBySemanticPath();
var authoredPlacement = placementBuilder.Build().RequireValue();
var placedLoads = authoredPlacement.GetInput(loads);

var storageBinding = CosmosRelationQueryBinding.For(placedLoads)
    .Account(loadsContainer.Database.Client.Endpoint)
    .Database(loadsContainer.Database.Id)
    .Container(loadsContainer.Id)
    .Identity(load => load.Id)
    .StableUnique(load => load.Id)
    .ExactOrdering(load => load.Id)
    .MaximumInputRows(10_000)
    .Build()
    .RequireValue();

var profileFeasibility = RelationQueryRealizationCompiler.Compile(
    plan,
    CosmosRelationQueryTargetProfile.Default,
    CosmosRelationQueryTargetProfile.Policy,
    RelationQueryResultObservability.NotRequested);

var contextualRequest = new RelationQueryBoundRealizationRequest(
    plan,
    profileFeasibility,
    authoredPlacement.Placement);

var compiler = new CosmosRelationQueryCompiler();
var boundRealization = compiler.Realize(contextualRequest, storageBinding);
if (!boundRealization.IsRealizable)
    throw new InvalidOperationException(string.Join(Environment.NewLine, boundRealization.Diagnostics));

var nativeRequest = new RelationQueryNativeCompilationRequest(
    plan,
    boundRealization,
    authoredPlacement.Placement);
var result = compiler.Compile(nativeRequest, storageBinding);
if (!result.IsSuccessful)
    throw new InvalidOperationException(string.Join(Environment.NewLine, result.Diagnostics));

var rows = result.Artifacts.Single(
    artifact => artifact.Branch.Kind == RelationQueryNativeResultKind.QueryRows);
var aggregation = result.Artifacts.Single(
    artifact => artifact.Branch.Kind == RelationQueryNativeResultKind.QueryAggregation);

QueryDefinition rowsQuery = rows.Bind(parameterValues).ToQueryDefinition();
QueryDefinition aggregationQuery = aggregation.Bind(parameterValues).ToQueryDefinition();

var executor = new CosmosRelationQueryArtifactExecutor(loadsContainer);
var executions = await executor.ExecuteAsync(
    result.Artifacts
        .Select(artifact => new CosmosRelationQueryArtifactExecutionRequest(
            nativeRequest.PlanReference,
            nativeRequest.ProfileFeasibility.Fingerprint,
            nativeRequest.Placement.Fingerprint,
            artifact.StorageBinding.Fingerprint,
            artifact,
            maximumRows: 1_000,
            parameterValues))
        .ToArray(),
    cancellationToken);
```

`Bind(...).ToQueryDefinition()` remains useful when an application wants to inspect the SDK command or pass it to
its own Cosmos integration. `CosmosRelationQueryArtifactExecutor` is the explicit adapter-native execution API; the
target-neutral `IRelationQueryEvaluator` does not implicitly compile or select this executor. Each request carries
the exact plan, profile feasibility, bound realization, placement, storage binding, artifact-embedded branch,
invocation parameters, and row boundary. A row-and-aggregation batch is preflighted in full before the first SDK
call, then executed in deterministic request order with an independently attributed result for each branch.
Unknown, missing, or incompatible parameters and stale affinity facts fail before I/O.

Preflight is all-or-none validation, not an atomic data snapshot. Every artifact branch is a separate sequential
Cosmos query and can observe a different state when writes occur between requests. A host that requires cross-branch
snapshot equivalence must supply and document a stronger realization instead of inferring it from this batch API.

The executor reconstructs shaped canonical rows exclusively from the artifact's result bindings. Omitted aliases
remain missing, JSON null remains semantic null, and unexpected scalar encodings, duplicate result aliases, or
duplicate canonical relation identities fail closed. SDK exhaustion produces a successful result. Reaching a declared row boundary produces an incomplete result
with an attributable provider-order prefix; provider and decoding failures do not expose untrustworthy partial
rows. Cancellation propagates through iterator creation, page reads, and materialization, and the executor owns and
disposes each SDK iterator.

The storage binding is a versioned physical interpretation of one exact placed source. The typed builder resolves
CLR selectors through the same structural field-path mapping used by imported shapes; explicit field overrides map
compiled inputs to arbitrary Cosmos document paths. The fixed Cosmos target and target-profile selection are
recorded as adapter-convention decisions. Source and placement-binding identities are inherited as exact affinity
from the placed input rather than treated as configurable storage settings. A successful `Build` proves a
well-formed binding with exact affinity; realization and native compilation remain authoritative for branch-specific
capability sufficiency. The low-level constructor and `FromSemanticPathConvention` remain available as escape
hatches. A direct constructor call may omit both the compiled-plan and placement fingerprints, but that creates an
explicitly unverified binding; supplying only one is rejected. Builder-authored bindings always persist both, and
native compilation rejects either fingerprint when it does not match the request. Account endpoint, database,
container, document-root, identity, partition, missing/null, stable-unique ordering, exact-ordering,
maximum-input-row, origin, convention, and per-setting configuration-attribution facts participate in the binding
fingerprint. `maximumInputRows` is an asserted deployment fact needed only by plans containing row `COUNT`; it must
be no greater than `CosmosRelationQueryTargetProfile.MaximumExactInteger` (`2^53 - 1`). When a binding is rehydrated,
its persisted schema version must be supported and its supplied fingerprint must match the recomputed fingerprint
of the normalized facts. Stale or modified persisted bindings are rejected before compilation.
Collection-element segments are retained in field selectors and selected-input provenance (for example,
`Items.*.Name`) and are interpreted only through an expansion alias. Identity, partition, and ordering proofs—and
every document path rendered directly into Cosmos SQL—remain property-only.

### Correlated Structured-Collection Existentials

Canonical `Any` represents an existential predicate over one structured collection. For example, this expression
means that one stop must have both the requested location and the `Pickup` type:

```csharp
var stopsPath = FieldPath.FromField("Stops");
var locationPath = FieldPath.FromField("Location");
var typePath = FieldPath.FromField("Type");

Expr pickupInLocation = Expr.Any(
    Expr.Field(loadBinding, stopsPath),
    Expr.And(
        Expr.Eq(
            Expr.Field($"{ExprFieldRoots.CurrentItem}.Location"),
            Expr.Param("location")),
        Expr.Eq(
            Expr.Field($"{ExprFieldRoots.CurrentItem}.Type"),
            Expr.Const("Pickup"))));
```

All reads in the predicate share one current-item scope. A load with a Seattle delivery stop and a Portland pickup
stop therefore does not match a request for a Seattle pickup. An empty collection evaluates to false without
removing or multiplying the root row before filtering.

The outer canonical `Stops` input owns the physical child mappings and correlation evidence; the compiler does not
invent synthetic canonical inputs for `Location` or `Type`. The typed binding builder derives those semantic paths
from the placed CLR shape while leaving the physical JSON paths explicit:

```csharp
var exactComparisons =
    CosmosRelationQueryCollectionElementSemanticCapabilities.ExactEquality
    | CosmosRelationQueryCollectionElementSemanticCapabilities.ExactInequality;
var requiredValue =
    CosmosRelationQueryStructuredCollectionAbsenceBehavior.ProhibitedByIngestion;

var storageBinding = CosmosRelationQueryBinding.For(placedLoads)
    .Account(loadsContainer.Database.Client.Endpoint)
    .Database(loadsContainer.Database.Id)
    .Container(loadsContainer.Id)
    .Identity(load => load.Id)
    .StructuredCollection(
        (LoadDocument load) => load.Stops,
        FieldPath.FromField("stops"),
        collection => collection
            .AttestCanonicalAnyRepresentation("loads/stops-json-array-v1")
            .Child(
                stop => stop.Location,
                FieldPath.FromField("location"),
                CosmosRelationQueryCollectionElementValueDomain.String,
                exactComparisons,
                "loads/stops-ordinal-string-v1",
                requiredValue,
                requiredValue)
            .Child(
                stop => stop.Type,
                FieldPath.FromField("type"),
                CosmosRelationQueryCollectionElementValueDomain.String,
                exactComparisons,
                "loads/stops-ordinal-string-v1",
                requiredValue,
                requiredValue))
    .Build()
    .RequireValue();
```

Every evidence fact participates in normalized storage-binding and artifact identity. The compiler requires proof
that the physical value is a JSON array, iteration produces one current element, all predicate terms retain
same-array-element correlation, empty arrays produce no elements, and ingestion prohibits missing or null
collections, null elements, and missing or null referenced children. Each child also attests its exact scalar value
domain, comparison facilities, and an attributable semantic profile.

When those facts and the canonical value contracts are sufficient, the compiler emits one expression-local,
correlated subquery:

```sql
EXISTS (
    SELECT VALUE e0
    FROM e0 IN c["Stops"]
    WHERE ((e0["Location"] = @p0) AND (e0["Type"] = @p1))
)
```

The v2 closure accepts the canonical two-argument `Any(collection, predicate)` form over a direct structured source
field. Predicates may compose direct current-element child comparisons with `And`, `Or`, and `Not`; comparisons are
exact scalar `Eq` and `Ne` between one child field and a required, non-null constant or invocation parameter.
Supported child domains are `Bool`, `Int32`, `String`, `Guid`, and `Date`, with equality and inequality advertised
separately by the binding. A missing capability, incompatible domain, ambiguous operand, weak absence guarantee,
deeper element path such as `item.Address.City`, nested collection, function, or conversion produces a structured
diagnostic and no trustworthy artifact.

These absence requirements are semantic, not merely defensive validation. Canonical `Any` treats a missing, null,
or non-array collection as an evaluation failure rather than an empty collection. Canonical equality and inequality
also distinguish missing from null and define `Ne` as the complement of `Eq`, whereas Cosmos propagates undefined
through comparisons and negation and omits a non-true subquery row. In particular, weakening child evidence can
turn `item.Code != value` or `Not(item.Code == value)` into a false negative. The compiler therefore fails closed
instead of relying on Cosmos's implicit undefined behavior.

`ARRAY_CONTAINS` is appropriate for scalar collection membership and is the physical primitive used by the
supported canonical `Contains` closure; it is not a substitute for a correlated predicate over multiple fields of
one structured element. A top-level `JOIN item IN c["Stops"]` represents collection expansion and can multiply root
rows, changing projection, paging, count, and aggregation semantics. Canonical structured `Any` uses the correlated
`EXISTS` expression above and preserves root cardinality.

The standalone builder exposes the same safe physical form for callers intentionally authoring Cosmos SQL:

```csharp
var exists = CosmosSqlExpression.CollectionExists(
    CosmosSqlExpression.Property("c", stopsPath),
    stop => CosmosSqlExpression.Binary(
        CosmosSqlBinaryOperator.And,
        CosmosSqlExpression.Binary(
            CosmosSqlBinaryOperator.Equal,
            CosmosSqlExpression.Property(stop, locationPath),
            CosmosSqlExpression.RuntimeParameter("location")),
        CosmosSqlExpression.Binary(
            CosmosSqlBinaryOperator.Equal,
            CosmosSqlExpression.Property(stop, typePath),
            CosmosSqlExpression.Parameter("Pickup"))));

CosmosSqlStatement statement = new CosmosSqlBuilder("c")
    .Select(CosmosSqlExpression.Property("c", FieldPath.FromField("Id")), "id")
    .Where(exists)
    .BuildTemplate()
    .Bind(new Dictionary<string, object?>
    {
        ["location"] = "SEA"
    });
```

`CollectionExists` allocates the item alias and parameter slots deterministically, escapes every property path, and
keeps the item expression inside its predicate scope. As with every direct builder use, this statement has Cosmos
semantics; only canonical compilation combines the physical expression with value contracts, binding evidence,
capability decisions, and provenance.

The compiler consumes the standalone builder but adds semantic validation. Each successful branch artifact
retains selected-input and result-field bindings, canonical parameter contracts, paging evidence, its complete
storage binding, deterministic artifact identity, and provenance back to the exact plan, realization decisions,
placement, target evidence, operating boundaries, compiler profile, and convention set. Each result-field binding
also retains the canonical `ValueContract` and its expected physical Cosmos JSON encoding, so the artifact
executor reconstructs values without guessing from SQL aliases or CLR target types.

Capability realization and adapter-native compilation are deliberately separate checks. The generic capability
vocabulary currently advertises operations such as count, minimum, maximum, and distinctness without distinguishing
every structurally significant variant: row count versus value count, grouped versus ungrouped aggregation, or
whole-row versus keyed distinctness. After realization succeeds, the Cosmos compiler therefore inspects the
demand-scoped compiled plan and accepts only the exact native variants described below. A rejected structural
variant produces an attributable `REL22xx` diagnostic; successful capability realization alone is not a promise
that every shape of the operation has a Cosmos-native lowering.

### Exact v2 Semantic Envelope

The default `cohesive.adapters.cosmos.sql/canonical-v2` profile and compiler support only the closure they can
currently prove exact:

- One placed source set bound to one Cosmos container, with no relationship traversal or cross-source stage.
- Named query-row and query-aggregation terminals. Canonical relation terminals remain deferred; the executor can
  reconstruct a retained relation identity when consuming an independently supplied valid artifact contract.
- Demand-selected source fields, filters, projection, conservatively proven whole-row `DISTINCT`, supported
  aggregates, ordering, and offset paging in the compiler's validated pipeline order. Unordered ordinary row
  branches receive an identity-path order only when the binding supplies exact physical ordering evidence.
- Field and nested-field reads, invocation parameters, constants, typed field/literal sites, collection current
  items, boolean negation, supported comparisons and boolean operators, conditionals, and `contains` when their
  compiled value contracts meet the target's exactness constraints.
- Correlated structured-collection `Any` over one direct JSON-array field, with direct required child comparisons
  and the explicit same-element, absence, empty-array, scalar-domain, and comparison evidence described above.
- Numeric comparison and ordering over known required, non-null `Int32` values. String and date `ORDER BY`
  additionally require the source path in the binding's `ExactOrderingPaths` proof set.
- Ungrouped row `COUNT` (emitted as `COUNT(1)`) when the storage binding proves
  `maximumInputRows <= 2^53 - 1`.

The v2 compiler rejects unsupported topology or semantics with deterministic `REL22xx` diagnostics. Notable
deferrals include relationship joins, relation-row output, cross-container queries, keyset paging, aggregate
filters, `COUNT(expression)`, `SUM`, ungrouped `MIN`/`MAX`, aggregate ordering or paging, `GROUP BY` combined
with `ORDER BY`, grouped aggregation without an attributable deterministic output-order strategy, expanded row
results without collection-element ordering evidence, unordered `DISTINCT`, precision-unsafe numeric comparison
or ordering, `DateTime`/`Instant` relational comparison or ordering, string/date `ORDER BY` without physical
ordering evidence, nested collections and deeper current-element paths, and any expression or aggregate outside the
advertised exact type closure. It never falls back to client evaluation or silently substitutes weaker Cosmos
behavior.

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
deferred; they fail closed rather than changing which source row or distinct value survives. A whole-row
`DISTINCT` branch also requires an explicit deterministic order because Cosmos cannot reproduce canonical
first-seen order implicitly.

Ordering is compiled only for supported required, non-null operands. Numeric ordering is exact only for `Int32`
by default. String or date ordering requires an explicit `ExactOrderingPaths` entry in the storage binding,
attributing the proof that physical Cosmos order matches canonical order. Every `ORDER BY`, whether paged or
not, must end in the identity path or another path declared stable and unique because Cosmos cannot reproduce
canonical input-order tie breaking. An ordinary row branch without an authored order receives an ascending identity
order only when the binding explicitly lists the identity in `ExactOrderingPaths`; otherwise compilation fails
closed. Expanded row branches remain deferred because root identity does not establish array-element order. Offset
paging additionally requires a preceding order and a `limit` no
greater than `CosmosRelationQueryTargetProfile.MaximumPageSize` (currently 1,000). The artifact retains the
stable physical proof path and page bounds. Null-placement requests are therefore exact only inside the declared
non-null boundary; the compiler does not claim general cross-type or nullable ordering equivalence.

Canonical parameters are accepted only when their analyzed type has an allow-listed, exact Cosmos parameter
encoding. Optional parameters without defaults cannot represent semantic undefined, operands used by strict
scalar operations must be required and non-null, and runtime values are checked against both the compiled value
contract and the representation returned by canonical result decoding. Canonical `Int32`, `Guid`, `Date`,
`DateTime`, and `Instant` values—and recursive arrays of them—are bound without changing their
`ObservationValue` representation. Guid and temporal values use contract-valid JSON strings, matching bounded source
acquisition and reference interpretation, and retain their exact spelling. Specialized CLR-shaped value kinds are
rejected before I/O instead of being normalized only by Cosmos and changing reference-interpreter equality or
projection results. Unsupported scalar
widths, structures, defaults, typed literals, or invocation values produce structured diagnostics or binding
errors instead of coercion.

Aggregation is intentionally conservative. Canonical v2 supports ungrouped row count, which yields one
deterministically positioned result row and requires an explicit positive `maximumInputRows` storage fact no greater
than `9,007,199,254,740,991` (`2^53 - 1`), the largest integer Cosmos's binary64 JSON-number domain represents
exactly. Grouped aggregation is deferred until an attributable output-order strategy can reproduce canonical group
order. `COUNT(expression)` is distinct from row count; `MIN`/`MAX` are rejected because the currently exact numeric
closure was grouped, while ungrouped canonical and Cosmos empty-input results differ. `SUM` is also rejected because
canonical decimal accumulation is not equivalent to Cosmos's binary-number aggregation. Per-aggregate filters and
aggregate result ordering/paging remain deferred. A builder user may still emit those Cosmos SQL aggregates
directly, but that direct statement carries Cosmos semantics rather than a canonical equivalence proof.

The native artifact describes SQL plus the exact metadata used by runtime decoding: every field names its semantic
value contract and physical encoding, including the special exact-integer encoding for row count. The artifact
executor consumes that metadata directly and rejects an unexpected physical representation rather than inferring
or coercing a result type.

The native storage binding retains a normalized account endpoint plus the exact database and container names as
fingerprinted physical affinity. The executor rejects an SDK container whose normalized account endpoint, database
`Id`, or container `Id` differs before I/O. The binding still does not carry a physical source predicate. The bound
container must contain exactly the logical source represented by the placed input. Do not execute a native artifact
directly against a repository container shared by entity and outbox documents. Register that container through the
bounded source reader below, whose account, database, container, document-kind, observation-type, and
payload-presence facts are explicit and attributable.

## Bounded Cosmos Source Acquisition

Native artifact execution is the explicit efficient path for a query branch that the Cosmos compiler can realize
wholly in one dedicated logical source. Canonical physical execution through `IRelationQueryEvaluator` uses
`CosmosRelationQuerySourceReader`, including federated plans and target-independent execution of operations not
lowered natively. The reader evaluates no canonical filter, join, projection, aggregation, ordering, or paging
semantics. It only performs the physical request selected by the canonical planner: bounded enumeration, an
observation-identity batch, or a relationship-reference batch, with identity plus exactly the requested semantic
and correlation fields.

Key batches are deterministically chunked. One SDK query accepts at most
`CosmosRelationQuerySourcePolicy.MaximumSupportedKeysPerQuery` keys, relationship predicates use a balanced
expression tree, and every fully bound command is measured before iterator creation against explicit SQL-text and
JSON-encoded request-size limits. The configured byte boundary can therefore reduce usable key width below the
structural maximum without risking unbounded rendering depth.

Cosmos entity documents use the conventional `observationId` identity and `observation.*` field envelope. The
identity path must resolve to a nonempty JSON string and is also the `ORDER BY` path for every source read, so the
container indexing policy must provide a compatible range index. Identity, semantic-field, and relationship-key
selectors are overridable. Custom field and relationship selector delegates are evaluated and property-path
validated for each requested semantic path; because delegates have no portable content identity, registration also
requires an explicit source identity when either delegate is customized.

Every read includes document-kind, observation-type, and payload-presence predicates so a container shared with
outbox documents cannot widen the logical entity source. A customized discriminator must be the exact value used by
the repository. The following illustrative fragment assumes `loadShape`, `entitiesContainer`, `services`, and
`physicalPlanningPolicy`; `databaseId` and `containerId` must match the supplied SDK container exactly:

```csharp
using Cohesive.Adapters.Cosmos;
using Cohesive.Storage;

var repositoryOptions = new CosmosObservationOutboxRepositoryOptions
{
    EntityDocumentKind = "entity-v2"
};
// Construct CosmosEntityOutboxRepository for this container with the same repositoryOptions.

var cosmosSource = CosmosEntityRelationQuerySourceRegistration.Create(
    loadShape.Id,
    entitiesContainer,
    databaseId: "operations",
    containerId: "entities",
    policy: new CosmosRelationQuerySourcePolicy(
        partitionSourceSelector: "partitionKey",
        logicalPartition: RelationQueryLogicalPartitionIdentity.WholeSource,
        crossPartitionPolicy: CosmosRelationQueryCrossPartitionPolicy.AllowBoundedQueries,
        maximumEnumerationRows: 10_000,
        maximumKeysPerQuery: 100,
        maximumQueryChunks: 16,
        maximumSdkPageSize: 256),
    entityDocumentKind: repositoryOptions.EntityDocumentKind);

services.RegisterEntityRelationQuerySource(cosmosSource);
services.RegisterEntityRelationQueryEvaluator(physicalPlanningPolicy);
```

Cross-partition execution is never inferred. A fixed `PartitionKey` is a caller assertion that every registered
document resides in that logical partition; the reader does not verify the assertion against container metadata,
and an incorrect key can make authoritative absence evidence unsound. Likewise, `partitionSourceSelector` is a
caller-declared property-only path to one scalar partition coordinate and is used for cross-partition conflict
evidence without being compared with the container definition. V1 does not model hierarchical partition-key tuples.
Explicitly allow bounded cross-partition queries or provide a fixed partition. Source registration rejects the
otherwise unusable combination of cross-partition prohibition and no fixed scope because the reader itself rejects
that policy before I/O.

A convention-derived source identity fingerprints the account endpoint, database, container, partition policy,
shape, selectors, limits, and discriminator. Explicit source identities remain explicit. The convention-derived
execution domain records physical account/database/container affinity only; supply an explicit domain when client
consistency level or routing preferences must distinguish otherwise identical containers. Neither form promises an
atomic snapshot across separate SDK queries. Competing shape or source registrations are rejected by the immutable
Storage catalog rather than selected by registration order.

Complete and not-found results are authoritative for one physical acquisition only after every required chunk and
SDK page is exhausted. They do not assert a common temporal snapshot across separate reader calls or SDK queries;
that stronger semantic requires separate consistency capability evidence.
Configured row, fan-out, buffer, or feed boundaries produce partial or inconclusive evidence; malformed identities,
duplicate projected aliases, conflicting identities, and provider failures produce failed evidence. Per-row fields
distinguish value, null, authoritative missing, and failed decoding; the source-read state separately distinguishes
partial and inconclusive acquisition. The canonical physical executor then owns joins and the remaining
relation/query semantics over that evidence.

`MaximumBufferedRows` is a row-count boundary, not a byte-memory guarantee. Cosmos service item and page limits keep
each provider response finite, while a future cross-adapter materialization budget should model cumulative byte
memory as a canonical physical capability instead of introducing unrelated adapter-only semantics.

## Materialization Source

`CosmosMaterializationSource` composes the canonical Relations reader with Cosmos all-versions-and-deletes change
feed consumption. The first profile is deliberately constrained to one fixed logical partition, the conventional
`observationId`, `partitionKey`, and `observation.*` envelope, and a caller-attested Strong-consistency account.
Both source-set enumeration and bounded relationship traversal placements are supported; traversal changes project
correlation keys from current and previous observation envelopes. Baseline queries explicitly request
`ConsistencyLevel.Strong`; a production
change-feed client may inherit that account setting or request Strong, but an explicitly weaker client is rejected.
This closes the cut-before-scan gap in which a stale baseline could otherwise omit a write committed before the
captured change position.

The deployment must also attest account-level continuous backup, its full-fidelity retention horizon, and previous
image availability. Those references, the Strong-consistency evidence, physical affinity, placement, query limits,
admission limits, and cursor bounds participate in the source capability fingerprint. The runtime should share one
`CosmosMaterializationAdmissionIndex` across sources using the same physical resources so container and partition
parallelism are enforced coherently.

Baseline and change cursors are authenticated adapter-owned values. Persist the authentication secret for as long
as a generation may resume; rotating it intentionally invalidates outstanding cursors and therefore requires a new
generation. An intra-page resume replays the complete SDK response and verifies the consumed semantic prefix.
Provider page resegmentation or ambiguous transactional ordering fails closed. Same-item records sharing an LSN
are ordered only when their complete previous/current image chain proves a unique transition sequence. Distinct
physical items sharing an LSN may not affect the same semantic observation identity because their relative order
cannot be proven. In-scope replacements must also advance their Cohesive observation version.

ARI-187 introduces the v2 pull continuation, position, change-ID, and delivery-ID formats so cursor compatibility can
remain bound to the capability profile while semantic change identity remains independent of that profile. Existing
v1 cursors are intentionally rejected and require a new materialization generation; their provider continuations are
not converted. The exact pull source scope includes a semantic binding digest covering the document discriminator,
persisted observation type, identity and partition selectors, and fixed partition. Consequently different logical
document families cannot share progress even when they occupy the same physical container, while operational page,
parallelism, admission, retention, and cursor policy does not perturb stable change identity.

The profile advertises baseline-plus-catch-up convergence, stable bounded paging, complete retained create/update/
delete mutation delivery, at-least-once change delivery, and reconciliation. Full-fidelity previous images provide
selected-field and correlation-key before images. The
profile does not claim a cross-page MVCC snapshot, retained-history start, or explicit provider settlement. Batched
point reads are currently a composed parameterized-query path;
native `ReadManyItemsAsync` remains unavailable until the placement proves exact physical item-id and partition-key
addresses.

The full-fidelity pull realization currently requires the projected shape to equal the persisted observation type
and does not support a top-level stream filter. Managed outbox projection is intentionally broader; attaching an
outbox-shaped reader to the pull realization fails during construction instead of silently changing its source set.

### Positions, Checkpoints, and Processor Leases

`CosmosMaterializationSource` uses the Cosmos change-feed pull model. Each canonical change position is an
authenticated adapter-owned value that retains an opaque Cosmos pull continuation and, when needed, intra-page
prefix progress. Reading from a position does not persist application progress, update a lease, or acknowledge
delivery. The owning Process decides whether and when to cover that position with a durable materialization
checkpoint. The all-versions-and-deletes retention horizon remains an independently attested provider constraint.

`CosmosManagedMaterializationChangeSource` is the complementary Change Feed Processor realization. It projects the
same canonical `MaterializationChangePage`, change envelope, delivery identity, source-position, application-
checkpoint, and settlement contracts. Each latest-version callback is grouped by logical partition key, the ordering
boundary Cosmos guarantees, and projected as upserts. The SDK manual checkpoint operation runs only after every group
handler returns an applied or exact-replayed durable Cohesive checkpoint covering that group's authenticated callback
continuation and delivery identities. One provider acknowledgement then emits one settlement observation per covered
group. A callback filtered down to no relevant documents still commits one empty progressed page before lease
advancement. Feed ranges, lease tokens, worker instances, and callback batching never participate in semantic change
or delivery identity, so lease transfer, rebatching, and range splits retain revision identity. The adapter derives a
binding-, lease-store-, and initial-boundary-specific processor namespace and then derives the effective deployment
name from the materialization, execution-definition fingerprint, and generation. The lease container's account,
database, and container affinity is capability/profile provenance; reopening the source against another lease store
is a different deployment rather than an invisible checkpoint reset. The lease store must use a different database
or container identifier from the monitored container so provider lease writes can never recursively enter the source
feed. This synchronous boundary deliberately does not treat different account endpoint text as separation evidence:
global and regional endpoints may address the same underlying account. Cross-account lease stores with matching
database and container names must therefore choose a distinct database or container name until a future metadata-
validated binding can prove resource identity. Beginning, current, and explicit-time start
policies cannot race to initialize the same lease namespace. Workers for the same request and lease store therefore
cooperate through the same leases, while independent entity, outbox, or stream bindings and new generations cannot
share leases or checkpoint past one another's work. Pause/continue retains the same generation and deployment name;
a restarted rebuild that allocates a new generation necessarily starts with a distinct provider lease namespace.

Each authenticated managed source position retains the exact provider feed-range representation and continuation,
so it remains independently inspectable as a resumable source boundary even though the running SDK processor realizes
ordinary recovery through its lease container. Neither value is projected as a Cohesive application checkpoint.
The current managed envelope requires the canonical top-level `partitionKey` selector because grouping and ordering
are derived from that persisted wire field. A reader declaring another partition selector is rejected during source
construction rather than allowing relevant changes to be mistaken for filtered provider input.

The installed Cosmos SDK exposes manual checkpointing only for latest-version change feed. Its public
all-versions-and-deletes builder completion-gates automatic checkpointing, and its mode setter is internal. The
adapter therefore does not claim previous images, delete delivery, or full-fidelity semantics for the managed
realization. Its capability evidence carries `LatestVersionUpsertDelivery`, which cannot satisfy a requirement for
`CompleteMutationDelivery`. `CosmosMaterializationSource` remains the full-fidelity bounded pull realization. Pull continuations and
processor leases are distinct and are not contractually interchangeable; the adapter exposes no conversion between
them. A managed callback continuation is recorded as its exact source boundary, while the lease document remains a
provider-owned settlement realization rather than a second application-checkpoint authority.

The former Cosmos `IObservationStream` surface was removed. Entity and outbox consumption now binds an explicit
document discriminator, graph-qualified persisted observation type, and optional outbox stream to the managed
materialization source. `CosmosRelationQuerySourceReader` separately declares its projected semantic shape and its
persisted envelope type. Entity-outbox documents retain the exact canonical interaction envelope and its content
fingerprint; the existing stream, subject, and observation fields are derived physical projections for
materialization acquisition rather than a parallel message model.
`CosmosEntityOutboxRepository` owns only entity/outbox persistence and atomic write behavior; it no longer owns a
lease container or a change processor.

See the Cosmos documentation for the
[pull model](https://learn.microsoft.com/azure/cosmos-db/nosql/change-feed-pull-model) and
[change feed processor](https://learn.microsoft.com/azure/cosmos-db/change-feed-processor).

## Query Authority

Canonical relation/query IR is the sole semantic query authority for this adapter. Compile supported native
branches with `CosmosRelationQueryCompiler`, execute the resulting artifacts through the Cosmos SDK, and use
`CosmosRelationQuerySourceReader` for bounded physical acquisition selected by the canonical planner.
`CosmosSqlConstruction` remains available as an adapter-local construction layer for intentionally hand-crafted
Cosmos SQL, but direct statements carry Cosmos semantics and do not provide canonical equivalence or provenance.
`CosmosEntityOutboxRepository` is limited to persistence, point reads, concurrency, and atomic outbox writes; it is
not a semantic query execution or change-delivery surface.

The former parallel predicate and aggregation compilers were removed intentionally. The adapter provides no
automatic translation bridge from their deleted model to canonical relation/query IR.

## Aggregate storage realization

`CosmosStorageRealizationCompiler` projects a canonical `StorageStructureDefinition` through the existing
fingerprinted Cosmos relation/query binding. Each owned collection must map to one structured JSON array with exact
scope, same-element correlation, required collection/element presence, complete child-field domains, root-local
identity equality, and the
`cohesive.adapters.cosmos/storage-realization/ordered-owned-json-array/v1` semantic profile. That profile asserts the
physical JSON array order is the canonical component-ordinal order.

The resulting embedded realization declares in-document expansion, single-document atomicity, and root-document
change attribution. Physical container and document paths remain in the Cosmos binding and are referenced through its
fingerprint; missing scope, order, tenant, identity, or child-domain evidence returns structured `CSST` diagnostics.

## Related Packages

- `Cohesive.Storage` for repository abstractions.
- `Cohesive.Relations` for canonical relation/query IR, static plans, realization, and native-compilation inputs.
- `Cohesive.AI` for vector storage contracts.
