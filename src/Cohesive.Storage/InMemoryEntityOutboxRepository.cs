using Cohesive.Relations.Mapping;
using Cohesive.Relations.Model;
using Cohesive.Relations.Queries;
using Cohesive.Transitions.Model;

namespace Cohesive.Storage;

/// <summary>
/// In-memory observation repository with atomic outbox support for one logical observation type.
/// </summary>
public sealed class InMemoryEntityOutboxRepository : IEntityOutboxRepository, IEntityQueryRepository
{
    readonly Lock gate = new();
    readonly Dictionary<string, EntitySnapshot> snapshotsByKey = new(StringComparer.Ordinal);
    readonly Dictionary<string, HashSet<string>> partitionKeysByObservationId = new(StringComparer.Ordinal);
    readonly List<EntityOutboxMessage> outboxMessages = [];
    readonly EntityDefinition entityDefinition;
    readonly EntityPartitionKeyPolicy partitionKeyPolicy;
    long nextConcurrencyVersion;

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

    public EntityDefinition EntityDefinition => entityDefinition;

    public ShapeMappingContext MappingContext { get; }

    public string EntityType => entityDefinition.Shape.Id.Value;

    public IReadOnlyList<EntityOutboxMessage> OutboxMessages
    {
        get
        {
            lock (gate)
                return [.. outboxMessages];
        }
    }

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

    public Task<EntityQueryResponse<EntitySnapshot>> Query(OperationContext context, EntityQuery query)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(query);
        context.ThrowIfCancellationRequested();

        EntitySnapshot[] snapshots;
        lock (gate)
            snapshots = [.. snapshotsByKey.Values];

        var filtered = snapshots
            .Where(snapshot => query.Predicate is null || EntityPredicateEvaluator.Evaluate(snapshot.Entity, query.Predicate))
            .ToArray();

        IReadOnlyList<EntitySnapshot> rows = [];
        QueryPageInfo? pageInfo = null;
        if (query.IncludeRows)
        {
            rows = [.. ApplyWindow(filtered, query.Window).Select(snapshot => Project(snapshot, query.Fields?.Fields))];
            pageInfo = CreatePageInfo(query.Window, filtered.Length, rows.Count);
        }

        var aggregations = query.Aggregations is null
            ? null
            : AggregationPlanEvaluator.Evaluate(
                snapshots.Select(static snapshot => snapshot.Entity),
                new(query.Aggregations.Roots, query.Predicate));

