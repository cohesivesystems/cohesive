using Cohesive.Storage.Processes;
using Npgsql;

namespace Cohesive.Adapters.Postgres;

/// <summary>Classifies PostgreSQL Process-store failures by whether the transaction commit may have succeeded.</summary>
/// <remarks>
/// Provider and cancellation failures can cross the commit acknowledgement boundary and therefore remain
/// ambiguous. Exceptions produced locally by canonical serialization, validation, or configured limits occur
/// before commit and are safe to propagate without retrying an allegedly unknown outcome.
/// </remarks>
public sealed class PostgresProcessStoreMutationExceptionClassifier : IProcessStoreMutationExceptionClassifier
{
    /// <summary>Shared stateless PostgreSQL mutation classifier.</summary>
    public static PostgresProcessStoreMutationExceptionClassifier Instance { get; } = new();

    PostgresProcessStoreMutationExceptionClassifier()
    {
    }

    /// <inheritdoc />
    public ProcessStoreMutationExceptionClassification Classify(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception is NpgsqlException or OperationCanceledException
            ? ProcessStoreMutationExceptionClassification.Ambiguous
            : ProcessStoreMutationExceptionClassification.NotAmbiguous;
    }
}
