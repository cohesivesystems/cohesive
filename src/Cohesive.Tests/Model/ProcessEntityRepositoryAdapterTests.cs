using Cohesive.Relations.Model;
using Cohesive.Storage;

namespace Cohesive.Tests.Model;

public sealed class ProcessEntityRepositoryAdapterTests
{
    [Fact]
    public async Task ProcessEntityReadRepositoryAdapter_GetByIdsAsync_LoadsObservationsFromProcessStorage()
    {
        var storage = new InMemoryProcessStorageAdapter();
        storage.SeedEntity(
            new("Counter", "counter-1"),
            CounterEntity.Instance.CreateState("counter-1", new { Value = 5 }, version: 7),
            version: 7
            );

        var repository = new ProcessEntityReadRepositoryAdapter(storage, "Counter");

        var result = await repository.GetByIds(OperationContext.Create(), ["counter-1", "missing"]);

        Assert.Single(result);
        Assert.Equal(5, result["counter-1"].GetField("Value").GetInt32());
    }

    [Fact]
    public async Task DispatchingProcessEntityRepository_RoutesLoadAndCommitByEntityType()
    {
        var context = OperationContext.Create();
        var routeRef = new ProcessEntityRef("Route", "route-1");
        var counterRef = new ProcessEntityRef("Counter", "counter-1");

        var routes = new InMemoryProcessStorageAdapter();
        routes.SeedEntity(routeRef, RouteEntity.Instance.CreateState("route-1", new { Value = 10 }, version: 2), version: 2);

        var counters = new InMemoryProcessStorageAdapter();
        counters.SeedEntity(counterRef, CounterEntity.Instance.CreateState("counter-1", new { Value = 0 }, version: 0), version: 0);

        var repository = new DispatchingProcessEntityRepository()
            .Register(RouteEntity.Instance.Definition, routes)
            .Register(CounterEntity.Instance.Definition, counters);

        var routeSnapshot = await repository.Get(context, routeRef);
        var counterSnapshot = await repository.Get(context, counterRef);

        Assert.Equal(2, routeSnapshot.Version);
        Assert.Equal(ProcessEntityConcurrencyToken.FromVersion(2), routeSnapshot.ConcurrencyToken);
        Assert.Equal(0, counterSnapshot.Version);

        var incrementedCounterState = CounterEntity.Instance.CreateState("counter-1", new { Value = 1 }, version: 1);
        var transition = new TransitionResult(
            TransitionName: "Increment",
            OldState: counterSnapshot.State,
            NewState: incrementedCounterState,
            Effects: [],
            NewVersion: 1);

        await repository.Update(
            context,
            counterRef,
            transition,
            processId: "proc-dispatch",
            options: ProcessEntityWriteOptions.Full(counterSnapshot.ConcurrencyToken));

        var finalCounter = await counters.Get(context, counterRef);
        var finalRoute = await routes.Get(context, routeRef);

        Assert.Equal(1, finalCounter.State.Fields["Value"].GetInt32());
        Assert.Equal(ProcessEntityConcurrencyToken.FromVersion(1), finalCounter.ConcurrencyToken);
        Assert.Equal(10, finalRoute.State.Fields["Value"].GetInt32());
    }

    [Fact]
    public async Task InMemoryProcessStorageAdapter_Get_WithFieldSubset_ProjectsState()
    {
        var context = OperationContext.Create();
        var entityRef = new ProcessEntityRef("Counter", "counter-1");
        var storage = new InMemoryProcessStorageAdapter();
        storage.SeedEntity(
            entityRef,
            CounterEntity.Instance.CreateState("counter-1", new { Value = 5, Label = "keep" }, version: 7),
            version: 7);

        var snapshot = await storage.Get(context, entityRef, options: ProcessEntityReadOptions.ForFields("Value"));

        Assert.False(snapshot.HasFullState);
        Assert.NotNull(snapshot.LoadedFields);
        Assert.Contains("Value", snapshot.LoadedFields!);
        Assert.Equal(5, snapshot.State.Fields["Value"].GetInt32());
        Assert.DoesNotContain("Label", snapshot.State.Fields.Keys);
    }

