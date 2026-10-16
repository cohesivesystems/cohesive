namespace AspireFirst.Orders;

/// <summary>One offline composition shared by the AppHost and worker; no connection or runtime is created.</summary>
/// <remarks>Each program owns its instance. Typed references connect declarations; CLR names are not semantic IDs.</remarks>
public sealed class FulfillmentApplication
{
    /// <summary>Constructs the connected declarations, freezing domain and query catalogs after construction.</summary>
    public FulfillmentApplication()
    {
        Domain = new();
        Queries = new(Domain);
        Storage = new(Domain);
        Orders = new(Domain);
        Inventory = new(Domain);
        Api = new(Orders);
        Process = new(Domain, Inventory, Orders);
        Service = new(Domain, Queries, Orders, Inventory, Process);
        Availability = new(Queries, Service);
        _ = Domain.Definition;
        _ = Queries.Definition;
        _ = Api.Definition;
    }
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
    /// <summary>Explicit execution and dependency bindings.</summary>
    public FulfillmentService Service { get; }
    /// <summary>Native and composed placement examples.</summary>
    public ReservationAvailabilityQueryBindings Availability { get; }
}
