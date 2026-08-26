using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Transitions.Model;

namespace Cohesive.Storage;

/// <summary>
/// Repository for persisted observations of a single logical entity type.
/// </summary>
public interface IEntityRepository
{
    /// <summary>
    /// Semantic entity definition handled by the repository.
    /// </summary>
    EntityDefinition EntityDefinition { get; }

    /// <summary>
    /// Logical observation/entity type handled by the repository.
    /// </summary>
    string EntityType => EntityDefinition.Shape.Id.Value;

    /// <summary>
    /// Native batch write capabilities advertised by this repository.
    /// </summary>
    EntityBatchCapabilities BatchCapabilities => EntityBatchCapabilities.SingleWriteFallback;

    /// <summary>
    /// Atomic Process-invoked Transition receipt capabilities advertised by this repository.
    /// </summary>
    EntityTransitionOperationCapabilities TransitionOperationCapabilities =>
        EntityTransitionOperationCapabilities.Unsupported;

    /// <summary>
    /// Attempts to load one persisted observation by id.
    /// Implementations may reject ambiguous ids that resolve to multiple partitions.
    /// </summary>
    Task<EntitySnapshot?> TryGet(OperationContext context, string id, EntityReadOptions? options = null);

    /// <summary>
    /// Upserts one observation snapshot.
    /// </summary>
    Task<EntitySnapshot> Upsert(OperationContext context, EntityWriteRequest write);

    /// <summary>
    /// Upserts a batch of observation snapshots. The default implementation preserves write order and uses single-write fallback.
    /// Repositories with native batch support should override this member and honor the requested atomicity semantics.
    /// </summary>
    async Task<EntityBatchWriteResult> UpsertBatch(OperationContext context, EntityBatchWriteRequest request)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        context.ThrowIfCancellationRequested();

        if (request.Atomicity != EntityBatchAtomicity.None)
        {
            throw new NotSupportedException(
                $"Repository '{EntityType}' does not support requested batch atomicity '{request.Atomicity}'.");
        }

        var writes = request.Writes ?? throw new ArgumentException("Batch write request must include writes.", nameof(request));
        EntitySnapshot[] snapshots = new EntitySnapshot[writes.Count];
        for (var i = 0; i < writes.Count; i++)
            snapshots[i] = await Upsert(context, writes[i]).ConfigureAwait(false);

        return new(snapshots, EntityBatchAtomicity.None);
    }
}

/// <summary>
/// Strongly typed entity repository backed by object/observation mapping.
/// </summary>
public interface IEntityRepository<TEntity> : IEntityRepository where TEntity : notnull
{
    /// <summary>
    /// Attempts to load one persisted entity and materialize it as <typeparamref name="TEntity"/>.
    /// </summary>
    Task<TEntity?> TryGetEntity(OperationContext context, string id, EntityReadOptions? options = null);

    /// <summary>
    /// Upserts one entity value by mapping it into the repository's semantic entity definition.
    /// </summary>
    Task<EntitySnapshot> Upsert(OperationContext context, TEntity entity, EntityConcurrencyToken? expectedConcurrencyToken = null);

    /// <summary>
    /// Upserts a batch of observations.
    /// </summary>
    /// <param name="context"></param>
    /// <param name="writes"></param>
    /// <returns></returns>
    async Task<IReadOnlyList<EntitySnapshot>> UpsertBatch(
        OperationContext context,
        IReadOnlyList<TEntity> writes) =>
        await Task.WhenAllThrottled(writes, w => Upsert(context, w), new(maxConcurrency: 5), context.CancellationToken);
}

/// <summary>
/// Entity repository that can atomically persist entity state together with canonical interaction envelopes.
/// </summary>
public interface IEntityOutboxRepository : IEntityRepository
{
    /// <summary>
    /// Upserts one entity observation and appends zero or more canonical interaction envelopes atomically.
    /// </summary>
    /// <param name="context">Operation context carrying time, cancellation, and physical attribution.</param>
    /// <param name="commit">Validated direct-Transition entity and envelope commit.</param>
    /// <returns>The committed snapshot and exact canonical envelopes, or retained evidence for an exact replay.</returns>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException">The operation is cancelled before the atomic boundary.</exception>
    /// <exception cref="ObservationConcurrencyConflictException">The entity concurrency fence is stale.</exception>
    /// <exception cref="InvalidOperationException">
    /// A retained emission identity has different canonical content or candidate entity state.
    /// </exception>
    Task<EntityCommitResult> UpsertWithOutbox(OperationContext context, EntityOutboxCommit commit);
}

/// <summary>
/// Strongly typed outbox repository backed by object/observation mapping.
/// </summary>
public interface IEntityOutboxRepository<TEntity> : IEntityRepository<TEntity>, IEntityOutboxRepository where TEntity : notnull;

