# Cohesive.Simulation.Storage

`Cohesive.Simulation.Storage` is the optional integration between deterministic simulation worlds and generic
Cohesive entity repositories. It implements `IWorldProvisioningSink` without making repository behavior part of the
provider-neutral `Cohesive.Simulation` package.

## Install

The current alpha targets .NET 10:

```bash
dotnet add package Cohesive.Simulation.Storage --prerelease
```

## Repository provisioning

Bind each population explicitly to one repository, state version, and requested batch atomicity. Entity identity is
already resolved by the canonical world definition and carried by each generated item:

```csharp
using Cohesive.Simulation.Provisioning;
using Cohesive.Simulation.Storage;

var sink = new RepositoryWorldProvisioningSink(
    destinationId: "demo/local",
    operationContext: OperationContext.Create(),
    bindings:
    [
        new RepositoryWorldPopulationBinding(
            populationId: "customers",
            repository: customerRepository,
            stateVersion: 0,
            atomicity: EntityBatchAtomicity.None)
    ]);

WorldProvisioningResult result = await WorldProvisioner.ProvisionAsync(
    world,
    rootSeed: 42,
    sink,
    new WorldProvisioningOptions(batchSize: 100));
```

Declare `WorldEntityIdentityPolicy.PopulationSequence` or `FromUniqueObservationField` while authoring the world.
Pure world generation resolves those policies and detects duplicate unique-field identities. The sink consumes each
artifact-provided `EntityId`; it cannot select a different mapping that would break generated references.

Generated observation shapes must exactly equal the repository entity's qualified state shape. Every candidate is
validated against entity semantics before repository access begins. Missing bindings, unsupported atomicity, native
batch-size limits, shape mismatches, invalid identities, duplicate identities, and entity-rule violations produce an
explicit rejected batch receipt.

Successful batches are deterministic upserts through the shared `RepositorySeedWriter`. The generic repository
contract does not provide a durable provisioning-batch ledger, so the sink returns `Committed` on every successful
upsert and does not claim exactly-once delivery or `AlreadyCommitted`. Repository exceptions remain unwrapped because
a non-atomic batch may have an unknown partial outcome. Storage-specific durable ledgers or create-only policies
belong in adapters that can actually guarantee those semantics.

The sink derives its effective `TargetId` from the logical destination plus normalized population bindings. Changes
to repository entity identity, qualified shape, state version, or requested atomicity therefore produce a different
provisioning run identity automatically. World identity-policy changes already change the artifact identity.

The [seeding and Playwright guide](https://github.com/cohesivesystems/cohesive/blob/main/src/Cohesive.Simulation/docs/seeding-and-playwright.md)
distinguishes direct repository provisioning from portable JSONL exchange. A complete in-memory repository flow is
maintained as an [executable example](https://github.com/cohesivesystems/cohesive/blob/main/src/Cohesive.Examples/Simulation/SimulationAdoptionExamples.cs).
