# Cohesive.Simulation

`Cohesive.Simulation` defines provider-neutral, deterministic value generation over Cohesive shapes. The first slice
generates identity-free core `Observation` values and materializes them into ordinary mutable classes or immutable
records through the shared core `ObservationMaterializer<T>`.

The canonical `GenerationDefinition` is the semantic authority. Typed fluent authoring produces that IR; it does not
create a delegate-based generator model. The reference interpreter derives entropy from the root seed and stable
semantic addresses—record identity, binding or member identity, and sequence index—so declaration order and
unrelated additions do not perturb existing generated fields. A `GenerationScope` adds a stable semantic namespace for
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

## Correlated record generation

Use a sampled record binding when several output fields must come from one coherent catalog row rather than from
independent distributions:

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

The route source is sampled once per generated shipment at an entropy address derived from the stable `route`
binding identity. Every projection then evaluates against that retained portable object value. Binding and member
declaration order is non-semantic, and adding an unrelated binding does not perturb existing direct member streams.

`SampleRecord` and `Project` are typed authoring conveniences. They lower immediately to canonical
`RecordGenerationBinding` and `ExpressionGenerationNode` values using core `ValueBindingId`, `FieldPath`, and `Expr`
semantics; no selector callback or CLR reflection dependency remains in persisted IR. The compiler currently admits
whole-binding and object field-path expressions only. Derived arithmetic/conditional expressions, collection-element
navigation, cohorts, and inter-population references remain explicit later capabilities rather than hidden runtime
behavior.

Property-case shrinking treats a sampled binding as one semantic unit. For a categorical record source it considers
earlier source records and recomputes every dependent projection together, preserving cross-field coherence in both
the minimized counterexample and its replay token.

Use `CompileResult()` when tooling needs structured diagnostics without exceptions. Invalid ranges, probabilities,
weights, duplicate semantic identities, invalid record bindings or projections, missing CLR bindings, shape
mismatches, and unsupported materialization all produce stable `DocumentValidationDiagnostic` codes. `Compile()` is
the convenience form and raises
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

## Property cases

The same compiled generator can drive bounded property checks without adopting a particular test framework:

```csharp
CompiledPocoGenerator<Customer> generator = customers.Compile();
PropertyCaseRunResult run = generator.CheckProperty(
    seed: 42,
    property: static customer => customer.Age < 80,
    options: new(requiredPassedCases: 250));

if (run.Status == PropertyCaseRunStatus.CounterexampleFound)
{
    PropertyCase counterexample = run.BestCounterexample!;
    string replayToken = counterexample.Replay.ToToken();
    Customer replayed = generator.ReplayPropertyCase(replayToken);
}
```

`ReferencePropertyCaseInterpreter.Check` exposes the equivalent raw `Observation` surface for scripts, agents, and
runner adapters. A richer evaluator can return `PropertyCaseEvaluation` to attach stable classifications or discard
cases that do not satisfy a local precondition. Runs bound required passes, total discards, and shrink candidates;
invalid options and exhausted bounds return structured diagnostics instead of claiming a conclusive result. Coverage
counts classifications on all initially generated cases in stable ordinal order; shrink attempts do not distort that
distribution.

The local property callback is interpretation policy, not persisted generation semantics. A failing case is shrunk
against the canonical generator IR: Int32 values move toward zero or the nearest zero-facing range boundary,
Bernoulli values move from true to false, weighted categorical values move toward earlier authored options, constants
do not shrink, and record members are considered in canonical identity order. Every candidate is validated against
the governing core shape before evaluation. The result is a deterministic local semantic minimum; if a discard or
shrink bound is exhausted, `BestCounterexample` retains the best failure reached but `IsConclusive` remains false.

The `csimpc1` replay token retains the original generation coordinates, exact generation fingerprint, shrinker
version, and accepted semantic candidate ordinals. Replay therefore fails closed for a different definition or
shrinker version. It restores the counterexample observation; it does not serialize or rerun the local property.
Test-framework integrations can translate the result and diagnostics into their own assertion and reporting model
without becoming a second generation authority.

## Worlds

