namespace Cohesive.Relations.Queries;

/// <summary>
/// An executable query that produces a value.
/// </summary>
public interface IExecutableQuery
{
    /// <summary>
    /// Executes the query against the configured read-repository registry.
    /// </summary>
    /// <param name="context">Operation context controlling cancellation and ambient execution metadata.</param>
    /// <param name="repositoryRegistry">Repository registry used to resolve read sources during query execution.</param>
    Task<object?> ExecuteAsync(OperationContext context, IReadRepositoryRegistry repositoryRegistry);
}

/// <summary>
/// An executable that produces a value.
/// </summary>
/// <typeparam name="TResult">The type of the query result.</typeparam>
public sealed class ExecutableQuery<TResult>(Func<OperationContext, IReadRepositoryRegistry, Task<TResult>> executeAsync) : IExecutableQuery
{
    /// <summary>
    /// Executes the query.
    /// </summary>
    /// <param name="context">Operation context.</param>
    /// <param name="repositoryRegistry">Repository registry used to resolve read sources during query execution.</param>
    public Task<TResult> ExecuteAsync(OperationContext context, IReadRepositoryRegistry repositoryRegistry) =>
        executeAsync(Guard.RequireNotNull(context), Guard.RequireNotNull(repositoryRegistry));

    async Task<object?> IExecutableQuery.ExecuteAsync(OperationContext context, IReadRepositoryRegistry repositoryRegistry) =>
        await ExecuteAsync(context, repositoryRegistry).ConfigureAwait(false);
}