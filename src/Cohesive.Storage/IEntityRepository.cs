using Cohesive.Relations.Mapping;
using Cohesive.Relations.Model;
using Cohesive.Relations.Queries;
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
    /// Shared shape-mapping configuration used for typed materialization and object writes.
    /// </summary>
    ShapeMappingContext MappingContext { get; }

    /// <summary>
    /// Logical observation/entity type handled by the repository.
    /// </summary>
    string EntityType => EntityDefinition.Shape.Id.Value;

    /// <summary>
    /// Native batch write capabilities advertised by this repository.
    /// </summary>
    EntityBatchCapabilities BatchCapabilities => EntityBatchCapabilities.SingleWriteFallback;

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
    async Task<IReadOnlyList<EntitySnapshot>> UpsertBatch(OperationContext context, IReadOnlyList<TEntity> writes) =>
        await Task.WhenAllThrottled(writes, w => Upsert(context, w), new(maxConcurrency: 5), context.CancellationToken);
}

/// <summary>
/// Temporary legacy query repository retained for the Cosmos entity-repository compatibility path.
/// </summary>
/// <remarks>
/// New integrations register canonical source readers and execute
/// <see cref="Cohesive.Relations.Authoring.RelationQueryEvaluation"/> through
/// <see cref="Cohesive.Relations.Execution.IRelationQueryEvaluator"/>. This facade will be removed with
/// <c>Cohesive.Relations.Queries</c> after the Cosmos repository migrates.
/// </remarks>
public interface IEntityQueryRepository : IEntityRepository
{
    /// <summary>
    /// Executes a structured query and returns rows, pagination metadata, and optional aggregations.
    /// </summary>
    /// <param name="context">Operation context carrying cancellation and host metadata.</param>
    /// <param name="query">Legacy structured entity query to execute.</param>
    /// <returns>The materialized legacy row, page, and aggregation response.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="context"/> or <paramref name="query"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// The cancellation token carried by <paramref name="context"/> is canceled.
    /// </exception>
    Task<EntityQueryResponse<EntitySnapshot>> Query(OperationContext context, EntityQuery query);

    /// <summary>
    /// Streams row results from a materialized query response.
    /// </summary>
    /// <param name="context">Operation context carrying cancellation and host metadata.</param>
    /// <param name="query">Legacy structured entity query to execute.</param>
    /// <returns>An asynchronous stream over the materialized response rows.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="context"/> or <paramref name="query"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// The cancellation token carried by <paramref name="context"/> is canceled.
    /// </exception>
    async IAsyncEnumerable<EntitySnapshot> QueryStream(OperationContext context, EntityQuery query)
    {
        var response = await Query(context, query).ConfigureAwait(false);
        foreach (var row in response.Rows)
            yield return row;
    }
}

/// <summary>
/// Strongly typed wrapper for the temporary Cosmos-compatible legacy query repository.
/// </summary>
/// <remarks>
/// New typed query consumers should author canonical relation/query evaluations and materialize their canonical
/// outputs through the Relations mapping infrastructure.
/// </remarks>
public interface IEntityQueryRepository<TEntity> : IEntityRepository<TEntity>, IEntityQueryRepository where TEntity : notnull;

/// <summary>
/// Entity repository that can atomically persist entity state together with outbox events.
/// </summary>
public interface IEntityOutboxRepository : IEntityRepository, IChangeStreamRepository
{
    /// <summary>
    /// Upserts one entity observation and appends zero or more outbox messages atomically.
    /// </summary>
    Task<EntityCommitResult> UpsertWithOutbox(OperationContext context, EntityOutboxCommit commit);

    /// <summary>
    /// Returns the outbox stream, optionally narrowed to a named logical stream.
    /// </summary>
    IObservationStream GetOutboxStream(string processorName, string? streamName = null, DateTimeOffset? startTime = null);
}

/// <summary>
/// Strongly typed outbox repository backed by object/observation mapping.
/// </summary>
public interface IEntityOutboxRepository<TEntity> : IEntityRepository<TEntity>, IEntityOutboxRepository where TEntity : notnull;

/// <summary>
/// Persisted observation snapshot.
/// </summary>
public sealed record EntitySnapshot(
    Observation Entity,
    string PartitionKey,
    EntityConcurrencyToken ConcurrencyToken,
    IReadOnlySet<string>? LoadedFields = null
);

/// <summary>
/// Observation write request.
/// </summary>
public sealed record EntityWriteRequest(
    Observation Entity,
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
/// Atomic outbox commit of an upsert and zero or more outbox messages.
/// </summary>
/// <param name="Write">The write/upsert request to commit.</param>
/// <param name="Messages">The outbox messages to commit.</param>
public sealed record EntityOutboxCommit(
    EntityWriteRequest Write,
    IReadOnlyList<EntityOutboxMessage> Messages
);

/// <summary>
/// Outbox message carried as an observation.
/// </summary>
public sealed record EntityOutboxMessage(
    string MessageId,
    string StreamName,
    string SubjectType,
    string SubjectId,
    string PartitionKey,
    Observation Entity,
    long? SubjectVersion = null,
    DateTimeOffset? OccurredAtUtc = null,
    string? CorrelationId = null
);

/// <summary>
/// Result of an atomic outbox commit.
/// </summary>
public sealed record EntityCommitResult(
    EntitySnapshot Entity,
    IReadOnlyList<EntityOutboxMessage> Messages
);
