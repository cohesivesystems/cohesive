# Cohesive.Relations internals: semantic model

This document retains the complete semantic model, compiler architecture, advanced examples, and design rationale
behind the [package overview](../../README.md).

Portable relational programming for .NET and heterogeneous data systems.

`Cohesive.Relations` brings the declarative character of database-oriented relational programming into a general-purpose programming environment.

SQL makes it possible to describe facts, relationships, projections, filters, and aggregations without prescribing an execution algorithm. Its usual limitation is that the program is scoped to a particular database catalog, server, dialect, and execution boundary.

Cohesive separates those relational semantics from their physical realization. Facts may come from PostgreSQL, Cosmos DB, a search index, an API, supplied CLR objects, observations, caches, or several sources together. The same semantic definition may be interpreted as a native query, a compiled DTO mapper, an in-memory computation, a dependency manifest, a lineage report, or a diagnostic explanation.

The canonical relationship catalog and relation/query IR are the sources of semantic truth. Authoring DSLs, importers, and inference systems such as Ari produce these IRs; compilers and interpreters decide how to realize them.

## Start Here

Install the semantic core:

```bash
dotnet add package Cohesive.Relations
```

The ordinary C# entry point is the expression authoring surface. A complete `Load -> LoadDto`
relation needs no semantic IDs, names, node or binding arguments, source references, placement, or adapter
configuration:

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

var authored = loadDtos.BuildRelation(dto => dto.Id);
var relation = author.CreateRelation(
    authored,
    loads,
    loadDtos,
    new ExecutionRevisionId("1"));
```

`authored` is the validated producer result for the canonical, persistable relation definition. `relation` is an
immutable typed projection for an exactly-one rooted Relation revision. It retains the canonical document and its
captured shape/relationship evidence—not expressions or callbacks—and proves its CLR input and result types against
the canonical root and output shapes. Its invocation and result `ValueContract`s use the portable structural CLR
projection shared by Process authoring and runtime value conversion; graph-qualified named binding types remain in
the Relation document and captured shape evidence rather than leaking into cross-language invocation contracts.
Relations with optional, many, or set cardinality continue to use their
explicit result semantics rather than being represented by this singular handle. From there, add only the capability
your application needs:

1. [Map a Load, then enrich it with Customer and Equipment](../GETTING_STARTED.md).
2. [Invoke and execute the definition in memory or through an adapter](../EXECUTION_AND_ADAPTERS.md).
3. [Handle missing Customer data and other structured diagnostics](../DIAGNOSTICS.md).
4. [Inspect the generated adapter capability reference](../CAPABILITIES.md).
5. [Migrate from the deleted legacy relation-query stack](../MIGRATION.md).

Adapter-specific construction and override examples live with
[`Cohesive.Adapters.Cosmos`](https://github.com/cohesivesystems/cohesive/blob/main/src/adapters/Cohesive.Adapters.Cosmos/README.md),
[`Cohesive.Adapters.Elastic`](https://github.com/cohesivesystems/cohesive/blob/main/src/adapters/Cohesive.Adapters.Elastic/README.md), and
[`Cohesive.Adapters.Postgres`](https://github.com/cohesivesystems/cohesive/blob/main/src/adapters/Cohesive.Adapters.Postgres/README.md).

The smallest authoring, field-demand, composed-read, and missing-input examples are executable Relations
documentation conformance tests. Longer host and adapter fragments isolate one boundary and link to the exact
conformance coverage. The expression API is the primary application surface; structural authoring and direct
canonical IR construction remain available later for tooling, imports, persistence, and metaprogramming.

## Mental Model

Cohesive relational programs are built from several related concepts.

### Facts

Facts are shaped values available to a relational program.

Examples include:

- A `Load` supplied as a CLR object.
- A customer observation read from Cosmos DB.
- Rows from a PostgreSQL table.
- Equipment data returned by an API.
- Previously materialized search documents.

The semantic value authority is `Cohesive.Model.Observation`: an immutable identity-free value governed by an exact
graph-qualified shape. Allocation-sensitive execution may interpret that value as an
`IndexedObservationOccurrence`, which composes the existing evaluation-scoped occurrence with an ordinal layout,
dense slots, packed presence bits, and derived-field lineage. Its factories validate against the core semantics and
its `ToObservation` projection is lossless. The same core `ObservationMaterializer<T>` can read it directly through
`IObservationFieldReader`; neither layout nor buffer becomes a core dependency. The older
identity-bearing Relations row has been removed; stable source identity and entity version now enter through their
explicit acquisition and snapshot contracts.

A logical source declares that values of a particular shape are available. It does not prescribe where they live or how they must be acquired.

### Relationships

Relationships describe semantic connections between facts.

For example:

```text
Load.CustomerId → Customer observation identity
```

A relationship may be traversed during querying, DTO enrichment, compilation, or dependency analysis. Its physical realization might be a SQL join, a bounded lookup, or an in-memory hash join, provided the selected target can preserve the declared semantics.

The canonical `RelationshipDefinition` is an oriented edge from the graph-qualified shape that
holds a reference to the graph-qualified shape it addresses. It stores the source field path,
target-key semantics, and any global uniqueness guarantee. It does not duplicate the source
field's presence, nullability, or cardinality; those remain authoritative on the source
`ShapeGraph`.

For example, a single `Load.CustomerId` yields at most one customer when traversed forward. The
inverse traversal yields many loads by default. Declaring the reference globally unique reduces
the inverse result to at most one load. A required `CustomerId` means the reference key must be
present; it does not claim that the customer observation exists.

Relationships can be authored directly or through the typed producer:

```csharp
var loadCustomer = Relationship
    .From<Load>(loadShape)
    .Reference(static load => load.CustomerId)
    .To(customerShape);