    [Fact]
    public void ProcessEntityReadOptions_WithFieldSelection_StoresSelection()
    {
        var selection = FieldSelection.ForFields("Value", "Value");
        var options = new ProcessEntityReadOptions(fieldSelection: selection)
            .WithExpectedVersion(7)
            .WithExpectedConcurrencyToken(new("etag-7"));

        Assert.Same(selection, options.FieldSelection);
        Assert.Equal(["Value"], options.Fields);
        Assert.True(options.HasFieldProjection);
        Assert.Equal(7, options.ExpectedVersion);
        Assert.Equal(new ProcessEntityConcurrencyToken("etag-7"), options.ExpectedConcurrencyToken);
    }

    [Fact]
    public void ProcessEntityWriteOptions_WithFieldSelection_StoresSelection()
    {
        var selection = FieldSelection.ForFields("Value", "Value");
        var options = new ProcessEntityWriteOptions(new("etag-7"), selection);

        Assert.Equal(new ProcessEntityConcurrencyToken("etag-7"), options.ExpectedConcurrencyToken);
        Assert.Same(selection, options.FieldSelection);
        Assert.Equal(["Value"], options.Fields);
        Assert.True(options.HasFieldProjection);
    }

    [Fact]
    public async Task InMemoryProcessStorageAdapter_Update_WithFieldSubset_MergesState()
    {
        var context = OperationContext.Create();
        var entityRef = new ProcessEntityRef("Counter", "counter-1");
        var storage = new InMemoryProcessStorageAdapter();
        storage.SeedEntity(
            entityRef,
            CounterEntity.Instance.CreateState("counter-1", new { Value = 5, Label = "keep" }, version: 7),
            version: 7);

        var snapshot = await storage.Get(context, entityRef);
        var transition = new TransitionResult(
            TransitionName: "Increment",
            OldState: snapshot.State,
            NewState: CounterEntity.Instance.CreateState("counter-1", new { Value = 6, Label = "replace" }, version: 8),
            Effects: [],
            NewVersion: 8);

        await storage.Update(
            context,
            entityRef,
            transition,
            processId: "proc-partial",
            options: ProcessEntityWriteOptions.ForFields(snapshot.ConcurrencyToken, "Value"));

        var finalSnapshot = await storage.Get(context, entityRef);
        Assert.Equal(6, finalSnapshot.State.Fields["Value"].GetInt32());
        Assert.Equal("keep", finalSnapshot.State.Fields["Label"].GetString());
    }

    [Fact]
    public async Task InMemoryProcessStorageAdapter_Get_WithExpectedVersion_Mismatch_ThrowsConcurrencyConflict()
    {
        var context = OperationContext.Create();
        var entityRef = new ProcessEntityRef("Counter", "counter-1");
        var storage = new InMemoryProcessStorageAdapter();
        storage.SeedEntity(
            entityRef,
            CounterEntity.Instance.CreateState("counter-1", new { Value = 5 }, version: 7),
            version: 7);

        await Assert.ThrowsAsync<ProcessConcurrencyConflictException>(() => storage.Get(
            context,
            entityRef,
            options: new(expectedVersion: 8)
            )
        );
    }

    [Fact]
    public async Task ObservationProcessEntityRepository_Get_ReturnsSnapshotWithObservationConcurrencyToken()
    {
        var context = OperationContext.Create();
        var entityRef = new ProcessEntityRef("Counter", "counter-1");
        var repository = new StubObservationOutboxRepository(CounterEntity.Instance.Definition);
        repository.Seed(CreateObservationSnapshot(
            CounterEntity.Instance.CreateState("counter-1", new { Value = 5 }, version: 7),
            concurrencyToken: "etag-7"));

        var adapter = new ProcessEntityRepositoryAdapter(repository);
        var snapshot = await adapter.Get(context, entityRef);

        Assert.Equal(7, snapshot.Version);
        Assert.Equal(new("etag-7"), snapshot.ConcurrencyToken);
        Assert.Equal(5, snapshot.State.Fields["Value"].GetInt32());
    }

