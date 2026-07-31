using Cohesive.Relations.Mapping;
using Cohesive.Relations.Model;
using Cohesive.Transitions.Model;

namespace Cohesive.Storage;

/// <summary>
/// In-memory observation repository with atomic outbox support for one logical observation type.
/// </summary>
public sealed class InMemoryEntityOutboxRepository : IEntityOutboxRepository
{
    readonly Lock gate = new();
    readonly Dictionary<string, EntitySnapshot> snapshotsByKey = new(StringComparer.Ordinal);
    readonly Dictionary<string, HashSet<string>> partitionKeysByObservationId = new(StringComparer.Ordinal);
    readonly List<EntityOutboxMessage> outboxMessages = [];
    readonly EntityDefinition entityDefinition;
    readonly EntityPartitionKeyPolicy partitionKeyPolicy;
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
            return Task.FromResult(snapshot);
        }
    }

    /// <summary>Atomically upserts an entity snapshot and appends its outbox messages.</summary>
    public async Task<EntityCommitResult> UpsertWithOutbox(OperationContext context, EntityOutboxCommit commit)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(commit);
        context.ThrowIfCancellationRequested();

        var snapshot = await Upsert(context, commit.Write).ConfigureAwait(false);
        lock (gate)
            outboxMessages.AddRange(commit.Messages);

        return new(snapshot, commit.Messages);
    }

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
