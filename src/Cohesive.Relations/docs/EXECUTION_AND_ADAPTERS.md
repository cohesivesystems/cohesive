# Execution and Adapters

One canonical relation/query definition can be interpreted through several execution paths. A backend artifact is a
derived, fingerprinted interpretation of that definition; it never becomes a second semantic authority.

```text
expression authoring
-> canonical relation/query document
-> demand-scoped static plan
-> target-profile feasibility
-> exact placement and adapter binding
-> contextual realization
-> physical execution or native artifact
-> canonical result and optional CLR DTO
```

The early phases are target-independent. Placement and adapter binding are introduced only where a runtime must
decide where facts live and which physical evidence proves the requested semantics.

## Definition, evaluation, and result

A `RelationDefinition` is reusable and rooted in supplied input. A `QueryDefinition` has independently acquired,
named row or aggregation results. A `RelationQueryEvaluation` is one invocation of either definition: parameter
values, selected outputs, supplied roots, and exact semantic snapshots are all explicit.

```csharp
var evaluation = author
    .Evaluate(relation, new("load-search/load-42"))
    .Supply([load], static value => value.Id)
    .Build();

RelationQueryEvaluationOutcome outcome =
    await evaluator.EvaluateAsync(evaluation, cancellationToken);
```

The outcome retains the phase artifacts rather than flattening them into an opaque response:

- `Compilation` contains validation, selected fields, requirements, lineage, and dependency manifests.
- `Realization` says whether each demand can be native, composed, constrained, overridden, or is unavailable.
- `Placement` records the exact physical source choices and their provenance.
- `PhysicalPlanning` contains bounded acquisition and local-computation stages.
- `PhysicalExecution` records source-read traces and the canonical interpretation.
- `Result` contains relation rows or named query rows/aggregations, completeness, diagnostics, and requirement gaps.

`RelationQueryExplainProjector.Project(outcome)` creates a portable, sanitized explain artifact from those retained
facts without rerunning the query. `RelationQueryCapabilitySummaryProjector` indexes the exact profile and
realization evidence by canonical capability.

## In-memory reference and composed execution

`RelationQueryInMemoryInterpreter.Default` is the semantic reference interpreter. It evaluates a compiled plan over
explicit `RelationQueryRuntimeEvidence`; tests and adapters use it for differential conformance. Application hosts
normally use `RelationQueryEvaluator`, which adds deterministic placement, bounded source acquisition, physical
planning, and the same reference interpretation:

```csharp
IRelationQueryEvaluator evaluator = new RelationQueryEvaluator(
    plan => placementCatalog.Resolve(plan),
    physicalPlanningPolicy,
    sourceReaders);
```

This configuration is host bootstrap, not relation authoring. `IRelationQuerySourceReader` implementations return
observations plus explicit completeness and field evidence. They do not independently implement filters, joins,
aggregations, or output shaping.

For a cross-source relationship the physical planner can produce:

```text
BoundedEnumeration(Loads)
-> extract distinct CustomerId values
-> IdentityBatch(Customers)
-> LocalCorrelation(HashJoin)
-> Project(LoadSearchDto)
```

Limits for rows, keys per batch, buffering, fan-out, and concurrency are part of source placement and physical
planning policy. Exceeding a proven bound fails or chunks according to the explicit plan; it does not silently switch
to unbounded or per-row acquisition.

## PostgreSQL native join versus Cosmos composed reads

The physical difference is easiest to see with an independently acquired query. Author the Load-to-Customer
semantics once:

```csharp
var author = RelationQuery.Expression();
var loads = author.Source<Load>();
var customers = author.Traverse<Load, Customer>(
    loads,
    load => load.CustomerId);

var documents = author.Project(
    customers,
    (Load load, Customer customer) => new LoadSearchDto
    {
        Id = load.Id,
        CustomerId = load.CustomerId,
        CustomerName = customer.Name
    });

var rows = author.Rows(documents, id: "rows");
var query = author.BuildQuery(
    new QueryId("load-search"),
    new QueryName("LoadSearch"),
    rows);

var compilation = RelationQueryStaticCompiler.Compile(new(
    query.CreateDocument(),
    author.ShapeDocuments,
    author.CreateRelationshipCatalogDocument()));

var plan = compilation.Plan
    ?? throw new InvalidOperationException(string.Join(Environment.NewLine, compilation.Diagnostics));
```

Only placement and interpretation change after this point.

### PostgreSQL: one native statement

Bind Load and Customer to tables in the same `RelationQueryExecutionDomainId`, and bind the reference field and
Customer identity as exact PostgreSQL columns. Profile feasibility is then qualified against those concrete facts:

