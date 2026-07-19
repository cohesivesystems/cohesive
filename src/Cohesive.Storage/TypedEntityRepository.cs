using Cohesive.Relations.Mapping;
using Cohesive.Relations.Queries;
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

/// <summary>Typed wrapper for the deletion-boundary legacy entity query repository.</summary>
/// <remarks>
/// Cohesive ships no built-in production backend for the underlying legacy contract. This type remains only until
/// the legacy query facade is deleted. New code should use
/// <see cref="Cohesive.Relations.Execution.IRelationQueryEvaluator"/>.
/// </remarks>
/// <param name="repository">Typed point-read and write repository.</param>
/// <param name="queryRepository">Legacy query repository for the same entity source.</param>
/// <exception cref="ArgumentNullException">
/// <paramref name="repository"/> or <paramref name="queryRepository"/> is <see langword="null"/>.
/// </exception>
public sealed class TypedEntityQueryRepository<TEntity>(
    IEntityRepository<TEntity> repository,
    IEntityQueryRepository queryRepository
    ) : IEntityQueryRepository<TEntity> where TEntity : notnull
{
    readonly IEntityRepository<TEntity> repository = Guard.RequireNotNull(repository);
    readonly IEntityQueryRepository queryRepository = Guard.RequireNotNull(queryRepository);

    /// <inheritdoc />
    public EntityDefinition EntityDefinition => repository.EntityDefinition;

    /// <inheritdoc />
    public ShapeMappingContext MappingContext => repository.MappingContext;

    /// <inheritdoc />
    public string EntityType => repository.EntityType;

    /// <inheritdoc />
    public Task<EntitySnapshot?> TryGet(OperationContext context, string id, EntityReadOptions? options = null) =>
        repository.TryGet(context, id, options);

    /// <inheritdoc />
    public Task<TEntity?> TryGetEntity(OperationContext context, string id, EntityReadOptions? options = null) =>
        repository.TryGetEntity(context, id, options);

    /// <inheritdoc />
    public Task<EntitySnapshot> Upsert(OperationContext context, EntityWriteRequest write) =>
        repository.Upsert(context, write);

    /// <inheritdoc />
    public Task<EntitySnapshot> Upsert(OperationContext context, TEntity entity, EntityConcurrencyToken? expectedConcurrencyToken = null) =>
        repository.Upsert(context, entity, expectedConcurrencyToken);

    /// <inheritdoc />
    public Task<EntityQueryResponse<EntitySnapshot>> Query(OperationContext context, EntityQuery query) =>
        queryRepository.Query(context, query);
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

    /// <summary>Gets change stream.</summary>
    public IObservationStream GetChangeStream(string processorName, DateTimeOffset? startTime = null) =>
        outboxRepository.GetChangeStream(processorName, startTime);

    /// <summary>Gets outbox stream.</summary>
    public IObservationStream GetOutboxStream(string processorName, string? streamName = null, DateTimeOffset? startTime = null) =>
        outboxRepository.GetOutboxStream(processorName, streamName, startTime);
}