    [Fact]
    public async Task ObservationProcessEntityRepository_Update_WithFieldSubset_MergesState()
    {
        var context = OperationContext.Create();
        var entityRef = new ProcessEntityRef("Counter", "counter-1");
        var repository = new StubObservationOutboxRepository(CounterEntity.Instance.Definition);
        repository.Seed(CreateObservationSnapshot(
            CounterEntity.Instance.CreateState("counter-1", new { Value = 5, Label = "keep" }, version: 7),
            concurrencyToken: "etag-7"));

        var adapter = new ProcessEntityRepositoryAdapter(repository);
        var snapshot = await adapter.Get(context, entityRef);
        var transition = new TransitionResult(
            TransitionName: "Increment",
            OldState: snapshot.State,
            NewState: CounterEntity.Instance.CreateState("counter-1", new { Value = 6, Label = "replace" }, version: 8),
            Effects: [],
            NewVersion: 8);

        await adapter.Update(
            context,
            entityRef,
            transition,
            processId: "proc-observation",
            options: ProcessEntityWriteOptions.ForFields(snapshot.ConcurrencyToken, "Value"));

        var stored = repository.GetSnapshot("counter-1", "counter-1");
        Assert.Equal(8, stored.Entity.Version);
        Assert.Equal(6, stored.Entity.GetField("Value").GetInt32());
        Assert.Equal("keep", stored.Entity.GetField("Label").GetString());
        Assert.Equal(new EntityConcurrencyToken("etag-8"), stored.ConcurrencyToken);
    }

    [Fact]
    public async Task ObservationProcessEntityRepository_Update_WithPersistEffectsInOutbox_WritesMessages()
    {
        var context = OperationContext.Create();
        var entityRef = new ProcessEntityRef("Counter", "counter-1");
        var repository = new StubObservationOutboxRepository(CounterEntity.Instance.Definition);
        repository.Seed(CreateObservationSnapshot(
            CounterEntity.Instance.CreateState("counter-1", new { Value = 5 }, version: 7),
            concurrencyToken: "etag-7"));

        var adapter = new ProcessEntityRepositoryAdapter(
            repository,
            options: new()
            {
                PersistEffectsInOutbox = true
            });

        var snapshot = await adapter.Get(context, entityRef);
        var effect = EffectRequest.Named(
            "Notify",
            new { Message = "hello" },
            continuation: new EffectContinuation("ApplyResult"),
            snapshot: new EffectSnapshot("snap-1", ["Value"]));

        await adapter.Update(
            context,
            entityRef,
            new TransitionResult(
                TransitionName: "Increment",
                OldState: snapshot.State,
                NewState: CounterEntity.Instance.CreateState("counter-1", new { Value = 6 }, version: 8),
                Effects: [effect],
                NewVersion: 8),
            processId: "proc-outbox",
            options: ProcessEntityWriteOptions.Full(snapshot.ConcurrencyToken));

        var commit = Assert.IsType<EntityOutboxCommit>(repository.LastOutboxCommit);
        var message = Assert.Single(commit.Messages);
        Assert.Equal("process-effects", message.StreamName);
        Assert.Equal("Counter", message.SubjectType);
        Assert.Equal("counter-1", message.SubjectId);
        Assert.Equal("counter-1", message.PartitionKey);
        Assert.Equal(8, message.SubjectVersion);
        Assert.Equal("proc-outbox", message.CorrelationId);

        var observation = message.Entity;
        Assert.Equal("PersistedProcessEffect", observation.ShapeId.Value);
        Assert.Equal("proc-outbox", observation.GetField("ProcessId").GetString());
        Assert.Equal("Counter", observation.GetField("EntityType").GetString());
        Assert.Equal("counter-1", observation.GetField("EntityId").GetString());
        Assert.Equal("Increment", observation.GetField("TransitionName").GetString());
        Assert.Equal("Notify", observation.GetField("RequestName").GetString());
        Assert.Equal("hello", observation.GetField("RequestPayload").GetProperty("Message").GetString());
        Assert.Equal("ApplyResult", observation.GetField("ContinuationTransitionName").GetString());
        Assert.Equal("snap-1", observation.GetField("SnapshotToken").GetString());
        Assert.Equal(["Value"], observation.GetField("SnapshotFieldNames").EnumerateArray().Select(static value => value.GetString()!).ToArray());
    }

