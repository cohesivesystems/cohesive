using Cohesive.Adapters.Cosmos;
using Cohesive.Relations.Mapping;
using Cohesive.Relations.Model;
using Cohesive.Storage;

namespace Cohesive.Tests.Model;

public sealed class ObservationRepositoryAdapterTests
{
    static readonly EntityDefinition SampleEntityDefinition = new(
        name: new("Sample"),
        fields:
        [
            new(new("Name"), new ScalarTypeRef(ScalarTypeKind.String)),
            new(new("Status"), new ScalarTypeRef(ScalarTypeKind.String), presence: FieldPresence.Optional, nullability: FieldNullability.Nullable)
        ]);

    [Fact]
    public async Task ObservationRepositoryReadAdapter_GetByIdsAsync_LoadsObservations()
    {
        var repository = new StubObservationRepository(
            entityType: "Sample",
            snapshots:
            [
                CreateSnapshot("obs-1", "alpha"),
                CreateSnapshot("obs-2", "beta")
            ]);
        var adapter = new ObservationReadRepositoryAdapter(repository);

        var result = await adapter.GetByIds(
            OperationContext.Create(),
            ["obs-1", "obs-2", "missing"]);

        Assert.Equal(2, result.Count);
        Assert.Equal("alpha", result["obs-1"].GetField("Name").GetString());
        Assert.Equal("beta", result["obs-2"].GetField("Name").GetString());
    }

    [Fact]
    public async Task ObservationQueryReadRepositoryAdapter_QueryAsync_AdaptsSnapshotsAndProjectsFields()
    {
        EntitySnapshot[] snapshots =
        [
            CreateSnapshot("obs-1", "alpha", "active"),
            CreateSnapshot("obs-2", "beta", "inactive")
        ];
        var queryRepository = new StubObservationQueryRepository(entityType: "Sample", snapshots: snapshots);
        var adapter = new ObservationQueryReadRepositoryAdapter(queryRepository);

        var result = (await adapter.Query(
            OperationContext.Create(),
            new(Predicate: new(
                    new FieldPredicate(
                        FieldPath.FromField("Name"),
                        new PrefixValuePredicate("al"))
                    ),
                Fields: FieldSelection.ForFields("Name")
                )
            )).Rows;

        var observation = Assert.Single(result);
        Assert.Equal("obs-1", observation.Id);
        Assert.Equal("alpha", observation.GetField("Name").GetString());
        Assert.False(observation.TryGetField("Status", out _));
    }

    [Fact]
    public async Task ObservationQueryReadRepositoryAdapter_GetByIdsAsync_UsesQueryRepositoryPointReads()
    {
        EntitySnapshot[] snapshots =
        [
            CreateSnapshot("obs-1", "alpha", "active"),
            CreateSnapshot("obs-2", "beta", "inactive")
        ];
        var queryRepository = new StubObservationQueryRepository(entityType: "Sample", snapshots: snapshots);
        var adapter = new ObservationQueryReadRepositoryAdapter(queryRepository);

        var result = await adapter.GetByIds(
            OperationContext.Create(),
            ["obs-1", "missing"],
            FieldSelection.ForFields("Name"));

        var observation = Assert.Single(result).Value;
        Assert.Equal("obs-1", observation.Id);
        Assert.Equal("alpha", observation.GetField("Name").GetString());
        Assert.False(observation.TryGetField("Status", out _));
    }

    [Fact]
    public void ObservationReadOptions_WithExpectedConcurrencyToken_StoresValue()
    {
        var options = new EntityReadOptions(expectedConcurrencyToken: new("etag-7"));

        Assert.Equal(new EntityConcurrencyToken("etag-7"), options.ExpectedConcurrencyToken);
    }

    [Fact]
    public void ObservationReadOptions_WithoutExpectedConcurrencyToken_KeepsNull()
    {
        var options = new EntityReadOptions();

        Assert.Null(options.ExpectedConcurrencyToken);
    }

