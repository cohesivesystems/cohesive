# Cohesive.Simulation

`Cohesive.Simulation` provides deterministic synthetic-data generation, property cases, replayable worlds, and
provider-neutral provisioning over canonical Cohesive shapes. Typed C# builders are the ordinary authoring surface;
portable IR remains the semantic authority used by tests, scripts, seeders, and tools.

## Install

The current alpha targets .NET 10:

```bash
dotnet add package Cohesive.Simulation --prerelease
```

Add only the integrations the application uses:

| Package | Use it for |
| --- | --- |
| `Cohesive.Simulation` | POCO generation, property cases, worlds, artifacts, replay, and JSONL |
| `Cohesive.Simulation.Relations` | References between generated populations |
| `Cohesive.Simulation.Storage` | Seeding Cohesive entity repositories |
| `Cohesive.Simulation.Xunit` | Translating property-case results into xUnit failures |
| `Cohesive.Simulation.Cli` | `cohesive-sim` for scripts, CI, and Playwright setup |
| `Cohesive.Adapters.Bogus` | Optional finite Bogus imports into exact retained generation catalogs |
| `Cohesive.Simulation.ExternalProcess` | Bounded provider-process imports from Python or another runtime |
| `Cohesive.Adapters.Mimesis` | Typed finite Mimesis imports through the bounded Python process boundary |

Core depends only on `Cohesive`; Relations, Storage, xUnit, CLI, and provider integrations stay in optional packages.
The CLI can consume a strict portable ExternalProcess import definition while keeping executable paths and process
limits local to the invocation, then atomically retain and independently verify the resulting catalog.

## Generate a POCO

```csharp
using Cohesive.Simulation;
using Cohesive.Simulation.Generation;

var customers = Simulation.Define<Customer>(customer => customer
    .Member(value => value.Name, Gen.Categorical(
        Gen.Weighted("Ada", weight: 1d),
        Gen.Weighted("Grace", weight: 1d)))
    .Member(value => value.Age, Gen.Int32(minimum: 18, maximum: 90))
    .Member(value => value.IsActive, Gen.Bernoulli(probability: 0.85)));

Generated<Customer> generated = customers.Compile().Generate(seed: 42);

public sealed record Customer(string Name, int Age, bool IsActive);
```

The seed is only one replay coordinate. Exact definition identity, revision, fingerprint, scope, interpreter, entropy
algorithm, and sequence index are retained with generated observations so replay fails closed against changed
semantics.

## Check properties

```csharp
PropertyCaseRunResult run = customers.Compile().CheckProperty(
    seed: 42,
    property: static customer => customer.Age >= 18,
    options: new(requiredPassedCases: 250));
```

The runner-neutral result contains stable diagnostics, coverage, and a semantically shrunk counterexample with a
replay token when the property fails. `Cohesive.Simulation.Xunit` adds `PropertyCaseAssert.Passed(run)` without making
xUnit part of generation semantics.

## Compose and retain a world

```csharp
using Cohesive.Simulation.Artifacts;
using Cohesive.Simulation.Worlds;

var world = Simulation.DefineWorld("world/demo", "r1", builder => builder
    .Population("customers", count: 100, customers)
    .Exemplar("customer-for-browser", "customers", sequenceIndex: 7));

CompiledWorldPlan plan = world.Compile();
WorldArtifactManifest artifact = WorldArtifactManifest.FromWorld(plan, rootSeed: 42);
string manifestJson = WorldArtifactManifestJsonSerializer.Serialize(artifact);
```

The manifest is the retained cross-process authority. It embeds the exact portable world, root seed, interpreter,
entropy algorithm, compiled population projections, and exemplars behind a content-addressed artifact ID. Persist it
before provisioning when data crosses a process or storage boundary.

```csharp
using Cohesive.Simulation.Provisioning;

await using var output = File.Create("demo.jsonl");
var sink = new WorldJsonLinesSink("demo/artifact", output);
await WorldProvisioner.ProvisionAsync(artifact, sink, new(batchSize: 500));

output.Position = 0;
await WorldJsonLinesVerifier.VerifyAsync(artifact, output);
```

Provisioning is bounded, deterministic, and sink-neutral. Stable run and batch identities support reconciliation,
but the reference provisioner does not invent retries or transactional guarantees a destination cannot provide.

## Current alpha boundary

Implemented generators are constants, inclusive uniform `Int32`, Bernoulli, weighted categorical, exact retained
generation catalogs, object/member composition, and correlated record sampling with typed projections. Catalog
documents pin values, weights, locale, adapter/provider versions, normalized producer capability profiles, distinct
profile and import source evidence, known deviations, and a semantic fingerprint. The same canonical definitions
support direct examples, bounded sequences, property cases, static populations, named exemplars, relationship-linked
worlds, repository seeding, and portable artifacts.

Worlds currently describe static initial state. Virtual time, activity, events, queues, resources, failures, actors,
actions, scenario traces, runtime provider interpreters, additional provider import adapters, and learned synthesis are
later interpretations, not implicit callback behavior in this alpha. `Cohesive.Adapters.Bogus` and
`Cohesive.Adapters.Mimesis` provide optional snapshot producers without making either provider a core dependency.

Current portable schemas and replay tokens reject earlier versions rather than silently assigning compatibility
semantics. See the compatibility guide before retaining artifacts across package upgrades.

## Learn more

- [Getting started](https://github.com/cohesivesystems/cohesive/blob/main/src/Cohesive.Simulation/docs/getting-started.md)
- [Artifacts, replay, and verification](https://github.com/cohesivesystems/cohesive/blob/main/src/Cohesive.Simulation/docs/artifacts-and-replay.md)
- [Repository seeding and Playwright](https://github.com/cohesivesystems/cohesive/blob/main/src/Cohesive.Simulation/docs/seeding-and-playwright.md)
- [Alpha compatibility contract](https://github.com/cohesivesystems/cohesive/blob/main/src/Cohesive.Simulation/docs/compatibility.md)
- [Executable end-to-end example](https://github.com/cohesivesystems/cohesive/blob/main/src/Cohesive.Examples/Simulation/SimulationAdoptionExamples.cs)
- [Relationship worlds](https://github.com/cohesivesystems/cohesive/blob/main/src/Cohesive.Simulation.Relations/README.md)
- [Repository provisioning](https://github.com/cohesivesystems/cohesive/blob/main/src/Cohesive.Simulation.Storage/README.md)
- [xUnit integration](https://github.com/cohesivesystems/cohesive/blob/main/src/Cohesive.Simulation.Xunit/README.md)
- [CLI tool](https://github.com/cohesivesystems/cohesive/blob/main/src/Cohesive.Simulation.Cli/README.md)
- [Bogus catalog adapter](https://github.com/cohesivesystems/cohesive/blob/main/src/adapters/Cohesive.Adapters.Bogus/README.md)
- [Mimesis catalog adapter](https://github.com/cohesivesystems/cohesive/blob/main/src/adapters/Cohesive.Adapters.Mimesis/README.md)
