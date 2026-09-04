using System.Collections.Immutable;
using Cohesive.Model;
using Cohesive.Simulation.Generation;
using Cohesive.Simulation.Provisioning;
using Cohesive.Simulation.Storage;
using Cohesive.Simulation.Worlds;
using Cohesive.Storage;
using Cohesive.Transitions.Model;

namespace Cohesive.Simulation.Tests;

public sealed class RepositoryWorldProvisioningSinkTests
{
    [Fact]
    public async Task UniqueObservationFieldIdentity_UpsertsValidatedGeneratedEntityState()
    {
        var entity = CustomerEntity();
        var repository = new InMemoryEntityOutboxRepository(entity, EntityPartitionKeyPolicy.ObservationId);
        var plan = CustomerWorld(
            entity,
            count: 1,
            externalId: "customer-42",
            entityIdentity: WorldEntityIdentityPolicy.FromUniqueObservationField("ExternalId")).Compile();
        var sink = RepositorySink(repository, stateVersion: 7);

        var result = await WorldProvisioner.ProvisionAsync(plan, rootSeed: 42, sink);
        var snapshot = await repository.TryGet(
            OperationContext.Create(),
            id: "customer-42",
            EntityReadOptions.Full);

        Assert.NotNull(snapshot);
        Assert.Equal(7, snapshot.Entity.Version);
        Assert.Equal(
            plan.GetPopulation("customers").Generate(seed: 42)[0].Observation,
            snapshot.Entity.Observation);
        Assert.Equal(1, result.ItemCount);
        Assert.Equal(0, result.AlreadyCommittedBatchCount);
    }

    [Fact]
    public async Task PopulationSequenceIdentity_ConvergesStableEntitySlotsAcrossSeeds()
    {
        var entity = CustomerEntity();
        var repository = new InMemoryEntityOutboxRepository(entity, EntityPartitionKeyPolicy.ObservationId);
        var plan = CustomerWorld(entity, count: 2, externalId: "ignored-by-sequence-policy").Compile();
        var population = plan.GetPopulation("customers");
        var sink = RepositorySink(repository);

        await WorldProvisioner.ProvisionAsync(plan, rootSeed: 41, sink);
        await WorldProvisioner.ProvisionAsync(plan, rootSeed: 42, sink);

        for (var index = 0; index < 2; index++)
        {
            var entityId = WorldEntitySequenceIdentityConvention.Create(population.Scope, index);
            var snapshot = await repository.TryGet(
                OperationContext.Create(),
                entityId.Value,
                EntityReadOptions.Full);
            Assert.NotNull(snapshot);
            Assert.Equal(
                population.Generate(seed: 42)[index].Observation,
                snapshot.Entity.Observation);
        }
    }

    [Fact]
    public async Task DuplicateResolvedEntityIdentitiesAcrossBatches_FailWorldGenerationBeforeConflictingWrite()
    {
        var entity = CustomerEntity();
        var repository = new InMemoryEntityOutboxRepository(entity, EntityPartitionKeyPolicy.ObservationId);
        var plan = CustomerWorld(
            entity,
            count: 2,
            externalId: "duplicate",
            entityIdentity: WorldEntityIdentityPolicy.FromUniqueObservationField("ExternalId")).Compile();
        var sink = RepositorySink(repository);

        var exception = await Assert.ThrowsAsync<WorldGenerationException>(() =>
            WorldProvisioner.ProvisionAsync(
                plan,
                rootSeed: 42,
                sink,
                new(batchSize: 1)));

        Assert.Contains(
            exception.Validation.Diagnostics,
            static diagnostic => diagnostic.Code == "simulation.world.entityIdentityDuplicate");
        var firstCommitted = await repository.TryGet(
            OperationContext.Create(),
            "duplicate",
            EntityReadOptions.Full);
        Assert.NotNull(firstCommitted);
        Assert.Equal("mem:1", firstCommitted.ConcurrencyToken.Value);
    }

