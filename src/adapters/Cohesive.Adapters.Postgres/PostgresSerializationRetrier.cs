using Cohesive.Execution;
using Npgsql;

namespace Cohesive.Adapters.Postgres;

/// <summary>Shared bounded retry policy for PostgreSQL serialization and deadlock conflicts.</summary>
internal static class PostgresSerializationRetrier
{
    const int MaximumAttempts = 8;

    internal static async Task<TResult> ExecuteAsync<TResult>(
        OperationContext context,
        Func<Task<TResult>> operation)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(operation);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await operation().ConfigureAwait(false);
            }
            catch (PostgresException exception) when (
                attempt < MaximumAttempts
                && exception.SqlState is PostgresErrorCodes.SerializationFailure
                    or PostgresErrorCodes.DeadlockDetected)
            {
                context.ThrowIfCancellationRequested();
            }
        }
    }
}