```

Typed selectors are immediately lowered to canonical field paths. CLR reflection and expression
objects are not retained in persisted relationship IR. `Cohesive.Transitions` can compile
`EntityReferenceTypeRef` fields into the same definitions and deterministic IDs.

### Derivations

A derivation declares how new facts can be established from available facts.

A DTO mapping is one form of relational derivation:

```text
LoadSearchDto(loadId, status, customerName) :-
    Load(loadId, customerId, status),
    Customer(customerId, customerName)
```

The output DTO is a derived fact. Its fields retain provenance to the source facts and expressions that establish them.

### Relations

A relation is a reusable, rooted derivation.

It answers a question such as:

> Given a load and the facts related to it, what `LoadSearchDto` values can be derived?

A relation declares:

- The root input binding.
- The logical derivation graph.
- The output shape.
- Output cardinality relative to each root.
- Required and optional related inputs.
- Semantic invariants.

Relations are useful for DTO mapping, enrichment, lineage analysis, and dependency tracking.

### Queries

A query is an independently invoked request over a logical relational graph.

It may declare:

- Runtime parameters.
- Filters.
- Requested projections.
- Ordering and paging.
- Row results.
- Aggregation results.
- Multiple named result branches over shared predicates.

Queries are useful for retrieval, search, reporting, exploration, and aggregation across one or more sources.

### Hosted Queries

A hosted Query is used when invocation semantics include acquisition or policy that cannot be expressed by the
portable logical graph alone. For example, an invocation may select a tenant partition, verify an admitted event
against persisted state, and then evaluate a portable projection Relation over the acquired snapshot.

```csharp
var byEvent = HostedQuery<NormalizationStart, PinnedSource>.Create(
    definitionId: new("query/training/event-source/schema-mapping"),
    revisionId: new("1"),
    implementation: new("training.event-source", "1"),
    configuration: new EventSourcePolicy("schema-mapping", "tenant-and-payload-exact"),
    provenance: provenance,
    dependencies:
    [
        schemaMappingProjection.AsHostedQueryDependency("projection")
    ]);
```

The shared `ExecutionDefinitionDocument` is authoritative for the hosted Query's input/result contracts, exact
implementation family and version, role-named exact dependencies, portable configuration, provenance, and
fingerprint. The typed handle is only an immutable C# projection of that document. Executable handlers,
repositories, credentials, service instances, deployment choices, and ambient configuration are runtime
registration concerns and never enter canonical content.

A hosted Query is not a substitute for a portable Relation or Query. Its dependency can point to the portable
Relation that performs deterministic projection after acquisition, while its own distinct fingerprint truthfully
attests to the external invocation contract and acquisition policy.
