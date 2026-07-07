using Cohesive.Relations.Queries;
using Cohesive.Storage;

namespace Cohesive.Tests.Model;

public sealed class InMemoryEntityOutboxRepositoryTests
{
    [Fact]
    public async Task InMemoryObservationOutboxRepository_WithSeedData_LoadsSnapshots()
    {
        var repository = CreateRepository(
            [
                new SampleSeed { Id = "obs-1", PartitionKey = "tenant-a", Name = "alpha" }
            ]);

        var snapshot = await repository.TryGet(
            OperationContext.Create(),
            id: "obs-1",
            options: EntityReadOptions.Full);

        Assert.NotNull(snapshot);
        Assert.Equal("alpha", snapshot.Entity.GetField(nameof(SampleObservation.Name)).GetString());
        Assert.Equal("tenant-a", snapshot.PartitionKey);
    }

    [Fact]
    public async Task InMemoryObservationOutboxRepository_QueryAsync_WithDuplicateIdsAcrossPartitions_PreservesPartitionKeys()
    {
        var repository = CreateRepository(
            [
                new SampleSeed { Id = "shared-id", PartitionKey = "tenant-a", Name = "alpha-a" },
                new SampleSeed { Id = "shared-id", PartitionKey = "tenant-b", Name = "alpha-b" }
            ]);

        var results = await ReadAllAsync(repository.QueryStream(
            OperationContext.Create(),
            new EntityQuery(
                Predicate: new(
                    new FieldPredicate(
                        FieldPath.FromField(nameof(SampleObservation.Name)),
                        new PrefixValuePredicate("alpha"))))));

        Assert.Equal(2, results.Count);
        Assert.Contains(results, snapshot => snapshot.PartitionKey == "tenant-a");
        Assert.Contains(results, snapshot => snapshot.PartitionKey == "tenant-b");
        Assert.All(results, snapshot => Assert.Equal("shared-id", snapshot.Entity.Id));
    }

    [Fact]
    public async Task InMemoryObservationOutboxRepository_TryGet_WithDuplicateIdsAcrossPartitions_Throws()
    {
        var repository = CreateRepository(
            [
                new SampleSeed { Id = "shared-id", PartitionKey = "tenant-a", Name = "alpha-a" },
                new SampleSeed { Id = "shared-id", PartitionKey = "tenant-b", Name = "alpha-b" }
            ]);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.TryGet(
                OperationContext.Create(),
                id: "shared-id",
                options: EntityReadOptions.Full));

        Assert.Contains("multiple partitions", error.Message);
    }

    [Fact]
    public async Task InMemoryObservationOutboxRepository_TryGet_UsesContextAwarePartitionPolicy()
    {
        const string partitionKeyItem = "sample.partition-key";
        var repository = new InMemoryEntityOutboxRepository(
            SampleObservation.Instance.Definition,
            new EntityPartitionKeyPolicy(
                description: "operation-context partition",
                writePartitionKeyResolver: static (context, _) => ReadPartitionKey(context, partitionKeyItem),
                pointReadPartitionKeyResolver: static (context, _) => ReadPartitionKey(context, partitionKeyItem)
                ));
        var state = SampleObservation.Instance.CreateState("shared-id", new SampleSeed
        {
            Id = "shared-id",
            PartitionKey = "ignored-by-policy",
            Name = "alpha"
        });
        var tenantAContext = OperationContext.Create().WithItem(partitionKeyItem, "tenant-a");
        var tenantBContext = OperationContext.Create().WithItem(partitionKeyItem, "tenant-b");

        await repository.Upsert(
            tenantAContext,
            new(state.Observation));

        Assert.Null(await repository.TryGet(tenantBContext, id: "shared-id", options: EntityReadOptions.Full));
        var snapshot = await repository.TryGet(tenantAContext, id: "shared-id", options: EntityReadOptions.Full);
        Assert.NotNull(snapshot);
        Assert.Equal("tenant-a", snapshot.PartitionKey);
    }

    [Fact]
    public async Task InMemoryObservationOutboxRepository_UpsertWithOutboxAsync_PersistsSnapshotAndMessages()
    {
        var repository = CreateRepository();
        var state = SampleObservation.Instance.CreateState("obs-1", new SampleSeed
        {
            Id = "obs-1",
            PartitionKey = "tenant-a",
            Name = "alpha"
        });
        var message = new EntityOutboxMessage(
            MessageId: "msg-1",
            StreamName: "sample-stream",
            SubjectType: "SampleObservation",
            SubjectId: "obs-1",
            PartitionKey: "tenant-a",
            Entity: new(
                shapeId: new("SampleGenerated"),
                id: "msg-1",
                fields: new Dictionary<string, ObservationValue>(StringComparer.Ordinal)
                {
                    ["Name"] = ObservationValue.FromString("generated")
                }));

        var result = await repository.UpsertWithOutbox(
            OperationContext.Create(),
            new(
                Write: new(state.Observation),
                Messages: [message]));

        Assert.Equal("obs-1", result.Entity.Entity.Id);
        Assert.Equal("tenant-a", result.Entity.PartitionKey);
        Assert.Single(result.Messages);
        Assert.Single(repository.OutboxMessages);
        Assert.Equal("msg-1", repository.OutboxMessages[0].MessageId);
    }

    static string ReadPartitionKey(OperationContext context, string key) =>
        context.TryGetItem<string>(key, out var partitionKey)
            ? partitionKey ?? ""
            : "";

    static InMemoryEntityOutboxRepository CreateRepository(IEnumerable<object>? seedData = null) =>
        new(
            entityDefinition: SampleObservation.Instance.Definition,
            seedData: seedData,
            partitionKeyFieldName: nameof(SampleObservation.PartitionKey),
            idFieldName: nameof(SampleSeed.Id));

    static async Task<IReadOnlyList<EntitySnapshot>> ReadAllAsync(IAsyncEnumerable<EntitySnapshot> snapshots)
    {
        List<EntitySnapshot> results = [];
        await foreach (var snapshot in snapshots)
            results.Add(snapshot);

        return results;
    }

    sealed class SampleObservation : Entity<SampleObservation>
    {
        public SampleObservation()
            : base(nameof(SampleObservation))
        {
            Id = WriteOnceField<string>(nameof(Id));
            PartitionKey = WriteOnceField<string>(nameof(PartitionKey));
            Name = WriteOnceField<string>(nameof(Name));
        }

        public Field<string> Id { get; }

        public Field<string> PartitionKey { get; }

        public Field<string> Name { get; }
    }

    sealed record SampleSeed
    {
        public required string Id { get; init; }

        public required string PartitionKey { get; init; }

        public required string Name { get; init; }
    }
}
