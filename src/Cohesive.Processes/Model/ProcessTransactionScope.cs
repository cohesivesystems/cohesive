namespace Cohesive.Processes.Model;

/// <summary>
/// Transaction scope kinds.
/// </summary>
public enum ProcessTransactionScopeKind
{
    /// <summary>Represents the single entity option.</summary>
    SingleEntity = 0,
    /// <summary>Represents the single partition option.</summary>
    SinglePartition = 1,
    /// <summary>Represents the multi entity option.</summary>
    MultiEntity = 2,
    /// <summary>Represents the database transaction option.</summary>
    DatabaseTransaction = 3,
    /// <summary>Represents the absence of a selected option.</summary>
    None = 4
}

/// <summary>
/// Transaction scope declaration.
/// </summary>
public sealed record ProcessTransactionScope
{
    ProcessTransactionScope(
        ProcessTransactionScopeKind kind,
        string? entityId = null,
        string? partitionKey = null,
        IReadOnlyList<string>? entityIds = null,
        string? connectionName = null
    )
    {
        Kind = kind;
        EntityId = entityId;
        PartitionKey = partitionKey;
        EntityIds = entityIds ?? [];
        ConnectionName = connectionName;
    }

    /// <summary>
    /// Scope kind.
    /// </summary>
    public ProcessTransactionScopeKind Kind { get; }

    /// <summary>
    /// Entity id for single-entity scope.
    /// </summary>
    public string? EntityId { get; }

    /// <summary>
    /// Partition key for single-partition scope.
    /// </summary>
    public string? PartitionKey { get; }

    /// <summary>
    /// Entity ids for multi-entity scope.
    /// </summary>
    public IReadOnlyList<string> EntityIds { get; }

    /// <summary>
    /// Connection name for DB transaction scope.
    /// </summary>
    public string? ConnectionName { get; }

    /// <summary>
    /// Creates a single-entity scope.
    /// </summary>
    public static ProcessTransactionScope SingleEntity(string entityId) => 
        new(ProcessTransactionScopeKind.SingleEntity, entityId: Guard.RequireNotNullOrWhiteSpace(entityId));

    /// <summary>
    /// Creates a single-partition scope.
    /// </summary>
    public static ProcessTransactionScope SinglePartition(string partitionKey) => 
        new(ProcessTransactionScopeKind.SinglePartition, partitionKey: Guard.RequireNotNullOrWhiteSpace(partitionKey));

    /// <summary>
    /// Creates a multi-entity scope.
    /// </summary>
    public static ProcessTransactionScope MultiEntity(IReadOnlyList<string> entityIds)
    {
        ArgumentNullException.ThrowIfNull(entityIds);
        var normalized = entityIds
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();
        if (normalized.Length == 0)
            throw new SemanticRuleViolationException("MultiEntity transaction scope requires at least one entity id.");

        return new(ProcessTransactionScopeKind.MultiEntity, entityIds: normalized);
    }

    /// <summary>
    /// Creates a database transaction scope.
    /// </summary>
    public static ProcessTransactionScope DatabaseTransaction(string connectionName)
    {
        return new(
            kind: ProcessTransactionScopeKind.DatabaseTransaction,
            connectionName: Guard.RequireNotNullOrWhiteSpace(connectionName));
    }

    /// <summary>
    /// Creates an explicit no-transaction scope.
    /// </summary>
    public static ProcessTransactionScope None()
    {
        return new(ProcessTransactionScopeKind.None);
    }
}
