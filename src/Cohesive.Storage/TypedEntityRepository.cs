using Cohesive.Relations.Mapping;
using Cohesive.Transitions.Model;

namespace Cohesive.Storage;

/// <summary>Represents a typed entity repository.</summary>
public sealed class TypedEntityRepository<TEntity>(
    IEntityRepository repository,
    Action<ObjectObservationMapperBuilder<TEntity>>? configureObjectMapper = null,
    ShapeMappingContext? mappingContext = null
    ) : IEntityRepository<TEntity> where TEntity : notnull
{
    /// <summary>Gets the entity definition.</summary>
    public EntityDefinition EntityDefinition => repository.EntityDefinition;

    /// <summary>Gets the mapping context.</summary>
    public ShapeMappingContext MappingContext { get; } = mappingContext ?? repository.MappingContext;

    /// <summary>Gets the entity type.</summary>
    public string EntityType => repository.EntityType;

    /// <summary>Attempts to get the value.</summary>
    public Task<EntitySnapshot?> TryGet(OperationContext context, string id, EntityReadOptions? options = null) =>
        repository.TryGet(context, id, options);

    /// <summary>Attempts to get entity.</summary>
    public Task<TEntity?> TryGetEntity(OperationContext context, string id, EntityReadOptions? options = null) =>
        repository.TryGetEntity<TEntity>(context,
            id,
            options,
            mappingContext: MappingContext);

    /// <summary>Upserts the value.</summary>
    public Task<EntitySnapshot> Upsert(OperationContext context, EntityWriteRequest write) =>
        repository.Upsert(context, write);

    /// <summary>Upserts the value.</summary>
    public Task<EntitySnapshot> Upsert(OperationContext context, TEntity entity, EntityConcurrencyToken? expectedConcurrencyToken = null) =>
        repository.Upsert(context,
            entity,
            expectedConcurrencyToken,
            configureObjectMapper,
            MappingContext);
}

/// <summary>Represents a typed entity outbox repository.</summary>
public sealed class TypedEntityOutboxRepository<TEntity>(
    IEntityRepository<TEntity> repository,
    IEntityOutboxRepository outboxRepository
    ) : IEntityOutboxRepository<TEntity> where TEntity : notnull
{
    /// <summary>Gets the entity definition.</summary>
    public EntityDefinition EntityDefinition => repository.EntityDefinition;

    /// <summary>Gets the mapping context.</summary>
    public ShapeMappingContext MappingContext => repository.MappingContext;

    /// <summary>Gets the entity type.</summary>
    public string EntityType => repository.EntityType;

    /// <summary>Attempts to get the value.</summary>
    public Task<EntitySnapshot?> TryGet(OperationContext context, string id, EntityReadOptions? options = null) =>
        repository.TryGet(context, id, options);

    /// <summary>Attempts to get entity.</summary>
    public Task<TEntity?> TryGetEntity(OperationContext context, string id, EntityReadOptions? options = null) =>
        repository.TryGetEntity(context, id, options);

    /// <summary>Upserts the value.</summary>
    public Task<EntitySnapshot> Upsert(OperationContext context, EntityWriteRequest write) =>
        repository.Upsert(context, write);

    /// <summary>Upserts the value.</summary>
    public Task<EntitySnapshot> Upsert(OperationContext context, TEntity entity, EntityConcurrencyToken? expectedConcurrencyToken = null) =>
        repository.Upsert(context, entity, expectedConcurrencyToken);

    /// <summary>Upserts with outbox.</summary>
    public Task<EntityCommitResult> UpsertWithOutbox(OperationContext context, EntityOutboxCommit commit) =>
        outboxRepository.UpsertWithOutbox(context, commit);
}
