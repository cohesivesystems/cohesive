using Cohesive.Relations.Model;

namespace Cohesive.Processes.Runtime;

/// <summary>
/// Checkpoint repository used by the process runtime to persist execution state.
/// </summary>
public interface IProcessCheckpointRepository
{
    /// <summary>
    /// Persists process checkpoint.
    /// </summary>
    Task SaveCheckpointAsync(OperationContext context, ProcessCheckpoint checkpoint);

    /// <summary>
    /// Loads process checkpoint by id.
    /// </summary>
    Task<ProcessCheckpoint?> LoadCheckpointAsync(OperationContext context, string processId);
}

/// <summary>
/// Transaction gateway used by the process runtime to execute work within a storage transaction.
/// </summary>
public interface IProcessTransactionGateway
{
    /// <summary>
    /// Executes callback within the declared transaction scope.
    /// </summary>
    Task<TResult> ExecuteInTransactionAsync<TResult>(OperationContext context, ProcessTransactionScope scope, Func<OperationContext, Task<TResult>> action, ProcessIsolationLevel? isolationLevel = null);
}

/// <summary>
/// Convenience contract for runtimes that use the same adapter for loading and persistence.
/// </summary>
public interface IProcessRuntimeStorage
    : IProcessEntityRepository,
      IProcessCheckpointRepository,
      IProcessTransactionGateway;


/// <summary>
/// In-memory storage substrate for process runtime testing (<see cref="IProcessEntityRepository"/>, <see cref="IProcessCheckpointRepository"/>, <see cref="IProcessTransactionGateway"/>).
/// </summary>
public sealed class InMemoryProcessStorageAdapter : IProcessRuntimeStorage
{
    readonly Lock gate = new();
    Dictionary<ProcessEntityRef, ProcessEntitySnapshot> snapshots = [];
    Dictionary<string, ProcessCheckpoint> checkpoints = new(StringComparer.Ordinal);
    List<PersistedProcessEffect> persistedEffects = [];
    readonly Dictionary<ProcessEntityRef, int> queuedConflicts = [];

    /// <summary>
    /// Current persisted effect list.
    /// </summary>
    public IReadOnlyList<PersistedProcessEffect> PersistedEffects
    {
        get
        {
            lock (gate)
                return [.. persistedEffects];
        }
    }

    /// <summary>
    /// Seeds or replaces entity snapshot.
    /// </summary>
    public void SeedEntity(ProcessEntityRef entity, EntityState state, long version = 0, ProcessEntityConcurrencyToken? concurrencyToken = null)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(state);

