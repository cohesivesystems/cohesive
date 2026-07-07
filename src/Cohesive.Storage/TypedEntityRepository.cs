using Cohesive.Relations.Mapping;
using Cohesive.Relations.Queries;
using Cohesive.Transitions.Model;

namespace Cohesive.Storage;

public sealed class TypedEntityRepository<TEntity>(
    IEntityRepository repository,
    Action<ObjectObservationMapperBuilder<TEntity>>? configureObjectMapper = null,
    ShapeMappingContext? mappingContext = null
    ) : IEntityRepository<TEntity> where TEntity : notnull
{
    public EntityDefinition EntityDefinition => repository.EntityDefinition;

    public ShapeMappingContext MappingContext { get; } = mappingContext ?? repository.MappingContext;

    public string EntityType => repository.EntityType;

    public Task<EntitySnapshot?> TryGet(OperationContext context, string id, EntityReadOptions? options = null) =>
        repository.TryGet(context, id, options);

    public Task<TEntity?> TryGetEntity(OperationContext context, string id, EntityReadOptions? options = null) =>
        repository.TryGetEntity<TEntity>(context,
            id,
            options,
            mappingContext: MappingContext);

    public Task<EntitySnapshot> Upsert(OperationContext context, EntityWriteRequest write) =>
        repository.Upsert(context, write);

    public Task<EntitySnapshot> Upsert(OperationContext context, TEntity entity, EntityConcurrencyToken? expectedConcurrencyToken = null) =>
        repository.Upsert(context,
            entity,
            expectedConcurrencyToken,
            configureObjectMapper,
            MappingContext);
}

public sealed class TypedEntityQueryRepository<TEntity>(
    IEntityRepository<TEntity> repository,
    IEntityQueryRepository queryRepository
    ) : IEntityQueryRepository<TEntity> where TEntity : notnull
{
    public EntityDefinition EntityDefinition => repository.EntityDefinition;

    public ShapeMappingContext MappingContext => repository.MappingContext;

    public string EntityType => repository.EntityType;

    public Task<EntitySnapshot?> TryGet(OperationContext context, string id, EntityReadOptions? options = null) =>
        repository.TryGet(context, id, options);

    public Task<TEntity?> TryGetEntity(OperationContext context, string id, EntityReadOptions? options = null) =>
        repository.TryGetEntity(context, id, options);

    public Task<EntitySnapshot> Upsert(OperationContext context, EntityWriteRequest write) =>
        repository.Upsert(context, write);

    public Task<EntitySnapshot> Upsert(OperationContext context, TEntity entity, EntityConcurrencyToken? expectedConcurrencyToken = null) =>
        repository.Upsert(context, entity, expectedConcurrencyToken);

    public Task<EntityQueryResponse<EntitySnapshot>> Query(OperationContext context, EntityQuery query) =>
        queryRepository.Query(context, query);
}

public sealed class TypedEntityOutboxRepository<TEntity>(
    IEntityRepository<TEntity> repository,
    IEntityOutboxRepository outboxRepository
    ) : IEntityOutboxRepository<TEntity> where TEntity : notnull
{
    public EntityDefinition EntityDefinition => repository.EntityDefinition;

    public ShapeMappingContext MappingContext => repository.MappingContext;

    public string EntityType => repository.EntityType;

    public Task<EntitySnapshot?> TryGet(OperationContext context, string id, EntityReadOptions? options = null) =>
        repository.TryGet(context, id, options);

    public Task<TEntity?> TryGetEntity(OperationContext context, string id, EntityReadOptions? options = null) =>
        repository.TryGetEntity(context, id, options);

    public Task<EntitySnapshot> Upsert(OperationContext context, EntityWriteRequest write) =>
        repository.Upsert(context, write);

    public Task<EntitySnapshot> Upsert(OperationContext context, TEntity entity, EntityConcurrencyToken? expectedConcurrencyToken = null) =>
        repository.Upsert(context, entity, expectedConcurrencyToken);

    public Task<EntityCommitResult> UpsertWithOutbox(OperationContext context, EntityOutboxCommit commit) =>
        outboxRepository.UpsertWithOutbox(context, commit);

    public IObservationStream GetChangeStream(string processorName, DateTimeOffset? startTime = null) =>
        outboxRepository.GetChangeStream(processorName, startTime);

    public IObservationStream GetOutboxStream(string processorName, string? streamName = null, DateTimeOffset? startTime = null) =>
        outboxRepository.GetOutboxStream(processorName, streamName, startTime);
}
