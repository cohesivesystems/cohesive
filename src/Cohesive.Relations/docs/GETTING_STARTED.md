# Getting Started with Cohesive.Relations

This guide starts with one CLR object and adds relational capabilities only when the example needs them. The C#
expression surface is the normal application API. Every operation immediately lowers to the same canonical
relation/query IR used by persistence, compilers, inference tools, and other host languages.

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

`IRelationQueryEvaluator` is application-host infrastructure configured once with source placement, bounded planning
policy, and source readers. It performs static compilation, capability realization, physical planning, acquisition,
and canonical interpretation. The evaluation remains target-neutral and carries exact parameter and supplied-root
evidence.

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
- The package [README](../README.md#structural-c-authoring) introduces structural authoring for tools that need
  direct node, binding, expression, or identity control.

The expression author, structural builder, persisted document, compiled plan, and backend artifact are successive
representations of one semantic definition—not competing APIs that must be kept in sync.
