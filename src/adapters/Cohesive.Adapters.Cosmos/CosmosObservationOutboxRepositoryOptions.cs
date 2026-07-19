namespace Cohesive.Adapters.Cosmos;

/// <summary>
/// Options for Cosmos-backed observation repositories and streams.
/// </summary>
public sealed record CosmosObservationOutboxRepositoryOptions
{
    /// <summary>Conventional discriminator for persisted entity documents.</summary>
    public const string DefaultEntityDocumentKind = "entity";

    /// <summary>Conventional discriminator for persisted outbox documents.</summary>
    public const string DefaultOutboxDocumentKind = "outbox";

    /// <summary>
    /// Logical worker instance name used by the Cosmos change-feed processor.
    /// </summary>
    public string InstanceName { get; init; } = Environment.MachineName;

    /// <summary>
    /// Polling interval for lag estimation.
    /// </summary>
    public TimeSpan LagPollingInterval { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Non-empty entity document discriminator. It must differ ordinally from <see cref="OutboxDocumentKind"/> and
    /// must be supplied identically when registering a canonical Cosmos entity source.
    /// </summary>
    public string EntityDocumentKind { get; init; } = DefaultEntityDocumentKind;

    /// <summary>
    /// Non-empty outbox document discriminator. It must differ ordinally from <see cref="EntityDocumentKind"/>.
    /// </summary>
    public string OutboxDocumentKind { get; init; } = DefaultOutboxDocumentKind;

    /// <summary>
    /// Whether to persist trace identifiers when available.
    /// </summary>
    public bool WriteTraceId { get; init; } = true;

    /// <summary>
    /// Whether to persist span identifiers when available.
    /// </summary>
    public bool WriteSpanId { get; init; } = true;

    internal static CosmosObservationOutboxRepositoryOptions RequireValid(
        CosmosObservationOutboxRepositoryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.EntityDocumentKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.OutboxDocumentKind);
        if (string.Equals(options.EntityDocumentKind, options.OutboxDocumentKind, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Cosmos entity and outbox document discriminators must be distinct.",
                nameof(options));
        }

        return options;
    }
}
