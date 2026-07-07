namespace Cohesive.Processes.Model;

/// <summary>
/// Entity reference used by process nodes.
/// </summary>
public sealed record ProcessEntityRef
{
    /// <summary>
    /// Creates an entity reference.
    /// </summary>
    public ProcessEntityRef(string entityType, string entityId, string? partitionKey = null)
    {
        EntityType = Guard.RequireNotNullOrWhiteSpace(entityType);
        EntityId = Guard.RequireNotNullOrWhiteSpace(entityId);
        PartitionKey = partitionKey;
    }

    /// <summary>
    /// Entity type name.
    /// </summary>
    public string EntityType { get; }

    /// <summary>
    /// Entity id.
    /// </summary>
    public string EntityId { get; }

    /// <summary>
    /// Optional partition key.
    /// </summary>
    public string? PartitionKey { get; }
}