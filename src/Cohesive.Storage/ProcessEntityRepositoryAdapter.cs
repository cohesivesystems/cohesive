using Cohesive.Processes.Model;
using Cohesive.Processes.Runtime;
using Cohesive.Relations.Model;
using Cohesive.Transitions.Authoring;
using Cohesive.Transitions.Model;

namespace Cohesive.Storage;

/// <summary>
/// Adapts observation repositories to the process entity repository contract.
/// </summary>
public sealed class ProcessEntityRepositoryAdapter : IProcessEntityRepository
{
    readonly IEntityRepository repository;
    readonly IEntityOutboxRepository? outboxRepository;
    readonly EntityPartitionKeyPolicy partitionKeyPolicy;
    readonly ObservationProcessEntityRepositoryOptions options;

    /// <summary>
    /// Creates a process entity repository over an observation repository.
    /// </summary>
    public ProcessEntityRepositoryAdapter(
        IEntityRepository repository,
        EntityPartitionKeyPolicy? partitionKeyPolicy = null,
        ObservationProcessEntityRepositoryOptions? options = null
        ) : this(repository, repository as IEntityOutboxRepository, partitionKeyPolicy, options)
    {
    }

    /// <summary>
    /// Creates a process entity repository over an observation repository with outbox support.
    /// </summary>
    public ProcessEntityRepositoryAdapter(
        IEntityOutboxRepository repository,
        EntityPartitionKeyPolicy? partitionKeyPolicy = null,
        ObservationProcessEntityRepositoryOptions? options = null
        ) : this(repository, repository, partitionKeyPolicy, options)
    {
    }

    ProcessEntityRepositoryAdapter(
        IEntityRepository repository,
        IEntityOutboxRepository? outboxRepository,
        EntityPartitionKeyPolicy? partitionKeyPolicy,
        ObservationProcessEntityRepositoryOptions? options
        )
    {
        this.repository = Guard.RequireNotNull(repository);
        this.outboxRepository = outboxRepository;
        this.partitionKeyPolicy = partitionKeyPolicy ?? EntityPartitionKeyPolicy.ObservationId;
        this.options = options ?? new();

        if (this.options.PersistEffectsInOutbox && this.outboxRepository is null)
        {
            throw new InvalidOperationException(
                $"'{nameof(ObservationProcessEntityRepositoryOptions.PersistEffectsInOutbox)}' requires '{nameof(IEntityOutboxRepository)}' support.");
        }

        if (this.outboxRepository is not null
            && !string.Equals(this.outboxRepository.EntityType, this.repository.EntityType, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Observation repository entity type '{this.repository.EntityType}' does not match outbox repository entity type '{this.outboxRepository.EntityType}'.");
        }
    }

    /// <inheritdoc />
    public async Task<ProcessEntitySnapshot> Create(
        OperationContext context,
        ProcessEntityRef entity,
        EntityState state,
        string processId)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(processId);
        context.ThrowIfCancellationRequested();
        EnsureEntityType(entity);

        if (!string.Equals(state.EntityId.Value, entity.EntityId, StringComparison.Ordinal))
        {
            throw new SemanticRuleViolationException($"Created state for entity id '{state.EntityId.Value}', but create target was '{entity.EntityId}'.");
        }

        var existing = await repository
            .TryGet(context, entity.EntityId, ToObservationReadOptions(partitionKey: ResolveReadPartitionKey(context, entity, state.Observation)))
            .ConfigureAwait(false);
        if (existing is not null)
            throw new SemanticRuleViolationException($"Entity '{entity.EntityType}:{entity.EntityId}' already exists in process storage.");

        var write = new EntityWriteRequest(Entity: state.Observation);
        try
        {
            var snapshot = await repository.Upsert(context, write).ConfigureAwait(false);
            return new(
                entity: entity,
                state: state,
                concurrencyToken: ToProcessToken(snapshot.ConcurrencyToken)
                );
        }
        catch (ObservationConcurrencyConflictException ex)
        {
            throw new ProcessConcurrencyConflictException(ex.Message);
        }
    }

    /// <inheritdoc />
    public async Task<ProcessEntitySnapshot> Get(
        OperationContext context,
        ProcessEntityRef entity,
        ProcessEntityReadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(entity);
        context.ThrowIfCancellationRequested();
        EnsureEntityType(entity);

        try
        {
            var snapshot = await repository
                .TryGet(context, entity.EntityId, ToObservationReadOptions(options, ResolveReadPartitionKey(context, entity)))
                .ConfigureAwait(false);
            if (snapshot is null)
                throw new SemanticRuleViolationException($"Entity '{entity.EntityType}:{entity.EntityId}' was not found in process storage.");

            return new(
                entity: entity,
                state: new(snapshot.Entity),
                concurrencyToken: ToProcessToken(snapshot.ConcurrencyToken),
                loadedFields: snapshot.LoadedFields
                );
        }
        catch (ObservationConcurrencyConflictException conflict)
        {
            throw new ProcessConcurrencyConflictException(conflict.Message);
        }
        catch (InvalidOperationException)
        {
            throw CreateEntityNotFoundException(entity);
        }
    }