    [Fact]
    public async Task MissingPopulationBinding_RejectsBeforeRepositoryAccess()
    {
        var entity = CustomerEntity();
        CountingRepository repository = new(entity);
        var binding = new RepositoryWorldPopulationBinding(
            "orders",
            repository);
        var sink = new RepositoryWorldProvisioningSink(
            "demo/repositories",
            OperationContext.Create(),
            [binding]);

        var exception = await Assert.ThrowsAsync<WorldProvisioningRejectedException>(() =>
            WorldProvisioner.ProvisionAsync(
                CustomerWorld(entity, count: 1, externalId: "customer-1").Compile(),
                rootSeed: 42,
                sink));

        Assert.Contains("no repository binding", exception.Receipt.Detail, StringComparison.Ordinal);
        Assert.Equal(0, repository.ReadCount);
        Assert.Equal(0, repository.WriteCount);
    }

    [Fact]
    public async Task ShapeMismatch_RejectsBeforeRepositoryAccess()
    {
        var generatedEntity = CustomerEntity();
        var repositoryEntity = AlternateEntity();
        CountingRepository repository = new(repositoryEntity);
        var sink = RepositorySink(repository);

        var exception = await Assert.ThrowsAsync<WorldProvisioningRejectedException>(() =>
            WorldProvisioner.ProvisionAsync(
                CustomerWorld(generatedEntity, count: 1, externalId: "customer-1").Compile(),
                rootSeed: 42,
                sink));

        Assert.Contains("does not match", exception.Receipt.Detail, StringComparison.Ordinal);
        Assert.Equal(0, repository.ReadCount);
        Assert.Equal(0, repository.WriteCount);
    }

    [Fact]
    public async Task UnsupportedAtomicityAndBatchLimit_ProduceActionableRejections()
    {
        var entity = CustomerEntity();
        CountingRepository nonAtomic = new(entity);
        var atomicSink = RepositorySink(
            nonAtomic,
            atomicity: EntityBatchAtomicity.SamePartition);
        CountingRepository bounded = new(
            entity,
            new(
                SupportsNativeBatching: true,
                SupportsSamePartitionAtomicity: false,
                SupportsAllOrNothingAtomicity: false,
                MaxItemsPerBatch: 1));
        var boundedSink = RepositorySink(bounded);
        var plan = CustomerWorld(entity, count: 2, externalId: "customer").Compile();

        var atomicity = await Assert.ThrowsAsync<WorldProvisioningRejectedException>(() =>
            WorldProvisioner.ProvisionAsync(plan, rootSeed: 42, atomicSink, new(batchSize: 2)));
        var limit = await Assert.ThrowsAsync<WorldProvisioningRejectedException>(() =>
            WorldProvisioner.ProvisionAsync(plan, rootSeed: 42, boundedSink, new(batchSize: 2)));

        Assert.Contains("does not support", atomicity.Receipt.Detail, StringComparison.Ordinal);
        Assert.Contains("at most '1'", limit.Receipt.Detail, StringComparison.Ordinal);
        Assert.Equal(0, nonAtomic.ReadCount + nonAtomic.WriteCount);
        Assert.Equal(0, bounded.ReadCount + bounded.WriteCount);
    }

    [Fact]
    public async Task RepositoryFailure_IsPreservedAsUnknownOutcomeWithoutAutomaticRetry()
    {
        var entity = CustomerEntity();
        var expected = new IOException("repository outcome unknown");
        CountingRepository repository = new(entity, writeFailure: expected);
        var sink = RepositorySink(repository);

        var observed = await Assert.ThrowsAsync<IOException>(() =>
            WorldProvisioner.ProvisionAsync(
                CustomerWorld(entity, count: 1, externalId: "customer-1").Compile(),
                rootSeed: 42,
                sink));

        Assert.Same(expected, observed);
        Assert.Equal(1, repository.WriteCount);
    }

