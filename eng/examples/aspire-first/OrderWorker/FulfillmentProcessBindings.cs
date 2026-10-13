using Cohesive.Api;
using Cohesive.Api.Execution.Services;
using Cohesive.Identity;
using Cohesive.Execution;
using Cohesive.Storage;

namespace AspireFirst.Orders;

/// <summary>Host-lifetime preparation of the declared process against receipt-capable local repositories.</summary>
/// <remarks>This is single-partition demo composition, not a production authorization policy or recovery worker.</remarks>
public static class FulfillmentProcessBindings
{
    /// <summary>Prepares exact transition links and the finite process interpreter once.</summary>
    /// <param name="orders">Local order authority with atomic receipt support.</param>
    /// <param name="inventory">Local inventory authority with atomic receipt support.</param>
    /// <exception cref="InvalidOperationException">Compilation or required persistence capability is invalid.</exception>
    /// <returns>The single prepared service declaration and runtime.</returns>
    public static HostedServiceProcess Bind(IEntityRepository<Order> orders, IEntityRepository<InventoryItem> inventory)
    {
        var readOptions = new EntityReadOptions(partitionKey: inventory.TransitionOperationCapabilities.PartitionKey
            ?? throw new InvalidOperationException("The demo requires a fixed receipt partition."));
        return Service.Define(new("fulfillment"), new("1"), FulfillmentProcess.Provenance)
            .Operation("fulfill").Run(FulfillmentProcess.Definition, bindings => bindings
            .Transition(InventoryTransitions.Reserve, inventory)
            .Transition(InventoryTransitions.Release, inventory)
            .Transition(OrderTransitions.Submit, orders)
            .Query(FulfillmentProcess.Stock, async (context, _, input) =>
            {
                var item = await inventory.TryGetEntity(context, input.Sku, readOptions);
                return new InventoryAvailability(item is not null, item?.Available ?? 0);
            }))
            .Build(authority: "aspire-first", timeout: TimeSpan.FromSeconds(15),
                authorization: new IdentityServiceInvocationAuthorization("demo", new(FulfillmentDomain.PartitionField)));
    }
}
