# Getting Started with Cohesive.Relations

This guide starts with one CLR object and adds relational capabilities only when the example needs them. The C#
expression surface is the normal application API. Every operation immediately lowers to the same canonical
relation/query IR used by persistence, compilers, inference tools, and other host languages.

## Common local DTO projection

For a reusable single-source scalar DTO mapping, the facade prepares the existing canonical pipeline:

```csharp
using Cohesive.Relations.Mapping;

var mapping = RelationDtoMapping.Create(
    (Load load) => new LoadSummaryDto(load.Id, load.Status, load.Amount));

var dto = await mapping.MapAsync(new Load("load-42", "Ready", 125m, "Internal only"));
Console.WriteLine(dto);
// LoadSummaryDto { Id = load-42, Status = Ready, Amount = 125 }

public record Load(string Id, string Status, decimal Amount, string InternalNotes);
public record LoadSummaryDto(string Id, string Status, decimal Amount);
```

Prepare once and reuse the mapping. `MapManyAsync(values)` enumerates a batch once and returns DTOs in input
order, including repeated values; an empty batch returns an empty array. DTOs need no `Id` member. Each input
occurrence receives an invocation-local ordinal identity, not an inferred entity identity. The complete batch
is bounded by `maximumRows` (default `RelationQueryEvaluator.DefaultMaximumRootRows`, currently 10,000),
checked during enumeration; input validation, failure and cancellation never publish a partial batch.

This facade lowers the projection through the same expression authoring and retains one immutable compilation
request, shared static plan and compiled DTO mapper. `CompilationRequest`, `Compilation`, `AuthoringProvenance`
and `Mapper.Descriptor` expose the canonical definition, lowering attribution, diagnostics and mapping decisions. For retained phase evidence and source
provenance, call `EvaluateAsync(values, new("request/42"))`, inspect its outcome, then use
`mapping.Mapper.Map(outcome.PhysicalExecution!)`. Value-only methods use a fixed operation-local evaluation
identity; explicitly identified evaluation is the attribution path for callers that retain results.

No database, direct CLR callback interpreter or global source/destination-type registry is introduced. The
projection is inspected, not compiled as executable CLR behavior; captures and unsupported constructs retain
existing authoring diagnostics. Every call still performs the full canonical supplied-only pipeline and input
admission. This convenience API does not imply AutoMapper-like direct-conversion cost or a new performance
optimization. Concurrent calls are supported; callers own their input and must keep it stable during admission.

The current DTO mapper supports its existing scalar conversions. Nested source fields can project to scalar
members, but nested/collection-valued output DTOs fail preparation with structured `REL3308` diagnostics in
`RelationQueryPreparationException.DtoDiagnostics`. Richer relations, external sources, filters, joins, invariants,
explicit domain identities and alternate targets use the expanded authoring/evaluation API below. The compact
source is not a second semantic authority; its request can be inspected, serialized or realized elsewhere through
the existing compiler APIs.

## Compact joins and grouped totals

Compose the typed source handles and an inline grouped sum, then prepare a relation with explicit root cardinality.

```csharp
using Cohesive.Relations.Authoring;
using Cohesive.Relations.IR;
using Cohesive.Relations.Mapping;

var q = RelationQuery.Expression();
var loads = q.Source<Load>();
var summaries = loads
    .Join(q.Source<LineItem>().SumBy(i => i.LoadId, i => i.Quantity * i.UnitPrice),
        (l, t) => l.Id == t.Key,
        (l, t) => new { l.Id, l.CustomerId, t.Total })
    .Join(q.Source<Customer>(), (l, c) => l.CustomerId == c.Id,
        (l, c) => new LoadSummaryDto(l.Id, c.Name, l.Total));

var mapping = RelationDtoMapping.Create(loads, summaries, mode: RelationOutputMode.ZeroOrOnePerRoot);

Load[] loadValues = [new("load-42", "customer-7")];
Customer[] customerValues = [new("customer-7", "Acme")];
LineItem[] lineItemValues = [
    new("item-1", "load-42", 2m, 50m),
    new("item-2", "load-42", 1m, 25m)
];

var evaluator = mapping.CreateInMemoryEvaluator(customerValues, lineItemValues);
var dtos = await mapping.MapAsync(evaluator, loadValues);
foreach (var dto in dtos)
    Console.WriteLine(dto);

public record Load(string Id, string CustomerId);
public record Customer(string Id, string Name);
public record LineItem(string Id, string LoadId, decimal Quantity, decimal UnitPrice);
public record LoadSummaryDto(string Id, string CustomerName, decimal Total);
```