/// <summary>
/// Persisted observation snapshot.
/// </summary>
public sealed record EntitySnapshot(
    EntityObservationSnapshot Entity,
    string PartitionKey,
    EntityConcurrencyToken ConcurrencyToken,
    IReadOnlySet<string>? LoadedFields = null
);

/// <summary>
/// Observation write request.
/// </summary>
public sealed record EntityWriteRequest(
    EntityObservationSnapshot Entity,
    EntityConcurrencyToken? ExpectedConcurrencyToken = null
);

/// <summary>
/// Requested atomicity semantics for a batch entity write.
/// </summary>
public enum EntityBatchAtomicity
{
    /// <summary>
    /// No all-or-nothing guarantee is requested. Implementations may use independent writes.
    /// </summary>
    None = 0,

    /// <summary>
    /// The caller requires atomicity when all writes are in one logical partition.
    /// </summary>
    SamePartition = 1,

    /// <summary>
    /// The caller requires all-or-nothing atomicity across every write in the batch.
    /// </summary>
    AllOrNothing = 2
}

/// <summary>
/// Native batch behavior supported by an entity repository.
/// </summary>
public sealed record EntityBatchCapabilities(
    bool SupportsNativeBatching,
    bool SupportsSamePartitionAtomicity,
    bool SupportsAllOrNothingAtomicity,
    int? MaxItemsPerBatch = null
    )
{
    /// <summary>
    /// Capability set for repositories that only support single-write fallback.
    /// </summary>
    public static EntityBatchCapabilities SingleWriteFallback { get; } = new(
        SupportsNativeBatching: false,
        SupportsSamePartitionAtomicity: false,
        SupportsAllOrNothingAtomicity: false);

    /// <summary>
    /// Indicates whether this capability set satisfies the requested atomicity.
    /// </summary>
    public bool SupportsAtomicity(EntityBatchAtomicity atomicity) => atomicity switch
    {
        EntityBatchAtomicity.None => true,
        EntityBatchAtomicity.SamePartition => SupportsSamePartitionAtomicity,
        EntityBatchAtomicity.AllOrNothing => SupportsAllOrNothingAtomicity,
        _ => false
    };
}

/// <summary>
/// Batch write request for one logical entity repository.
/// </summary>
public sealed record EntityBatchWriteRequest(
    IReadOnlyList<EntityWriteRequest> Writes,
    EntityBatchAtomicity Atomicity = EntityBatchAtomicity.None
    );

/// <summary>
/// Result of a batch write against one logical entity repository.
/// </summary>
public sealed record EntityBatchWriteResult(
    IReadOnlyList<EntitySnapshot> Snapshots,
    EntityBatchAtomicity Atomicity
    );

