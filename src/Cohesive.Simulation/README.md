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

## Worlds

A `WorldDefinition` composes named bounded populations into one portable static initial state. Each population owns
one canonical generation definition; declaration order is non-semantic, while population identity and count are
semantic. The compiler derives an isolated scope from exact world and population identities and exposes eager or lazy
raw-observation streams:

```csharp
using Cohesive.Simulation.Worlds;

var world = Simulation.DefineWorld("demo", "r1", builder => builder
    .Population("customers", count: 100, customers)
    .Population("operators", count: 5, operators));

CompiledWorldPlan plan = world.Compile();
IEnumerable<GeneratedObservation> customerObservations = plan
    .GetPopulation("customers")
    .Enumerate(seed: 42);
```

Typed generation remains a local interpretation and must match the population's exact generation identity, revision,
and fingerprint:

```csharp
IEnumerable<Generated<Customer>> typedCustomers = plan
    .GetPopulation("customers")
    .Enumerate(seed: 42, customers.Compile());
```

Persist or exchange a complete world with `WorldDefinitionJsonSerializer`. The strict document embeds each
population's generation semantics and governing shape graph, normalizes population and member order, verifies a world
fingerprint, and can be consumed by scripts without CLR authoring callbacks. Population replay evidence remains valid
when unrelated populations are added because the exact generation definition and derived population scope are the
replay coordinates.

Worlds currently define static initial populations only. A future scenario layer will add activity after ordering,
causality, clock, transition, and failure semantics are explicit; those concerns are intentionally not represented as
placeholder callbacks or opaque host-language code.

## Provisioning tests and demo environments

`WorldProvisioner` is the provider-neutral execution boundary between a compiled world and a test fixture, setup
script, or environment seeder. It generates one bounded batch at a time in stable population and sequence order and
delivers each batch through `IWorldProvisioningSink`:

```csharp
using Cohesive.Simulation.Provisioning;

await using var output = File.Create("demo-world.jsonl");
var sink = new WorldJsonLinesSink("artifact/demo-world", output);

WorldProvisioningResult result = await WorldProvisioner.ProvisionAsync(
    plan,
    rootSeed: 42,
    sink,
    new WorldProvisioningOptions(batchSize: 500));
```

The run identity covers the exact world identity, revision, fingerprint, root seed, batch size, reference interpreter,
entropy algorithm, and logical sink target. Each batch gets a stable identity from that run plus its population scope
and contiguous sequence range. A durable sink should use the batch identity as its idempotency key and respond with
`AlreadyCommitted` only after verifying the same complete batch. `Committed` and `AlreadyCommitted` both acknowledge
the whole batch; `Rejected` stops execution with the exact batch and receipt attached.

The reference provisioner performs no automatic retries. A sink exception can mean that the commit outcome is
unknown, so policy belongs in a concrete adapter that can reconcile the stable batch identity with its target. This
keeps storage atomicity, replacement policy, and entity identity out of the Simulation semantic authority.

`WorldJsonLinesSink` provides a framework-independent bridge for unit-test artifacts, command-line scripts, and
Playwright global setup. It emits one generated item per UTF-8 line with world and generation fingerprints, population
scope, deterministic run and batch IDs, replay token, and the Core canonical observation envelope. The signed 64-bit
root seed is encoded as a decimal string so JavaScript can consume it without numeric precision loss. The sink flushes
each acknowledged batch, never closes the caller-owned stream, and intentionally does not claim durable deduplication.

Use the optional `Cohesive.Simulation.Storage` package to bind world populations to generic entity repositories. That
integration keeps repository selection, entity-ID policy, state version, batch atomicity, and upsert behavior outside
the provider-neutral Simulation package while deriving them into the effective provisioning target identity.

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
weighted categorical, record/member composition, and portable static worlds. Generation scopes, bounded lazy
enumeration, deterministic provisioning batches, and the JSON Lines sink provide population isolation and streaming
mechanics. Property runners, shrinking, inter-population relationships, automatic POCO inference, storage-specific
provisioning adapters, temporal scenarios, and provider plugins are intentionally deferred.