The evaluator matches each collection's exact CLR element type to one acquired input in the prepared relation
and snapshots its rows. For roots and acquired inputs, it uses the shape's sole string identity field, or a
string field named `Id` when no identity field is declared. Every identity must be unique and nonempty within
its input. The two-collection overload accepts either collection order. Use the configuration callback for
a different number of collections or to choose identity fields with `Root(shape, identity)` and
`Bind(shape, rows, identity)`. Use explicit placement for relationship lookups or multiple acquired inputs
sharing a shape. The optional `maximumRows` argument limits each snapshot and local execution.

The two-argument mapping call reads identities from the admitted observations. It supplies a complete root
collection using the existing whole-source partition default and observes cancellation between inputs.
For caller-attributed invocations, pass an identity directly:

```csharp
var dtos = await mapping.MapAsync(evaluator, loadValues, load => load.Id,
    evaluation: new("load-summary/request-42"), cancellationToken: cancellationToken);
```

Omitting `evaluation` uses the same fixed operation-local identity as the single-source mapping facade;
it is not a globally unique request identifier. Use explicit identities when retaining cross-invocation attribution.
The corresponding `EvaluateAsync(evaluator, roots, selectIdentity, evaluation: ...)` overload retains the full
outcome. For parameters, partial root evidence, explicit partitions or other refinements, the builder path is
still the complete invocation surface:

```csharp
var evaluation = mapping.Evaluate(new("load-summary/request-42"))
    .Supply(loadValues, load => load.Id)
    .Build();
var dtos = await mapping.MapAsync(evaluator, evaluation);
```

The example prints `LoadSummaryDto { Id = load-42, CustomerName = Acme, Total = 125 }`.
Its detailed source row retains all four contributing occurrences, including the load, customer and both items.

`SumBy` lowers one grouping key and one exact sum through the canonical `Aggregate` builder. Its default
result exposes `Key` and `Total`. To name the fields yourself, pass a third argument that constructs the
result and assigns the key and sum directly to two properties. Three-argument `Join` is equivalent to
`Join(...).Select(...)`. `LeftJoin` has the corresponding explicit overload.

Both joins above are inner joins. Loads without line items or customers are omitted; grouped empty item input
produces no groups, not a synthetic zero total. `ZeroOrOnePerRoot` is explicitly declared and checked by canonical
interpretation; `OnePerRoot` fails if a load is omitted. Multiple matches violating the selected cardinality fail.
Incomplete item evidence cannot become a conclusive zero. Left-join missing values, nulls, numeric-domain and
overflow behavior retain the existing canonical rules; this facade invents no default/coalesce policy.
Grouping and sum input/output types are inferred independently so the shorthand cannot introduce an implicit
numeric widening conversion that the expanded builder would reject; write conversions explicitly when intended.

The existing bounded equijoin planner still requires unique-key evidence on at least one side. In this fixture,
placements explicitly bind the semantic `Load.Id` and `Customer.Id` fields to their observation identities.
Naming a POCO property `Id` does not assert uniqueness. Providers remain responsible for the identity evidence,
read completeness, partition scope, resource bounds and consistency they declare. Native-target capability
mismatches (including unsupported exact-contributor aggregation) still produce existing diagnostics; the facade
does not silently replace or weaken the chosen target.

`mapping.EvaluateAsync(evaluator, evaluation)` retains full phase diagnostics and execution evidence; use
`mapping.Mapper.Map(outcome.PhysicalExecution!)` for DTO rows with provenance. `MapAsync` returns only a complete
DTO array in canonical result order, and throws for unsuccessful/inconclusive evaluation or strict mapping failure.
It does not promise input order for a composed relation unless that order is declared. Foreign compilation-request
instances are rejected, cancellation propagates, and caller-owned clients/evaluators are never disposed or retried.
Parameters and supplied-root completeness can be declared through the existing evaluation builder. Richer aggregate
operations remain available on the expanded builder; the focused convenience currently exposes Group and Sum.

## 1. Map one object shape to a DTO shape

Start with ordinary CLR types:

```csharp
using System.Text.Json.Serialization;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Execution;
using Cohesive.Relations.IR;
using Cohesive.Relations.Mapping;

public sealed class Load
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("customerId")]
    public required string CustomerId { get; init; }

    [JsonPropertyName("equipmentId")]
    public required string EquipmentId { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }
}

public sealed class LoadDto
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }
}
```

Author the mapping as a rooted relation:

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

That is the complete semantic definition. `Source<T>()` discovers deterministic shape metadata, `Project` defines
the output shape and field provenance, and `BuildRelation` derives the relation identity, name, root, and source
references by convention. `relation.Validation` contains structured authoring diagnostics, while
`relation.CreateDocument()` creates the validated and fingerprinted persistence envelope.

For this supplied-only mapping, execute the full canonical pipeline without configuring a store or source reader:

