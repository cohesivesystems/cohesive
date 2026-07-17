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

## Compile a Query and Aggregation

Canonical adapter compilation starts after
[Cohesive.Relations static compilation](../../Cohesive.Relations/README.md#demand-driven-static-compilation)
and source placement. In this example, `plan` contains two named result branches over the same
`endsWith(CustomerName, suffix)` filter: paged rows and a global `COUNT`. `placement` maps the source to an
Elasticsearch target, while the versioned `storageBinding` maps the plan's inputs to the `loads-read` index and
records the physical mapping evidence required for exact compilation.

```csharp
using System.Text.Json;
using Cohesive.Adapters.Elastic;
using Cohesive.Model;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.IR;
using Cohesive.Relations.Realization;
using Elastic.Clients.Elasticsearch;

var realization = RelationQueryRealizationCompiler.Compile(
    plan,
    ElasticRelationQueryTargetProfile.Default,
    ElasticRelationQueryTargetProfile.Policy,
    RelationQueryResultObservability.NotRequested);

if (!realization.IsRealizable)
    throw new InvalidOperationException(string.Join(Environment.NewLine, realization.Diagnostics));

var result = new ElasticRelationQueryCompiler().Compile(
    new RelationQueryNativeCompilationRequest(plan, realization, placement),
    storageBinding);

if (!result.IsSuccessful)
    throw new InvalidOperationException(string.Join(Environment.NewLine, result.Diagnostics));

var rows = result.Artifacts.Single(
    artifact => artifact.Branch.Kind == RelationQueryNativeResultKind.QueryRows);
var count = result.Artifacts.Single(
    artifact => artifact.Branch.Kind == RelationQueryNativeResultKind.QueryAggregation);
var parameters = new Dictionary<QueryParameterId, ObservationValue>
{
    [new("suffix")] = ObservationValue.FromString("Inc")
};

SearchRequest rowsRequest = rows.Bind(parameters);
SearchRequest countRequest = count.Bind(parameters);

await client.SearchAsync<JsonElement>(rowsRequest, cancellationToken);
await client.SearchAsync<JsonElement>(countRequest, cancellationToken);
```

Each result branch compiles to its own reusable artifact and must be bound independently. The global count is also
an SDK `SearchRequest`; its exact lowering uses `size: 0` and exact total-hit tracking.

Lowering policy is configurable. For example, this explicit-local preference forces `endsWith` to use an exact
wildcard query instead of the conventional reversed-field prefix strategy:

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

var compiler = new ElasticRelationQueryCompiler(loweringPolicy: suffixPolicy);
var result = compiler.Compile(
    new RelationQueryNativeCompilationRequest(plan, realization, placement),
    storageBinding);
```

The storage binding must attest that the selected field supports
`ElasticRelationQueryFieldSemanticCapabilities.WildcardSuffix`; otherwise compilation returns a structured
diagnostic. Policy decisions participate in the artifact fingerprint and provenance. Advanced users can also
register an `IElasticQueryLoweringStrategy` for a new exact lowering within the adapter's supported physical IR.

After binding, the result is an ordinary, fresh SDK request and can be explicitly adjusted when needed:

```csharp
rowsRequest.Timeout = "5s";
```

That mutation is intentionally outside the compiler's exactness and provenance guarantees; those describe the SDK
request as initially materialized by `Bind`.

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

The immutable physical query IR supports exact term, range, existence, wildcard, prefix, and Boolean clauses with
explicit `filter`, `should`, and `must_not` placement; offset and `search_after` hit pagination; exact total-hit
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

The canonical v1 target profile advertises operation families. A successful artifact still depends on the concrete
storage binding and compiler proving the narrower structural closure for that branch. Row artifacts currently accept
one linear, single-index pipeline; direct field or scalar-constant projections; required non-null scalar predicates;
deterministic field ordering; and a bounded page. Grouped counts accept required non-null text, GUID, or integer keys
through a paged composite aggregation. Unsupported shapes fail with structured diagnostics instead of weakening the
canonical semantics.

Field bindings separately attest their mapping, root-versus-nested document scope, retrieval channel, physical JSON
encoding, and exact query facilities. Canonical v1 reads scalar row values only from root-document `_source` fields.
Nested querying and nested-source extraction are deferred. Temporal values may be projected when their canonical
string encoding is attested, but temporal filtering, ordering, and grouping are deferred because Elasticsearch date
normalization and precision do not yet preserve Cohesive's complete representation and comparison semantics.

Offset artifacts describe one bounded request against that invocation's current view and make no cross-request
continuation claim. `search_after` hit pages and composite after-key pages span a logical sequence, so canonical v1
requires `ElasticRelationQueryPaginationConsistency.StableSearchView`: refreshes are complete and the visible
document set, ordering, and concrete target remain unchanged for the sequence.
Point-in-time-backed pagination over mutable indexes is intentionally deferred until PIT lifecycle and continuation
metadata are represented in the artifact contract.

## Related Packages

- `Cohesive.Relations` for query and aggregation plan definitions.
- `Cohesive.Storage` for read repository abstractions.
