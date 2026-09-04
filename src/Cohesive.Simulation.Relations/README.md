# Cohesive.Simulation.Relations

`Cohesive.Simulation.Relations` is the optional semantic composition between deterministic simulation worlds and
canonical Cohesive relationships. It lets one generated population carry references to actual entity identities in
another generated population without making `Cohesive.Simulation` depend on the Relations language.

The linked `RelationshipCatalogDocument` is the sole authority for the source field, endpoint shapes, target key,
cardinality, and uniqueness guarantee. A `WorldPopulationRelationshipBinding` declares only which world populations
occupy those endpoints and how often a target is selected. This avoids a second relationship model in simulation.

## Relationship-linked worlds

Build each population's local `GenerationDefinition` as usual, omitting fields supplied by canonical relationships.
Then bind the relationship to its source and target populations:

```csharp
using Cohesive.Relations.Model;
using Cohesive.Relations.Serialization;
using Cohesive.Simulation.Relations;

RelationshipCatalogDocument catalog = RelationshipCatalogDocument.FromCatalog(
    new RelationshipCatalog([loadCarrierRelationship]));

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
```

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

## Portable definition

Persist a self-validating relationship world with `RelationshipWorldDefinitionJsonSerializer`:

```csharp
string json = RelationshipWorldDefinitionJsonSerializer.Serialize(world);
RelationshipWorldDefinitionDocument restored =
    RelationshipWorldDefinitionJsonSerializer.Deserialize(json);
```

The strict current-version document embeds the exact fingerprint-pinned relationship catalog and the complete world
definition, normalizes non-semantic declaration order, rejects unknown or duplicate properties, and recomputes its
fingerprint on read.

This first package slice exposes generation and portable definition APIs. Core `WorldArtifactManifest`, JSON Lines,
CLI, and provisioning currently accept `WorldDefinition`; a later bridge must extend those retained-artifact paths
without weakening their fail-closed manifest authority.
