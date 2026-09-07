# Getting started with Cohesive.Simulation

Use `Cohesive.Simulation` when test data must be deterministic, replayable, and governed by the same semantic shapes
across unit tests, property checks, scripts, and environment seeding.

## 1. Author one typed definition

```csharp
using Cohesive.Simulation;
using Cohesive.Simulation.Generation;

var customers = Simulation.Define<Customer>(customer => customer
    .Member(value => value.ExternalId, Gen.Constant("customer-001"))
    .Member(value => value.Name, Gen.Categorical(
        Gen.Weighted("Ada", weight: 1d),
        Gen.Weighted("Grace", weight: 1d)))
    .Member(value => value.Age, Gen.Int32(minimum: 18, maximum: 90)));

public sealed record Customer(string ExternalId, string Name, int Age);
```

The expression builder lowers immediately to a canonical `GenerationDefinition`. CLR selectors and callbacks do not
survive in the portable IR. When an application already owns a `ClrShapeGraphBuildResult`, pass it to
`Simulation.Define<T>(shapes, ...)` so generation, relationships, materialization, and storage share one graph.

## 2. Compile and inspect diagnostics

```csharp
GenerationCompilationResult compilation = customers.Definition.CompileResult();
if (!compilation.IsSuccessful)
{
    foreach (var diagnostic in compilation.Validation.Diagnostics)
        Console.Error.WriteLine($"{diagnostic.Code}: {diagnostic.Message}");
}

CompiledPocoGenerator<Customer> generator = customers.Compile();
```

`CompileResult()` is the tooling surface. `Compile()` is the convenience surface and throws
`GenerationCompilationException` carrying the same structured validation result.

## 3. Generate examples or bounded sequences

```csharp
Generated<Customer> example = generator.Generate(seed: 42);

GenerationScope scope = new("tests/customer-import");
IEnumerable<Generated<Customer>> cases = generator.EnumerateSequence(
    seed: 42,
    scope: scope,
    count: 100);
```

Use stable semantic scope names rather than test method names, process IDs, or object identities. Entropy is addressed
by definition, binding/member identity, scope, and sequence index. Adding an unrelated field or population therefore
does not perturb existing streams.

## 4. Keep correlated fields coherent

When several fields must come from one catalog row, sample the record once and project its members:

```csharp
var shipments = Simulation.Define<Shipment>(shipment =>
{
    var route = shipment.SampleRecord("route", Gen.Categorical(
        Gen.Weighted(new Route("SEA", "PDX", 174), weight: 1d),
        Gen.Weighted(new Route("LAX", "SFO", 383), weight: 2d)));

    shipment
        .Member(value => value.Origin, route.Project(value => value.Origin))
        .Member(value => value.Destination, route.Project(value => value.Destination))
        .Member(value => value.DistanceMiles, route.Project(value => value.DistanceMiles));
});

public sealed record Route(string Origin, string Destination, int DistanceMiles);
public sealed record Shipment(string Origin, string Destination, int DistanceMiles);
```

The binding is one semantic shrink unit. Counterexample shrinking keeps all projections coherent.

## 5. Use a retained provider catalog

Provider adapters can import a finite locale or domain catalog into a strict `GenerationCatalogDocument`. The exact
catalog values become the replay authority; adapter objects and callbacks are no longer needed after import:

```csharp
GenerationCatalogDocument people = GenerationCatalogJsonSerializer.Deserialize(
    await File.ReadAllTextAsync("person-profiles.catalog.json"));

var customers = Simulation.Define<Customer>(customer =>
{
    var person = customer.SampleRecord(
        "person",
        Gen.Catalog<PersonProfile>(people));

    customer
        .Member(value => value.Name, person.Project(value => value.DisplayName))
        .Member(value => value.Email, person.Project(value => value.Email));
});

public sealed record PersonProfile(string DisplayName, string Email);
public sealed record Customer(string Name, string Email);
```

