# Migrating to Canonical Cohesive.Relations v1

The prototype relation/query, hydration, repository-query, and compatibility stacks were deleted. Canonical v1 has
one semantic authority: `Cohesive.Relations.IR.RelationDefinition` or `QueryDefinition`, persisted in a
`RelationQueryDocument`. There is no compatibility shim and no automatic loader for prototype JSON.

This is an intentional breaking migration. Reauthor definitions through the expression or structural producer, or
write an explicit one-time importer that emits and validates canonical documents.

## Migration at a glance

| Deleted or legacy concept | Canonical v1 replacement | Semantic change |
| --- | --- | --- |
| Prototype `Cohesive.Relations.Model.RelationDefinition` | `Cohesive.Relations.IR.RelationDefinition` or `QueryDefinition` | The definition is a logical graph plus a terminal contract. |
| `Relation.Define`, relation/join/map builders | `RelationQuery.Expression()` with `Source`, `Traverse`, `Join`, `Filter`, `Project`, and terminals | Expression authoring immediately lowers through the canonical structural core. |
| `MappingDefinition`, `FieldAssignment` | `ProjectQueryNode` assignments and `RelationOutputDefinition` | Mapping is semantic projection; CLR materialization happens later. |
| `JoinDefinition`, `JoinSpec`, `JoinOne`, `JoinMany` | `Traverse` for catalog relationships; `Join` for an explicit predicate | Traversal retains relationship identity, direction, cardinality, and requirement. |
| Runtime `JoinContext` alias lookup | Typed lambda parameters and binding handles | No alias-based object lookup during mapping. |
| `EntityPredicate`, `BoolExpr`, `MemberSelector` | Canonical `Expr`, normally produced by C# expressions | Predicates are portable, persistable, analyzable, and target-compilable. |
| `AggregationPlan` and builder | `Aggregate`, `Aggregation`, and named query results | Rows and aggregation can share one canonical logical scope and acquisition. |
| `EntityQuery`, row/aggregation query variants | `QueryDefinition` with row and aggregation result definitions | Ordering, paging, and result branches are graph semantics. |
| Query `FieldSelection` | Evaluation `.Select(...)` and `RelationQueryCompilationDemand` | Demand drives static graph and field pruning. |
| `RelationQueryInvocation` and `.Invoke(...)` | `RelationQueryEvaluation` and `.Evaluate(...)` | One invocation model covers relations and queries, parameters, supplied roots, demand, and attribution. |
| `IExecutableQuery`, `QueryExecutionEngine` | `IRelationQueryEvaluator.EvaluateAsync(...)` | One gateway retains compilation through canonical interpretation. |
| `EntityQueryResponse<T>` | `RelationQueryEvaluationOutcome.Result`, then relation or named query results | Partiality, gaps, diagnostics, and provenance stay explicit. |
| Query repositories and registries | `IRelationQuerySourceReader`, source registrations/catalog, and `IRelationQueryEvaluator` | Readers acquire bounded evidence; canonical execution owns relational semantics. |
| `IEntityQueryRepository` | Evaluator plus registered source readers | Entity repositories retain point storage operations, not semantic query interpretation. |
| Hydration store/planner/hydrator | Static compiler, source placement, physical planner, and physical executor | Required fields and related reads are demand-derived and completeness-aware. |
| Hydration-plan records and options | Compiled input contract, placement, physical plan, and source-read requests | Bounds, provenance, and missing evidence are explicit. |
| `RelationMappingRuntime` and runtime inputs | Evaluation `.Supply(...)`, execution, then `RelationDtoMapperCompiler` | Acquisition, canonical evaluation, and CLR construction are separate stages. |
| Prototype relation JSON and serializer | Strict `RelationQueryDocument` and `RelationshipCatalogDocument` serializers | Fingerprinted canonical documents replace prototype wire shapes. |

`Cohesive.Model.FieldSelection` remains valid for repository point reads. It is not the semantic query-demand API.

## Before: prototype mapping and hydration

The following is representative historical code, not a compilable compatibility surface:

```csharp
// Deleted prototype API
var relation = Relation.Define<Load>()
    .JoinOne<Customer>(load => load.CustomerId)
    .MapFields<LoadSearchDto>((load, customer) => new()
    {
        Id = load.Id,
        CustomerName = customer.Name
    });

var dto = await relationHydrator.HydrateAsync(load, relation);
```

The prototype combined semantic definition, acquisition, and mapping behind a hydration facade. Unsupported
placement, missing data, and completeness behavior were difficult to inspect or compile consistently across targets.

## After: canonical authoring, evaluation, and mapping

Author the same relation with the expression producer:

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
        CustomerName = customer.Name
    });

var relation = documents.BuildRelation(dto => dto.Id);
```

Invoke it with explicit supplied-root evidence:

```csharp
var evaluation = author
    .Evaluate(relation, new("load-search/load-42"))
    .Supply([load], static value => value.Id)
    .Build();