    [Fact]
    public void ObservationReadOptions_WithFieldSelection_StoresSelection()
    {
        var selection = FieldSelection.ForFields("Name", "Name");
        var options = new EntityReadOptions(fieldSelection: selection);

        Assert.Same(selection, options.FieldSelection);
        Assert.Equal(["Name"], options.Fields);
    }

    [Fact]
    public void FieldSelection_WithWhitespaceField_Throws()
    {
        var error = Assert.Throws<ArgumentException>(() => FieldSelection.ForFields("Name", " "));

        Assert.Contains("must not be null, empty, or whitespace", error.Message);
    }

    [Fact]
    public void CosmosObservationOutboxRepository_ValidateReadPreconditions_WithExpectedConcurrencyTokenMismatch_ThrowsConcurrencyConflict()
    {
        var document = CreateDocument(
            observationId: "obs-1",
            version: 7,
            etag: "etag-actual");

        var error = Assert.Throws<ObservationConcurrencyConflictException>(() =>
            CosmosEntityOutboxRepository.ValidateReadPreconditions(
                entityType: "Sample",
                id: "obs-1",
                document: document,
                read: new EntityReadOptions(expectedConcurrencyToken: new("etag-expected"))));

        Assert.Contains("expected ETag 'etag-expected' but found 'etag-actual'", error.Message);
    }

    [Fact]
    public void CosmosObservationOutboxRepository_ValidateReadPreconditions_WithExpectedConcurrencyTokenMatch_DoesNotThrow()
    {
        var document = CreateDocument(
            observationId: "obs-1",
            version: 7,
            etag: "etag-7");

        CosmosEntityOutboxRepository.ValidateReadPreconditions(
            entityType: "Sample",
            id: "obs-1",
            document: document,
            read: new EntityReadOptions(expectedConcurrencyToken: new("etag-7")));
    }

    [Fact]
    public void CosmosObservationOutboxRepositoryOptions_RequireDistinctNonemptyDocumentKinds()
    {
        Assert.Throws<ArgumentException>(() =>
            CosmosObservationOutboxRepositoryOptions.RequireValid(new() { EntityDocumentKind = " " }));
        Assert.Throws<ArgumentException>(() =>
            CosmosObservationOutboxRepositoryOptions.RequireValid(new() { OutboxDocumentKind = string.Empty }));
        Assert.Throws<ArgumentException>(() =>
            CosmosObservationOutboxRepositoryOptions.RequireValid(new()
            {
                EntityDocumentKind = "document",
                OutboxDocumentKind = "document"
            }));

        var options = new CosmosObservationOutboxRepositoryOptions
        {
            EntityDocumentKind = "entity-v2",
            OutboxDocumentKind = "outbox-v2"
        };
        Assert.Same(options, CosmosObservationOutboxRepositoryOptions.RequireValid(options));
    }

    static CosmosObservationContainerDocument CreateDocument(
        string observationId,
        long version,
        string? etag) =>
        new(
            Id: observationId,
            PartitionKey: observationId,
            DocumentKind: "entity",
            ObservationType: "Sample",
            ObservationId: observationId,
            ObservationVersion: version,
            Observation: new Dictionary<string, ObservationValue>(StringComparer.Ordinal)
            {
                ["Name"] = ObservationValue.FromString("alpha")
            },
            ETag: etag);

    static EntitySnapshot CreateSnapshot(string id, string name, string status = "active") =>
        new(
            new Observation(
                new ShapeId("Sample"),
                id,
                new Dictionary<string, ObservationValue>(StringComparer.Ordinal)
                {
                    ["Name"] = ObservationValue.FromString(name),
                    ["Status"] = ObservationValue.FromString(status)
                }),
            PartitionKey: id,
            ConcurrencyToken: new($"etag-{id}"));

    sealed class StubObservationRepository : IEntityRepository
    {
        readonly IReadOnlyDictionary<string, EntitySnapshot> snapshots;
        readonly string entityType;

