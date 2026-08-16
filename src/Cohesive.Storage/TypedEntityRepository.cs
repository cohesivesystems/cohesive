using Cohesive.Relations.Mapping;
using Cohesive.Transitions.Model;

namespace Cohesive.Storage;

/// <summary>Represents a typed entity repository.</summary>
public sealed class TypedEntityRepository<TEntity>(
    IEntityRepository repository,
    Action<ObjectObservationMapperBuilder<TEntity>>? configureObjectMapper = null,
    ShapeMappingContext? mappingContext = null
    ) : IEntityRepository<TEntity>, IEntityTransitionOperationRepository where TEntity : notnull
{
    /// <summary>Gets the entity definition.</summary>
    public EntityDefinition EntityDefinition => repository.EntityDefinition;

    /// <summary>Gets the mapping context.</summary>
    public ShapeMappingContext MappingContext { get; } = mappingContext ?? repository.MappingContext;

    /// <summary>Gets the entity type.</summary>
    public string EntityType => repository.EntityType;

    /// <summary>Gets atomic Process Transition operation capabilities.</summary>
    public EntityTransitionOperationCapabilities TransitionOperationCapabilities =>
        repository.TransitionOperationCapabilities;

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

    /// <summary>Looks up one exact Process Transition operation receipt.</summary>
    /// <param name="context">Operation context and cancellation.</param>
    /// <param name="request">Exact replay lookup identity.</param>
    /// <returns>Missing, replay, conflict, or capability evidence.</returns>
    public Task<EntityTransitionOperationResult> TryGetTransitionOperation(
        OperationContext context,
        EntityTransitionOperationRequest request) =>
        repository.TryGetTransitionOperation(context, request);

    /// <summary>Looks up one subject-scoped creation Transition receipt.</summary>
    /// <param name="context">Operation context and cancellation.</param>
    /// <param name="request">Candidate creation request whose exact occurrence was not retained.</param>
    /// <returns>Missing, semantic replay, conflict, or capability evidence.</returns>
    public Task<EntityTransitionOperationResult> TryGetCreationTransitionOperation(
        OperationContext context,
        EntityTransitionOperationRequest request) =>
        repository.TryGetCreationTransitionOperation(context, request);

    /// <summary>Atomically commits entity state and one Process Transition operation receipt.</summary>
    /// <param name="context">Operation context, time, and cancellation.</param>
    /// <param name="commit">Complete deterministic atomic commit intent.</param>
    /// <returns>Committed, replayed, conflict, or capability evidence.</returns>
    public Task<EntityTransitionOperationResult> CommitTransitionOperation(
        OperationContext context,
        EntityTransitionOperationCommit commit) =>
        repository.CommitTransitionOperation(context, commit);
}

/// <summary>Represents a typed entity outbox repository.</summary>
public sealed class TypedEntityOutboxRepository<TEntity>(
    IEntityRepository<TEntity> repository,
    IEntityOutboxRepository outboxRepository
    ) : IEntityOutboxRepository<TEntity>, IEntityTransitionOperationRepository where TEntity : notnull
{
    /// <summary>Gets the entity definition.</summary>
    public EntityDefinition EntityDefinition => repository.EntityDefinition;

    /// <summary>Gets the mapping context.</summary>
    public ShapeMappingContext MappingContext => repository.MappingContext;

    /// <summary>Gets the entity type.</summary>
    public string EntityType => repository.EntityType;

    /// <summary>Gets atomic Process Transition operation capabilities.</summary>
    public EntityTransitionOperationCapabilities TransitionOperationCapabilities =>
        outboxRepository.TransitionOperationCapabilities;

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

    /// <summary>Looks up one exact Process Transition operation receipt.</summary>
    /// <param name="context">Operation context and cancellation.</param>
    /// <param name="request">Exact replay lookup identity.</param>
    /// <returns>Missing, replay, conflict, or capability evidence.</returns>
    public Task<EntityTransitionOperationResult> TryGetTransitionOperation(
        OperationContext context,
        EntityTransitionOperationRequest request) =>
        outboxRepository.TryGetTransitionOperation(context, request);

    /// <summary>Looks up one subject-scoped creation Transition receipt.</summary>
    /// <param name="context">Operation context and cancellation.</param>
    /// <param name="request">Candidate creation request whose exact occurrence was not retained.</param>
    /// <returns>Missing, semantic replay, conflict, or capability evidence.</returns>
    public Task<EntityTransitionOperationResult> TryGetCreationTransitionOperation(
        OperationContext context,
        EntityTransitionOperationRequest request) =>
        outboxRepository.TryGetCreationTransitionOperation(context, request);

    /// <summary>Atomically commits entity state and one Process Transition operation receipt.</summary>
    /// <param name="context">Operation context, time, and cancellation.</param>
    /// <param name="commit">Complete deterministic atomic commit intent.</param>
    /// <returns>Committed, replayed, conflict, or capability evidence.</returns>
    public Task<EntityTransitionOperationResult> CommitTransitionOperation(
        OperationContext context,
        EntityTransitionOperationCommit commit) =>
        outboxRepository.CommitTransitionOperation(context, commit);
}
