# Cohesive.Adapters.Elastic

Elasticsearch query and aggregation compilers for Cohesive relation plans.

## Install

```bash
dotnet add package Cohesive.Adapters.Elastic
```

## Use When

- You want Cohesive relation queries projected to Elasticsearch requests.
- You need aggregation plans interpreted against Elasticsearch.
- You want search infrastructure to attach to Cohesive relation semantics instead of shaping application code around Elasticsearch APIs.

## Complete Query and Aggregation Example

This example queries a denormalized `LoadSearchDocument` index containing exactly one document per load. It returns a
page of loads and a global load count over the same filter: the customer name ends with a supplied suffix, and the
required `StopLocations` scalar collection contains a supplied location. `StopLocations` is populated by the indexing
relation from the load's stops.

First declare the shapes and canonical query, then run
[Cohesive.Relations static compilation](../../Cohesive.Relations/README.md#demand-driven-static-compilation):

```csharp
using System.Text.Json;
using Cohesive.Adapters.Elastic;
using Cohesive.Model;
using Cohesive.Model.Expressions;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.IR;
using Cohesive.Relations.Physical;
using Cohesive.Relations.Realization;
using Cohesive.Relations.Serialization;
using Elastic.Clients.Elasticsearch;
using IRQueryDefinition = Cohesive.Relations.IR.QueryDefinition;

GraphId graph = new("example/load-search/v1");
QualifiedShapeId loadShape = new(graph, new("LoadSearchDocument"));
QualifiedShapeId rowShape = new(graph, new("LoadSearchRow"));
QualifiedShapeId countShape = new(graph, new("LoadSearchCount"));
var text = new ScalarTypeRef(ScalarTypeKind.String);
var idPath = FieldPath.FromField("Id");
var customerNamePath = FieldPath.FromField("CustomerName");
var stopLocationsPath = FieldPath.FromField("StopLocations");
var countPath = FieldPath.FromField("Count");

var shapes = ShapeGraphDocument.FromGraph(new(
    graph,
    [
        new Shape(
            loadShape.ShapeId,
            [
                new(new("Id"), text, role: FieldRole.Identity),
                new(new("CustomerName"), text),
                new(
                    new("StopLocations"),
                    text,
                    cardinality: FieldCardinality.Many)
            ],
            role: ShapeRoles.Entity),
        new Shape(
            rowShape.ShapeId,
            [
                new(new("Id"), text, role: FieldRole.Identity),
                new(new("CustomerName"), text)
            ],
            role: ShapeRoles.Projection),
        new Shape(
            countShape.ShapeId,
            [
                new(new("Count"), new ScalarTypeRef(ScalarTypeKind.Int64))
            ],
            role: ShapeRoles.Projection)
    ]));

ValueBindingId load = new("load");
ValueBindingId row = new("row");
ValueBindingId count = new("count");
QueryNodeId source = new("loads");
QueryNodeId filter = new("filter-loads");
QueryNodeId project = new("project-rows");
QueryNodeId order = new("order-rows");
QueryNodeId page = new("page-rows");
QueryNodeId aggregate = new("count-loads");
QueryParameterId customerNameSuffix = new("customer-name-suffix");
QueryParameterId location = new("location");

IRQueryDefinition query = new(
    new("loads-by-customer-and-stop"),
    new("LoadsByCustomerAndStop"),
    new(
        nodes:
        [
            new SourceQueryNode(source, load, loadShape),
            new FilterQueryNode(
                filter,
                source,
                Expr.And(
                    Expr.EndsWith(
                        Expr.Field(load, customerNamePath),
                        Expr.Param(customerNameSuffix.Value)),
                    Expr.Contains(
                        Expr.Field(load, stopLocationsPath),
                        Expr.Param(location.Value)))),
            new ProjectQueryNode(
                project,
                filter,
                row,
                rowShape,
                [
                    new(new("row-id"), idPath, Expr.Field(load, idPath)),
                    new(
                        new("row-customer-name"),
                        customerNamePath,
                        Expr.Field(load, customerNamePath))
                ]),
            new OrderQueryNode(order, project, [new(Expr.Field(row, idPath))]),
            new PageQueryNode(page, order, new OffsetPageDefinition(limit: 25)),
            new AggregateQueryNode(
                aggregate,
                filter,
                count,
                countShape,
                aggregates:
                [
                    new(new("count-matching-loads"), countPath, AggregateOperator.Count)
                ])
        ],
        parameters:
        [
            new(customerNameSuffix, text),
            new(location, text)
        ]),
    [
        new RowsQueryResultDefinition(new("rows"), page),
        new AggregationQueryResultDefinition(new("count"), aggregate)
    ]);

var staticCompilation = RelationQueryStaticCompiler.Compile(new(
    RelationQueryDocument.FromDefinition(query),
    [shapes]));
var plan = staticCompilation.Plan as CompiledRelationQueryPlan
    ?? throw new InvalidOperationException(
        string.Join(Environment.NewLine, staticCompilation.Diagnostics));
```

Placement and storage bindings are versioned deployment artifacts that would normally be persisted and loaded rather
than rebuilt for each query. The adapter-owned authoring surface consumes the exact plan-bound placed input, maps it
to the `loads-read` index, and records both the effective evidence and the origin of every configurable decision:

```csharp
var sourceContract = plan.InputContract.Sources.Single();
var placementBuilder = RelationQueryPlacement.For(plan);
var source = placementBuilder.Source(
    sourceKey: "elastic/loads-read",
    targetProfile: ElasticRelationQueryTargetProfile.Default);
var placementInput = placementBuilder.Place(sourceContract, source)
    .Identity("Id")
    .FieldsBySemanticPath();
var authoredPlacement = placementBuilder.Build().RequireValue();
var placement = authoredPlacement.Placement;
var placed = authoredPlacement.GetInput(placementInput);

var storageBinding = ElasticRelationQueryBinding.For(placed)
    .Index("loads-read")
    .FieldsExplicitly()
    .Field(idPath, field => field
        .Source(FieldPath.FromField("id"), ElasticRelationQueryFieldValueEncoding.JsonString)
        .Query(FieldPath.Parse("id.keyword"), ElasticRelationQueryFieldMappingKind.Keyword)
        .RootDocument()
        .Attest(
            ElasticRelationQueryFieldSemanticCapabilities.ExactOrdering
                | ElasticRelationQueryFieldSemanticCapabilities.StableUniqueOrdering,
            "example/ordinal-keyword-v1"))
    .Field(customerNamePath, field => field
        .Source(
            FieldPath.FromField("customerName"),
            ElasticRelationQueryFieldValueEncoding.JsonString)
        .Query(
            FieldPath.Parse("customerName.keyword"),
            ElasticRelationQueryFieldMappingKind.Keyword)
        .RootDocument()
        .ReversedSuffix(FieldPath.Parse("customerName.reversed"))
        .Attest(
            ElasticRelationQueryFieldSemanticCapabilities.WildcardSuffix
                | ElasticRelationQueryFieldSemanticCapabilities.ReversedPrefixSuffix,
            "example/ordinal-suffix-v1"))
    .CollectionKeyword(
        stopLocationsPath,
        FieldPath.Parse("stopLocations.keyword"),
        "example/ordinal-keyword-array-v1")
    .Build()
    .RequireValue();

var realization = RelationQueryRealizationCompiler.Compile(
    plan,
    ElasticRelationQueryTargetProfile.Default,
    ElasticRelationQueryTargetProfile.Policy,
    RelationQueryResultObservability.NotRequested);
if (!realization.IsRealizable)
    throw new InvalidOperationException(string.Join(Environment.NewLine, realization.Diagnostics));

var compilation = new ElasticRelationQueryCompiler().Compile(
    new RelationQueryNativeCompilationRequest(plan, realization, placement),
    storageBinding);
if (!compilation.IsSuccessful)
    throw new InvalidOperationException(string.Join(Environment.NewLine, compilation.Diagnostics));

var parameters = new Dictionary<QueryParameterId, ObservationValue>
{
    [customerNameSuffix] = ObservationValue.FromString("Inc"),
    [location] = ObservationValue.FromString("Seattle, WA")
};
var rows = compilation.Artifacts.Single(
    artifact => artifact.Branch.Kind == RelationQueryNativeResultKind.QueryRows);
var countResult = compilation.Artifacts.Single(
    artifact => artifact.Branch.Kind == RelationQueryNativeResultKind.QueryAggregation);

SearchRequest rowsRequest = rows.Bind(parameters);
SearchRequest countRequest = countResult.Bind(parameters);
```

`ElasticRelationQueryBindingAuthoringOptions` supplies a named scoped profile between adapter conventions and local
declarations. Local calls such as `Index` take precedence. The fixed Elasticsearch target and target-profile
selection are recorded as adapter-convention decisions. Source instance and placement-binding identities are not
configurable storage-binding settings: they are inherited plan-bound affinity from `placed`, preventing a binding
from being silently reused with a different compiled placement. Repeating a setting at the same precedence tier,
selecting an undemanded field, using a stale or non-Elasticsearch placed input, or leaving required evidence absent
returns structured diagnostics from `Build`.

Successful authoring proves that the artifact is well formed and has exact plan/source/placement affinity. It does
not claim that every query branch is realizable: the Elasticsearch compiler still checks each branch against the
field capabilities, physical evidence, operating boundaries, and selected lowering policy.

The immutable constructors remain available for importing or rehydrating an already normalized binding, while the
authoring builder is the usual path for application configuration. A direct constructor call may omit both the
compiled-plan and placement fingerprints as an explicitly unverified escape hatch; supplying only one is rejected.
Builder-authored bindings always persist both, and native compilation rejects either fingerprint when it does not
match the request:

```csharp
var productionProfile = new ElasticRelationQueryBindingAuthoringOptions(
    authority: "example/elastic-production/v3",
    indexName: "loads-read-blue",
    sourceMode: ElasticRelationQuerySourceMode.Synthetic,
    maximumPageSize: 500);

var locallyOverridden = ElasticRelationQueryBinding.For(placed, productionProfile)
    .Index("loads-read-green") // Explicit local declaration wins over the scoped index.
    .Build();
```

When placement authoring has a `RelationQueryPlacedInput<T>`, the same builder accepts CLR property expressions and
resolves them through the authoritative CLR shape mapping rather than reflection-time naming guesses:

```csharp
var typedBinding = ElasticRelationQueryBinding.For(typedPlacedLoad)
    .Index("loads-read")
    .SourceOnly(
        load => load.Id,
        FieldPath.FromField("id"),
        ElasticRelationQueryFieldValueEncoding.JsonString)
    .Keyword(
        load => load.CustomerName,
        FieldPath.Parse("customerName.keyword"),
        ElasticRelationQueryFieldSemanticCapabilities.ExactTerm,
        "example/ordinal-keyword-v1",
        sourceField: FieldPath.FromField("customerName"))
    .CollectionKeyword(
        load => load.StopLocations,
        FieldPath.Parse("stopLocations.keyword"),
        "example/ordinal-keyword-array-v1")
    .Build();
```

Both values are fresh Elastic SDK `SearchRequest` objects. The count request uses `size: 0` and exact total-hit
tracking. Each branch is independently reusable and must be bound independently.

The suffix lowering can be overridden through attributable compiler policy. This explicit-local preference selects
the exact wildcard strategy even though the binding also supplies a reversed suffix field:

```csharp
var suffixPolicy = ElasticQueryLoweringPolicy.CreateConventional(
    additionalPreferences:
    [
        new(
            ElasticQueryLoweringOperation.Suffix,
            ElasticQueryLoweringPreferenceOrigin.ExplicitLocal,
            ElasticQueryLoweringFallbackPolicy.RequirePreferred,
            [ElasticQueryLoweringStrategies.WildcardExactKeywordId])
    ]);

var customized = new ElasticRelationQueryCompiler(loweringPolicy: suffixPolicy).Compile(
    new RelationQueryNativeCompilationRequest(plan, realization, placement),
    storageBinding);
```

The policy decision participates in artifact fingerprints and provenance. The binding must attest
`WildcardSuffix`, while `ExactCollectionMembership` attests that every `StopLocations` element maps one-to-one to an
indexed term without lossy normalization. Missing evidence produces a structured diagnostic rather than a weaker
query. Advanced users can also register an `IElasticQueryLoweringStrategy` within the supported physical IR.

After binding, callers can inspect or explicitly adjust the ordinary SDK request:

```csharp
rowsRequest.Timeout = "5s";
```

That mutation is outside the compiler's exactness and provenance guarantees; those describe the request initially
materialized by `Bind`.

The scalar membership capability above remains the simplest choice when each location is independently searchable.
When predicates must correlate fields from the same structured element, use canonical `Any`. The following is an
alternative version of the complete example: include `stopsField` in `LoadSearchDocument`, replace the filter's
`Contains` expression with `pickupInLocation`, and then run static compilation. It means “there is one pickup stop in
the requested location,” not “one stop has the location and some other stop is a pickup”:

```csharp
var stopsPath = FieldPath.FromField("Stops");
var locationPath = FieldPath.FromField("Location");
var typePath = FieldPath.FromField("Type");
var stopType = new ObjectTypeRef(
[
    new("Location", text),
    new("Type", text)
]);
var stopsField = new FieldDefinition(
    new("Stops"),
    stopType,
    cardinality: FieldCardinality.Many);

Expr pickupInLocation = Expr.Any(
    Expr.Field(load, stopsPath),
    Expr.And(
        Expr.Eq(
            Expr.Field($"{ExprFieldRoots.CurrentItem}.Location"),
            Expr.Param(location.Value)),
        Expr.Eq(
            Expr.Field($"{ExprFieldRoots.CurrentItem}.Type"),
            Expr.Const("Pickup"))));
```

The resulting plan has one outer `Stops` input. That input owns all Elasticsearch-specific nested evidence; no
synthetic child input is added to the canonical plan:

```csharp
// placedWithStops is the plan-bound placed input produced for the alternative Stops query.
var nestedStorageBinding = ElasticRelationQueryBinding.For(placedWithStops)
    .Index("loads-read")
    .Nested(stopsPath, FieldPath.Parse("stops"), nested => nested
        .AttestCanonicalAnyRepresentation()
        .Child(
            locationPath,
            FieldPath.Parse("stops.location.keyword"),
            ElasticRelationQueryFieldMappingKind.Keyword,
            ElasticRelationQueryFieldSemanticCapabilities.ExactTerm,
            "example/ordinal-keyword-v1")
        .Child(
            typePath,
            FieldPath.Parse("stops.type.keyword"),
            ElasticRelationQueryFieldMappingKind.Keyword,
            ElasticRelationQueryFieldSemanticCapabilities.ExactTerm,
            "example/ordinal-keyword-v1"))
    .Build()
    .RequireValue();
```

The resulting SDK request contains one `NestedQuery` at `stops`, with both term clauses inside the same child Boolean
query. A flattened `object` mapping, missing same-element evidence, dropped null elements, or weak missing/null
behavior fails closed with `REL2244`; the diagnostic recommends a denormalized scalar collection plus `Contains`
when correlation is not needed.

## Request Construction and Lowering Extensions

`ElasticSearchRequestTemplate` is an immutable request-construction component that can also be used directly. It
binds canonical constants and invocation parameters into a fresh
`Elastic.Clients.Elasticsearch.SearchRequest`, including the required `AllowPartialSearchResults = false` transport
option. The returned SDK request can be inspected, adjusted, and passed directly to `ElasticsearchClient`. Each bind
creates an independent object graph. Mutating it is an explicit escape hatch: the compiled artifact's provenance,
fingerprint, and exactness guarantee describe the initially materialized request, not caller changes made afterward.

```csharp
SearchRequest request = compiledArtifact.Bind(invocationParameters);
request.Timeout = "5s"; // Optional explicit caller override.
SearchResponse<JsonElement> response = await client.SearchAsync<JsonElement>(request, cancellationToken);
```

The immutable physical query IR supports exact term, range, existence, wildcard, prefix, Boolean, and nested clauses
with explicit `filter`, `should`, and `must_not` placement; offset and `search_after` hit pagination; exact total-hit
counts; and paged composite grouped counts. Fingerprinting uses this closed template rather than the mutable SDK
request.

`IElasticQueryLoweringStrategy` is an extension point over that closed, inspectable physical clause IR. Extensions
may select or compose its supported exact clauses and participate in attributable policy precedence. ARI-131 does not
accept arbitrary raw JSON, scripts, or regular-expression clauses from an extension. Additional physical clauses
should be introduced later through a versioned contract that preserves deterministic binding, fingerprinting, and
semantic proof.

Suffix strategy flags are executable capability attestations, not mapping-name guesses. A binding may assert
`WildcardSuffix` or `ReversedPrefixSuffix` only when its fingerprinted semantic profile covers the relevant mapping,
normalizer/transform, and cluster query settings. In particular, the profile must account for
`search.allow_expensive_queries` or an indexed realization that remains executable when expensive queries are
disabled. When that evidence is absent, policy resolution rejects the strategy instead of emitting a request that
the target may refuse.

## Exact Compiler Closure

The canonical v2 target profile advertises operation families. A successful artifact still depends on the concrete
storage binding and compiler proving the narrower structural closure for that branch. Row artifacts currently accept
one linear, single-index pipeline; direct field or scalar-constant projections; required non-null scalar predicates;
direct membership tests over supported required root scalar arrays; deterministic field ordering; and a bounded page. Grouped
counts accept required non-null text, GUID, or integer keys through a paged composite aggregation. Unsupported shapes
fail with structured diagnostics instead of weakening the canonical semantics.

Field bindings separately attest their mapping, root-versus-nested document scope, retrieval channel, physical JSON
encoding, and exact query facilities. Canonical v2 reads scalar row values only from root-document `_source` fields.
Scalar arrays may be query-only membership inputs but cannot yet be projected as result fields. Structured collection
existentials support direct required child comparisons only when the binding proves an Elasticsearch `nested` mapping
and same-element correlation; deeper nested traversal and nested-source extraction are deferred. Temporal values may
be projected when their canonical string encoding is attested, but temporal filtering, ordering, and grouping are
deferred because Elasticsearch date normalization and precision do not yet preserve Cohesive's complete
representation and comparison semantics.

Offset artifacts describe one bounded request against that invocation's current view and make no cross-request
continuation claim. `search_after` hit pages and composite after-key pages span a logical sequence, so canonical v2
requires `ElasticRelationQueryPaginationConsistency.StableSearchView`: refreshes are complete and the visible
document set, ordering, and concrete target remain unchanged for the sequence.
Point-in-time-backed pagination over mutable indexes is intentionally deferred until PIT lifecycle and continuation
metadata are represented in the artifact contract.

## Related Packages

- `Cohesive.Relations` for query and aggregation plan definitions.
- `Cohesive.Storage` for read repository abstractions.