```csharp
var feasibility = RelationQueryRealizationCompiler.Compile(
    plan,
    PostgresRelationQueryTargetProfile.Default,
    PostgresRelationQueryTargetProfile.Policy,
    RelationQueryResultObservability.NotRequested);

if (!feasibility.IsRealizable)
    throw new InvalidOperationException(string.Join(Environment.NewLine, feasibility.Diagnostics));

var request = new RelationQueryBoundRealizationRequest(
    plan,
    feasibility,
    placement.Placement);

var compiler = new PostgresRelationQueryCompiler();
var bound = compiler.Realize(request, storageBinding);

if (!bound.IsRealizable)
    throw new InvalidOperationException(string.Join(Environment.NewLine, bound.Diagnostics));

var nativeRequest = new RelationQueryNativeCompilationRequest(
    plan,
    bound,
    placement.Placement);

var native = compiler.Compile(nativeRequest, storageBinding);

if (!native.IsSuccessful)
    throw new InvalidOperationException(string.Join(Environment.NewLine, native.Diagnostics));

var artifact = native.Artifacts.Single();
var statement = artifact.Bind(
    new Dictionary<QueryParameterId, ObservationValue>());

var capabilityEvidence = bound.Evidence.Assessments
    .SelectMany(assessment => assessment.CapabilityEvidence)
    .ToArray();
var explain = PostgresRelationQueryExplainProjector.Project(nativeRequest, native);

Console.WriteLine(statement.Text);
Console.WriteLine($"invocation parameters: {artifact.Parameters.Length}"); // 0
Console.WriteLine($"bound statement parameters: {statement.Parameters.Length}"); // 1 compiler literal
Console.WriteLine($"capability evidence: {capabilityEvidence.Length}");
Console.WriteLine($"explain: {explain.Status}");
```

The resulting provider-neutral command is one parameterized `SELECT` whose generated SQL contains one inline
`LEFT JOIN` for this query. Its outer aliases use canonical output names; selected fields, result reconstruction,
lowering decisions, bound-realization fingerprint, and plan provenance remain on the artifact.

```sql
SELECT
    "LoadSearchDto_result"."LoadSearchDto__id" AS "id",
    "LoadSearchDto_result"."LoadSearchDto__customerId" AS "customerId",
    "LoadSearchDto_result"."LoadSearchDto__customerName" AS "customerName"
FROM (... "loads" ...
LEFT JOIN "customers" ...
    ON ... "customer_id" = ... "customer_id") AS "LoadSearchDto_result"
```

The ellipses above abbreviate deterministic compiler-generated subquery aliases; they are not hand-authored SQL.
This query declares no invocation parameters (`artifact.Parameters` is empty), but its bound statement contains one
compiler-owned Boolean presence literal used to reconstruct the outer join. Adding a parameterized predicate adds
its value in positional-placeholder order; values never become interpolated SQL text. `artifact.SelectedFields`
carries the exact semantic input columns, each bound assessment retains the capability-evidence identities and
operating boundaries that made it available, and the payload-free explain stage retains the plan, placement,
bound-realization, storage-binding, and artifact fingerprints.