    [Fact]
    public void TargetIdentity_IsOrderIndependentAndCoversRepositoryBindingPolicy()
    {
        var entity = CustomerEntity();
        CountingRepository repository = new(entity);
        RepositoryWorldPopulationBinding customers = new(
            "customers",
            repository,
            stateVersion: 0);
        RepositoryWorldPopulationBinding orders = new(
            "orders",
            repository,
            stateVersion: 0);
        RepositoryWorldPopulationBinding revisedCustomers = new(
            "customers",
            repository,
            stateVersion: 1);

        var first = RepositoryWorldProvisioningTargetConvention.Create(
            "demo/repositories",
            [customers, orders]);
        var reordered = RepositoryWorldProvisioningTargetConvention.Create(
            "demo/repositories",
            [orders, customers]);
        var revised = RepositoryWorldProvisioningTargetConvention.Create(
            "demo/repositories",
            [revisedCustomers, orders]);

        Assert.Equal(first, reordered);
        Assert.NotEqual(first, revised);
        Assert.Throws<ArgumentException>(() => RepositoryWorldProvisioningTargetConvention.Create(
            "demo/repositories",
            [customers, customers]));
    }

    static RepositoryWorldProvisioningSink RepositorySink(
        IEntityRepository repository,
        long stateVersion = 0,
        EntityBatchAtomicity atomicity = EntityBatchAtomicity.None) =>
        new(
            "demo/repositories",
            OperationContext.Create(),
            [new("customers", repository, stateVersion, atomicity)]);

    static WorldDefinition CustomerWorld(
        EntityDefinition entity,
        int count,
        string externalId,
        WorldEntityIdentityPolicy? entityIdentity = null)
    {
        var stateShape = entity.StateShape;
        GenerationDefinition generation = new(
            "generation/customer",
            "r1",
            stateShape.Graph,
            new(
                stateShape.ShapeId,
                [
                    new(new("ExternalId"), new ConstantGenerationNode(
                        new ScalarTypeRef(ScalarTypeKind.String),
                        ObservationValue.FromString(externalId))),
                    new(new("Name"), new WeightedCategoricalGenerationNode(
                        new ScalarTypeRef(ScalarTypeKind.String),
                        [
                            new(ObservationValue.FromString("Ada"), weight: 1d),
                            new(ObservationValue.FromString("Grace"), weight: 1d)
                        ])),
                    new(new("Age"), new Int32GenerationNode(minimum: 18, maximum: 90))
                ]));
        return new(
            "world/repository-demo",
            "r1",
            [new("customers", count, entityIdentity ?? WorldEntityIdentityPolicy.PopulationSequence, generation)]);
    }

    static EntityDefinition CustomerEntity() => new(
        new("Customer"),
        [
            new(new("ExternalId"), new ScalarTypeRef(ScalarTypeKind.String)),
            new(new("Name"), new ScalarTypeRef(ScalarTypeKind.String)),
            new(new("Age"), new ScalarTypeRef(ScalarTypeKind.Int32))
        ]);

    static EntityDefinition AlternateEntity() => new(
        new("AlternateCustomer"),
        [new(new("Value"), new ScalarTypeRef(ScalarTypeKind.String))]);

    sealed class CountingRepository(
        EntityDefinition entityDefinition,
        EntityBatchCapabilities? capabilities = null,
        Exception? writeFailure = null)
        : IEntityRepository
    {
        readonly Dictionary<string, EntitySnapshot> snapshots = new(StringComparer.Ordinal);

        public EntityDefinition EntityDefinition { get; } = entityDefinition;

        public EntityBatchCapabilities BatchCapabilities { get; } =
            capabilities ?? EntityBatchCapabilities.SingleWriteFallback;

        public int ReadCount { get; private set; }

        public int WriteCount { get; private set; }

        public Task<EntitySnapshot?> TryGet(
            OperationContext context,
            string id,
            EntityReadOptions? options = null)
        {
            context.ThrowIfCancellationRequested();
            ReadCount++;
            return Task.FromResult(snapshots.GetValueOrDefault(id));
        }

        public Task<EntitySnapshot> Upsert(
            OperationContext context,
            EntityWriteRequest write)
        {
            context.ThrowIfCancellationRequested();
            WriteCount++;
            if (writeFailure is not null)
            {
                throw writeFailure;
            }

            EntitySnapshot snapshot = new(
                write.Entity,
                PartitionKey: write.Entity.EntityId.Value,
                ConcurrencyToken: new($"test:{WriteCount}"));
            snapshots[write.Entity.EntityId.Value] = snapshot;
            return Task.FromResult(snapshot);
        }
    }
}