    /// <inheritdoc />
    public async Task Update(
        OperationContext context,
        ProcessEntityRef entity,
        TransitionResult transition,
        string processId,
        ProcessEntityWriteOptions options)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(transition);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(processId);
        context.ThrowIfCancellationRequested();
        EnsureEntityType(entity);
        EnsureTransitionMatches(entity, transition);

        var writeFields = options.Fields;
        var writeObservation = writeFields is null
            ? transition.NewState.Observation
            : await LoadAndMergeObservationAsync(
                context,
                entity,
                options.ExpectedConcurrencyToken,
                transition.NewState.Observation,
                transition.NewVersion,
                writeFields)
                .ConfigureAwait(false);
        var partitionKey = ResolveWritePartitionKey(context, entity, writeObservation);

        var write = new EntityWriteRequest(Entity: writeObservation, ExpectedConcurrencyToken: ToConcurrencyToken(options.ExpectedConcurrencyToken));
        try
        {
            if (this.options.PersistEffectsInOutbox && transition.Effects.Count > 0)
            {
                await outboxRepository!.UpsertWithOutbox(
                    context,
                    new EntityOutboxCommit(
                        Write: write,
                        Messages: CreateOutboxMessages(context, entity, transition, processId, partitionKey)))
                    .ConfigureAwait(false);
                return;
            }

            await repository.Upsert(context, write).ConfigureAwait(false);
        }
        catch (ObservationConcurrencyConflictException conflict)
        {
            throw new ProcessConcurrencyConflictException(conflict.Message);
        }
        catch (InvalidOperationException)
        {
            throw CreateEntityNotFoundException(entity);
        }
    }

    async Task<Observation> LoadAndMergeObservationAsync(
        OperationContext context,
        ProcessEntityRef entity,
        ProcessEntityConcurrencyToken expectedConcurrencyToken,
        Observation updatedObservation,
        long version,
        IReadOnlySet<string> fields)
    {
        var current = await repository
            .TryGet(
                context,
                entity.EntityId,
                ToObservationReadOptions(
                    read: new(expectedConcurrencyToken: expectedConcurrencyToken),
                    partitionKey: ResolveReadPartitionKey(context, entity, updatedObservation)))
            .ConfigureAwait(false);

        if (current is null)
            throw new SemanticRuleViolationException($"Entity '{entity.EntityType}:{entity.EntityId}' was not found in process storage.");

        Dictionary<string, ObservationValue> merged = new(current.Entity.Fields, StringComparer.Ordinal);
        foreach (var field in fields)
        {
            if (updatedObservation.Fields.TryGetValue(field, out var value))
                merged[field] = value;
            else
                merged.Remove(field);
        }

        return new Observation(
            shapeId: updatedObservation.ShapeId,
            id: updatedObservation.Id,
            fields: merged,
            version: version,
            lineage: updatedObservation.Lineage);
    }

    IReadOnlyList<EntityOutboxMessage> CreateOutboxMessages(
        OperationContext context,
        ProcessEntityRef entity,
        TransitionResult transition,
        string processId,
        string partitionKey)
    {
        List<EntityOutboxMessage> messages = new(transition.Effects.Count);
        for (var index = 0; index < transition.Effects.Count; index++)
        {
            var effect = transition.Effects[index];
            var messageId = BuildOutboxMessageId(processId, entity, transition, index);
            messages.Add(new(
                MessageId: messageId,
                StreamName: options.EffectOutboxStreamName,
                SubjectType: entity.EntityType,
                SubjectId: entity.EntityId,
                PartitionKey: partitionKey,
                Entity: CreateEffectObservation(context, entity, transition, processId, effect, messageId),
                SubjectVersion: transition.NewVersion,
                OccurredAtUtc: context.UtcNow,
                CorrelationId: processId));
        }

        return messages;
    }

    Observation CreateEffectObservation(
        OperationContext context,
        ProcessEntityRef entity,
        TransitionResult transition,
        string processId,
        EffectRequest effect,
        string messageId)
    {
        Dictionary<string, ObservationValue> fields = new(StringComparer.Ordinal)
        {
            ["ProcessId"] = ObservationValue.FromString(processId),
            ["EntityType"] = ObservationValue.FromString(entity.EntityType),
            ["EntityId"] = ObservationValue.FromString(entity.EntityId),
            ["TransitionName"] = ObservationValue.FromString(transition.TransitionName),
            ["RequestName"] = ObservationValue.FromString(effect.Name),
            ["RequestPayload"] = effect.Payload,
            ["PersistedAtUtc"] = ObservationValue.FromDateTimeOffset(context.UtcNow)
        };

        if (effect.Continuation is not null)
            fields["ContinuationTransitionName"] = ObservationValue.FromString(effect.Continuation.TransitionName);

        if (effect.Snapshot is not null)
        {
            fields["SnapshotToken"] = ObservationValue.FromString(effect.Snapshot.Token);
            fields["SnapshotFieldNames"] = ObservationValue.FromImmutableArray(
                [.. effect.Snapshot.FieldNames.Select(ObservationValue.FromString)]);
        }

        return new(
            shapeId: new(options.EffectObservationType),
            id: messageId,
            fields: fields
            );
    }

    void EnsureEntityType(ProcessEntityRef entity)
    {
        if (!MatchesEntityType(entity.EntityType, repository.EntityType))
        {
            throw new SemanticRuleViolationException($"Observation repository for '{repository.EntityType}' cannot serve process entity type '{entity.EntityType}'.");
        }
    }

    static void EnsureTransitionMatches(ProcessEntityRef entity, TransitionResult transition)
    {
        if (!string.Equals(transition.NewState.EntityId.Value, entity.EntityId, StringComparison.Ordinal))
        {
            throw new SemanticRuleViolationException($"Transition '{transition.TransitionName}' produced state for entity id '{transition.NewState.EntityId.Value}', but commit target was '{entity.EntityId}'.");
        }

        if (!MatchesEntityType(entity.EntityType, transition.NewState.Observation.ShapeId.Value))
        {
            throw new SemanticRuleViolationException($"Transition '{transition.TransitionName}' produced state for entity type '{transition.NewState.Observation.ShapeId.Value}', but commit target was '{entity.EntityType}'.");
        }
    }

    static bool MatchesEntityType(string entityType, string observedType) =>
        string.Equals(entityType, observedType, StringComparison.Ordinal)
        || string.Equals(ToCanonicalEntityShapeId(entityType), observedType, StringComparison.Ordinal);

    static string ToCanonicalEntityShapeId(string entityType) => $"shape.entity.{entityType}";

    string? ResolveReadPartitionKey(
        OperationContext context,
        ProcessEntityRef entity,
        Observation? writeObservation = null)
    {
        if (NormalizePartitionKey(entity.PartitionKey) is { } explicitPartitionKey)
            return explicitPartitionKey;

        if (partitionKeyPolicy.TryResolvePointReadPartitionKey(context, entity.EntityId) is { } pointReadPartitionKey)
            return pointReadPartitionKey;

        return writeObservation is not null && TryResolveWritePartitionKey(context, entity, writeObservation, out var writePartitionKey)
            ? writePartitionKey
            : null;
    }

    string ResolveWritePartitionKey(
        OperationContext context,
        ProcessEntityRef entity,
        Observation observation)
    {
        if (NormalizePartitionKey(entity.PartitionKey) is { } explicitPartitionKey)
            return explicitPartitionKey;

        try
        {
            return partitionKeyPolicy.ResolveWritePartitionKey(context, observation);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException(
                $"Process entity '{entity.EntityType}:{entity.EntityId}' did not resolve a partition key from {partitionKeyPolicy.Description}.",
                ex);
        }
    }

    bool TryResolveWritePartitionKey(
        OperationContext context,
        ProcessEntityRef entity,
        Observation observation,
        out string partitionKey)
    {
        try
        {
            partitionKey = ResolveWritePartitionKey(context, entity, observation);
            return true;
        }
        catch (ArgumentException)
        {
            partitionKey = string.Empty;
            return false;
        }
        catch (InvalidOperationException)
        {
            partitionKey = string.Empty;
            return false;
        }
        catch (KeyNotFoundException)
        {
            partitionKey = string.Empty;
            return false;
        }
    }

    static string? NormalizePartitionKey(string? partitionKey) =>
        string.IsNullOrWhiteSpace(partitionKey) ? null : partitionKey.Trim();

    static EntityReadOptions? ToObservationReadOptions(
        ProcessEntityReadOptions? read = null,
        string? partitionKey = null)
    {
        var normalizedPartitionKey = string.IsNullOrWhiteSpace(partitionKey) ? null : partitionKey.Trim();
        if (read is null && normalizedPartitionKey is null)
            return null;

        return new EntityReadOptions(
            fieldSelection: read?.FieldSelection,
            expectedVersion: read?.ExpectedVersion,
            expectedConcurrencyToken: read?.ExpectedConcurrencyToken is { } token ? ToConcurrencyToken(token) : null,
            partitionKey: normalizedPartitionKey);
    }

    static ProcessEntityConcurrencyToken ToProcessToken(EntityConcurrencyToken token) => new(token.Value);

    static EntityConcurrencyToken ToConcurrencyToken(ProcessEntityConcurrencyToken token) => new(token.Value);

    static string BuildOutboxMessageId(
        string processId,
        ProcessEntityRef entity,
        TransitionResult transition,
        int index) =>
        $"{processId}:{entity.EntityType}:{entity.EntityId}:{transition.NewVersion}:{index}";

    static SemanticRuleViolationException CreateEntityNotFoundException(ProcessEntityRef entity) =>
        new($"Entity '{entity.EntityType}:{entity.EntityId}' was not found in process storage.");
}
