using Cohesive.Api.Execution.Services;

namespace AspireFirst.Orders;

/// <summary>Selected service behavior and query roots; native repositories are attached only at host setup.</summary>
public static class FulfillmentService
{
    /// <summary>The shared authoring builder produces a portable service document and retains runtime attachments separately.</summary>
    public static ServiceBuilder Definition { get; } = Service.Define(
            new("fulfillment"), new("1"), FulfillmentProcess.Provenance)
        .Use(new ServiceDependencies()
            .Transition(InventoryTransitions.Reserve)
            .Transition(InventoryTransitions.Release)
            .Transition(OrderTransitions.Submit)
            .Lookup(FulfillmentProcess.StockPointRead, FulfillmentDomain.Inventory,
                key: input => input.Sku,
                result: item => new InventoryAvailability(item is not null, item?.Available ?? 0))
            .Query(FulfillmentQueries.OrderDetails, new("1"), maximumRows: 1000, maximumBytes: 1_000_000)
            .Query(FulfillmentQueries.ReservationAvailability, new("1"), maximumRows: 1000, maximumBytes: 1_000_000))
        .Operation("fulfill").Run(FulfillmentProcess.Definition)
        .ExecuteEphemerally(TimeSpan.FromSeconds(15));
}
