using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Model;
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
    public async Task InMemoryObservationOutboxRepository_UpsertWithOutboxAsync_PersistsAndReplaysExactEnvelopes()
    {
        var repository = CreateRepository();
        var initialState = SampleObservation.Instance.CreateState("obs-1", new SampleSeed
        {
            Id = "obs-1",
            PartitionKey = "tenant-a",
            Name = "alpha"
        });
        var initial = await repository.Upsert(OperationContext.Create(), new(initialState.Observation));
        var candidate = new Observation(
            initial.Entity.ShapeId,
            initial.Entity.Id,
            initial.Entity.Fields.ToDictionary(static field => field.Key, static field => field.Value, StringComparer.Ordinal),
            version: initial.Entity.Version + 1,
            lineage: initial.Entity.Lineage);
        var envelope = Envelope("emission/1", "generated", candidate);
        var commit = new EntityOutboxCommit(
            new(candidate, initial.ConcurrencyToken),
            [envelope]);

        var result = await repository.UpsertWithOutbox(
            OperationContext.Create(),
            commit);
        var replay = await repository.UpsertWithOutbox(OperationContext.Create(), commit);

        Assert.Equal("obs-1", result.Entity.Entity.Id);
        Assert.Equal("tenant-a", result.Entity.PartitionKey);
        Assert.Equal(result.Entity.ConcurrencyToken, replay.Entity.ConcurrencyToken);
        Assert.Single(result.Envelopes);
        Assert.Single(repository.OutboxEnvelopes);
        Assert.Equal(envelope, repository.OutboxEnvelopes[0]);
    }

    [Fact]
    public async Task InMemoryObservationOutboxRepository_UpsertWithOutboxAsync_RejectsIdentityConflictBeforeMutation()
    {
        var repository = CreateRepository();
        var initialState = SampleObservation.Instance.CreateState("obs-1", new SampleSeed
        {
            Id = "obs-1",
            PartitionKey = "tenant-a",
            Name = "alpha"
        });
        var initial = await repository.Upsert(OperationContext.Create(), new(initialState.Observation));
        var candidate = new Observation(
            initial.Entity.ShapeId,
            initial.Entity.Id,
            initial.Entity.Fields,
            version: initial.Entity.Version + 1,
            lineage: initial.Entity.Lineage);
        await repository.UpsertWithOutbox(
            OperationContext.Create(),
            new(new(candidate, initial.ConcurrencyToken), [Envelope("emission/1", "first", candidate)]));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => repository.UpsertWithOutbox(
            OperationContext.Create(),
            new(new(candidate, initial.ConcurrencyToken), [Envelope("emission/1", "different", candidate)])));

        Assert.Contains("different canonical content", error.Message, StringComparison.Ordinal);
        Assert.Single(repository.OutboxEnvelopes);
        var retained = await repository.TryGet(OperationContext.Create(), id: candidate.Id, options: EntityReadOptions.Full);
        Assert.NotNull(retained);
        Assert.True(retained.Entity.HasSameContent(candidate));
    }

    [Fact]
    public void EntityOutboxCommit_RejectsDuplicateEmissionIdentity()
    {
        var candidate = SampleObservation.Instance.CreateState("obs-1", new SampleSeed
        {
            Id = "obs-1",
            PartitionKey = "tenant-a",
            Name = "alpha"
        }).Observation;

        var error = Assert.Throws<ArgumentException>(() => new EntityOutboxCommit(
            new(candidate),
            [Envelope("emission/1", "first", candidate), Envelope("emission/1", "first", candidate)]));

        Assert.Contains("duplicated", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InMemoryObservationOutboxRepository_StaleConcurrencyAppendsNoEnvelope()
    {
        var repository = CreateRepository();
        var state = SampleObservation.Instance.CreateState("obs-1", new SampleSeed
        {
            Id = "obs-1",
            PartitionKey = "tenant-a",
            Name = "alpha"
        });
        var initial = await repository.Upsert(OperationContext.Create(), new(state.Observation));
        var concurrent = new Observation(
            initial.Entity.ShapeId,
            initial.Entity.Id,
            initial.Entity.Fields,
            version: initial.Entity.Version + 1,
            lineage: initial.Entity.Lineage);
        await repository.Upsert(OperationContext.Create(), new(concurrent, initial.ConcurrencyToken));
        var staleCandidate = new Observation(
            initial.Entity.ShapeId,
            initial.Entity.Id,
            initial.Entity.Fields,
            version: initial.Entity.Version + 1,
            lineage: initial.Entity.Lineage);

        await Assert.ThrowsAsync<ObservationConcurrencyConflictException>(() => repository.UpsertWithOutbox(
            OperationContext.Create(),
            new(
                new(staleCandidate, initial.ConcurrencyToken),
                [Envelope("emission/stale", "stale", staleCandidate)])));

        Assert.Empty(repository.OutboxEnvelopes);
    }

    static DomainEventEnvelope Envelope(string emissionId, string payload, Observation entity) => new(
        InteractionEnvelope.CurrentSchemaVersion,
        new(
            new(emissionId),
            new TransitionInteractionOrigin(
                Definition("transition/sample"),
                new("emit/sample"),
                new(new(entity.ShapeId.Value), new(entity.Id)),
                new("outcome/sample")),
            new("correlation/sample"),
            causationId: null,
            new("authority/tests", "tenant-a"),
            new($"idempotency/{emissionId}"),
            ordering: null,
            new(InteractionDurabilityDemand.Durable, InteractionVisibilityDemand.AfterOriginCommit),
            Provenance()),
        new(Definition("event/sample")),
        PortableValue.Concrete(
            new(new ScalarTypeRef(ScalarTypeKind.String)),
            ObservationValue.FromString(payload)));

    static ExecutionDefinitionReference Definition(string id) => new(
        new(id),
        new("revision/1"),
        new(
            ExecutionDefinitionFingerprinter.Algorithm,
            ExecutionDefinitionFingerprinter.Canonicalization,
            new string('a', 64)));

    static ExecutionProvenance Provenance() => new(
        new("in-memory-entity-outbox-tests", "1"),
        new("tests/model/in-memory-entity-outbox"),
        DocumentOrigin.Generated);

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
