# Cohesive.Simulation

`Cohesive.Simulation` defines provider-neutral, deterministic value generation over Cohesive shapes. The first slice
generates identity-free core `Observation` values and materializes them into ordinary mutable classes or immutable
records through the shared core `ObservationMaterializer<T>`.

The canonical `GenerationDefinition` is the semantic authority. Typed fluent authoring produces that IR; it does not
create a delegate-based generator model. The reference interpreter derives entropy from the root seed and stable
semantic addresses—record identity, member identity, and sequence index—so member declaration order and unrelated
member additions do not perturb existing generated fields. A `GenerationScope` adds a stable semantic namespace for
a fixture, script, world population, or scenario role, allowing those uses to share one definition and seed without
coupling their generated streams. Replay evidence remains beside the observation because a seed, scope, interpreter
version, and generation-definition fingerprint are evidence about how a value was produced, not part of what was
observed.

## Example

```csharp
using Cohesive.Simulation;
using Cohesive.Simulation.Generation;

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
index as part of the semantic entropy address and carries compact replay evidence. `EnumerateSequence(seed, count)`
exposes the same bounded semantics lazily, so scripts and seeders can consume a prefix or write each item without
materializing the entire sequence.

Use an explicit stable scope when the same definition and root seed serve more than one semantic population:

```csharp
GenerationScope scope = new("world/demo/customers");

IEnumerable<Generated<Customer>> stream = customers
    .Compile()
    .EnumerateSequence(seed: 42, scope: scope, count: 10_000);
```

Scope identities are exact ordinal strings and are retained in replay evidence. Callers should derive them from
stable scenario or world identities, not runtime object identities. Calls that omit a scope use
`GenerationScope.Default` for the original single-stream convenience behavior.

## Portable definitions and replay

Persist a validated definition for scripts, tooling, or another process with the strict portable document boundary:

```csharp
string json = GenerationDefinitionJsonSerializer.Serialize(customers.Definition);

GenerationDefinitionDocument document = GenerationDefinitionJsonSerializer.Deserialize(json);
CompiledGenerationPlan plan = document.Compile();
GeneratedObservation observation = ReferenceGenerationInterpreter.Generate(plan, seed: 42);
```

The document contains the exact governing shape graph, normalized generator IR, schema version, and a verified
semantic fingerprint. Compact output is canonical; indented output is available for human authoring and review.
Unknown properties, duplicate properties, unsupported schemas, invalid generator content, noncanonical member order,
and fingerprint mismatches are rejected. Use `TryDeserialize` when a tool needs structured diagnostics.

Every `GenerationReplayEvidence` can be encoded as one opaque URL-safe token and replayed against the exact compiled
definition:

```csharp
string token = observation.Replay.ToToken();
GeneratedObservation replayed = ReferenceGenerationInterpreter.Replay(plan, token);
```

Replay restores the exact scope and fails when the token names another definition identity, revision, fingerprint,
interpreter, or entropy algorithm. The token does not embed the definition itself; the portable definition document
remains the semantic authority. Scope-aware replay tokens use the current `csimr2` contract; prior scope-less token
versions are rejected rather than assigned an implicit scope.

## Materialization boundary

By default, every readable CLR property must be explicitly bound and its deterministic JSON field identity is used as
the semantic member identity. Mutable classes and public-constructor immutable records use the core compiled
materializer. For custom constructors, converters, member mappings, or domain value objects, compile an
`ObservationMaterializer<T>` against `definition.OutputShape` and attach it with `WithMaterializer`. This is a local
CLR interpretation and never enters the canonical generator IR.

## Package boundary and current scope

The package references only `Cohesive`; it does not require Entities, Transitions, Processes, Storage, a property-test
framework, or a fake-data provider. Current canonical generators are constant, inclusive uniform Int32, Bernoulli,
weighted categorical, and record/member composition. Generation scopes and bounded lazy enumeration provide
population isolation and streaming mechanics without yet defining world semantics. Property runners, shrinking,
populations, relationships, automatic POCO inference, world artifacts, provisioning, and provider plugins are
intentionally deferred.