The executable
[`PostgresCosmosGuide_CustomerOnlyRowsUseOnePostgresJoinAndRejectNativeCosmosTraversal`](https://github.com/cohesivesystems/cohesive/blob/main/src/Cohesive.Tests/Relations/CanonicalFederatedAdapterConformanceTests.cs)
test asserts a single artifact, a single statement, exact selected semantic fields, and the inline join.
`PostgresRelationQueryExplainProjector.Project(nativeRequest, native)` projects its payload-free native explain
stage.

The compiler and standalone SQL builder deliberately remain provider-neutral: a native artifact binds to
`PostgresSqlStatement.Text` and ordered CLR parameter values, and the application still owns dispatch of that native
statement. The single adapter package also provides a separate Npgsql-backed canonical source path. See the
[PostgreSQL adapter guide](https://github.com/cohesivesystems/cohesive/blob/main/src/adapters/Cohesive.Adapters.Postgres/README.md)
for complete placement, storage binding, standalone SQL construction, temporal domains, and runtime responsibilities.

`PostgresRelationQuerySourceReader` implements bounded enumeration, identity point/batch lookup, and parameterized
relationship-key predicate batches for composed physical plans. Each canonical request is one set-oriented,
parameterized Npgsql statement over the exact bound table and fields; key batches use a typed array predicate instead
of one command per key. The source and physical policy retain explicit key, row, buffering, fan-out, and concurrency
boundaries, and expected provider failures return attributable canonical evidence. The reader borrows a caller-owned
single-host `NpgsqlDataSource`; ambient transactions and multi-host replica selection are rejected rather than treated
as hidden consistency evidence.

`PostgresMaterializationSource` reuses that reader for bounded rebuild and reconciliation pages. It enforces item and
canonical encoded-byte limits and resumes through an opaque keyset continuation over a bound UUID or ordering-proven
ordinal-text identity. Each page is a new PostgreSQL statement snapshot, including after pause/resume. The source can
therefore claim stable ordering, request-local completeness, and reconciliation, but not a coordinated cross-page
snapshot. It does not provide change delivery, settlement, or a PostgreSQL write target.

### Cosmos: two logical source-read stages

Cosmos SQL compilation is intentionally single-container. It must reject a traversal between separately stored Load
and Customer documents instead of pretending Cosmos's intra-document array `JOIN` is a relational cross-document
join. The supported cross-document path is composed execution:

```text
Cosmos Load source reader
  BoundedEnumeration(maximumRows)
  selected: Id, CustomerId

canonical physical executor
  distinct CustomerId: customer-1, customer-2

Cosmos Customer source reader
  IdentityBatch(customer-1, customer-2)
  identity selector: Id
  selected semantic fields: Name

canonical physical executor
  LocalCorrelation(HashJoin)
  Project LoadSearchDto
```

For the bounded executable example, all distinct customer identities fit in one batch. Its assertions are:

```csharp
Assert.Single(loadReader.Requests);
Assert.IsType<RelationQueryBoundedEnumeration>(
    loadReader.Requests.Single().Constraint);

Assert.Single(customerReader.Requests);
var customerBatch = Assert.IsType<RelationQueryIdentityBatchLookup>(
    customerReader.Requests.Single().Constraint);

Assert.Equal(
    ["customer-1", "customer-2"],
    customerBatch.Identities);

var explain = RelationQueryExplainProjector.Project(outcome);
```

The executable canonical-physical-plan
[`PostgresCosmosGuide_ComposedExecutionEnumeratesLoadsAndBatchesCustomersWithoutNPlusOne`](https://github.com/cohesivesystems/cohesive/blob/main/src/Cohesive.Relations.Tests/RelationQueryDocumentationExamplesTests.cs)
test uses deterministic readers with the same primitive acquisition contract. Ten Loads repeat two references, so
it proves one logical Customer batch rather than ten Customer reads. The adapter-level
[`BatchedLookups_ChunkDeterministicallyAndDeduplicateRelationshipRows`](https://github.com/cohesivesystems/cohesive/blob/main/src/Cohesive.Tests/Cosmos/CosmosRelationQuerySourceReaderTests.cs)
test separately proves that `CosmosRelationQuerySourceReader` lowers bounded batches to the Cosmos SDK, chunks them,
and deduplicates returned relationship rows. If the distinct key set exceeds the declared request, partition, or
batch limit, the related acquisition stage issues multiple bounded chunks. The general guarantee is therefore one
Load enumeration plus one or more batched Customer requests—not a universal claim of exactly two SDK calls.

The separate reads do not provide an atomic cross-container snapshot. Consistency, ordering, partition routing,
completeness, and failure evidence are retained as explicit adapter and source-read boundaries. A missing Customer
produces the same canonical requirement gap and partiality semantics as any other exact execution path; see
[Diagnostics](DIAGNOSTICS.md).

`CosmosRelationQuerySourceReader` lowers each bounded source request to the Cosmos SDK. The
[Cosmos adapter guide](https://github.com/cohesivesystems/cohesive/blob/main/src/adapters/Cohesive.Adapters.Cosmos/README.md)
covers container bindings, partition constraints, query chunking, the SDK artifact executor, and the independently
usable `CosmosSqlBuilder`.

### What stays the same

| Semantic fact | PostgreSQL | Cosmos cross-document composition |
| --- | --- | --- |
| Canonical definition and fingerprint | Same | Same |
| Demanded fields and lineage | Same | Same |
| Relationship identity and direction | Same | Same |
| Missing-Customer policy | Same | Same |
| Correlation realization | Native `LEFT JOIN` | Batched lookup plus local hash join |
| Predicate placement | Supported predicates lower into SQL | Source constraint when supported; otherwise bounded local evaluation |
| Projection placement | Exact selected columns in SQL | Exact fields per source read, then canonical output projection |
| Physical read boundary | One database statement | One root read plus bounded related batches |
| Shared snapshot | Database/transaction binding must prove it | Not implied across separate reads |
| Explain and provenance | Native artifact and binding evidence | Physical-plan stages and source-read traces |

The executable
[`CanonicalFederatedAdapterConformanceTests`](https://github.com/cohesivesystems/cohesive/blob/main/src/Cohesive.Tests/Relations/CanonicalFederatedAdapterConformanceTests.cs)
uses one demand-scoped Load scenario to keep those boundaries honest. PostgreSQL realizes the co-located row and
aggregation branches natively. Cosmos SQL and Elasticsearch realize supported single-source aggregation branches,
while their profiles reject the cross-source row traversal before binding or native compilation. Cosmos then uses
the composed source-reader path above; a denormalized Elasticsearch document can instead use the adapter's
single-index query surface. These are explicit capability outcomes, not silently different definitions.

`IRelationQueryEvaluator` currently owns the composed physical path. When a `PostgresRelationQuerySourceReader` is
registered for a placed source, that path performs its bounded acquisition through Npgsql. The evaluator still does
not automatically choose or execute a PostgreSQL native artifact; native statement dispatch remains an explicit
application integration.

## Cosmos SQL and SDK execution

For a single supported container branch, `CosmosRelationQueryCompiler` consumes an exact contextual realization and
emits a `CosmosRelationQueryArtifact`. `CosmosRelationQueryArtifactExecutor` validates artifact, container, parameter,
and result affinity before using the Cosmos SDK. The compiler supports only the closure advertised by
`CosmosRelationQueryTargetProfile.Default`; unsupported cross-source or value semantics produce structured native
compilation diagnostics.

`CosmosSqlBuilder` is a lower-level, independently usable construction API for explicitly hand-authored Cosmos SQL.
Using it does not turn hand-authored SQL into canonical Relations semantics. Keep direct construction local and
explicit when no canonical definition is intended.

## Elasticsearch SDK lowering and overrides

`ElasticRelationQueryCompiler` emits an SDK request descriptor, not raw JSON. The adapter validates the constructed
query and aggregation container and lets an application inspect or locally modify the SDK request before execution.
Its binding authoring surface controls field paths, scalar/keyword semantics, nested-object correlation, paging, and
provider choices. Ambiguous lowerings are selected through explicit deterministic providers rather than hidden
compiler behavior.

See the [Elasticsearch adapter guide](https://github.com/cohesivesystems/cohesive/blob/main/src/adapters/Cohesive.Adapters.Elastic/README.md)
for a complete query that filters Customer name, tests a correlated Stop location, emits rows and aggregation, and
overrides compilation.

## Selected fields and branch demand

Invocation selection becomes `RelationQueryCompilationDemand`; it is not applied after a backend returns all fields.
For example:

```csharp
var evaluation = author
    .Evaluate(query, new("load-search/request-42"))
    .Select(rows, dto => dto.Id, dto => dto.CustomerName)
    .Select(aggregation)
    .Build();
```

Static compilation prunes unselected outputs and unrelated logical branches, then retains fields needed by the
remaining predicates, traversal keys, projection assignments, ordering, grouping, aggregation, identity, and
invariants. Adapter `SelectedFields` and physical source-read fields point back to those exact compiled inputs.

## Placement, configuration, and overrides

Configuration precedence remains visible:

1. Explicit local declarations and overrides.
2. Scoped application or subsystem profiles.
3. Adapter/compiler conventions.
4. Framework defaults.

`RelationQueryPlacement.For(plan)` produces plan-bound typed or structural placement handles. Adapter binding
builders then map those handles to containers, indexes, tables, columns, selectors, encodings, collations, temporal
domains, or partition rules. Every convention decision records its origin; explicit overrides are local and
fingerprinted. A target may use an override only through the canonical realization contract, where the override's
capabilities and preserved guarantees remain attributable.

Consult [Capabilities](CAPABILITIES.md) for the generated profile inventory and the difference between profile
feasibility and exact contextual realization.

## Current limits and deferred work

- `RelationQueryEvaluator` executes the composed physical path. Automatic native-artifact selection and driver
  dispatch are not part of the evaluator yet.
- PostgreSQL native compilation still emits provider-neutral parameterized SQL. Npgsql-backed bounded source
  acquisition and rebuild/reconciliation materialization paging are available; automatic native-artifact dispatch,
  change-feed acquisition, write/update targets, extraction of a shared SQL substrate, and additional SQL dialects
  remain separate follow-ups.
- Cosmos SQL remains a single-container compiler. Cross-container enrichment uses bounded reads and does not imply
  one atomic snapshot. Partition-aware batching can produce more than one SDK request per logical related stage.
- Elasticsearch supports the documented SDK query/aggregation closure and narrow correlated collection
  membership. Broader nested-query and collection operators remain deferred.
- Gremlin compilation and execution remain deferred.
- The PostgreSQL source can feed the Cohesive.Storage materialization ports, but the complete index rebuild and
  real-time synchronization engine, retry, throttling, and orchestration belong to the separate
  Cohesive.Storage/Cohesive.Control workstream.
- Ari's graph proposal UI and AI-specific proposal evidence remain Ari-owned producer concerns; Ari lowers accepted
  semantics into the same canonical relation/query documents.
