using Cohesive.Relations.Mapping;
using Cohesive.Relations.Model;
using Cohesive.Storage.Processes;
using Cohesive.Transitions.Model;

namespace Cohesive.Storage;

/// <summary>
/// In-memory observation repository with atomic outbox support for one logical observation type.
/// </summary>
public sealed class InMemoryEntityOutboxRepository : IEntityOutboxRepository, IEntityTransitionOperationRepository
{
    readonly Lock gate = new();
    readonly Dictionary<string, EntitySnapshot> snapshotsByKey = new(StringComparer.Ordinal);
    readonly Dictionary<string, HashSet<string>> partitionKeysByObservationId = new(StringComparer.Ordinal);
    readonly Dictionary<ProcessOperationOccurrence, EntityTransitionOperationReceipt> transitionOperationReceipts = [];
    readonly List<EntityOutboxMessage> outboxMessages = [];
    readonly EntityDefinition entityDefinition;
    readonly EntityPartitionKeyPolicy partitionKeyPolicy;
    readonly Action<EntityTransitionOperationCommitPhase>? transitionOperationCommitBoundary;
    long nextConcurrencyVersion;

    /// <summary>Initializes a new instance of the in memory entity outbox repository type.</summary>
    public InMemoryEntityOutboxRepository(
        EntityDefinition entityDefinition,
        string partitionKeyFieldName,
        IEnumerable<EntitySnapshot>? seedSnapshots = null,
        ShapeMappingContext? mappingContext = null
        ) : this(
            entityDefinition,
            EntityPartitionKeyPolicy.FromField(partitionKeyFieldName),
            seedSnapshots,
            mappingContext)
    {
    }

    /// <summary>Initializes a new instance of the in memory entity outbox repository type.</summary>
    public InMemoryEntityOutboxRepository(
        EntityDefinition entityDefinition,
        EntityPartitionKeyPolicy partitionKeyPolicy,
        IEnumerable<EntitySnapshot>? seedSnapshots = null,
        ShapeMappingContext? mappingContext = null
        )
    {
        this.entityDefinition = Guard.RequireNotNull(entityDefinition);
        this.partitionKeyPolicy = Guard.RequireNotNull(partitionKeyPolicy);
        MappingContext = mappingContext ?? ShapeMappingContext.Default;

        if (seedSnapshots is null)
            return;

        foreach (var snapshot in seedSnapshots)
            SeedSnapshot(snapshot);
    }

    internal InMemoryEntityOutboxRepository(
        EntityDefinition entityDefinition,
        EntityPartitionKeyPolicy partitionKeyPolicy,
        Action<EntityTransitionOperationCommitPhase> transitionOperationCommitBoundary,
        IEnumerable<EntitySnapshot>? seedSnapshots = null,
        ShapeMappingContext? mappingContext = null)
        : this(entityDefinition, partitionKeyPolicy, seedSnapshots, mappingContext)
    {
        this.transitionOperationCommitBoundary =
            transitionOperationCommitBoundary ?? throw new ArgumentNullException(nameof(transitionOperationCommitBoundary));
    }

    /// <summary>Initializes a new instance of the in memory entity outbox repository type.</summary>
    public InMemoryEntityOutboxRepository(
        EntityDefinition entityDefinition,
        Func<Observation, string> partitionKeySelector,
        IEnumerable<EntitySnapshot>? seedSnapshots = null,
        ShapeMappingContext? mappingContext = null
        ) : this(
            entityDefinition,
            EntityPartitionKeyPolicy.FromObservation(partitionKeySelector),
            seedSnapshots,
            mappingContext)
    {
    }

