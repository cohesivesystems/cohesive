using Cohesive.Api.Execution.Services;

namespace AspireFirst.Orders;

/// <summary>Selected service behavior and query roots; native repositories are attached only at host setup.</summary>
public sealed class FulfillmentService
{
    /// <summary>The shared authoring builder produces a portable service document and retains runtime attachments separately.</summary>
    public ServiceBuilder Definition { get; }

    /// <summary>Composes explicit execution policy and exact dependencies.</summary>
    /// <param name="domain">Entity authorities.</param>
    /// <param name="queries">Independent API query roots.</param>
    /// <param name="orders">Order transitions.</param>
    /// <param name="inventory">Inventory transitions.</param>
    /// <param name="process">Generated orchestration.</param>
    public FulfillmentService(FulfillmentDomain domain, FulfillmentQueries queries, OrderTransitions orders, InventoryTransitions inventory, FulfillmentProcess process)
    {
        Definition = Service.Define(
            new("fulfillment"), new("1"), process.Provenance)
            .Use(new ServiceDependencies()
                .Transition(inventory.Reserve)
                .Transition(inventory.Release)
                .Transition(orders.Submit)
                .Lookup(process.StockPointRead, domain.Inventory,
                    key: input => input.Sku,
                    result: item => new InventoryAvailability(item is not null, item?.Available ?? 0))
                .Query(queries.OrderDetails, new("1"), maximumRows: 1000, maximumBytes: 1_000_000)
                .Query(queries.ReservationAvailability, new("1"), maximumRows: 1000, maximumBytes: 1_000_000))
            .Operation("fulfill").Run(process.Definition)
            .ExecuteEphemerally(TimeSpan.FromSeconds(15));
    }
}
