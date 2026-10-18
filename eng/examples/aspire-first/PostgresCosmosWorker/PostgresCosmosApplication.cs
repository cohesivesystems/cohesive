using AspireFirst.Orders;
using Cohesive.Host.Composition;

namespace AspireFirst.PostgresCosmos;

/// <summary>Same domain, queries, processes and API with an explicit heterogeneous storage composition.</summary>
public sealed class PostgresCosmosApplication : ApplicationModel
{
    /// <summary>Selected heterogeneous storage definition.</summary>
    public required PostgresCosmosStorage Storage { get; init; }
    /// <summary>Shared HTTP implementation recipes.</summary>
    public required OrderEndpoints Endpoints { get; init; }
    /// <summary>Shared API declaration.</summary>
    public required OrderApi Api { get; init; }
    /// <summary>Canonical shared entities and their owned transitions.</summary>
    public required FulfillmentDomain Domain { get; init; }
    /// <summary>Canonical shared queries.</summary>
    public required FulfillmentQueries Queries { get; init; }
    /// <summary>Separately registered multi-entity process.</summary>
    public required FulfillmentProcess Process { get; init; }
}
