# Cohesive.Simulation

`Cohesive.Simulation` defines provider-neutral, deterministic value generation over Cohesive shapes. The first slice
generates identity-free core `Observation` values and materializes them into ordinary mutable classes or immutable
records through the shared core `ObservationMaterializer<T>`.

The canonical `GenerationDefinition` is the semantic authority. Typed fluent authoring produces that IR; it does not
create a delegate-based generator model. The reference interpreter derives entropy from the root seed and stable
semantic addresses—record identity, member identity, and sequence index—so member declaration order and unrelated
member additions do not perturb existing generated fields. Replay evidence remains beside the observation because a
seed, interpreter version, and generation-definition fingerprint are evidence about how a value was produced, not
part of what was observed.

## Example

```csharp
using Cohesive.Simulation;

var customers = Simulation.Define<Customer>(customer => customer
    .Member(value => value.Name, Gen.Constant("Ada"))
    .Member(value => value.Age, Gen.Int32(minimum: 18, maximum: 90))
    .Member(value => value.IsActive, Gen.Bernoulli(probability: 0.85)));

Generated<Customer> generated = customers
    .Compile()
    .Generate(seed: 42);

public sealed record Customer(string Name, int Age, bool IsActive);
```

Use `CompileResult()` when tooling needs structured diagnostics without exceptions. Invalid ranges, probabilities,
weights, duplicate semantic identities, missing CLR bindings, shape mismatches, and unsupported materialization all
produce stable `DocumentValidationDiagnostic` codes. `Compile()` is the convenience form and raises
`GenerationCompilationException` with the same validation result.

Generate a deterministic bounded sequence with `GenerateSequence(seed, count)`. Every item uses its zero-based item
index as part of the semantic entropy address and carries compact replay evidence.

## Materialization boundary

By default, every readable CLR property must be explicitly bound and its deterministic JSON field identity is used as
the semantic member identity. Mutable classes and public-constructor immutable records use the core compiled
materializer. For custom constructors, converters, member mappings, or domain value objects, compile an
`ObservationMaterializer<T>` against `definition.OutputShape` and attach it with `WithMaterializer`. This is a local
CLR interpretation and never enters the canonical generator IR.

## Package boundary and current scope

The package references only `Cohesive`; it does not require Entities, Transitions, Processes, Storage, a property-test
framework, or a fake-data provider. Current canonical generators are constant, inclusive uniform Int32, Bernoulli,
weighted categorical, and record/member composition. Property runners, shrinking, populations, relationships,
automatic POCO inference, persistence of generation IR, and provider plugins are intentionally deferred.
