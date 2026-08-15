namespace Cohesive.Adapters.Cosmos;

/// <summary>Cosmos persistence conventions for a target-deduplicating domain-event inbox.</summary>
public sealed record CosmosDomainEventInboxOptions
{
    /// <summary>Default document discriminator.</summary>
    public const string DefaultDocumentKind = "domainEventInbox";

    /// <summary>Document discriminator retained with every inbox entry.</summary>
    public string DocumentKind { get; init; } = DefaultDocumentKind;

    internal static CosmosDomainEventInboxOptions RequireValid(CosmosDomainEventInboxOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.DocumentKind);
        return options;
    }
}
