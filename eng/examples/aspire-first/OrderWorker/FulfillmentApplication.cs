using Cohesive.Host.Composition;

namespace AspireFirst.Orders;

/// <summary>One offline composition shared by the AppHost and worker; no connection or runtime is created.</summary>
/// <remarks>Each program owns its instance. Typed references connect declarations; CLR names are not semantic IDs.</remarks>
public sealed class FulfillmentApplication
{
    /// <summary>Constructs the connected declarations, freezing domain and query catalogs after construction.</summary>
    public FulfillmentApplication()
    {
        var model = new ApplicationModelBuilder()
            .Add<OrderApi>().Add<FulfillmentProcess>()
            .Add<FulfillmentQueries>().Add<FulfillmentStorage>().Add<FulfillmentDomain>()
            .Add<OrderTransitions>().Add<InventoryTransitions>().Add<ReservationAvailabilityQueryBindings>()
            .Build();
        Domain = model.Get<FulfillmentDomain>();
        Queries = model.Get<FulfillmentQueries>();
        Storage = model.Get<FulfillmentStorage>();
        Orders = model.Get<OrderTransitions>();
        Inventory = model.Get<InventoryTransitions>();
        Api = model.Get<OrderApi>();
        Process = model.Get<FulfillmentProcess>();
        Availability = model.Get<ReservationAvailabilityQueryBindings>();
        Services = OrderEndpoints.Define(this);
    }
    /// <summary>Partial API implementation recipes shared by host and infrastructure preparation.</summary>
    internal Cohesive.Adapters.AspNet.Services.ServiceApiBindingsBuilder Services { get; }
    /// <summary>Query capability requirements projected from the API implementations.</summary>
    public Cohesive.Api.Execution.Services.ServiceDependencies StorageQueries => Services.Plan.QueryDependencies;
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
