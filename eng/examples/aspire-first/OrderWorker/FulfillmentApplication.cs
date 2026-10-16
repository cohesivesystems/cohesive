using Cohesive.Host.Composition;

namespace AspireFirst.Orders;

/// <summary>One offline composition shared by the AppHost and worker; no connection or runtime is created.</summary>
/// <remarks>Each program owns its instance. Typed references connect declarations; CLR names are not semantic IDs.</remarks>
public sealed class FulfillmentApplication : ApplicationModel
{
    /// <summary>Exposes the activated application components; dependencies are prepared before this constructor.</summary>
    /// <param name="domain">Shared domain.</param><param name="queries">Query catalog.</param>
    /// <param name="storage">Storage attachments.</param><param name="orders">Order transitions.</param>
    /// <param name="inventory">Inventory transitions.</param><param name="api">API contract.</param>
    /// <param name="process">Generated process.</param><param name="endpoints">Offline API bindings.</param>
    /// <param name="availability">Alternate query placement.</param>
    public FulfillmentApplication(FulfillmentDomain domain, FulfillmentQueries queries, FulfillmentStorage storage,
        OrderTransitions orders, InventoryTransitions inventory, OrderApi api, FulfillmentProcess process,
        OrderEndpoints endpoints, ReservationAvailabilityQueryBindings availability)
    {
        Domain = domain;
        Queries = queries;
        Storage = storage;
        Orders = orders;
        Inventory = inventory;
        Api = api;
        Process = process;
        Endpoints = endpoints;
        Availability = availability;
    }
    /// <summary>Offline API binding contributions with explicit native host preparation.</summary>
    public OrderEndpoints Endpoints { get; }
    /// <summary>Query capability requirements projected from the API implementations.</summary>
    public Cohesive.Api.Execution.Services.ServiceDependencies StorageQueries => Endpoints.Plan.QueryDependencies;
    /// <summary>Public API contract.</summary>
    public OrderApi Api { get; }
    /// <summary>Canonical entity and relationship declarations.</summary>
    public FulfillmentDomain Domain { get; }
    /// <summary>Canonical query declarations.</summary>
    public FulfillmentQueries Queries { get; }
    /// <summary>Offline native storage attachments.</summary>
    public FulfillmentStorage Storage { get; }
    /// <summary>Order transitions.</summary>
    public OrderTransitions Orders { get; }
    /// <summary>Inventory transitions.</summary>
    public InventoryTransitions Inventory { get; }
    /// <summary>Generated multi-entity process.</summary>
    public FulfillmentProcess Process { get; }
    /// <summary>Native and composed placement examples.</summary>
    public ReservationAvailabilityQueryBindings Availability { get; }
}