        lock (gate)
            snapshots[entity] = new(entity, state, concurrencyToken ?? ProcessEntityConcurrencyToken.FromVersion(version));
    }

    /// <summary>
    /// Queues synthetic conflicts for the next N commits for this entity.
    /// </summary>
    public void QueueConflict(ProcessEntityRef entity, int count = 1)
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (count <= 0)
            throw new SemanticRuleViolationException("Conflict count must be greater than zero.");

        lock (gate)
            queuedConflicts[entity] = queuedConflicts.GetValueOrDefault(entity) + count;
    }

    /// <inheritdoc />
    public Task<ProcessEntitySnapshot> Create(OperationContext context, ProcessEntityRef entity, EntityState state, string processId)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(processId);
        context.CancellationToken.ThrowIfCancellationRequested();

        lock (gate)
        {
            if (snapshots.ContainsKey(entity))
                throw new SemanticRuleViolationException($"Entity '{entity.EntityType}:{entity.EntityId}' already exists in process storage.");

            var snapshot = new ProcessEntitySnapshot(
                entity: entity,
                state: state,
                concurrencyToken: ProcessEntityConcurrencyToken.FromVersion(state.Version));
            snapshots[entity] = snapshot;
            return Task.FromResult(snapshot);
        }
    }

    /// <inheritdoc />
    public Task<ProcessEntitySnapshot> Get(OperationContext context, ProcessEntityRef entity, ProcessEntityReadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(entity);
        context.CancellationToken.ThrowIfCancellationRequested();

        lock (gate)
        {
            if (!snapshots.TryGetValue(entity, out var snapshot))
                throw new SemanticRuleViolationException($"Entity '{entity.EntityType}:{entity.EntityId}' was not found in process storage.");

            if (options?.ExpectedVersion is { } expectedVersion && snapshot.Version != expectedVersion)
            {
                throw new ProcessConcurrencyConflictException(
                    $"Entity '{entity.EntityType}:{entity.EntityId}' expected version '{expectedVersion}' but found '{snapshot.Version}'.");
            }

            if (options?.Fields is null)
                return Task.FromResult(snapshot);

            return Task.FromResult(new ProcessEntitySnapshot(
                entity: snapshot.Entity,
                state: ProjectState(snapshot.State, options.Fields),
                concurrencyToken: snapshot.ConcurrencyToken,
                loadedFields: options.Fields));
        }
    }

    /// <inheritdoc />
    public Task Update(
        OperationContext context,
        ProcessEntityRef entity,
        TransitionResult transition,
        string processId,
        ProcessEntityWriteOptions options
        )
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(transition);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(processId);
        context.CancellationToken.ThrowIfCancellationRequested();

        lock (gate)
        {
            if (queuedConflicts.TryGetValue(entity, out var pendingConflicts) && pendingConflicts > 0)
            {
                if (pendingConflicts == 1)
                    queuedConflicts.Remove(entity);
                else
                    queuedConflicts[entity] = pendingConflicts - 1;

                throw new ProcessConcurrencyConflictException($"Synthetic concurrency conflict queued for entity '{entity.EntityType}:{entity.EntityId}'.");
            }

            if (!snapshots.TryGetValue(entity, out var current))
                throw new SemanticRuleViolationException($"Entity '{entity.EntityType}:{entity.EntityId}' was not found in process storage.");

            if (current.ConcurrencyToken != options.ExpectedConcurrencyToken)
            {
                throw new ProcessConcurrencyConflictException(
                    $"Entity '{entity.EntityType}:{entity.EntityId}' expected concurrency token '{options.ExpectedConcurrencyToken}' but found '{current.ConcurrencyToken}'.");
            }

            var writeFields = options.Fields;
            var persistedState = writeFields is null
                ? transition.NewState
                : MergeState(
                    currentState: current.State,
                    updatedState: transition.NewState,
                    fields: writeFields,
                    version: transition.NewVersion
                    );

            snapshots[entity] = new(
                entity,
                persistedState,
                ProcessEntityConcurrencyToken.FromVersion(transition.NewVersion));
            foreach (var effect in transition.Effects)
            {
                persistedEffects.Add(new(
                    ProcessId: processId,
                    Entity: entity,
                    TransitionName: transition.TransitionName,
                    Request: effect,
                    PersistedAtUtc: context.UtcNow));
            }
        }

        return Task.CompletedTask;
    }

    static EntityState ProjectState(EntityState state, IReadOnlySet<string> fields)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(fields);

        Dictionary<string, ObservationValue> projected = new(StringComparer.Ordinal);
        foreach (var field in fields)
        {
            if (state.Fields.TryGetValue(field, out var value))
                projected[field] = value;
        }

        return new EntityState(new Observation(
            shapeId: state.Observation.ShapeId,
            id: state.EntityId.Value,
            fields: projected,
            version: state.Version));
    }

    static EntityState MergeState(EntityState currentState, EntityState updatedState, IReadOnlySet<string> fields, long version)
    {
        ArgumentNullException.ThrowIfNull(currentState);
        ArgumentNullException.ThrowIfNull(updatedState);
        ArgumentNullException.ThrowIfNull(fields);

        Dictionary<string, ObservationValue> merged = new(currentState.Fields, StringComparer.Ordinal);
        foreach (var field in fields)
        {
            if (updatedState.Fields.TryGetValue(field, out var value))
                merged[field] = value;
            else
                merged.Remove(field);
        }

        return new(new(
            shapeId: updatedState.Observation.ShapeId,
            id: updatedState.EntityId.Value,
            fields: merged,
            version: version
            )
        );
    }

    /// <inheritdoc />
    public Task SaveCheckpointAsync(OperationContext context, ProcessCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(checkpoint);
        context.CancellationToken.ThrowIfCancellationRequested();

        lock (gate)
            checkpoints[checkpoint.ProcessId] = checkpoint;

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<ProcessCheckpoint?> LoadCheckpointAsync(OperationContext context, string processId)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(processId);
        context.CancellationToken.ThrowIfCancellationRequested();

        lock (gate)
        {
            checkpoints.TryGetValue(processId, out var checkpoint);
            return Task.FromResult(checkpoint);
        }
    }

    /// <inheritdoc />
    public async Task<TResult> ExecuteInTransactionAsync<TResult>(
        OperationContext context,
        ProcessTransactionScope scope,
        Func<OperationContext, Task<TResult>> action,
        ProcessIsolationLevel? isolationLevel = null
        )
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(action);
        context.CancellationToken.ThrowIfCancellationRequested();

        Dictionary<ProcessEntityRef, ProcessEntitySnapshot> snapshotBackup;
        Dictionary<string, ProcessCheckpoint> checkpointBackup;
        List<PersistedProcessEffect> effectsBackup;
        lock (gate)
        {
            snapshotBackup = new(snapshots);
            checkpointBackup = new(checkpoints, StringComparer.Ordinal);
            effectsBackup = [.. persistedEffects];
        }

        try
        {
            return await action(context).ConfigureAwait(false);
        }
        catch
        {
            lock (gate)
            {
                snapshots = snapshotBackup;
                checkpoints = checkpointBackup;
                persistedEffects = effectsBackup;
            }

            throw;
        }
    }
}
