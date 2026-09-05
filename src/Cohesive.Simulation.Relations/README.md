# Cohesive.Simulation.Relations

`Cohesive.Simulation.Relations` is the optional semantic composition between deterministic simulation worlds and
canonical Cohesive relationships. It lets one generated population carry references to actual entity identities in
another generated population without making `Cohesive.Simulation` depend on the Relations language.

## Install

The current alpha targets .NET 10:

```bash
dotnet add package Cohesive.Simulation.Relations --prerelease
```

The linked `RelationshipCatalogDocument` is the sole authority for the source field, endpoint shapes, target key,
cardinality, and uniqueness guarantee. A `WorldPopulationRelationshipBinding` declares only which world populations
occupy those endpoints and how often a target is selected. This avoids a second relationship model in simulation.

## Relationship-linked worlds

CLR authoring can produce one shared shape graph, typed member generators, and a typed relationship without making
CLR reflection or selectors part of the retained definition. Omit fields supplied by canonical relationships from
the local generator:

```csharp
using Cohesive.Model;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.Model;
using Cohesive.Relations.Serialization;
using Cohesive.Simulation;
using Cohesive.Simulation.Relations;

ClrShapeGraphBuildResult shapes = new ClrShapeGraphBuilder()
    .AddShape<Carrier>(ShapeRoles.Entity)
    .AddShape<Load>(ShapeRoles.Entity)
    .AddEntityReference<Load, Carrier>(load => load.CarrierId)
    .BuildResult(new GraphId("freight/v1"));

RelationshipDefinition loadCarrierRelationship = Relationship
    .From<Load>(shapes)
    .Reference(load => load.CarrierId)
    .To(shapes.GetShape<Carrier>());

RelationshipCatalogDocument catalog = RelationshipCatalogDocument.FromCatalog(
    new RelationshipCatalog([loadCarrierRelationship]));

PocoGenerationDefinition<Carrier> carrierGeneration = Simulation.Define<Carrier>(shapes, carrier => carrier
    .Member(value => value.Name, Gen.Constant("Carrier")));
PocoGenerationDefinition<Load> loadGeneration = Simulation.Define<Load>(shapes, load => load
    .Member(value => value.Number, Gen.Int32(minimum: 1, maximum: 10_000)));

RelationshipWorldDefinition world = SimulationRelations.DefineWorld(
    id: "world/freight-demo",
    revision: "r1",
    relationshipCatalog: catalog,
    configure: world => world
        .Population("carriers", count: 20, carrierGeneration)
        .Population("loads", count: 100, loadGeneration)
        .Relationship(
            sourcePopulationId: "loads",
            relationshipId: loadCarrierRelationship.Id,
            targetPopulationId: "carriers"));

CompiledRelationshipWorldPlan plan = world.Compile();
GeneratedRelationshipWorldItem load = plan
    .GetPopulation("loads")
    .GenerateItem(seed: 42, sequenceIndex: 0);

public sealed record Carrier(string Name);
public sealed record Load(int Number, string CarrierId);
```

The shared `ClrShapeGraphBuildResult` is the single graph authority for both generators and the relationship.
`AddEntityReference` projects the selected CLR member as a canonical `EntityReferenceTypeRef`, assigns its reference
role, and annotates the target CLR shape with the matching entity type. Presence and nullability can be overridden
independently when the semantic contract is more precise than CLR nullability alone. `Simulation.Define<T>(shapes,
...)` resolves selectors through that exact metadata snapshot and fails immediately when `T` or a selected member is
not part of it.

Compilation verifies the exact relationship catalog and every population shape together. It rejects missing or
conflicting relationship authority, incompatible endpoints, locally generated relationship-field collisions,
unsupported reference contracts, invalid presence probabilities, empty selectable targets, insufficient unique
target capacity, and entity identities that depend on a relationship-bound field. The local generation plan cannot
be interpreted directly while it has externally supplied fields; only the owning relationship-world interpreter can
complete and validate that observation.

The current profile supports top-level, single-valued entity-reference fields targeting observation identity.
Selection is uniform over the complete target population. `PresenceProbability` may omit an optional field; required
references must always select a target. A canonical `GloballyUnique` source-reference guarantee uses a deterministic
permutation without replacement and fails compilation when a possibly selected source population exceeds target
capacity.

Target references use the target population's canonical `WorldEntityIdentityPolicy`, including unique observation
fields. Generation and compact `csimwr1.` replay tokens are deterministic from the root seed and semantic addresses.
Unrelated world populations and world-revision labels do not perturb an existing population's replay coordinates.

## Portable definition and retained artifacts

Persist a self-validating relationship world with `RelationshipWorldDefinitionJsonSerializer`:

```csharp
string json = RelationshipWorldDefinitionJsonSerializer.Serialize(world);
RelationshipWorldDefinitionDocument restored =
    RelationshipWorldDefinitionJsonSerializer.Deserialize(json);
```

The strict current-version document embeds the exact fingerprint-pinned relationship catalog and the complete world
definition, normalizes non-semantic declaration order, rejects unknown or duplicate properties, and recomputes its
fingerprint on read.

Retain that complete authority through provisioning and JSON Lines rather than reconstructing the relationship world
from the core world:

```csharp
using Cohesive.Simulation.Artifacts;
using Cohesive.Simulation.Provisioning;

WorldArtifactManifest artifact = RelationshipWorldArtifact.FromWorld(restored, rootSeed: 42);

await using var output = File.Create("freight-demo.jsonl");
var sink = new WorldJsonLinesSink("demo/freight", output);
await RelationshipWorldProvisioner.ProvisionAsync(artifact, sink);

output.Position = 0;
await RelationshipWorldJsonLinesVerifier.VerifyAsync(artifact, output);
```

The core manifest envelope embeds and fingerprints the exact relationship-world document while remaining independent
of the optional Relations assembly. The Relations package validates that document, proves its indexed world,
population, generation, and exemplar projections, and supplies the relationship interpreter behind the shared bounded
provisioning and JSONL seams. Calling the core-only provisioner with a relationship artifact fails closed. For scripts,
`cohesive-sim manifest --relationship-world ...` creates the same retained artifact and `cohesive-sim provision`
dispatches from its pinned interpreter identity.

For the complete repository-seeding and browser-fixture flow, see the
[executable adoption example](https://github.com/cohesivesystems/cohesive/blob/main/src/Cohesive.Examples/Simulation/SimulationAdoptionExamples.cs)
and the [seeding and Playwright guide](https://github.com/cohesivesystems/cohesive/blob/main/src/Cohesive.Simulation/docs/seeding-and-playwright.md).