The catalog fingerprint covers exact values, weights, portable type, locale, adapter and provider versions, normalized
producer capability evidence, distinct profile/import source references, and known deviations. This supports
deterministic unit, property, and world generation without making an external fake-data library a runtime dependency
of `Cohesive.Simulation`.

Persist adapter output with `GenerationCatalogJsonSerializer.Serialize(...)`. Scripts and CI can then validate the
retained authority independently before embedding or consuming it:

```bash
cohesive-sim catalog verify --catalog person-profiles.catalog.json
```

Language-neutral producers can instead be invoked through `cohesive-sim catalog import-external`. Its portable
definition retains provider semantics and deterministic inputs, while executable paths and operational limits remain
local invocation settings. See the [ExternalProcess guide](../../Cohesive.Simulation.ExternalProcess/README.md) for
the complete workflow.

Install `Cohesive.Adapters.Bogus` when Bogus should produce the retained snapshot. Its
`BogusGenerationCatalog.Import(...)` helper applies an explicit locale, importer-local seed, and fixed UTC time
reference, then records exact adapter/provider package evidence. The callback runs only during import; the resulting
document is consumed through the same `Gen.Catalog<T>` path shown above. See the
[Bogus adapter guide](../../adapters/Cohesive.Adapters.Bogus/README.md) for the complete example and determinism
boundary.

## 6. Check properties

```csharp
PropertyCaseRunResult result = generator.CheckProperty(
    seed: 42,
    property: static customer => customer.Age >= 18,
    options: new(requiredPassedCases: 250));
```

The result is runner-neutral. A failure retains the best semantic counterexample and an exact `csimpc1.` replay token:

```csharp
if (result.BestCounterexample is { } counterexample)
{
    Customer replayed = generator.ReplayPropertyCase(counterexample.Replay.ToToken());
}
```

Install `Cohesive.Simulation.Xunit` and call `PropertyCaseAssert.Passed(result)` when xUnit should own failure
reporting.

## 7. Compose a static world

```csharp
using Cohesive.Simulation.Worlds;

var world = Simulation.DefineWorld("world/customer-demo", "r1", builder => builder
    .Population("customers", count: 100, customers)
    .Exemplar("customer-for-browser", "customers", sequenceIndex: 7));

CompiledWorldPlan plan = world.Compile();
Generated<Customer> browserCustomer = plan.GenerateExemplar(
    "customer-for-browser",
    seed: 42,
    generator);
```

An exemplar is a stable alias for one exact population coordinate. It does not duplicate an observation or assert an
unstated predicate. Use `WorldEntityIdentityPolicy.FromUniqueObservationField(...)` when a generated domain field,
rather than the default population slot, owns entity identity.

## 8. Persist before crossing boundaries

`GenerationDefinitionJsonSerializer` and `WorldDefinitionJsonSerializer` create strict fingerprinted documents.
`WorldArtifactManifest` additionally pins the root seed and interpreter for one exact generation run. Retain that
manifest before producing JSONL, seeding a remote environment, or handing work to another process.

Continue with [artifacts, replay, and verification](artifacts-and-replay.md) or
[repository seeding and Playwright](seeding-and-playwright.md). The complete relationship/storage/browser flow is an
[executable test](../../Cohesive.Examples/Simulation/SimulationAdoptionExamples.cs).

## 9. Schedule activity over the retained world

Use the exact `WorldArtifactManifest` as the starting authority for a deterministic scenario. Bind actors to named
world exemplars, declare portable operation contracts, and schedule action inputs on a fixed virtual UTC timeline.
Materialize those actors through `ScenarioWorldSnapshot.FromCoreWorld` or the relationship package's
`RelationshipScenarioWorldSnapshot.Materialize`, then execute the immutable snapshot through `ScenarioRunner`. The
typed authoring projection, strict portable document, materialization boundary, and trace are covered in
[deterministic scenarios](scenarios.md).
