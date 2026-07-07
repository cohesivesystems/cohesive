namespace Cohesive.Processes.Model;

/// <summary>
/// Non-generic runtime contract for authored entity reads.
/// </summary>
public interface IProcessEntityReadInvocation
{
    /// <summary>
    /// Executes the entity read against the process entity repository.
    /// </summary>
    Task<object?> ExecuteAsync(OperationContext context, IProcessEntityRepository entityRepository);
}

/// <summary>
/// Authored entity read that produces a typed value.
/// </summary>
public sealed class ProcessEntityRead<TResult> : IProcessEntityReadInvocation
{
    readonly Func<OperationContext, IProcessEntityRepository, Task<TResult>> executeAsync;

    internal ProcessEntityRead(Func<OperationContext, IProcessEntityRepository, Task<TResult>> executeAsync)
    {
        this.executeAsync = Guard.RequireNotNull(executeAsync);
    }

    /// <summary>
    /// Executes the typed entity read.
    /// </summary>
    public Task<TResult> ExecuteAsync(OperationContext context, IProcessEntityRepository entityRepository) =>
        executeAsync(Guard.RequireNotNull(context), Guard.RequireNotNull(entityRepository));

    async Task<object?> IProcessEntityReadInvocation.ExecuteAsync(OperationContext context, IProcessEntityRepository entityRepository) =>
        await ExecuteAsync(context, entityRepository).ConfigureAwait(false);
}

/// <summary>
/// Factory helpers for process-native entity reads.
/// </summary>
public static class ProcessEntityRead
{
    /// <summary>
    /// Loads an entity snapshot by id and binds it to the supplied entity definition.
    /// </summary>
    public static ProcessEntityRead<EntitySnapshot<TEntity>> ReadById<TEntity>(
        TEntity entity,
        string entityId,
        string? partitionKey = null,
        ProcessEntityReadOptions? read = null)
        where TEntity : Entity
        =>
            entity.ReadById(entityId: entityId,
            project: static snapshot => snapshot,
            partitionKey: partitionKey,
            read: read);

    /// <summary>
    /// Loads an entity snapshot by id and projects it to a typed DTO.
    /// </summary>
    public static ProcessEntityRead<TResult> ReadById<TEntity, TResult>(
        this TEntity entity,
        string entityId,
        Func<EntitySnapshot<TEntity>, TResult> project,
        string? partitionKey = null,
        ProcessEntityReadOptions? read = null
        ) where TEntity : Entity
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);
        ArgumentNullException.ThrowIfNull(project);

        return new(async (context, entityRepository) =>
        {
            var entityRef = new ProcessEntityRef(
                entityType: entity.Definition.Name.Value,
                entityId: entityId,
                partitionKey: partitionKey);

            var snapshot = await entityRepository
                .Get(context, entityRef, read)
                .ConfigureAwait(false);

            return project(new EntitySnapshot<TEntity>(entity, snapshot.State));
        });
    }
}
