using Cohesive.Transitions.Model;

namespace Cohesive.Storage;

/// <summary>Represents a typed entity repository.</summary>
/// <param name="repository">Underlying canonical repository whose capabilities and batch behavior are preserved.</param>
/// <param name="selectEntityId">Optional typed identity selector.</param>
/// <param name="selectVersion">Optional typed semantic-version selector.</param>
/// <param name="configureMaterializer">Optional materializer configuration.</param>
public sealed class TypedEntityRepository<TEntity>(
    IEntityRepository repository,
    Func<TEntity, string>? selectEntityId = null,
    Func<TEntity, long>? selectVersion = null,
    Action<ObservationMaterializerBuilder<TEntity>>? configureMaterializer = null
    ) : IEntityRepository<TEntity>, IEntityTransitionOperationRepository where TEntity : notnull
{
    /// <summary>Gets the entity definition.</summary>
    public EntityDefinition EntityDefinition => repository.EntityDefinition;

    /// <summary>Gets the entity type.</summary>
    public string EntityType => repository.EntityType;

    /// <summary>Gets the underlying repository's native batching guarantees and limits.</summary>
    public EntityBatchCapabilities BatchCapabilities => repository.BatchCapabilities;

    /// <summary>Forwards an ordered canonical batch without replacing its native transaction semantics.</summary>
    /// <param name="context">Operation context and cancellation.</param>
    /// <param name="request">Ordered writes and required atomicity.</param>
    /// <returns>The underlying repository's batch result.</returns>
    public Task<EntityBatchWriteResult> UpsertBatch(OperationContext context, EntityBatchWriteRequest request) =>
        repository.UpsertBatch(context, request);

    /// <summary>Maps an ordered typed batch using this facade's selectors and the underlying native batch boundary.</summary>
    /// <param name="context">Operation context and cancellation.</param>
    /// <param name="writes">Complete typed candidates in write order.</param>
    /// <param name="atomicity">Required atomicity forwarded unchanged to the native repository.</param>
    /// <returns>Committed snapshots in input order.</returns>
    public async Task<IReadOnlyList<EntitySnapshot>> UpsertBatch(OperationContext context, IReadOnlyList<TEntity> writes,
        EntityBatchAtomicity atomicity = EntityBatchAtomicity.None) =>
        (await repository.UpsertBatch(context, writes, atomicity, selectEntityId, selectVersion).ConfigureAwait(false)).Snapshots;

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
            configureMaterializer);

    /// <summary>Upserts the value.</summary>
    public Task<EntitySnapshot> Upsert(OperationContext context, EntityWriteRequest write) =>
        repository.Upsert(context, write);

    /// <summary>Upserts the value.</summary>
    public Task<EntitySnapshot> Upsert(OperationContext context, TEntity entity, EntityConcurrencyToken? expectedConcurrencyToken = null) =>
        repository.Upsert(context,
            entity,
            expectedConcurrencyToken,
            selectEntityId,
            selectVersion);

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
/// <param name="repository">Typed repository supplying ordinary reads/writes and native batch behavior.</param>
/// <param name="outboxRepository">Repository supplying atomic outbox and retained-operation behavior.</param>
public sealed class TypedEntityOutboxRepository<TEntity>(
    IEntityRepository<TEntity> repository,
    IEntityOutboxRepository outboxRepository
    ) : IEntityOutboxRepository<TEntity>, IEntityTransitionOperationRepository where TEntity : notnull
{
    /// <summary>Gets the entity definition.</summary>
    public EntityDefinition EntityDefinition => repository.EntityDefinition;

    /// <summary>Gets the entity type.</summary>
    public string EntityType => repository.EntityType;

    /// <summary>Gets ordinary batch guarantees from the repository that owns those writes.</summary>
    public EntityBatchCapabilities BatchCapabilities => repository.BatchCapabilities;

    /// <summary>Forwards a canonical batch to the repository that owns ordinary writes.</summary>
    /// <param name="context">Operation context and cancellation.</param>
    /// <param name="request">Ordered writes and required atomicity.</param>
    /// <returns>The underlying native batch result.</returns>
    public Task<EntityBatchWriteResult> UpsertBatch(OperationContext context, EntityBatchWriteRequest request) =>
        repository.UpsertBatch(context, request);

    /// <summary>Forwards typed batches, including custom mapping selectors and requested atomicity.</summary>
    /// <param name="context">Operation context and cancellation.</param>
    /// <param name="writes">Complete typed candidates in input order.</param>
    /// <param name="atomicity">Required ordinary-write atomicity.</param>
    /// <returns>Committed snapshots in input order.</returns>
    public Task<IReadOnlyList<EntitySnapshot>> UpsertBatch(OperationContext context, IReadOnlyList<TEntity> writes,
        EntityBatchAtomicity atomicity = EntityBatchAtomicity.None) => repository.UpsertBatch(context, writes, atomicity);

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
