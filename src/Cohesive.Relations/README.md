# Cohesive.Relations

`Cohesive.Relations` lets applications describe relationships, projections, filters, and queries in typed C# while
leaving storage placement and execution strategy to compilers and adapters.

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
