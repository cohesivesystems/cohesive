# Cohesive.Simulation.Storage

`Cohesive.Simulation.Storage` is the optional integration between deterministic simulation worlds and generic
Cohesive entity repositories. It implements `IWorldProvisioningSink` without adding Storage, Transitions, or entity
identity policy to the provider-neutral `Cohesive.Simulation` package.

## Repository provisioning

Bind each population explicitly to one repository, entity identity policy, state version, and requested batch
atomicity:

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
            entityIdentity: WorldEntityIdentityPolicy.PopulationSequence,
            stateVersion: 0,
            atomicity: EntityBatchAtomicity.None)
    ]);

WorldProvisioningResult result = await WorldProvisioner.ProvisionAsync(
    world,
    rootSeed: 42,
    sink,
    new WorldProvisioningOptions(batchSize: 100));
```

The sequence policy derives stable entity slots from the world/population scope and sequence index. Its IDs do not
depend on the root seed, so reseeding with another seed updates the same logical slots. When a domain field already
owns identity, use `FromUniqueObservationField`; that policy is an assertion that the scalar value is unique across
the complete population. The sink verifies the assertion across sequential batches in an active run and rejects a
duplicate before its conflicting write. It retains that bounded-run identity evidence after an unknown repository
failure so the same run can resume safely, then releases it after the population's final batch commits.

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
to repository entity identity, qualified shape, entity-ID policy, state version, or requested atomicity therefore
produce a different provisioning run identity automatically.