    [Fact]
    public async Task ObservationProcessEntityRepository_Get_UsesContextAwarePartitionPolicy()
    {
        const string partitionKeyItem = "process.partition-key";
        var repository = new StubObservationOutboxRepository(PartitionedCounterEntity.Instance.Definition);
        repository.Seed(CreateObservationSnapshot(
            PartitionedCounterEntity.Instance.CreateState("counter-1", new { Tenant = "tenant-a", Value = 5 }, version: 7),
            concurrencyToken: "etag-a",
            partitionKey: "tenant-a"));
        repository.Seed(CreateObservationSnapshot(
            PartitionedCounterEntity.Instance.CreateState("counter-1", new { Tenant = "tenant-b", Value = 9 }, version: 3),
            concurrencyToken: "etag-b",
            partitionKey: "tenant-b"));
        var adapter = new ProcessEntityRepositoryAdapter(
            repository,
            partitionKeyPolicy: new(
                description: "operation-context partition",
                writePartitionKeyResolver: static (context, _) => ReadPartitionKey(context, partitionKeyItem),
                pointReadPartitionKeyResolver: static (context, _) => ReadPartitionKey(context, partitionKeyItem)
                ));
        var context = OperationContext.Create().WithItem(partitionKeyItem, "tenant-b");

        var snapshot = await adapter.Get(context, new("PartitionedCounter", "counter-1"));

        Assert.Equal(9, snapshot.State.Fields[nameof(PartitionedCounterEntity.Value)].GetInt32());
        Assert.Equal(new("etag-b"), snapshot.ConcurrencyToken);
    }

    [Fact]
    public async Task ObservationProcessEntityRepository_Create_UsesWriteStatePartitionPolicyForExistingRead()
    {
        var repository = new StubObservationOutboxRepository(PartitionedCounterEntity.Instance.Definition);
        repository.Seed(CreateObservationSnapshot(
            PartitionedCounterEntity.Instance.CreateState("counter-1", new { Tenant = "tenant-a", Value = 5 }, version: 7),
            concurrencyToken: "etag-a",
            partitionKey: "tenant-a"));
        var adapter = new ProcessEntityRepositoryAdapter(
            repository,
            partitionKeyPolicy: EntityPartitionKeyPolicy.FromField(nameof(PartitionedCounterEntity.Tenant)));
        var state = PartitionedCounterEntity.Instance.CreateState("counter-1", new { Tenant = "tenant-a", Value = 6 }, version: 0);

        var error = await Assert.ThrowsAsync<SemanticRuleViolationException>(() => adapter.Create(
            OperationContext.Create(),
            new("PartitionedCounter", "counter-1"),
            state,
            processId: "proc-create"));

        Assert.Contains("already exists", error.Message);
    }

    static string ReadPartitionKey(OperationContext context, string key) =>
        context.TryGetItem<string>(key, out var partitionKey)
            ? partitionKey ?? ""
            : "";

    static EntitySnapshot CreateObservationSnapshot(EntityState state, string concurrencyToken, string? partitionKey = null) =>
        new(Entity: state.Observation,
            PartitionKey: partitionKey ?? state.EntityId.Value,
            ConcurrencyToken: new(concurrencyToken)
            );

    sealed class CounterEntity : Entity<CounterEntity>
    {
        public CounterEntity()
            : base("Counter")
        {
            Value = MutableField<int>(nameof(Value));
            Label = MutableField<string?>(nameof(Label));
        }

        public Field<int> Value { get; }

        public Field<string?> Label { get; }
    }

    sealed class PartitionedCounterEntity : Entity<PartitionedCounterEntity>
    {
        public PartitionedCounterEntity()
            : base("PartitionedCounter")
        {
            Tenant = WriteOnceField<string>(nameof(Tenant));
            Value = MutableField<int>(nameof(Value));
        }

        public Field<string> Tenant { get; }

        public Field<int> Value { get; }
    }

    sealed class StubObservationOutboxRepository(EntityDefinition entityDefinition) : IEntityOutboxRepository
    {
        readonly Dictionary<(string Id, string PartitionKey), EntitySnapshot> snapshots = [];

        public EntityDefinition EntityDefinition { get; } = entityDefinition;

        public ShapeMappingContext MappingContext => ShapeMappingContext.Default;

        public string EntityType => EntityDefinition.Shape.Id.Value;

        public EntityOutboxCommit? LastOutboxCommit { get; private set; }

        public void Seed(EntitySnapshot snapshot) =>
            snapshots[(snapshot.Entity.Id, snapshot.PartitionKey)] = snapshot;

        public EntitySnapshot GetSnapshot(string id, string partitionKey) =>
            snapshots[(id, partitionKey)];

