using System.Text.Json.Serialization;
using Cohesive.Model;
using Cohesive.Storage;
using Cohesive.Transitions.Authoring;
using Cohesive.Transitions.Model;
using Microsoft.Data.Sqlite;

namespace Cohesive.Adapters.SQLite.Tests;

public sealed class SqliteEntityRepositoryTests
{
    static readonly OperationContext Context = OperationContext.Create();

    [Fact]
    public async Task FullScalarSnapshotReopensExactlyWithIndependentStorageAndSemanticVersions()
    {
        using var file = new DatabaseFixture();
        var repository = Repository(file);
        var write = Write(repository, id: "order/λ\0'", price: decimal.MaxValue, version: 17);
        var committed = await repository.Upsert(Context, write);
        var reopened = new SqliteEntityRepository(new(new(file.Path)), repository.Mapping);
        var loaded = Assert.IsType<EntitySnapshot>(await reopened.TryGet(Context, write.Entity.EntityId.Value));
        Assert.Equal(write.Entity.Observation.ToCanonicalJsonUtf8(), loaded.Entity.Observation.ToCanonicalJsonUtf8());
        var ordinalReader = Assert.IsAssignableFrom<IOrdinalObservationFieldReader>(loaded.Entity.Observation.Fields);
        Assert.Same(repository.Mapping.Layout, ordinalReader.Layout);
        Assert.Equal(17, loaded.Entity.Version);
        Assert.Equal(committed.ConcurrencyToken, loaded.ConcurrencyToken);
        Assert.Equal("tenant-a", loaded.PartitionKey);
        var rewritten = await reopened.Upsert(Context, write with { ExpectedConcurrencyToken = loaded.ConcurrencyToken });
        Assert.Equal(17, rewritten.Entity.Version);
        Assert.NotEqual(loaded.ConcurrencyToken, rewritten.ConcurrencyToken);
        Assert.NotEqual("17", rewritten.ConcurrencyToken.Value);
    }

    [Fact]
    public async Task StaleAndMissingConditionalTargetsCannotInsertOrOverwrite()
    {
        using var file = new DatabaseFixture();
        var repository = Repository(file);
        var first = await repository.Upsert(Context, Write(repository, "one"));
        var second = await repository.Upsert(Context, Write(repository, "one", price: 2m, version: 0));
        await Assert.ThrowsAsync<ObservationConcurrencyConflictException>(() => repository.Upsert(Context,
            Write(repository, "one", price: 3m) with { ExpectedConcurrencyToken = first.ConcurrencyToken }));
        await Assert.ThrowsAsync<ObservationConcurrencyConflictException>(() => repository.Upsert(Context,
            Write(repository, "absent") with { ExpectedConcurrencyToken = second.ConcurrencyToken }));
        Assert.Equal(second.ConcurrencyToken, (await repository.TryGet(Context, "one"))!.ConcurrencyToken);
        Assert.Null(await repository.TryGet(Context, "absent"));
    }

    [Fact]
    public async Task IndependentCompetingWritersHaveExactlyOneCompareAndSwapWinner()
    {
        using var file = new DatabaseFixture();
        var firstRepository = Repository(file);
        var original = await firstRepository.Upsert(Context, Write(firstRepository, "race"));
        var secondRepository = new SqliteEntityRepository(new(new(file.Path)), firstRepository.Mapping);
        using var start = new ManualResetEventSlim();
        var tasks = new[] { firstRepository, secondRepository }.Select((repository, index) => Task.Run(async () =>
        {
            start.Wait();
            try
            {
                return await repository.Upsert(Context, Write(repository, "race", price: index + 2)
                    with { ExpectedConcurrencyToken = original.ConcurrencyToken });
            }
            catch (ObservationConcurrencyConflictException) { return null; }
        })).ToArray();
        start.Set();
        var results = await Task.WhenAll(tasks);
        var winner = Assert.Single(results.OfType<EntitySnapshot>());
        Assert.Single(results, static result => result is null);
        Assert.Equal(winner.ConcurrencyToken, (await firstRepository.TryGet(Context, "race"))!.ConcurrencyToken);
    }

