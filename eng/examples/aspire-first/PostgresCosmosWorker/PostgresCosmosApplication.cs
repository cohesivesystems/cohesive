using AspireFirst.Orders;
using Cohesive.Host.Composition;
namespace AspireFirst.PostgresCosmos;

/// <summary>Same domain, queries, processes and API with an explicit heterogeneous storage composition.</summary>
/// <param name="storage">Heterogeneous storage placement.</param>
/// <param name="endpoints">Shared API binding recipes.</param>
/// <param name="api">Shared endpoint contracts.</param>
/// <param name="domain">Shared entity declarations.</param>
/// <param name="queries">Shared logical queries.</param>
public sealed class PostgresCosmosApplication(PostgresCosmosStorage storage, OrderEndpoints endpoints, OrderApi api,
    FulfillmentDomain domain, FulfillmentQueries queries) : ApplicationModel
{
    /// <summary>Selected heterogeneous storage definition.</summary>
    public PostgresCosmosStorage Storage { get; } = storage;
    /// <summary>Shared HTTP implementation recipes.</summary>
    public OrderEndpoints Endpoints { get; } = endpoints;
    /// <summary>Shared API declaration.</summary>
    public OrderApi Api { get; } = api;
    /// <summary>Canonical shared domain.</summary>
    public FulfillmentDomain Domain { get; } = domain;
    /// <summary>Canonical shared queries.</summary>
    public FulfillmentQueries Queries { get; } = queries;
}
