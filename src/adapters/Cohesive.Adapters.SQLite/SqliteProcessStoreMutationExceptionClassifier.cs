using Cohesive.Storage.Processes;
using Microsoft.Data.Sqlite;

namespace Cohesive.Adapters.SQLite;

/// <summary>Preserves ambiguous native/cancellation failures while allowing local validation failures to surface.</summary>
public sealed class SqliteProcessStoreMutationExceptionClassifier : IProcessStoreMutationExceptionClassifier
{
    /// <summary>Shared stateless classifier for the SQLite Process store.</summary>
    public static SqliteProcessStoreMutationExceptionClassifier Instance { get; } = new();
    SqliteProcessStoreMutationExceptionClassifier() { }

    /// <inheritdoc />
    public ProcessStoreMutationExceptionClassification Classify(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception is SqliteException or OperationCanceledException
            ? ProcessStoreMutationExceptionClassification.Ambiguous
            : ProcessStoreMutationExceptionClassification.NotAmbiguous;
    }
}
