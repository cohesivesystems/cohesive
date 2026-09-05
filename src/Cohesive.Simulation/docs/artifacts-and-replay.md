# Artifacts, replay, and verification

Portable simulation documents are durable semantic authorities, not incidental serializer output. Generated values,
JSONL records, database rows, and test reports are interpretations that retain provenance to those authorities.

## Authority chain

1. A `GenerationCatalogDocument` retains exact weighted values plus locale and producer/provider provenance when an
   external catalog contributes generation semantics.
2. A `GenerationDefinitionDocument` embeds any used catalog documents and retains one shaped generator definition.
3. A `WorldDefinitionDocument` retains bounded populations, entity identity policies, and named exemplars.
4. A `RelationshipWorldDefinitionDocument` optionally composes a world with a canonical relationship catalog and
   population bindings.
5. A `WorldArtifactManifest` retains the exact interpreter-owned world document, root seed, interpreter and entropy
   identities, and validated population/exemplar projections.
6. JSONL records cite the manifest, world, generation, run, batch, population, entity, exemplar, and replay
   coordinates that produced them.

The embedded world document, including complete embedded catalog documents, is the semantic authority. Indexed
manifest fields are validated projections for discovery and provenance; they do not become a second definition or a
dangling pointer to required catalog content.

## Create and retain a manifest

```csharp
CompiledWorldPlan plan = world.Compile();
WorldArtifactManifest manifest = WorldArtifactManifest.FromWorld(plan, rootSeed: 42);

await File.WriteAllTextAsync(
    "demo.manifest.json",
    WorldArtifactManifestJsonSerializer.Serialize(manifest));
```

Persist the manifest before provisioning when generated observations cross a process or persistence boundary. The
content-addressed artifact identity is independent of sink target and batch size; a provisioning run identity binds
the artifact to those execution choices.

Relationship worlds use `RelationshipWorldArtifact.FromWorld(...)`. Core can retain their opaque canonical document
without depending on Relations, while the Relations package owns strict deserialization, compilation, and projection
validation. Calling the core provisioner for a relationship artifact fails closed.

## Provision bounded output

```csharp
await using var output = File.Create("demo.jsonl");
var sink = new WorldJsonLinesSink("artifacts/demo", output);

WorldProvisioningResult result = await WorldProvisioner.ProvisionAsync(
    manifest,
    sink,
    new(batchSize: 500));
```

Populations and sequence indexes are visited in stable order. Only one batch is materialized at a time. A sink must
acknowledge the exact batch ID; `Rejected` stops the run, and an exception leaves the commit outcome unknown. The
reference provisioner never retries automatically because it cannot infer a target's durability or idempotency.

## Verify independently

```csharp
WorldArtifactManifest retained = WorldArtifactManifestJsonSerializer.Deserialize(
    await File.ReadAllTextAsync("demo.manifest.json"));

await using var input = File.OpenRead("demo.jsonl");
WorldJsonLinesValidationResult validation = await WorldJsonLinesVerifier.ValidateAsync(retained, input);
```

The verifier regenerates each expected item and compares exact canonical bytes while holding one record and one
observation at a time. It checks complete count/order, artifact and world provenance, target and batching identities,
entity identity, exemplars, replay evidence, and observation content. No item is exposed as trusted output before the
complete stream succeeds.

Use `VerifyAsync` when invalid content should throw `WorldJsonLinesVerificationException`. Use `ValidateAsync` when
tools need stable diagnostic codes and JSON Pointer locations.

Scripts can perform the same check without hosting .NET code:

```bash
cohesive-sim verify \
  --manifest demo.manifest.json \
  --jsonl demo.jsonl
```

Success writes a `cohesive-simulation-cli-verification/v1` JSON report containing artifact, world, interpreter,
target, run, batching, and item-count evidence. Invalid content exits nonzero and writes the same report shape with
structured diagnostics to standard error.

## Replay tokens

Replay tokens are compact coordinates, not embedded definitions:

- `csimr2.` replays a generated observation against an exact generation definition.
- `csimpc1.` replays a semantically shrunk property counterexample.
- `csimwr1.` replays a relationship-complete world item.

Replay fails when definition identity, revision, fingerprint, scope, interpreter, entropy algorithm, or shrinker
version differs. Keep the corresponding portable definition or artifact manifest with the token.

See [compatibility](compatibility.md) before retaining artifacts across package upgrades.