var outcome = await evaluator.EvaluateAsync(evaluation, cancellationToken);
```

Then materialize canonical output rows:

```csharp
var mapperCompilation =
    RelationDtoMapperCompiler.Default.Compile<LoadSearchDto>(outcome.Compilation.Plan!);

var mapped = mapperCompilation.Mapper!.Map(
    outcome.PhysicalExecution!,
    RelationDtoMappingFailurePolicy.CollectDiagnostics);
```

This separation is deliberate:

- The relation document is portable and persistable.
- The compiled input contract says exactly which Load and Customer fields are required.
- Placement and source readers decide where those facts are acquired.
- Requirement gaps explain missing Customer evidence.
- The same plan can be interpreted in memory or lowered by a supported adapter.
- The compiled mapper performs CLR construction without becoming a second relation model.

## Traverse versus explicit Join

Use `Traverse` when the correlation is a reusable domain relationship:

```csharp
var customers = author.Traverse<Load, Customer>(
    loads,
    load => load.CustomerId);
```

The relationship catalog records its source reference, target key, direction, and cardinality. Use `Join` when the
predicate itself is the query semantics and no catalog relationship should be asserted:

```csharp
var matched = author.Join(
    left.Node,
    right.Node,
    JoinKind.Inner,
    (Left l, Right r) => l.Code == r.Code,
    left.Binding,
    right.Binding);
```

Do not recreate legacy `JoinContext` aliases around either operation.

## Query and aggregation migration

Rows and aggregations are named terminal branches over one logical graph:

```csharp
var rows = author.Rows(documents, id: "rows");
var aggregation = author.Aggregation(summary, id: "summary");

var query = author.BuildQuery(
    new QueryId("load-search"),
    new QueryName("LoadSearch"),
    rows,
    aggregation);
```

Select result fields on the evaluation. The resulting demand is applied during static compilation, not as a
post-query response filter:

```csharp
var evaluation = author
    .Evaluate(query, new("load-search/request-42"))
    .Select(rows, dto => dto.Id, dto => dto.CustomerName)
    .Select(aggregation)
    .Build();
```

## Host-consumer migration

Replace executable-query dispatch with one canonical evaluation gateway:

```csharp
IRelationQueryEvaluator evaluator = new RelationQueryEvaluator(
    plan => placementCatalog.Resolve(plan),
    physicalPlanningPolicy,
    sourceReaders);

var outcome = await evaluator.EvaluateAsync(evaluation, cancellationToken);
```

HTTP hosts use `RelationQueryApiOperationBinding.Evaluate(...)` and endpoint `.RelationQuery(...)`, creating one
evaluation per request and explicitly projecting the outcome to the API response contract. Durable processes use
`EvaluateRelationQueryNode`/`AddRelationQueryEvaluationNode` and project the outcome to a declared durable result;
the complete in-process outcome is not a checkpoint contract.

## Cosmos migration

Remove `IEntityQueryRepository` implementations and the deleted entity-query compilers. Register bounded entity
sources and execute canonical evaluations through `IRelationQueryEvaluator`. `CosmosRelationQuerySourceReader`
acquires exact placed inputs through the Cosmos SDK; the physical executor owns batching, traversal correlation, and
canonical interpretation.

For supported single-container native branches, use `CosmosRelationQueryCompiler` and
`CosmosRelationQueryArtifactExecutor`. The standalone `CosmosSqlBuilder` remains available for explicitly
hand-authored Cosmos SQL, but such statements are not canonical Relations definitions.

Cross-document relationships are not Cosmos SQL `JOIN`s. Use composed bounded reads as described in
[Execution and adapters](EXECUTION_AND_ADAPTERS.md#cosmos-two-logical-source-read-stages).

## Adapter compiler migration

All target compilers consume a successful `CompiledRelationQueryPlan`, profile feasibility, exact placement, and
adapter binding evidence. Do not call a legacy compiler with a parallel query object.

- Cosmos: `CosmosRelationQueryCompiler` -> validated SDK artifact execution.
- Elasticsearch: `ElasticRelationQueryCompiler` -> inspectable Elasticsearch SDK request descriptors.
- PostgreSQL: `PostgresRelationQueryCompiler` -> provider-neutral parameterized SQL and ordered values.

Backend-specific preferences belong in binding builders, compiler options/providers, scoped policy, or attributable
overrides. Do not edit generated artifacts as an untracked source of semantic behavior.

For PostgreSQL composed acquisition, register `PostgresRelationQuerySourceReader` against the full semantic plan, its
exact physical plan, source identity resolved from that plan, and persisted PostgreSQL binding instead of adding a
query repository. It uses a caller-owned single-host `NpgsqlDataSource` for bounded enumeration and set-oriented
point/relationship-key batches; public registration also requires a `PostgresNpgsqlRuntimeBinding` that attests the
persisted database identity, exact data-source instance, sanitized endpoint fingerprint, and non-secret authority.
`PostgresMaterializationSource` can wrap that reader for item/byte-bounded rebuild or reconciliation pages with an
opaque HMAC-authenticated keyset continuation and caller-managed secret. It does not provide cross-page snapshot,
change-feed, settlement, or write-target guarantees.

## Diagnostics migration

Replace catch-all mapping/hydration exceptions and null checks with phase-specific structured results:

```csharp
if (!outcome.Diagnostics.IsDefaultOrEmpty)
    Handle(outcome.Diagnostics);