        return Task.FromResult(new EntityQueryResponse<EntitySnapshot>(rows, pageInfo, aggregations));
    }

    static IEnumerable<EntitySnapshot> ApplyWindow(IEnumerable<EntitySnapshot> snapshots, ResultPageOptions? window)
    {
        var results = snapshots;

        if (window?.OrderBy is { Length: > 0 } orderBy)
        {
            IOrderedEnumerable<EntitySnapshot>? ordered = null;
            foreach (var order in orderBy)
            {
                ordered = ordered is null
                    ? ApplyPrimaryOrdering(results, order)
                    : ApplySecondaryOrdering(ordered, order);
            }

            results = ordered ?? results;
        }

        if (window?.Cursor is not null)
            throw new NotSupportedException("In-memory entity repositories do not yet support cursor page resumption.");

        if (window?.EffectiveMode == ResultPaginationMode.Offset && window.Offset is { } offset and > 0)
            results = results.Skip(offset);

        if (window?.Limit is { } limit)
        {
            if (limit < 0)
                throw new ArgumentOutOfRangeException(nameof(window), limit, "Read-query limit must be non-negative.");

            results = results.Take(limit);
        }

        return results;
    }

    static QueryPageInfo CreatePageInfo(ResultPageOptions? window, int totalCount, int returnedCount)
    {
        var offset = window?.EffectiveMode == ResultPaginationMode.Offset ? window.Offset ?? 0 : 0;
        var limit = window?.Limit;
        var hasMore = limit is not null && offset + returnedCount < totalCount;

        return new(
            TotalCount: totalCount,
            NextCursor: null,
            Offset: window?.EffectiveMode == ResultPaginationMode.Offset ? offset : null,
            Limit: limit,
            HasMore: hasMore);
    }

    public IObservationStream GetChangeStream(string processorName, DateTimeOffset? startTime = null) =>
        throw new NotSupportedException("In-memory observation repository does not implement change streams.");

    public IObservationStream GetOutboxStream(string processorName, string? streamName = null, DateTimeOffset? startTime = null) =>
        throw new NotSupportedException("In-memory observation repository does not implement outbox streams.");

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

    static IOrderedEnumerable<EntitySnapshot> ApplyPrimaryOrdering(IEnumerable<EntitySnapshot> source, QueryOrderBy order) =>
        order.Descending
            ? source.OrderByDescending(static snapshot => snapshot.Entity, ObservationOrderingComparer.ForField(order.Path))
            : source.OrderBy(static snapshot => snapshot.Entity, ObservationOrderingComparer.ForField(order.Path));

    static IOrderedEnumerable<EntitySnapshot> ApplySecondaryOrdering(IOrderedEnumerable<EntitySnapshot> source, QueryOrderBy order) =>
        order.Descending
            ? source.ThenByDescending(static snapshot => snapshot.Entity, ObservationOrderingComparer.ForField(order.Path))
            : source.ThenBy(static snapshot => snapshot.Entity, ObservationOrderingComparer.ForField(order.Path));

    sealed class ObservationOrderingComparer(FieldPath path) : IComparer<Observation>
    {
        public int Compare(Observation? x, Observation? y)
        {
            if (ReferenceEquals(x, y))
                return 0;
            if (x is null)
                return -1;
            if (y is null)
                return 1;

            var leftResolved = TryResolveField(x, path, out var leftValue, out var leftExists);
            var rightResolved = TryResolveField(y, path, out var rightValue, out var rightExists);
            if (!leftResolved || !rightResolved)
            {
                throw new NotSupportedException(
                    $"In-memory observation ordering does not support field path '{path}'.");
            }

            if (!leftExists && !rightExists)
                return 0;
            if (!leftExists)
                return -1;
            if (!rightExists)
                return 1;

            return CompareObservationValues(leftValue, rightValue);
        }

        public static ObservationOrderingComparer ForField(FieldPath path) => new(path);

        static bool TryResolveField(Observation observation, FieldPath field, out ObservationValue value, out bool exists)
        {
            value = ObservationValue.FromObject(observation.Fields);
            exists = true;

            foreach (var segment in field.Segments)
            {
                switch (segment.Kind)
                {
                    case SegmentKind.Field:
                        if (value.Kind != ObservationValueKind.Object
                            || value.Fields is null
                            || !value.Fields.TryGetValue(segment.Segment!, out value))
                        {
                            value = default;
                            exists = false;
                            return true;
                        }

                        break;
                    case SegmentKind.Element:
                        throw new NotSupportedException(
                            $"In-memory observation ordering does not support element segment '{field}'.");
                    default:
                        throw new InvalidOperationException(
                            $"Unsupported field-path segment kind '{segment.Kind}'.");
                }
            }

            return true;
        }

        static int CompareObservationValues(ObservationValue left, ObservationValue right)
        {
            if (ObservationValue.DeepEquals(left, right))
                return 0;

            if (left.TryGetDateTimeOffset(out var leftDate) && right.TryGetDateTimeOffset(out var rightDate))
                return leftDate.CompareTo(rightDate);

            if (left.TryGetDecimal(out var leftDecimal) && right.TryGetDecimal(out var rightDecimal))
                return leftDecimal.CompareTo(rightDecimal);

            if (left.TryGetDouble(out var leftDouble) && right.TryGetDouble(out var rightDouble))
                return leftDouble.CompareTo(rightDouble);

            if (left.TryGetBoolean(out var leftBool) && right.TryGetBoolean(out var rightBool))
                return leftBool.CompareTo(rightBool);

            return string.CompareOrdinal(left.ToString(), right.ToString());
        }
    }
}
