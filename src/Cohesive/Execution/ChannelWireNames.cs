namespace Cohesive.Execution;

/// <summary>Stable wire names for canonical Channel definitions and requirement variants.</summary>
/// <remarks>
/// These values are the persisted authority for Channel discriminators. Provider and protocol names deliberately do
/// not appear in this catalog.
/// </remarks>
public static class ChannelWireNames
{
    /// <summary>Shared execution-definition kind for Channel documents.</summary>
    public const string DefinitionKind = "channel";

    /// <summary>JSON discriminator for the closed Channel exchange family.</summary>
    public const string ExchangeDiscriminator = "$exchange";

    /// <summary>One-way logical exchange.</summary>
    public const string OneWayExchange = "oneWay";

    /// <summary>Two-direction logical Request/Reply exchange.</summary>
    public const string RequestReplyExchange = "requestReply";

    /// <summary>JSON discriminator for the closed Channel requirement family.</summary>
    public const string RequirementDiscriminator = "$requirement";

    /// <summary>Distribution and interaction-shape requirement.</summary>
    public const string TopologyRequirement = "topology";

    /// <summary>Semantic routing and acquisition-isolation requirement.</summary>
    public const string RoutingRequirement = "routing";

    /// <summary>Message, frame, datagram, or byte-stream framing requirement.</summary>
    public const string FramingRequirement = "framing";

    /// <summary>Durability, retention, and replay requirement.</summary>
    public const string PersistenceRequirement = "persistence";

    /// <summary>Durable delivery-progress requirement.</summary>
    public const string ProgressRequirement = "progress";

    /// <summary>Delivery guarantee and ordering requirement.</summary>
    public const string DeliveryRequirement = "delivery";

    /// <summary>Reliability and timeliness requirement.</summary>
    public const string ReliabilityRequirement = "reliability";

    /// <summary>Completion and provider-settlement requirement.</summary>
    public const string SettlementRequirement = "settlement";

    /// <summary>Flow control, completion, and session-continuity requirement.</summary>
    public const string FlowRequirement = "flow";

    /// <summary>Atomic coupling requirement.</summary>
    public const string AtomicityRequirement = "atomicity";

    /// <summary>Transport-security requirement.</summary>
    public const string SecurityRequirement = "security";

    /// <summary>Operating-capacity requirement.</summary>
    public const string LimitRequirement = "limit";

    /// <summary>JSON discriminator for durable Channel progress floors.</summary>
    public const string ProgressFloorDiscriminator = "$floor";

    /// <summary>Replay-cursor progress floor.</summary>
    public const string ReplayCursorFloor = "replayCursor";

    /// <summary>Stable provider-delivery progress floor.</summary>
    public const string ProviderDeliveryFloor = "providerDelivery";

    /// <summary>Target-managed progress floor.</summary>
    public const string TargetManagedFloor = "targetManaged";

    /// <summary>JSON discriminator for pending Channel progress evidence.</summary>
    public const string PendingProgressDiscriminator = "$pending";

    /// <summary>Exact stable-delivery set evidence.</summary>
    public const string StableDeliverySet = "stableDeliverySet";

    /// <summary>Unresolved delivery-gap evidence.</summary>
    public const string UnresolvedGaps = "unresolvedGaps";

    /// <summary>Target-managed pending-delivery snapshot.</summary>
    public const string TargetManagedPending = "targetManaged";
}
