namespace Cohesive.Adapters.Cosmos;

/// <summary>
/// Options for Cosmos-backed observation repositories and streams.
/// </summary>
public sealed record CosmosObservationOutboxRepositoryOptions
{
    /// <summary>
    /// Logical worker instance name used by the Cosmos change-feed processor.
    /// </summary>
    public string InstanceName { get; init; } = Environment.MachineName;

    /// <summary>
    /// Polling interval for lag estimation.
    /// </summary>
    public TimeSpan LagPollingInterval { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Entity document discriminator.
    /// </summary>
    public string EntityDocumentKind { get; init; } = "entity";

    /// <summary>
    /// Outbox document discriminator.
    /// </summary>
    public string OutboxDocumentKind { get; init; } = "outbox";

    /// <summary>
    /// Whether to persist trace identifiers when available.
    /// </summary>
    public bool WriteTraceId { get; init; } = true;

    /// <summary>
    /// Whether to persist span identifiers when available.
    /// </summary>
    public bool WriteSpanId { get; init; } = true;
}