    /// <summary>Initializes a new instance of the in memory entity outbox repository type.</summary>
    public InMemoryEntityOutboxRepository(
        EntityDefinition entityDefinition,
        IEnumerable<object>? seedData,
        string partitionKeyFieldName,
        string idFieldName = "Id",
        ShapeMappingContext? mappingContext = null)
        : this(entityDefinition, partitionKeyFieldName, mappingContext: mappingContext)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idFieldName);

        if (seedData is null)
            return;

        foreach (var seed in seedData)
            SeedSnapshot(CreateSeedSnapshot(entityDefinition, seed, idFieldName));
    }

    /// <summary>Gets the entity definition.</summary>
    public EntityDefinition EntityDefinition => entityDefinition;

    /// <summary>Gets the mapping context.</summary>
    public ShapeMappingContext MappingContext { get; }

    /// <summary>Gets the entity type.</summary>
    public string EntityType => entityDefinition.Shape.Id.Value;

    /// <summary>Gets atomic entity-state and Process Transition receipt capabilities.</summary>
    public EntityTransitionOperationCapabilities TransitionOperationCapabilities =>
        EntityTransitionOperationCapabilities.AtomicStateAndReceipt;

    /// <summary>Gets the outbox messages.</summary>
    public IReadOnlyList<EntityOutboxMessage> OutboxMessages
    {
        get
        {
            lock (gate)
                return [.. outboxMessages];
        }
    }

    /// <summary>Attempts to get the value.</summary>
    public Task<EntitySnapshot?> TryGet(
        OperationContext context,
        string id,
        EntityReadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        context.ThrowIfCancellationRequested();

        lock (gate)
        {
            var partitionKey = options?.PartitionKey ?? partitionKeyPolicy.TryResolvePointReadPartitionKey(context, id);
            if (string.IsNullOrWhiteSpace(partitionKey)
                && !TryResolveSinglePartitionKey(id, out partitionKey))
            {
                return Task.FromResult<EntitySnapshot?>(null);
            }

            if (!snapshotsByKey.TryGetValue(CreateKey(id, partitionKey), out var snapshot))
                return Task.FromResult<EntitySnapshot?>(null);

            ValidateConcurrency(options, snapshot);
            return Task.FromResult<EntitySnapshot?>(Project(snapshot, options?.Fields));
        }
    }

    /// <summary>Upserts the value.</summary>
    public Task<EntitySnapshot> Upsert(OperationContext context, EntityWriteRequest write)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(write);
        context.ThrowIfCancellationRequested();
        EnsureEntityType(write.Entity);

        lock (gate)
            return Task.FromResult(UpsertUnderLock(context, write));
    }

    /// <summary>Atomically upserts an entity snapshot and appends its outbox messages.</summary>
    public Task<EntityCommitResult> UpsertWithOutbox(OperationContext context, EntityOutboxCommit commit)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(commit);
        context.ThrowIfCancellationRequested();
        EnsureEntityType(commit.Write.Entity);

        lock (gate)
        {
            var snapshot = UpsertUnderLock(context, commit.Write);
            outboxMessages.AddRange(commit.Messages);
            return Task.FromResult(new EntityCommitResult(snapshot, commit.Messages));
        }
    }

    /// <summary>Looks up an exact Process Transition operation receipt.</summary>
    /// <param name="context">Operation context and cancellation.</param>
    /// <param name="request">Exact replay lookup identity.</param>
    /// <returns>Missing, replay, or identity-conflict evidence.</returns>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException">The operation is cancelled before the lookup.</exception>
    public Task<EntityTransitionOperationResult> TryGetTransitionOperation(
        OperationContext context,
        EntityTransitionOperationRequest request)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        context.ThrowIfCancellationRequested();

        lock (gate)
        {
            return Task.FromResult(
                transitionOperationReceipts.TryGetValue(request.Operation, out var retained)
                    ? ReplayOrRequestConflict(request, retained)
                    : EntityTransitionOperationResult.NotFound());
        }
    }

    /// <summary>Atomically commits candidate entity state and one Process Transition operation receipt.</summary>
    /// <param name="context">Operation context, time, and cancellation.</param>
    /// <param name="commit">Complete deterministic atomic commit intent.</param>
    /// <returns>Committed, replayed, stale-concurrency, or identity-conflict evidence.</returns>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// The candidate entity does not belong to this repository or cannot resolve a partition key.
    /// </exception>
    /// <exception cref="OperationCanceledException">The operation is cancelled before the atomic boundary.</exception>
    public Task<EntityTransitionOperationResult> CommitTransitionOperation(
        OperationContext context,
        EntityTransitionOperationCommit commit)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(commit);
        context.ThrowIfCancellationRequested();
        EnsureEntityType(commit.Write.Entity);

        lock (gate)
        {
            if (transitionOperationReceipts.TryGetValue(commit.Request.Operation, out var retained))
                return Task.FromResult(ReplayOrCommitConflict(commit, retained));
        }

        ObserveTransitionOperationCommitBoundary(EntityTransitionOperationCommitPhase.BeforeAtomicCommit);

        EntityTransitionOperationResult result;
        lock (gate)
        {
            if (transitionOperationReceipts.TryGetValue(commit.Request.Operation, out var retained))
            {
                result = ReplayOrCommitConflict(commit, retained);
            }
            else
            {
                var partitionKey = GetPartitionKey(context, commit.Write.Entity);
                var key = CreateKey(commit.Write.Entity.Id, partitionKey);
                var expected = commit.Write.ExpectedConcurrencyToken!.Value;
                if (!snapshotsByKey.TryGetValue(key, out var current)
                    || current.ConcurrencyToken != expected)
                {
                    result = EntityTransitionOperationRepositoryExtensions.ConcurrencyConflict(
                        $"Entity '{EntityType}:{commit.Write.Entity.Id}' no longer matches concurrency fence '{expected.Value}'.");
                }
                else
                {
                    var snapshot = new EntitySnapshot(
                        Entity: commit.Write.Entity,
                        PartitionKey: partitionKey,
                        ConcurrencyToken: new(CreateConcurrencyToken()));
                    var receipt = new EntityTransitionOperationReceipt(commit, snapshot, context.UtcNow);
                    snapshotsByKey[key] = snapshot;
                    transitionOperationReceipts.Add(commit.Request.Operation, receipt);
                    TrackObservation(snapshot.Entity.Id, partitionKey);
                    result = EntityTransitionOperationResult.Committed(receipt);
                }
            }
        }

        if (result.Disposition == EntityTransitionOperationDisposition.Committed)
        {
            ObserveTransitionOperationCommitBoundary(
                EntityTransitionOperationCommitPhase.AfterAtomicCommitBeforeReturn);
        }

        return Task.FromResult(result);
    }

    EntitySnapshot UpsertUnderLock(OperationContext context, EntityWriteRequest write)
    {
        var partitionKey = GetPartitionKey(context, write.Entity);
        var key = CreateKey(write.Entity.Id, partitionKey);
        if (write.ExpectedConcurrencyToken is { } expected
            && (!snapshotsByKey.TryGetValue(key, out var current) || current.ConcurrencyToken != expected))
        {
            throw new ObservationConcurrencyConflictException(
                $"Observation '{EntityType}:{write.Entity.Id}' failed optimistic concurrency validation.");
        }

        var snapshot = new EntitySnapshot(
            Entity: write.Entity,
            PartitionKey: partitionKey,
            ConcurrencyToken: new(CreateConcurrencyToken()));
        snapshotsByKey[key] = snapshot;
        TrackObservation(snapshot.Entity.Id, partitionKey);
        return snapshot;
    }

    static EntityTransitionOperationResult ReplayOrRequestConflict(
        EntityTransitionOperationRequest request,
        EntityTransitionOperationReceipt retained) =>
        retained.Request.Fingerprint == request.Fingerprint
            ? EntityTransitionOperationResult.Replayed(retained)
            : EntityTransitionOperationRepositoryExtensions.IdentityConflict(
                "The Process operation occurrence is retained for another Transition, subject, or input.",
                "/request");

    static EntityTransitionOperationResult ReplayOrCommitConflict(
        EntityTransitionOperationCommit commit,
        EntityTransitionOperationReceipt retained)
    {
        if (retained.Request.Fingerprint != commit.Request.Fingerprint)
        {
            return EntityTransitionOperationRepositoryExtensions.IdentityConflict(
                "The Process operation occurrence is retained for another Transition, subject, or input.",
                "/request");
        }
        return retained.Commit.Fingerprint == commit.Fingerprint
            ? EntityTransitionOperationResult.Replayed(retained)
            : EntityTransitionOperationRepositoryExtensions.IdentityConflict(
                "The Process operation occurrence is retained with another candidate state or normalized result.",
                "/commit");
    }

    void ObserveTransitionOperationCommitBoundary(EntityTransitionOperationCommitPhase phase) =>
        transitionOperationCommitBoundary?.Invoke(phase);

    /// <summary>
    /// Captures one immutable snapshot for canonical in-memory relation/query acquisition.
    /// </summary>
    /// <returns>
    /// An isolated array of the current entity snapshots and monotonic repository version captured under the
    /// repository lock. The caller owns and may reorder the returned array.
    /// </returns>
    internal (EntitySnapshot[] Snapshots, long Version) CaptureRelationQuerySnapshot()
    {
        lock (gate)
            return ([.. snapshotsByKey.Values], nextConcurrencyVersion);
    }

    void SeedSnapshot(EntitySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        EnsureEntityType(snapshot.Entity);

        lock (gate)
        {
            snapshotsByKey[CreateKey(snapshot.Entity.Id, snapshot.PartitionKey)] = snapshot;
            TrackObservation(snapshot.Entity.Id, snapshot.PartitionKey);
        }
    }

    EntitySnapshot CreateSeedSnapshot(EntityDefinition entityDefinition, object seed, string idFieldName)
    {
        ArgumentNullException.ThrowIfNull(seed);

        var fields = ObservationValue.FromObject(seed).Fields
            ?? throw new InvalidOperationException(
                $"Seed data for '{EntityType}' must serialize to a JSON object.");
        if (!fields.TryGetValue(idFieldName, out var idValue))
        {
            throw new InvalidOperationException(
                $"Seed data for '{EntityType}' must contain an '{idFieldName}' field.");
        }

        var id = idValue.GetString();
        if (string.IsNullOrWhiteSpace(id))
            throw new InvalidOperationException($"Seed data for '{EntityType}' resolved an empty '{idFieldName}' field.");

        var state = entityDefinition.CreateState(id, seed);
        var partitionKey = GetPartitionKey(OperationContext.Create(), state.Observation);

        return new(
            Entity: state.Observation,
            PartitionKey: partitionKey,
            ConcurrencyToken: new(CreateConcurrencyToken())
            );
    }

    void EnsureEntityType(Observation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (!string.Equals(observation.ShapeId.Value, EntityType, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Repository for '{EntityType}' cannot persist observation '{observation.ShapeId.Value}:{observation.Id}'.");
        }
    }

    void ValidateConcurrency(EntityReadOptions? read, EntitySnapshot snapshot)
    {
        if (read?.ExpectedVersion is { } expectedVersion && snapshot.Entity.Version != expectedVersion)
        {
            throw new ObservationConcurrencyConflictException(
                $"Observation '{EntityType}:{snapshot.Entity.Id}' expected version '{expectedVersion}' but found '{snapshot.Entity.Version}'.");
        }

        if (read?.ExpectedConcurrencyToken is { } expectedConcurrencyToken && snapshot.ConcurrencyToken != expectedConcurrencyToken)
        {
            throw new ObservationConcurrencyConflictException(
                $"Observation '{EntityType}:{snapshot.Entity.Id}' expected concurrency token '{expectedConcurrencyToken.Value}' but found '{snapshot.ConcurrencyToken.Value}'.");
        }
    }

    EntitySnapshot Project(EntitySnapshot snapshot, IReadOnlySet<string>? fields)
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

    string CreateConcurrencyToken() => $"mem:{Interlocked.Increment(ref nextConcurrencyVersion)}";

    string GetPartitionKey(OperationContext context, Observation observation)
    {
        try
        {
            return partitionKeyPolicy.ResolveWritePartitionKey(context, observation);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException(
                $"Observation '{EntityType}:{observation.Id}' did not resolve a partition key from {partitionKeyPolicy.Description}.",
                ex);
        }
    }

    bool TryResolveSinglePartitionKey(string id, out string partitionKey)
    {
        partitionKey = string.Empty;
        if (!partitionKeysByObservationId.TryGetValue(id, out var partitionKeys) || partitionKeys.Count == 0)
            return false;

        if (partitionKeys.Count > 1)
        {
            throw new InvalidOperationException(
                $"Observation '{EntityType}:{id}' exists in multiple partitions and cannot be loaded by id alone.");
        }

        partitionKey = partitionKeys.First();
        return true;
    }

    void TrackObservation(string id, string partitionKey)
    {
        if (!partitionKeysByObservationId.TryGetValue(id, out var partitionKeys))
        {
            partitionKeys = new(StringComparer.Ordinal);
            partitionKeysByObservationId[id] = partitionKeys;
        }
        partitionKeys.Add(partitionKey);
    }

    static string CreateKey(string id, string partitionKey) => $"{partitionKey}::{id}";

}

internal enum EntityTransitionOperationCommitPhase
{
    BeforeAtomicCommit = 0,
    AfterAtomicCommitBeforeReturn = 1
}