    [Theory]
    [InlineData(EntityBatchAtomicity.None)]
    [InlineData(EntityBatchAtomicity.SamePartition)]
    [InlineData(EntityBatchAtomicity.AllOrNothing)]
    public async Task BatchesPreserveOrderAndRotateTokensForRepeatedIdentities(EntityBatchAtomicity atomicity)
    {
        using var file = new DatabaseFixture();
        var repository = Repository(file);
        var result = await repository.UpsertBatch(Context, new(
            [Write(repository, "same", price: 1m), Write(repository, "second"), Write(repository, "same", price: 3m)], atomicity));
        Assert.Equal(atomicity, result.Atomicity);
        Assert.Equal(["same", "second", "same"], result.Snapshots.Select(static snapshot => snapshot.Entity.EntityId.Value));
        Assert.NotEqual(result.Snapshots[0].ConcurrencyToken, result.Snapshots[2].ConcurrencyToken);
        Assert.Equal(result.Snapshots[2].ConcurrencyToken, (await repository.TryGet(Context, "same"))!.ConcurrencyToken);
        Assert.Equal(3m, (await repository.TryGet(Context, "same"))!.Entity.Observation.GetField("price").GetDecimal());
    }

    [Theory]
    [InlineData(EntityBatchAtomicity.None, true)]
    [InlineData(EntityBatchAtomicity.SamePartition, false)]
    [InlineData(EntityBatchAtomicity.AllOrNothing, false)]
    public async Task NoneRetainsEarlierCommitsWhileAtomicBatchesRollBack(EntityBatchAtomicity atomicity, bool firstSurvives)
    {
        using var file = new DatabaseFixture();
        var repository = Repository(file);
        await Assert.ThrowsAsync<ObservationConcurrencyConflictException>(() => repository.UpsertBatch(Context, new(
            [Write(repository, "first"), Write(repository, "absent") with { ExpectedConcurrencyToken = new("stale") }],
            atomicity)));
        Assert.Equal(firstSurvives, await repository.TryGet(Context, "first") is not null);
        Assert.Null(await repository.TryGet(Context, "absent"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TypedBatchesCarryPerWriteCasThroughBothFacades(bool useOutboxFacade)
    {
        using var file = new DatabaseFixture();
        var mapping = new SqliteEntityRepositoryMapping(ObjectEntityDefinition.For<StoredRun>(new("run-control")),
            identityField: "run_id", partitionField: nameof(StoredRun.Tenant));
        new SqliteSchema("runs", [mapping.InitialMigration]).Apply(file.Database);
        var native = new SqliteEntityRepository(file.Database, mapping);
        IEntityRepository<StoredRun> typed = new TypedEntityRepository<StoredRun>(native,
            selectEntityId: static run => run.Code, selectVersion: static run => run.Revision);
        if (useOutboxFacade) typed = new TypedEntityOutboxRepository<StoredRun>(typed, new OutboxStub(native));
        StoredRun first = new("first", "tenant", 1, 10m);
        StoredRun second = new("second", "tenant", 1, 20m);
        var initial = await typed.UpsertBatch(Context, [first, second], EntityBatchAtomicity.AllOrNothing);
        EntityWriteRequest<StoredRun>[] writes =
        [
            new(first with { Revision = 2, Price = 11m }, initial[0].ConcurrencyToken),
            new(second with { Revision = 2, Price = 21m }, initial[1].ConcurrencyToken)
        ];
        var committed = await typed.UpsertBatch(Context, new EntityBatchWriteRequest<StoredRun>(writes, EntityBatchAtomicity.AllOrNothing));
        Assert.Equal([2L, 2L], committed.Select(snapshot => snapshot.Entity.Version));
        Assert.Equal(writes[0].Entity, await typed.TryGetEntity(Context, "first"));
        await Assert.ThrowsAsync<ObservationConcurrencyConflictException>(() => typed.UpsertBatch(Context,
            new EntityBatchWriteRequest<StoredRun>(
            [
                new(first with { Revision = 3 }, committed[0].ConcurrencyToken),
                new(second with { Revision = 3 }, initial[1].ConcurrencyToken)
            ], EntityBatchAtomicity.AllOrNothing)));
        Assert.Equal(committed[0].ConcurrencyToken, (await native.TryGet(Context, "first"))!.ConcurrencyToken);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task LateConflictRollsBackEarlierWritesThroughEveryFacade(int facade)
    {
        using var file = new DatabaseFixture();
        var native = Repository(file);
        var current = await native.Upsert(Context, Write(native, "existing"));
        var typed = new TypedEntityRepository<StoredRun>(native);
        IEntityRepository repository = facade switch
        {
            0 => native,
            1 => typed,
            _ => new TypedEntityOutboxRepository<StoredRun>(typed, new OutboxStub(native))
        };
        Assert.Equal(native.BatchCapabilities, repository.BatchCapabilities);
        await Assert.ThrowsAsync<ObservationConcurrencyConflictException>(() => repository.UpsertBatch(Context, new(
            [Write(native, "inserted"), Write(native, "existing") with { ExpectedConcurrencyToken = new("stale") }],
            EntityBatchAtomicity.AllOrNothing)));
        Assert.Null(await native.TryGet(Context, "inserted"));
        Assert.Equal(current.ConcurrencyToken, (await native.TryGet(Context, "existing"))!.ConcurrencyToken);
    }

    [Fact]
    public async Task LateSqlFailureRollsBackNewRowsAndUpdatesAcrossPartitions()
    {
        using var file = new DatabaseFixture();
        var repository = Repository(file);
        var before = await repository.Upsert(Context, Write(repository, "existing"));
        using (var connection = file.Database.OpenConnection())
        using (var trigger = file.Database.CreateCommand(connection, null, """
            CREATE TRIGGER fail_insert BEFORE INSERT ON orders WHEN NEW.id = 'fail'
            BEGIN SELECT RAISE(ABORT, 'injected late SQL failure'); END;
            """)) trigger.ExecuteNonQuery();
        await Assert.ThrowsAsync<SqliteException>(() => repository.UpsertBatch(Context, new(
        [
            Write(repository, "existing", price: 9m) with { ExpectedConcurrencyToken = before.ConcurrencyToken },
            Write(repository, "inserted", tenant: "tenant-b"),
            Write(repository, "fail", tenant: "tenant-c")
        ], EntityBatchAtomicity.AllOrNothing)));
        Assert.Null(await repository.TryGet(Context, "inserted"));
        Assert.Null(await repository.TryGet(Context, "fail"));
        Assert.Equal(before.ConcurrencyToken, (await repository.TryGet(Context, "existing"))!.ConcurrencyToken);
    }

    [Fact]
    public async Task ExplicitPartitionsResolveAmbiguityAndReadPreconditionsAreEnforced()
    {
        using var file = new DatabaseFixture();
        var repository = Repository(file);
        var batch = await repository.UpsertBatch(Context, new(
            [Write(repository, "same", tenant: "a", version: 4), Write(repository, "same", tenant: "b", version: 8)],
            EntityBatchAtomicity.AllOrNothing));
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.TryGet(Context, "same"));
        Assert.Null(await repository.TryGet(Context, "same", new(partitionKey: "missing")));
        var read = await repository.TryGet(Context, "same", new(partitionKey: "a", expectedVersion: 4,
            expectedConcurrencyToken: batch.Snapshots[0].ConcurrencyToken, fieldSelection: FieldSelection.ForFields("price")));
        Assert.Equal(["price"], read!.LoadedFields);
        Assert.Equal(repository.EntityDefinition.Fields.Length, read.Entity.Observation.Fields.Count);
        await Assert.ThrowsAsync<ObservationConcurrencyConflictException>(() => repository.TryGet(Context, "same", new(partitionKey: "a", expectedVersion: 8)));
        await Assert.ThrowsAsync<ObservationConcurrencyConflictException>(() => repository.TryGet(Context, "same", new(partitionKey: "a", expectedConcurrencyToken: new("stale"))));
        await Assert.ThrowsAsync<ArgumentException>(() => repository.TryGet(Context, "same", EntityReadOptions.ForFields("unknown")));
        var empty = await repository.TryGet(Context, "same", new(partitionKey: "a", fieldSelection: new([])));
        Assert.Empty(empty!.LoadedFields!);
    }

    [Fact]
    public async Task ShapeRevisionMismatchIsDistinctFromConcurrencyForAllWrites()
    {
        using var file = new DatabaseFixture();
        var repository = Repository(file);
        var original = await repository.Upsert(Context, Write(repository, "one"));
        var otherDefinition = new EntityDefinition(new("different-entity"), repository.EntityDefinition.Fields);
        var other = new SqliteEntityRepository(file.Database, new(otherDefinition, identityField: "id", partitionField: "tenant", tableName: "orders"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => other.TryGet(Context, "one"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => other.Upsert(Context, Write(other, "one")));
        var shapeError = await Assert.ThrowsAsync<InvalidOperationException>(() => other.Upsert(Context,
            Write(other, "one") with { ExpectedConcurrencyToken = original.ConcurrencyToken }));
        Assert.Contains("different stored shape revision", shapeError.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => other.Upsert(Context,
            Write(other, "one") with { ExpectedConcurrencyToken = new("stale") }));
        await Assert.ThrowsAsync<SemanticRuleViolationException>(() => other.Upsert(Context, Write(repository, "two")));
        Assert.Equal(original.ConcurrencyToken, (await repository.TryGet(Context, "one"))!.ConcurrencyToken);
    }

    [Fact]
    public async Task BatchLimitsUnsupportedScopeAndCancellationFailBeforeOpening()
    {
        using var file = new DatabaseFixture();
        var repository = Repository(file, initialize: false, maximumBatchItems: 1);
        var write = Write(repository, "one");
        await Assert.ThrowsAsync<NotSupportedException>(() => repository.UpsertBatch(Context, new([write, write])));
        var larger = new SqliteEntityRepository(file.Database, new(repository.EntityDefinition, identityField: "id", partitionField: "tenant"));
        await Assert.ThrowsAsync<NotSupportedException>(() => larger.UpsertBatch(Context,
            new([write, Write(larger, "two", tenant: "other")], EntityBatchAtomicity.SamePartition)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => repository.UpsertBatch(Context, new([write], (EntityBatchAtomicity)99)));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var canceled = Context.WithCancellationToken(cancellation.Token);
        await Assert.ThrowsAsync<OperationCanceledException>(() => repository.TryGet(canceled, "one"));
        await Assert.ThrowsAsync<OperationCanceledException>(() => repository.Upsert(canceled, write));
        await Assert.ThrowsAsync<OperationCanceledException>(() => repository.UpsertBatch(canceled, new([write])));
        Assert.Empty((await repository.UpsertBatch(Context, new([], EntityBatchAtomicity.AllOrNothing))).Snapshots);
        Assert.False(File.Exists(file.Path));
    }

    [Fact]
    public async Task StoredScalarContractViolationsFailEvenForProjectedReads()
    {
        using var file = new DatabaseFixture();
        var repository = Repository(file);
        await repository.Upsert(Context, Write(repository, "one"));
        using (var connection = file.Database.OpenConnection())
        using (var corrupt = file.Database.CreateCommand(connection, null, "UPDATE orders SET state = 'unknown';")) corrupt.ExecuteNonQuery();
        await Assert.ThrowsAsync<ArgumentException>(() => repository.TryGet(Context, "one", EntityReadOptions.ForFields("price")));
    }

    [Fact]
    public async Task TypedBatchesRetainCustomSelectorsOrderingAndMaterialization()
    {
        using var file = new DatabaseFixture();
        var definition = ObjectEntityDefinition.For<StoredRun>(new("run-control"));
        var mapping = new SqliteEntityRepositoryMapping(definition, identityField: "run_id", partitionField: nameof(StoredRun.Tenant));
        new SqliteSchema("runs", [mapping.InitialMigration]).Apply(file.Database);
        var native = new SqliteEntityRepository(file.Database, mapping);
        IEntityRepository<StoredRun> typed = new TypedEntityRepository<StoredRun>(native,
            selectEntityId: static run => run.Code, selectVersion: static run => run.Revision);
        var records = new[] { new StoredRun("run/1", "tenant", 10, 123.4500m), new StoredRun("run/1", "tenant", 11, 100m) };
        var results = await typed.UpsertBatch(Context, records, EntityBatchAtomicity.AllOrNothing);
        Assert.Equal([10L, 11L], results.Select(static result => result.Entity.Version));
        Assert.Equal(records[1], await typed.TryGetEntity(Context, "run/1"));
        Assert.Equal(10, records[0].Revision);
        var outbox = new TypedEntityOutboxRepository<StoredRun>(typed, new OutboxStub(native));
        var throughOutbox = await outbox.UpsertBatch(Context, [records[1] with { Revision = 12 }], EntityBatchAtomicity.SamePartition);
        Assert.Equal(12, Assert.Single(throughOutbox).Entity.Version);
    }

    [Fact]
    public void MappingsAreDeterministicInspectableAndRejectUnsupportedRepresentations()
    {
        var definition = Definition();
        var overrides = new Dictionary<string, string> { ["price"] = "exact\" price" };
        var first = new SqliteEntityRepositoryMapping(definition, "id", partitionField: "tenant", columnNames: overrides);
        overrides["price"] = "changed";
        var second = new SqliteEntityRepositoryMapping(definition, "id", partitionField: "tenant", columnNames: new Dictionary<string, string> { ["price"] = "exact\" price" });
        Assert.Equal(first.InitialMigration.Fingerprint, second.InitialMigration.Fingerprint);
        Assert.Equal("exact\" price", first.FieldColumns["price"]);
        Assert.Contains(nameof(first.TableName), first.ConventionSuppliedSettings);
        Assert.DoesNotContain("FieldColumns/price", first.ConventionSuppliedSettings);
        Assert.Throws<ArgumentException>(() => new SqliteEntityRepositoryMapping(definition, "id", columnNames: new Dictionary<string, string> { ["id"] = "__cohesive_token" }));
        Assert.Throws<ArgumentException>(() => new SqliteEntityRepositoryMapping(definition, "id", columnNames: new Dictionary<string, string> { ["id"] = "PRICE" }));
        Assert.Throws<ArgumentException>(() => new SqliteEntityRepositoryMapping(definition, "price"));
        Assert.Throws<ArgumentException>(() => new SqliteEntityRepositoryMapping(definition, "id", columnNames: new Dictionary<string, string> { ["absent"] = "extra" }));
        Assert.Throws<NotSupportedException>(() => new SqliteEntityRepositoryMapping(new(new("optional"),
            [new(new("id"), new ScalarTypeRef(ScalarTypeKind.String)), new(new("extra"), new ScalarTypeRef(ScalarTypeKind.String), presence: FieldPresence.Optional)]), "id"));
        Assert.Throws<NotSupportedException>(() => new SqliteEntityRepositoryMapping(new(new("nested"),
            [new(new("id"), new ScalarTypeRef(ScalarTypeKind.String)), new(new("extra"), new ObjectTypeRef([]))]), "id"));
    }

    [Fact]
    public async Task DefaultIdentityPartitionsAndQuotedPhysicalOverridesExecuteExactly()
    {
        using var file = new DatabaseFixture();
        var mapping = new SqliteEntityRepositoryMapping(Definition(), identityField: "id", tableName: "order\" records",
            columnNames: new Dictionary<string, string> { ["id"] = "key\" column", ["price"] = "exact price" });
        new SqliteSchema("physical-overrides", [mapping.InitialMigration]).Apply(file.Database);
        var repository = new SqliteEntityRepository(file.Database, mapping);
        var first = await repository.Upsert(Context, Write(repository, "one"));
        Assert.Equal("one", first.PartitionKey);
        Assert.Null(await repository.TryGet(Context, "one", new(partitionKey: "tenant-a")));
        var second = await repository.Upsert(Context, Write(repository, "one", price: 100m)
            with { ExpectedConcurrencyToken = first.ConcurrencyToken });
        Assert.Equal(second.ConcurrencyToken, (await repository.TryGet(Context, "one"))!.ConcurrencyToken);
    }

    static SqliteEntityRepository Repository(DatabaseFixture file, bool initialize = true, int? maximumBatchItems = null)
    {
        var mapping = new SqliteEntityRepositoryMapping(Definition(), identityField: "id", partitionField: "tenant", maximumBatchItems: maximumBatchItems);
        if (initialize) new SqliteSchema("orders", [mapping.InitialMigration]).Apply(file.Database);
        return new(file.Database, mapping);
    }

    static EntityDefinition Definition() => new(new("orders"),
    [
        new(new("id"), new ScalarTypeRef(ScalarTypeKind.String)),
        new(new("tenant"), new ScalarTypeRef(ScalarTypeKind.String)),
        new(new("price"), new ScalarTypeRef(ScalarTypeKind.Decimal)),
        new(new("state"), new EnumTypeRef("status", ["pending-run", "approved-run"])),
        new(new("instant"), new ScalarTypeRef(ScalarTypeKind.Instant)),
        new(new("payload"), new ScalarTypeRef(ScalarTypeKind.Bytes)),
        new(new("note"), new ScalarTypeRef(ScalarTypeKind.String), nullability: FieldNullability.Nullable)
    ]);

    static EntityWriteRequest Write(SqliteEntityRepository repository, string id, string tenant = "tenant-a", decimal price = 1.2500m, long version = 0) =>
        new(repository.EntityDefinition.CreateState(id, new Dictionary<string, ObservationValue>
        {
            ["id"] = ObservationValue.FromString(id), ["tenant"] = ObservationValue.FromString(tenant),
            ["price"] = ObservationValue.FromDecimal(price), ["state"] = ObservationValue.FromString("pending-run"),
            ["instant"] = ObservationValue.FromDateTimeOffset(new DateTimeOffset(2026, 9, 5, 12, 34, 56, TimeSpan.FromHours(-7)).AddTicks(1)),
            ["payload"] = ObservationValue.FromBytes(new byte[] { 0, 1, 255 }), ["note"] = ObservationValue.Null
        }, version).Snapshot);

    public sealed record StoredRun([property: JsonPropertyName("run_id")] string Code, string Tenant, long Revision, decimal Price);

    sealed class OutboxStub(IEntityRepository repository) : IEntityOutboxRepository
    {
        public EntityDefinition EntityDefinition => repository.EntityDefinition;
        public Task<EntitySnapshot?> TryGet(OperationContext context, string id, EntityReadOptions? options = null) => repository.TryGet(context, id, options);
        public Task<EntitySnapshot> Upsert(OperationContext context, EntityWriteRequest write) => repository.Upsert(context, write);
        public Task<EntityCommitResult> UpsertWithOutbox(OperationContext context, EntityOutboxCommit commit) => throw new NotSupportedException("Outbox is not invoked by ordinary batch tests.");
    }
}
