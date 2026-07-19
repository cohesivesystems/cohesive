# Cohesive.Storage

Provider-neutral storage abstractions for entity repositories, observation streams, outbox records, seeding, and process repository adapters.

## Install

```bash
dotnet add package Cohesive.Storage
```

## Use When

- You need repository contracts for Cohesive entities and observations.
- You want storage behavior to attach to semantic entity and relation models without binding application code to a database SDK.
- You need adapters between entity snapshots, observation records, canonical relation/query source readers, and process execution.

## Canonical relation/query sources

Storage contributes physical acquisition to `Cohesive.Relations`; it does not define another predicate, join,
projection, aggregation, or paging model. Register an exact graph-qualified entity shape with its canonical source
instance, reader, selectors, capability profile, and limits. The immutable catalog then authors plan-affine
placement and constructs the existing canonical evaluator:

```csharp
var source = EntityRelationQuerySourceRegistration.InMemory(
    loadShape,
    loadRepository,
    limits: new(
        maximumBatchSize: 100,
        maximumBufferedRows: 10_000,
        maximumFanOut: 100,
        maximumConcurrency: 4));

var catalog = new EntityRelationQuerySourceCatalog([source]);
IRelationQueryEvaluator evaluator = catalog.CreateEvaluator(physicalPlanningPolicy);
var outcome = await evaluator.EvaluateAsync(evaluation, cancellationToken);
```

The in-memory reader supports bounded enumeration, identity batches, relationship-reference batches, exact field
selection, authoritative absence, partial/inconclusive evidence, and cancellation. Canonical interpretation owns
filters, joins, output shaping, aggregation, and paging. Query source roots are read from registered sources;
relation roots remain invocation inputs and must be supplied by the evaluation.

The same facilities can be registered with `IServiceCollection` through `RegisterEntityRelationQuerySource` and
`RegisterEntityRelationQueryEvaluator`. Registration order does not choose a source: the v1 catalog permits exactly
one source per graph-qualified shape and rejects duplicate shape or source identities.

## Query authority

`Cohesive.Relations` canonical relation/query IR is the sole authority for predicates, joins, projections,
aggregations, and paging. Storage repositories retain point reads, writes, typed object mapping, and atomic outbox
behavior; they do not expose a parallel entity-query contract. Query consumers author a `RelationQueryEvaluation`,
register explicit `IRelationQuerySourceReader` implementations through `EntityRelationQuerySourceRegistration`, and
execute through `IRelationQueryEvaluator` or a target's canonical artifact executor.

The former query-repository compatibility facade and observation adapters were removed intentionally. Storage does
not provide an automatic bridge from that deleted model to canonical relation/query definitions.

## Related Packages

- `Cohesive.Transitions` for entity state and transition models.
- `Cohesive.Relations` for canonical relation/query semantics, evaluation, placement, and source-reader contracts.
- `Cohesive.Adapters.Cosmos` for Cosmos DB-backed storage.