A `WorldDefinition` composes named bounded populations into one portable static initial state. Each population owns
one canonical generation definition and entity-identity policy; declaration order is non-semantic, while population
identity, count, and entity identity are semantic. The compiler derives an isolated scope from exact world and
population identities and exposes eager or lazy world-item streams:

```csharp
using Cohesive.Simulation.Worlds;

var world = Simulation.DefineWorld("demo", "r1", builder => builder
    .Population("customers", count: 100, customers)
    .Population("operators", count: 5, operators));

CompiledWorldPlan plan = world.Compile();
IEnumerable<GeneratedWorldItem> customerItems = plan
    .GetPopulation("customers")
    .Enumerate(seed: 42);
```

The default `PopulationSequence` policy derives stable entity slots from the world/population scope and sequence
index. These IDs do not depend on the root seed, so reseeding updates the same logical slots. When a generated domain
field already owns identity, declare it on the population:

```csharp
var world = Simulation.DefineWorld("demo", "r1", builder => builder
    .Population(
        "customers",
        count: 100,
        WorldEntityIdentityPolicy.FromUniqueObservationField("ExternalId"),
        customers));
```

Unique-field identity is resolved and checked as the deterministic population stream is consumed, independently of
storage semantics. Each `GeneratedWorldItem` and each portable JSONL item carries the resolved core `EntityId`, so
relationship generation and every provisioning adapter can address the same instance without choosing another
policy. A later duplicate can therefore fail a streaming provisioning run after earlier batches were acknowledged;
the provisioner does not claim rollback across sink commits.

Typed generation remains a local interpretation and must match the population's exact generation identity, revision,
and fingerprint:

```csharp
IEnumerable<Generated<Customer>> typedCustomers = plan
    .GetPopulation("customers")
    .Enumerate(seed: 42, customers.Compile());
```

Name purposeful instances as exemplars when a unit test, demo, or UI workflow needs stable semantic discovery rather
than an incidental query or a generated ordinal convention:

```csharp
var world = Simulation.DefineWorld("demo", "r1", builder => builder
    .Population("customers", count: 100, customers)
    .Exemplar("customer-for-ui", "customers", sequenceIndex: 7));

Generated<Customer> customer = world
    .Compile()
    .GenerateExemplar("customer-for-ui", seed: 42, generator: customers.Compile());
```

An exemplar aliases one exact population coordinate; it does not duplicate the observation or claim that the value
satisfies an unstated cohort condition. Exemplar declarations are portable, world-wide unique, canonicalized by
identity, included in the world fingerprint, and projected into provisioning evidence.

Persist or exchange a complete world definition with `WorldDefinitionJsonSerializer`. The strict document embeds each
population's generation semantics and governing shape graph, normalizes population and member order, verifies a world
fingerprint, and can be consumed by scripts without CLR authoring callbacks. Population replay evidence remains valid
when unrelated populations are added because the exact generation definition and derived population scope are the
replay coordinates.

When data crosses a process, persistence, or test-run boundary, use a `WorldArtifactManifest` to pin the complete
generation run independently of where or how its observations will be written:

```csharp
using Cohesive.Simulation.Artifacts;

WorldArtifactManifest artifact = WorldArtifactManifest.FromWorld(plan, rootSeed: 42);
string manifestJson = WorldArtifactManifestJsonSerializer.Serialize(artifact);

WorldExemplarDefinition customer = artifact.GetExemplar("customer-for-ui");
```

The manifest embeds the exact fingerprint-verified world definition, root seed, reference interpreter and entropy
algorithm, compiled population counts, scopes and identity policies, nested generation coordinates, and exemplar aliases. Its
content-addressed artifact identity is independent of sink target and batching policy. It does not contain generated
observations, so even a very large declared population produces a small manifest and remains suitable for scripts,
test reports, and agent inspection. Persist the manifest before provisioning when observations cross a process or
persistence boundary. Concrete framing and storage of streamed observation batches remain separate format and
adapter concerns.

Worlds currently define static initial populations only. A future scenario layer will add activity after ordering,
causality, clock, transition, and failure semantics are explicit; those concerns are intentionally not represented as
placeholder callbacks or opaque host-language code.

Use the optional `Cohesive.Simulation.Relations` package when generated populations must carry canonical entity
references to one another. It composes the same world and generation semantics with an exact
`RelationshipCatalogDocument` while keeping the provider-neutral Simulation package dependent only on Cohesive core.

