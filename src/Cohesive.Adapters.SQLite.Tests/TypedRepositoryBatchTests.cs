using Cohesive.Model;
using Cohesive.Storage;
using Cohesive.Transitions.Authoring;
using Cohesive.Transitions.Model;

namespace Cohesive.Adapters.SQLite.Tests;

public sealed class TypedRepositoryBatchTests
{
    [Fact]
    public async Task DefaultTypedInterfaceUsesOneNativeBatchInsteadOfIndividualWrites()
    {
        var native = new NativeRepository();
        IEntityRepository<Record> repository = native;
        var result = await repository.UpsertBatch(OperationContext.Create(),
            [new("first", 8), new("second", 3)], EntityBatchAtomicity.AllOrNothing);
        Assert.Equal(1, native.BatchCalls);
        Assert.Equal(EntityBatchAtomicity.AllOrNothing, native.LastBatch!.Atomicity);
        Assert.Equal(["first", "second"], result.Select(static item => item.Entity.EntityId.Value));
        Assert.Equal([8L, 3L], result.Select(static item => item.Entity.Version));
    }

    [Fact]
    public async Task TypedDispatchRejectsNativeLimitsWithoutSplittingTheBatch()
    {
        var native = new NativeRepository();
        IEntityRepository<Record> repository = new TypedEntityRepository<Record>(native);
        await Assert.ThrowsAsync<NotSupportedException>(() => repository.UpsertBatch(OperationContext.Create(),
            [new("first", 1), new("second", 1), new("third", 1)], EntityBatchAtomicity.AllOrNothing));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => repository.UpsertBatch(OperationContext.Create(),
            [new("first", 1)], (EntityBatchAtomicity)99));
        Assert.Equal(0, native.BatchCalls);
    }

    public sealed record Record(string Id, long Version);

    sealed class NativeRepository : IEntityRepository<Record>
    {
        public EntityDefinition EntityDefinition { get; } = ObjectEntityDefinition.For<Record>(new("typed-batch"));
        public EntityBatchCapabilities BatchCapabilities { get; } = new(true, true, true, MaxItemsPerBatch: 2);
        public int BatchCalls { get; private set; }
        public EntityBatchWriteRequest? LastBatch { get; private set; }
        public Task<EntityBatchWriteResult> UpsertBatch(OperationContext context, EntityBatchWriteRequest request)
        {
            BatchCalls++;
            LastBatch = request;
            EntitySnapshot[] snapshots = [.. request.Writes.Select(static write => new EntitySnapshot(write.Entity,
                write.Entity.EntityId.Value, new("opaque")))];
            return Task.FromResult(new EntityBatchWriteResult(snapshots, request.Atomicity));
        }
        public Task<EntitySnapshot?> TryGet(OperationContext context, string id, EntityReadOptions? options = null) => throw new NotSupportedException();
        public Task<Record?> TryGetEntity(OperationContext context, string id, EntityReadOptions? options = null) => throw new NotSupportedException();
        public Task<EntitySnapshot> Upsert(OperationContext context, EntityWriteRequest write) => throw new InvalidOperationException("Batch dispatch must not call single writes.");
        public Task<EntitySnapshot> Upsert(OperationContext context, Record entity, EntityConcurrencyToken? expectedConcurrencyToken = null) => throw new InvalidOperationException("Batch dispatch must not call single writes.");
    }
}
