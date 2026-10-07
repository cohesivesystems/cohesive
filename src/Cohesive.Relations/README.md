# Cohesive.Relations

`Cohesive.Relations` lets applications describe relationships, projections, filters, and queries in typed C# while
leaving storage placement and execution strategy to compilers and adapters.

Portable [relation drafts](docs/internals/RELATIONS_AND_QUERIES.md#portable-relation-drafts) support
explicit nested fields, statically keyed object construction, explicit `coalesce` defaults, bounded code-conditionals, key-based collection `join` and collection `select` with shape-aware
acceptance. Selectors preserve item scope and each target child's contract; source collections must
be present and non-null. Explicit `single` requires exactly one selected item, and explicit `parseInt32`/`parseInt64` convert in-range integer text. `parseDecimal` converts
invariant text without rounding. Named enum literals are checked in their owning graph. Implicit
element traversal and undeclared conversions remain unsupported.

Canonical execution IR declares `IImmutableExecutionDefinition`, so repeated typed reads share
document-owned immutable decoding. Integrity, canonical-wire, contextual semantics, and deployment
admission remain independently validated. See [immutable execution preparation](../../docs/performance/immutable-execution-preparation.md).

## Install

```bash
dotnet add package Cohesive.Relations
```

## Author a relation

A complete `Load -> LoadDto` relation needs no hand-authored node IDs, binding names, source placement, or adapter
configuration:

<!-- docs-sync:relations-basic:start -->
```csharp
var author = RelationQuery.Expression();
var loads = author.Source<Load>();

var loadDtos = author.Project(
    loads,
    (Load load) => new LoadDto
    {
        Id = load.Id,
        Status = load.Status
    });

var relation = loadDtos.BuildRelation(dto => dto.Id);
```
<!-- docs-sync:relations-basic:end -->

The authoring session derives the logical nodes, bindings, shapes, field assignments, relation identity, display name,
and provenance. `relation` contains the canonical definition and structured validation diagnostics.

Add a relationship when the result needs another fact:

<!-- docs-sync:relations-traverse:start -->
```csharp
var author = RelationQuery.Expression();
var loads = author.Source<Load>();

var customers = author.Traverse<Load, Customer>(
    loads,
    load => load.CustomerId);

var searchDocuments = author.Project(
    customers,
    (Load load, Customer customer) => new LoadSearchDto
    {
        Id = load.Id,
        CustomerName = customer.Name
    });

var loadSearch = searchDocuments.BuildRelation(dto => dto.Id);
```
<!-- docs-sync:relations-traverse:end -->

The same traversal may become an in-memory lookup, a PostgreSQL join, bounded source reads followed by local
correlation, or another capability-compatible realization. The relation itself does not change.

## Use it for

- DTO mapping and enrichment.
- Independently invoked row and aggregation queries.
- Application read models, API results, integration payloads, and reports.
- Field-demand, dependency, lineage, and missing-input analysis.
- Relation-derived materialized views and targeted rebuild planning.
- Portable execution across supplied objects and registered physical sources.

## How execution stays honest

The canonical relation/query document is the semantic authority. An invocation selects results and parameters;
compilation derives the exact fields and operations it needs. A target must prove those requirements against its
capabilities and operating boundaries. Unsupported semantics and incomplete source evidence produce structured
diagnostics rather than a weakened query or a misleading empty result.

The expression API is the normal application surface. Structural authoring and direct IR construction remain
available for importers, generators, persistence tooling, and compiler tests.

## Continue

- [Getting started](docs/GETTING_STARTED.md) builds, evaluates, and enriches a relation.
- [Execution and adapters](docs/EXECUTION_AND_ADAPTERS.md) covers supplied facts, acquisition, placement, and native
  compilation.
- [Diagnostics](docs/DIAGNOSTICS.md) explains incomplete evidence and requirement gaps.
- [Capability reference](docs/CAPABILITIES.md) is generated from the implemented target profiles.
- [Migration](docs/MIGRATION.md) covers the retired relation-query stack.
- [Internals](INTERNALS.md) retains the complete semantic model, compiler architecture, use cases, and design
  rationale.

Adapter-specific guides are available for
[`Cohesive.Adapters.Postgres`](../adapters/Cohesive.Adapters.Postgres/README.md),
[`Cohesive.Adapters.Cosmos`](../adapters/Cohesive.Adapters.Cosmos/README.md), and
[`Cohesive.Adapters.Elastic`](../adapters/Cohesive.Adapters.Elastic/README.md).

## Typed row-query results

Expression authoring can retain a typed input and public application result with
`BuildQuery(id, name, rows, parameter, result: values => ...)`. The returned
`RelationQuery<TInput,TResult>` captures the existing canonical query, shape documents and relationship
catalog without compiling or selecting a backend. Its row type is inferred and can be anonymous;
constructor projections still require verified direct field initialization/getters. `Where` retains a
focused binding for the existing typed traversal/projection overloads.

The `result` callback is a local presentation projection of complete typed rows. It explicitly handles
empty results and nesting; it is not serialized as portable query semantics. The shared boundary performs
observation decoding, so consumers invoke a typed prepared query instead of maintaining intermediary
DTO conversions. This convenience currently requires one exact invocation parameter and one row result;
additional or foreign parameters fail closed. Existing structural query and HostedQuery APIs retain their
separate responsibilities. See the [fulfillment example](../../eng/examples/aspire-first/README.md) for
backend registration and fluent API binding.

Prepared typed readers implement `IRelationQueryReader<TInput,TResult>` and retain the exact authored
`Definition`; HTTP bindings can accept that object without taking a dependency on its backend. The narrower
`IRelationQueryRowsReader` contract describes complete bounded rows before typed projection, including
missing-field versus null semantics. Neither contract silently discards partiality or adds retry.
These convenience contracts do not replace `IRelationQueryEvaluator`: the existing composed execution path
supports cross-source joins and retains phase artifacts, requirement gaps and source-read traces. Native
PostgreSQL selection remains explicit; no automatic native-versus-federated dispatcher is introduced.

### Preparation exception migration (unreleased PR405)

`RelationQueryPreparationException` now derives from `PreparationException`, which derives from
`InvalidOperationException`; it no longer derives from `ArgumentException`. Callers previously using
`catch (ArgumentException)` for preparation must catch `RelationQueryPreparationException` or the shared
`PreparationException` instead. Argument validation errors remain separate. The previous default
`relationQuery.preparation.invalid` code is replaced by `relationQuery.preparation.semantic` or
`relationQuery.preparation.physical`, based on the failed preparation phase. Update code-based handlers
accordingly. Explicit `relationQuery.subplan.*` codes retain their meanings, including `resultUnsupported`
for a successfully compiled query whose result contract is not supported by subplans. Original semantic
and physical compiler evidence stays available. No compatibility catch or code alias is introduced.


### Fluent query rows and array results

Focused nodes use `Where` and `Select`; joined nodes retain both bindings for a two-argument `Select`.
`Join` means inner join and `LeftJoin` preserves unmatched left rows. Typed `Traverse` and
`TraverseInverse` optionally accept a result selector over the starting and related rows, so application
code need not manually carry `.Binding` values into a session-level projection.

```csharp
var demand = orders.TraverseInverse(reservationOrder,
    (order, reservation) => new Demand(order.Id, reservation.Sku));
var definition = demand
    .LeftJoin(inventory, (row, item) => row.Sku == item.Sku)
    .Select((row, item) => new Availability(row.OrderId, item.Available))
    .ToArray(id: new("availability"), name: new("Availability"), parameter: orderId);
```

This is expression authoring, not `IQueryable` or deferred backend execution. `ToArray` captures a
`RelationQuery<TInput, TRow[]>`; adapters still own compilation and execution. It returns an empty array
for no complete rows, preserves declared ordering only, and creates the typed output array once per
projection. Traversal retains existing left/missing-value semantics; selectors lower to canonical IR
rather than executing arbitrary CLR callbacks. Existing session ownership and visibility checks apply.

Migration from alpha.132: fluent node `Project` becomes `Select`; node `Join(..., JoinKind.Left)` becomes
`LeftJoin(...)`. Explicit session `Project`/`Join` remain available for structural/multi-binding work.
There are no duplicate fluent aliases or new IR nodes. Fingerprint equivalence tests cover both directions
of traversal and inner/left joins. Query identities, invocation parameters and partitions remain explicit.
