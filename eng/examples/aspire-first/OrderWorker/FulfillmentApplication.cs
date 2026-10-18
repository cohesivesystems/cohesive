using Cohesive.Host.Composition;

namespace AspireFirst.Orders;

/// <summary>One offline composition shared by the AppHost and worker; no connection or runtime is created.</summary>
/// <remarks>Each program owns its instance. Typed references connect declarations; CLR names are not semantic IDs.</remarks>
public sealed class FulfillmentApplication : ApplicationModel
{
    /// <summary>Offline API binding contributions with explicit native host preparation.</summary>
    public required OrderEndpoints Endpoints { get; init; }
    /// <summary>Public API contract.</summary>
    public required OrderApi Api { get; init; }
    /// <summary>Canonical entity and relationship declarations.</summary>
    public required FulfillmentDomain Domain { get; init; }
    /// <summary>Canonical query declarations.</summary>
    public required FulfillmentQueries Queries { get; init; }
    /// <summary>Offline native storage attachments.</summary>
    public required FulfillmentStorage Storage { get; init; }
    /// <summary>Generated multi-entity process.</summary>
    public required FulfillmentProcess Process { get; init; }
}