        public StubObservationRepository(string entityType, IReadOnlyList<EntitySnapshot> snapshots)
        {
            this.entityType = entityType;
            this.snapshots = snapshots.ToDictionary(static snapshot => snapshot.Entity.Id, StringComparer.Ordinal);
        }

        public EntityDefinition EntityDefinition => SampleEntityDefinition;

        public ShapeMappingContext MappingContext => ShapeMappingContext.Default;

        public string EntityType => entityType;

        public Task<EntitySnapshot?> TryGet(
            OperationContext context,
            string id,
            EntityReadOptions? options = null)
        {
            context.ThrowIfCancellationRequested();
            snapshots.TryGetValue(id, out var snapshot);
            return Task.FromResult(snapshot);
        }

        public Task<EntitySnapshot> Upsert(OperationContext context, EntityWriteRequest write) =>
            throw new NotSupportedException();
    }

    sealed class StubObservationQueryRepository : IEntityQueryRepository
    {
        readonly Dictionary<string, EntitySnapshot> snapshotsById;
        readonly string entityType;

        public StubObservationQueryRepository(string entityType, IReadOnlyList<EntitySnapshot> snapshots)
        {
            this.entityType = entityType;
            snapshotsById = snapshots.ToDictionary(static snapshot => snapshot.Entity.Id, StringComparer.Ordinal);
        }

        public EntityDefinition EntityDefinition => SampleEntityDefinition;

        public ShapeMappingContext MappingContext => ShapeMappingContext.Default;

        public string EntityType => entityType;

        public Task<EntityQueryResponse<EntitySnapshot>> Query(OperationContext context, EntityQuery query)
        {
            context.ThrowIfCancellationRequested();
            var window = query.Window;

            var filtered = snapshotsById.Values
                .Where(snapshot => query.Predicate is null || EntityPredicateEvaluator.Evaluate(snapshot.Entity, query.Predicate))
                .ToArray();

            IEnumerable<EntitySnapshot> results = filtered;
            if (window?.Offset is { } offset and > 0)
                results = results.Skip(offset);
            if (window?.Limit is { } limit)
                results = results.Take(limit);

            return Task.FromResult(new EntityQueryResponse<EntitySnapshot>(
                Rows: [.. results],
                PageInfo: new(TotalCount: filtered.Length, Offset: window?.Offset, Limit: window?.Limit)));
        }

        public Task<EntitySnapshot?> TryGet(
            OperationContext context,
            string id,
            EntityReadOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentException.ThrowIfNullOrWhiteSpace(id);
            context.ThrowIfCancellationRequested();

            if (!snapshotsById.TryGetValue(id, out var snapshot))
                return Task.FromResult<EntitySnapshot?>(null);

            return Task.FromResult<EntitySnapshot?>(Project(snapshot, options?.Fields));
        }

        public Task<EntitySnapshot> Upsert(OperationContext context, EntityWriteRequest write)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(write);
            context.ThrowIfCancellationRequested();

            var snapshot = new EntitySnapshot(
                Entity: write.Entity,
                PartitionKey: write.Entity.Id,
                ConcurrencyToken: new($"etag-{write.Entity.Id}"));
            snapshotsById[write.Entity.Id] = snapshot;
            return Task.FromResult(snapshot);
        }

        static EntitySnapshot Project(EntitySnapshot snapshot, IReadOnlySet<string>? fields)
        {
            if (fields is null || fields.Count == 0)
                return snapshot;

            Dictionary<string, ObservationValue> projected = new(StringComparer.Ordinal);
            foreach (var field in fields)
            {
                if (snapshot.Entity.TryGetField(field, out var value))
                    projected[field] = value;
            }

            return new(
                Entity: new(
                    shapeId: snapshot.Entity.ShapeId,
                    id: snapshot.Entity.Id,
                    fields: projected,
                    version: snapshot.Entity.Version,
                    lineage: snapshot.Entity.Lineage),
                PartitionKey: snapshot.PartitionKey,
                ConcurrencyToken: snapshot.ConcurrencyToken,
                LoadedFields: fields);
        }
    }
}