        public Task<EntitySnapshot?> TryGet(
            OperationContext context,
            string id,
            EntityReadOptions? options = null)
        {
            context.ThrowIfCancellationRequested();

            var matches = snapshots.Values
                .Where(snapshot => string.Equals(snapshot.Entity.Id, id, StringComparison.Ordinal))
                .ToArray();
            if (!string.IsNullOrWhiteSpace(options?.PartitionKey))
            {
                matches = matches
                    .Where(snapshot => string.Equals(snapshot.PartitionKey, options.PartitionKey, StringComparison.Ordinal))
                    .ToArray();
            }

            if (matches.Length == 0)
                return Task.FromResult<EntitySnapshot?>(null);
            if (matches.Length > 1)
            {
                throw new InvalidOperationException(
                    $"Observation '{EntityType}:{id}' exists in multiple partitions and cannot be loaded by id alone.");
            }

            var snapshot = matches[0];

            if (options?.ExpectedVersion is { } expectedVersion && snapshot.Entity.Version != expectedVersion)
            {
                throw new ObservationConcurrencyConflictException(
                    $"Observation '{EntityType}:{id}' expected version '{expectedVersion}' but found '{snapshot.Entity.Version}'.");
            }

            if (options?.ExpectedConcurrencyToken is { } expectedConcurrencyToken && snapshot.ConcurrencyToken != expectedConcurrencyToken)
            {
                throw new ObservationConcurrencyConflictException(
                    $"Observation '{EntityType}:{id}' expected concurrency token '{expectedConcurrencyToken}' but found '{snapshot.ConcurrencyToken}'.");
            }

            if (options?.Fields is null)
                return Task.FromResult<EntitySnapshot?>(snapshot);

            return Task.FromResult<EntitySnapshot?>(new(
                Entity: ProjectObservation(snapshot.Entity, options.Fields),
                PartitionKey: snapshot.PartitionKey,
                ConcurrencyToken: snapshot.ConcurrencyToken,
                LoadedFields: options.Fields
                )
            );
        }

        public Task<EntitySnapshot> Upsert(OperationContext context, EntityWriteRequest write)
        {
            context.ThrowIfCancellationRequested();
            var partitionKey = write.Entity.Id;

            if (write.ExpectedConcurrencyToken is { } expectedConcurrencyToken)
            {
                if (!snapshots.TryGetValue((write.Entity.Id, partitionKey), out var existing))
                {
                    throw new InvalidOperationException(
                        $"Observation '{EntityType}:{write.Entity.Id}' was not found in partition '{partitionKey}'.");
                }

                if (existing.ConcurrencyToken != expectedConcurrencyToken)
                {
                    throw new ObservationConcurrencyConflictException(
                        $"Observation '{EntityType}:{write.Entity.Id}' expected concurrency token '{expectedConcurrencyToken}' but found '{existing.ConcurrencyToken}'.");
                }
            }

            var snapshot = new EntitySnapshot(
                Entity: write.Entity,
                PartitionKey: partitionKey,
                ConcurrencyToken: new($"etag-{write.Entity.Version}"));
            snapshots[(write.Entity.Id, partitionKey)] = snapshot;
            return Task.FromResult(snapshot);
        }

        public async Task<EntityCommitResult> UpsertWithOutbox(OperationContext context, EntityOutboxCommit commit)
        {
            LastOutboxCommit = commit;
            var snapshot = await Upsert(context, commit.Write);
            return new(snapshot, commit.Messages);
        }

        public IObservationStream GetChangeStream(string processorName, DateTimeOffset? startTime = null) =>
            throw new NotSupportedException();

        public IObservationStream GetOutboxStream(string processorName, string? streamName = null, DateTimeOffset? startTime = null) =>
            throw new NotSupportedException();

        static Observation ProjectObservation(Observation observation, IReadOnlySet<string> fields)
        {
            Dictionary<string, ObservationValue> projected = new(StringComparer.Ordinal);
            foreach (var field in fields)
            {
                if (observation.TryGetField(field, out var value))
                    projected[field] = value;
            }

            return new(
                shapeId: observation.ShapeId,
                id: observation.Id,
                fields: projected,
                version: observation.Version,
                lineage: observation.Lineage
                );
        }
    }

    sealed class RouteEntity : Entity<RouteEntity>
    {
        public RouteEntity()
            : base("Route")
        {
            Value = MutableField<int>(nameof(Value));
        }

        public Field<int> Value { get; }
    }
}