## Provisioning tests and demo environments

`WorldProvisioner` is the provider-neutral execution boundary between a compiled world and a test fixture, setup
script, or environment seeder. It generates one bounded batch at a time in stable population and sequence order and
delivers each batch through `IWorldProvisioningSink`:

```csharp
using Cohesive.Simulation.Artifacts;
using Cohesive.Simulation.Provisioning;

await using var output = File.Create("demo-world.jsonl");
var sink = new WorldJsonLinesSink("artifact/demo-world", output);

WorldArtifactManifest artifact = WorldArtifactManifest.FromWorld(plan, rootSeed: 42);
WorldProvisioningResult result = await WorldProvisioner.ProvisionAsync(
    artifact,
    sink,
    new WorldProvisioningOptions(batchSize: 500));
```

The artifact identity covers exact generation semantics and is stable across destinations and batch sizes. A
provisioning run identity combines that artifact identity with batch size and logical sink target. Each batch gets a
stable identity from that run plus its population scope and contiguous sequence range. A durable sink should use the
batch identity as its idempotency key and respond with `AlreadyCommitted` only after verifying the same complete
batch. `Committed` and `AlreadyCommitted` both acknowledge the whole batch; `Rejected` stops execution with the exact
batch and receipt attached.

The reference provisioner performs no automatic retries. A sink exception can mean that the commit outcome is
unknown, so policy belongs in a concrete adapter that can reconcile the stable batch identity with its target. This
keeps storage atomicity and replacement policy out of the Simulation semantic authority while preserving world-owned
entity identity end to end.

`WorldJsonLinesSink` provides a framework-independent bridge for unit-test artifacts, command-line scripts, and
Playwright global setup. It emits one generated item per UTF-8 line with artifact-manifest, world, and generation
fingerprints, population scope, zero or more exemplar identities, deterministic artifact, run, and batch IDs, replay
token, and the Core canonical observation envelope. The signed 64-bit root seed is encoded as a decimal string so
JavaScript can consume it without numeric precision loss. The sink flushes each acknowledged batch, never closes the
caller-owned stream, and intentionally does not claim durable deduplication.

`WorldJsonLinesVerifier.VerifyAsync` verifies a complete v3 stream against an independently retained manifest. It
uses the same internal v3 codec as `WorldJsonLinesSink` and checks canonical record bytes, exact item count and order,
manifest and world provenance, stable target and batching policy, recomputed run and batch identities, exemplar
aliases, replay evidence, and canonical regenerated observations. Verification holds only one record and regenerated
observation at a time and exposes completion evidence only after the entire stream passes; malformed, tampered,
missing, or extra records fail closed. Use `WorldJsonLinesVerifier.ValidateAsync` when tooling needs stable
`DocumentValidationResult` codes and JSON Pointer locations instead of an exception.

Use the optional `Cohesive.Simulation.Storage` package to bind world populations to generic entity repositories. That
integration keeps repository selection, entity-ID policy, state version, batch atomicity, and upsert behavior outside
the provider-neutral Simulation package while deriving them into the effective provisioning target identity.

Install the optional `Cohesive.Simulation.Cli` .NET tool when a shell script, CI job, or Playwright global setup needs
to provision a portable world without hosting .NET application code. `cohesive-sim manifest` creates and atomically
retains the strict artifact manifest from a world and root seed. `cohesive-sim provision` accepts only that verified
manifest and atomically writes the same versioned JSON Lines contract. This makes the retained manifest the
cross-process authority rather than reconstructing it opportunistically during provisioning.

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
Unknown properties, duplicate properties, unsupported schemas, invalid generator content, noncanonical binding or
member order, and fingerprint mismatches are rejected. Use `TryDeserialize` when a tool needs structured diagnostics.

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
enumeration, runner-neutral property cases and semantic shrinking, named exemplars, portable world-artifact manifests,
deterministic provisioning batches, and the JSON Lines sink provide population isolation, stable discovery,
cross-process provenance, and streaming mechanics. Test-runner-specific adapters, inter-population relationships,
automatic POCO inference, storage-specific provisioning adapters, temporal scenarios, and provider plugins are
intentionally deferred.