```csharp
var load = new Load
{
    Id = "load-42",
    CustomerId = "customer-7",
    EquipmentId = "equipment-3",
    Status = "Open"
};

var evaluation = author
    .Evaluate(relation, new("load-dto/load-42"))
    .Supply([load], static value => value.Id)
    .Build();

var outcome = await RelationQueryEvaluator
    .CreateSuppliedOnly()
    .EvaluateAsync(evaluation);

if (!outcome.IsSuccessful)
    throw new InvalidOperationException($"Mapping ended with {outcome.Status}; inspect its retained diagnostics.");

var mapper = RelationDtoMapperCompiler.Default
    .Compile<LoadDto>(outcome.Compilation.Plan!)
    .Mapper!;
var dto = mapper.Map(outcome.PhysicalExecution!).Rows.Single().Value;

Console.WriteLine($"{dto.Id}: {dto.Status}"); // load-42: Open
```

`CreateSuppliedOnly()` is deliberately restricted to a compiled relation plan with one supplied-root source and no
retained traversal. It still uses canonical compilation, realization, physical planning, interpretation, and DTO
materialization; it merely supplies deterministic no-I/O placement conventions. Runtime invocation with Customer or
Equipment acquisition is introduced in [step 4](#4-invoke-and-execute). Keeping execution separate from the
definition is what lets the same semantics become an in-memory computation, SQL, a search request, or a federated
plan.

## 2. Enrich the DTO through one relationship

Add the related CLR shape and the flattened output fields:

```csharp
public sealed class Customer
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("type")]
    public required string Type { get; init; }
}

public sealed class LoadSearchDto
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("customerId")]
    public required string CustomerId { get; init; }

    [JsonPropertyName("customerName")]
    public string? CustomerName { get; init; }

    [JsonPropertyName("customerType")]
    public string? CustomerType { get; init; }

    [JsonPropertyName("equipmentNumber")]
    public string? EquipmentNumber { get; init; }
}
```

The shortest traversal form declares `Load.CustomerId -> Customer` inline:

```csharp
var customers = author.Traverse<Load, Customer>(
    loads,
    load => load.CustomerId);

var searchDocuments = author.Project(
    customers,
    (Load load, Customer customer) => new LoadSearchDto
    {
        Id = load.Id,
        CustomerId = load.CustomerId,
        CustomerName = customer.Name,
        CustomerType = customer.Type
    });

var enrichedRelation = searchDocuments.BuildRelation(dto => dto.Id);
```

The authoring session retains the convention-derived relationship in
`author.CreateRelationshipCatalogDocument()`. The persisted relationship is a canonical edge whose source reference
is `Load.CustomerId` and whose target key is the Customer observation identity. The traversal may later become a SQL
join, a bounded lookup, or a local correlation without changing this definition.

## 3. Add a second traversal only when needed

The second relationship also originates at `Load`, while the current focus is `Customer`. That ambiguity is where
the current API first asks for explicit binding context:

```csharp
public sealed class Equipment
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("number")]
    public required string Number { get; init; }
}

var loadEquipment =
    author.Relationship<Load, Equipment>(load => load.EquipmentId);

var equipment = author.Traverse(
    customers,
    loads.Binding,
    loadEquipment,
    requirement: QueryInputRequirement.Optional);

var completeDocuments = author.Project(
    equipment,
    (Load load, Customer customer, Equipment unit) => new LoadSearchDto
    {
        Id = load.Id,
        CustomerId = load.CustomerId,
        CustomerName = customer.Name,
        CustomerType = customer.Type,
        EquipmentNumber = unit.Number
    },
    loads.Binding,
    customers.Binding);

var completeRelation = completeDocuments.BuildRelation(dto => dto.Id);
```

The extra handles are not a second semantic model. They disambiguate which earlier binding supplies the second
reference and the three projection parameters. Structural node construction is still unnecessary.

Static compilation keeps only demanded semantic inputs:

```csharp
var compilation = RelationQueryStaticCompiler.Compile(new(
    completeRelation.CreateDocument(),
    author.ShapeDocuments,
    author.CreateRelationshipCatalogDocument()));

if (!compilation.IsSuccessful)
    throw new InvalidOperationException(string.Join(Environment.NewLine, compilation.Diagnostics));

var plan = compilation.Plan!;
```

For this projection the compiled contract asks for `Load.Id`, `Load.CustomerId`, `Load.EquipmentId`,
`Customer.Name`, `Customer.Type`, and `Equipment.Number`—not every field on all three CLR types. Predicates,
ordering, grouping, aggregation, identities, and relationship correlation add their own precise inputs when present.
The same requirement graph feeds execution, lineage, dependency analysis, explain output, and adapter compilation.

## 4. Invoke and execute

A definition is reusable; an evaluation is one runtime invocation. Supply a root Load without adding placement or
backend policy to the semantic definition:

```csharp
var evaluation = author
    .Evaluate(completeRelation, new("load-search/load-42"))
    .Supply(
        [new Load
        {
            Id = "load-42",
            CustomerId = "customer-7",
            EquipmentId = "equipment-3",
            Status = "Open"
        }],
        static load => load.Id)
    .Build();

var outcome = await evaluator.EvaluateAsync(evaluation, cancellationToken);
```

Typed `Supply` values are projected directly into canonical observations. CLR enums use their member names unless
the enum declares a standard System.Text.Json string converter, in which case its declared wire names are retained;
the inferred relation shape and expression literals use the same catalog.

`IRelationQueryEvaluator` is application-host infrastructure configured once with source placement, bounded planning
policy, and source readers. It performs static compilation, capability realization, physical planning, acquisition,
and canonical interpretation. The evaluation remains target-neutral and carries exact parameter and supplied-root
evidence.

For a repeatedly invoked definition with the same shapes, catalog, and output demand, retain the immutable
`RelationQueryCompilationRequest` and author each invocation from it:

```csharp
var reusableCompilation = evaluation.Compilation;

var nextEvaluation = reusableCompilation
    .Evaluate(new("load-search/load-43"))
    .Supply([nextLoad], static load => load.Id)
    .Build();
```

An evaluator weakly associates immutable compilation, realization, placement, and physical-planning artifacts with
that exact request. Runtime parameters, supplied roots, source reads, and interpreted results are always evaluated
per invocation. Failed preparation is removed rather than poisoning later attempts. The
`cohesive.relations.preparation.cache_hit` trace attribute makes reuse visible without adding a metric dimension.

Map successful or partial canonical rows with the compiled DTO kernel:

```csharp
var mapperCompilation =
    RelationDtoMapperCompiler.Default.Compile<LoadSearchDto>(outcome.Compilation.Plan!);

if (!mapperCompilation.IsSuccessful)
    throw new InvalidOperationException(string.Join(Environment.NewLine, mapperCompilation.Diagnostics));

var mapping = mapperCompilation.Mapper!.Map(
    outcome.PhysicalExecution!,
    RelationDtoMappingFailurePolicy.CollectDiagnostics);

var dto = mapping.Rows.Single().Value;
```

The mapper materializes canonical output rows; it does not acquire Customer or Equipment through a parallel mapping
system. See [Execution and adapters](EXECUTION_AND_ADAPTERS.md) for evaluator bootstrap, query results and
aggregations, and native backend artifacts. See [Diagnostics](DIAGNOSTICS.md) before choosing a missing-input policy.

Warm kernel throughput, allocations, cold compilation, AutoMapper comparison boundaries, and full canonical
execution costs are tracked in the executable benchmark project's
[results](https://github.com/cohesivesystems/cohesive/blob/main/src/Cohesive.Relations.Benchmarks/RESULTS.md). Treat
those numbers as scenario- and scale-specific: the DTO kernel benchmark starts from canonical rows and therefore
does not include relation acquisition or interpretation.

## 5. Add rows and aggregation over one logical scope

A relation describes reusable output per supplied root. A query describes an independently invoked request with
named row and aggregation branches. Both reuse the same authored nodes:

```csharp
var rows = author.Rows(completeDocuments, id: "rows");

var summary = author.Aggregate(
    equipment.Node,
    author.Clr.Shape<LoadSearchSummary>(),
    aggregate => aggregate.Count(result => result.LoadCount));

var aggregation = author.Aggregation(summary, id: "summary");

var query = author.BuildQuery(
    new QueryId("load-search"),
    new QueryName("LoadSearch"),
    rows,
    aggregation);
```

```csharp
public sealed class LoadSearchSummary
{
    [JsonPropertyName("loadCount")]
    public long LoadCount { get; init; }
}
```

An evaluation can demand all fields or select a subset from each named result. Static compilation prunes unrelated
branches and fields before realization. This makes field selection semantic and portable rather than an
adapter-specific optimization hint.

## 6. Reveal lower-level controls progressively

Use the next layer only when the application needs it:

- [Diagnostics and requirement gaps](DIAGNOSTICS.md) explains incomplete evidence and actionable missing Customer
  output.
- [Execution and adapters](EXECUTION_AND_ADAPTERS.md) adds placement, adapter bindings, policy, native artifacts,
  overrides, and the PostgreSQL-versus-Cosmos comparison.
- [Capability reference](CAPABILITIES.md) records the generated target-profile inventory and demand-scoped
  realization decision kinds.
- [Migration guide](MIGRATION.md) maps deleted legacy hydration, repository, join, and query APIs to canonical v1.
- The [DTO mapping internals](internals/DTO_MAPPING.md#structural-c-authoring) introduce structural authoring for tools that need
  direct node, binding, expression, or identity control.

The expression author, structural builder, persisted document, compiled plan, and backend artifact are successive
representations of one semantic definition—not competing APIs that must be kept in sync.