/// <summary>
/// Opaque optimistic-concurrency token for one observation snapshot.
/// </summary>
public readonly record struct EntityConcurrencyToken(string Value)
{
    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>
/// Structured read request options for observation repositories.
/// </summary>
public sealed record EntityReadOptions
{
    /// <summary>
    /// Creates a read request.
    /// </summary>
    public EntityReadOptions(
        FieldSelection? fieldSelection = null,
        long? expectedVersion = null,
        EntityConcurrencyToken? expectedConcurrencyToken = null,
        string? partitionKey = null
        )
    {
        FieldSelection = fieldSelection ?? FieldSelection.Full;
        ExpectedVersion = expectedVersion;
        ExpectedConcurrencyToken = expectedConcurrencyToken;
        PartitionKey = string.IsNullOrWhiteSpace(partitionKey) ? null : partitionKey;
    }

    /// <summary>
    /// Field-selection request for this read.
    /// </summary>
    public FieldSelection FieldSelection { get; }

    /// <summary>
    /// Optional projected field subset, or <see langword="null"/> for a full-state read.
    /// </summary>
    public IReadOnlySet<string>? Fields => FieldSelection.Fields;

    /// <summary>
    /// Optional expected logical version.
    /// </summary>
    public long? ExpectedVersion { get; }

    /// <summary>
    /// Optional expected storage concurrency token.
    /// </summary>
    public EntityConcurrencyToken? ExpectedConcurrencyToken { get; }

    /// <summary>
    /// Optional partition key for point reads.
    /// </summary>
    public string? PartitionKey { get; }

    /// <summary>
    /// Full-state read.
    /// </summary>
    public static EntityReadOptions Full { get; } = new(FieldSelection.Full);

    /// <summary>
    /// Creates a projected-field read request.
    /// </summary>
    public static EntityReadOptions ForFields(params string[] fields) => new(FieldSelection.ForFields(fields));

    /// <summary>
    /// Returns a copy of these options scoped to a physical or logical partition key.
    /// </summary>
    public EntityReadOptions WithPartitionKey(string partitionKey) => new(
        fieldSelection: FieldSelection,
        expectedVersion: ExpectedVersion,
        expectedConcurrencyToken: ExpectedConcurrencyToken,
        partitionKey: Guard.RequireNotNullOrWhiteSpace(partitionKey)
        );
}

/// <summary>
/// Atomic entity-state and canonical interaction-envelope outbox commit.
/// </summary>
public sealed record EntityOutboxCommit
{
    /// <summary>Creates one validated direct-Transition outbox commit.</summary>
    /// <param name="write">Candidate entity state and optional optimistic-concurrency fence.</param>
    /// <param name="envelopes">Exact canonical envelopes made durable with the candidate state.</param>
    /// <exception cref="ArgumentNullException"><paramref name="write"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="envelopes"/> is default, contains a null or duplicate emission, is not a durable direct
    /// Transition emission, or identifies an entity other than the candidate state.
    /// </exception>
    public EntityOutboxCommit(
        EntityWriteRequest write,
        ImmutableArray<InteractionEnvelope> envelopes)
    {
        Write = write ?? throw new ArgumentNullException(nameof(write));
        if (envelopes.IsDefault)
            throw new ArgumentException("Entity outbox envelopes must be initialized.", nameof(envelopes));

        HashSet<EmissionId>? identities = envelopes.Length > 1 ? [] : null;
        var entityType = new EntityTypeName(write.Entity.Observation.ShapeId.ShapeId.Value);
        ExecutionDefinitionReference? transition = null;
        ExecutionNodeId? outcome = null;
        foreach (var envelope in envelopes)
        {
            if (envelope is null)
                throw new ArgumentException("Entity outbox envelopes cannot contain null values.", nameof(envelopes));

            if (identities is not null && !identities.Add(envelope.Context.EmissionId))
                throw new ArgumentException(
                    $"Entity outbox emission identity '{envelope.Context.EmissionId.Value}' is duplicated.",
                    nameof(envelopes));
            if (envelope is not (DomainEventEnvelope or RequestEnvelope))
                throw new ArgumentException(
                    "A direct Transition outbox can retain only Domain Event and Request envelopes.",
                    nameof(envelopes));
            if (envelope.Context.Origin is not TransitionInteractionOrigin origin)
                throw new ArgumentException(
                    "The entity outbox is authoritative only for envelopes emitted by a direct Transition.",
                    nameof(envelopes));
            if (origin.Entity.EntityType != entityType
                || origin.Entity.EntityId != write.Entity.EntityId)
                throw new ArgumentException(
                    "Every entity outbox envelope must identify the exact candidate entity as its Transition subject.",
                    nameof(envelopes));
            if (transition is not null
                && (origin.Definition != transition || origin.Outcome != outcome))
                throw new ArgumentException(
                    "Every entity outbox envelope in one commit must originate from the same Transition decision.",
                    nameof(envelopes));
            transition = origin.Definition;
            outcome = origin.Outcome;
            if (envelope.Context.Delivery.Durability != InteractionDurabilityDemand.Durable)
                throw new ArgumentException(
                    "An entity outbox can retain only interactions that demand durable delivery.",
                    nameof(envelopes));
        }

        Envelopes = envelopes;
    }

    /// <summary>Candidate entity state and optional optimistic-concurrency fence.</summary>
    public EntityWriteRequest Write { get; }

    /// <summary>Exact canonical envelopes committed under entity-outbox publication authority.</summary>
    public ImmutableArray<InteractionEnvelope> Envelopes { get; }
}

/// <summary>
/// Result of an atomic outbox commit.
/// </summary>
public sealed record EntityCommitResult
{
    /// <summary>Creates the observable result of one entity outbox commit.</summary>
    /// <param name="entity">Persisted candidate snapshot or the exact retained replay snapshot.</param>
    /// <param name="envelopes">Canonical envelopes committed or replayed with the entity.</param>
    /// <exception cref="ArgumentNullException"><paramref name="entity"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="envelopes"/> is default.</exception>
    public EntityCommitResult(EntitySnapshot entity, ImmutableArray<InteractionEnvelope> envelopes)
    {
        Entity = entity ?? throw new ArgumentNullException(nameof(entity));
        Envelopes = envelopes.IsDefault
            ? throw new ArgumentException("Committed outbox envelopes must be initialized.", nameof(envelopes))
            : envelopes;
    }

    /// <summary>Persisted candidate snapshot or the exact retained replay snapshot.</summary>
    public EntitySnapshot Entity { get; }

    /// <summary>Canonical envelopes committed or replayed with the entity.</summary>
    public ImmutableArray<InteractionEnvelope> Envelopes { get; }
}
