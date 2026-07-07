namespace Cohesive.Processes.Model;

/// <summary>
/// Non-generic runtime contract for authored entity creates.
/// </summary>
public interface IProcessEntityCreateInvocation
{
    /// <summary>
    /// Executes the entity create against the process entity repository.
    /// </summary>
    Task<object?> ExecuteAsync(OperationContext context, IProcessEntityRepository entityRepository, string processId);
}

/// <summary>
/// Authored entity create that produces a typed value.
/// </summary>
public sealed class ProcessEntityCreate<TResult> : IProcessEntityCreateInvocation
{
    readonly Func<OperationContext, IProcessEntityRepository, string, Task<TResult>> executeAsync;

    internal ProcessEntityCreate(Func<OperationContext, IProcessEntityRepository, string, Task<TResult>> executeAsync)
    {
        this.executeAsync = Guard.RequireNotNull(executeAsync);
    }

    /// <summary>
    /// Executes the typed entity create.
    /// </summary>
    public Task<TResult> ExecuteAsync(OperationContext context, IProcessEntityRepository entityRepository, string processId) =>
        executeAsync(
            Guard.RequireNotNull(context),
            Guard.RequireNotNull(entityRepository),
            Guard.RequireNotNullOrWhiteSpace(processId));

    async Task<object?> IProcessEntityCreateInvocation.ExecuteAsync(OperationContext context, IProcessEntityRepository entityRepository, string processId) =>
        await ExecuteAsync(context, entityRepository, processId).ConfigureAwait(false);
}

/// <summary>
/// Factory helpers for process-native entity creates.
/// </summary>
public static class ProcessEntityCreate
{
    /// <summary>
    /// Creates an entity state by id and returns the typed entity snapshot.
    /// </summary>
    public static ProcessEntityCreate<EntitySnapshot<TEntity>> Create<TEntity>(
        TEntity entity,
        string entityId,
        object? stateObject = null,
        string? partitionKey = null,
        long version = 0)
        where TEntity : Entity
        => Create(
            entity: entity,
            entityId: entityId,
            stateObject: stateObject,
            project: static snapshot => snapshot,
            partitionKey: partitionKey,
            version: version);

    /// <summary>
    /// Creates an entity state by id and projects it to a typed DTO.
    /// </summary>
    public static ProcessEntityCreate<TResult> Create<TEntity, TResult>(
        this TEntity entity,
        string entityId,
        object? stateObject,
        Func<EntitySnapshot<TEntity>, TResult> project,
        string? partitionKey = null,
        long version = 0)
        where TEntity : Entity
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);
        ArgumentNullException.ThrowIfNull(project);

        return new(async (context, entityRepository, processId) =>
        {
            var entityRef = new ProcessEntityRef(
                entityType: entity.Definition.Name.Value,
                entityId: entityId,
                partitionKey: partitionKey);

            var state = entity.CreateState(entityId, stateObject, version);
            _ = await entityRepository
                .Create(context, entityRef, state, processId)
                .ConfigureAwait(false);

            return project(new EntitySnapshot<TEntity>(entity, state));
        });
    }
}