if (!outcome.Compilation.Diagnostics.IsDefaultOrEmpty)
    Handle(outcome.Compilation.Diagnostics);
if (outcome.Realization is { Diagnostics.IsDefaultOrEmpty: false } realization)
    Handle(realization.Diagnostics);
if (outcome.PhysicalPlanning is { Diagnostics.IsDefaultOrEmpty: false } planning)
    Handle(planning.Diagnostics);
if (outcome.PhysicalExecution is { Diagnostics.IsDefaultOrEmpty: false } execution)
    Handle(execution.Diagnostics);
if (outcome.Result is { } result)
    Handle(result.RequirementGapAnalysis.Gaps, result.Diagnostics);
```

A completed empty relationship lookup, a partial read, a failed acquisition, a missing field, and an explicit null
are distinct states. See [Diagnostics](DIAGNOSTICS.md) before selecting an application policy.

## Persisted documents and versions

Current portable and adapter contracts expose their schema versions through constants, including:

- `relationship-catalog/v1`
- `relation-query/v1`
- `relation-draft/v1`
- `relation-query-evaluation/v2`
- `relation-query-source-placement/v3`
- `relation-query-physical-plan/v1`
- `relation-query-explain/v1`
- Cosmos SQL profile `canonical-v2` and binding `cosmos-binding/v5`
- Elasticsearch profile `canonical-v2` and binding `elastic-binding/v4`
- PostgreSQL SQL profile `canonical-v2`, source-reader profile `source-reader-v1`, binding `postgres-binding/v1`, and
  artifact `postgres-artifact/v3`

Migration rules:

1. Prototype relation/query JSON cannot be deserialized as canonical IR. Reauthor or explicitly import it.
2. Serialize through the dedicated strict document serializers; do not depend on incidental default JSON behavior.
3. Regenerate fingerprints after producing a canonical document under the current canonicalization profile.
4. Regenerate non-current adapter bindings, bound-realization reports, and native artifacts together.
5. Reauthor `relation-query-evaluation/v1` supplied-root documents as v2 with an explicit provider-neutral logical
   partition identity; use `RelationQueryLogicalPartitionIdentity.WholeSource` only for genuinely unpartitioned data.
6. Treat `RelationQueryEvaluationOutcome` and physical execution results as in-process composites, not durable wire
   contracts.
7. Treat persisted executable SQL artifacts as trusted code. PostgreSQL rehydration is intentionally named
   `DeserializeTrusted`; fingerprints detect inconsistency but are not cryptographic signatures.

Source-placement v3 adds optional semantic identity-path evidence independently from the adapter-interpreted physical
identity selector. Re-author typed or structural placements to retain that path when identity is a semantic shape
field; source-native identity such as `$identity` remains valid without one. Recompute placement and downstream
physical-plan fingerprints because the v3 canonicalization distinguishes absent and present semantic paths.

PostgreSQL artifact v3 tightens the persisted `TimestampWithTimeZone` constant domain to finite, microsecond-aligned
UTC values with a zero offset. Artifact v2 allowed non-zero-offset `DateTimeOffset` constants and used a different
canonicalization profile, so it is intentionally not rehydrated as v3. Recompile the canonical plan against its exact
storage binding and persist the newly fingerprinted v3 artifact; changing only `schemaVersion` or the fingerprint is
not a semantic migration.

Legacy parameter documents that omit `defaultKind` remain readable where documented, but an old JSON null cannot be
recovered as an explicit-null default because the historical encoding did not preserve that distinction.

## Migration checklist

- [ ] Reauthor or import prototype definitions into canonical relation/query documents.
- [ ] Replace relationship joins with `Traverse`; keep only true predicate joins as `Join`.
- [ ] Move field shaping into canonical `Project` nodes.
- [ ] Replace invocation and executable-query types with `RelationQueryEvaluation` and `IRelationQueryEvaluator`.
- [ ] Replace query repositories and hydration stores with placed bounded source readers.
- [ ] Bind PostgreSQL sources through `PostgresRelationQuerySourceReader`; use `PostgresMaterializationSource` only
      when per-statement reconciliation semantics satisfy the rebuild consistency policy.
- [ ] Configure requirement-gap policy explicitly where conventional incomplete output is not appropriate.
- [ ] Compile CLR DTO mappers from the canonical plan.
- [ ] Rebind adapter placement and storage evidence; regenerate target artifacts.
- [ ] Update HTTP/process consumers to project canonical outcomes intentionally.
- [ ] Re-persist canonical documents and verify fingerprints and schema versions.
- [ ] Add conformance tests comparing target results with the in-memory reference interpreter.
