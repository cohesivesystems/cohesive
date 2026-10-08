using Cohesive.Adapters.AspNet.Services;
using Cohesive.Api;
using Cohesive.Api.Execution.Services;
using Cohesive.Identity;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Storage;

namespace AspireFirst.Orders;

/// <summary>Host-lifetime preparation of the declared process against receipt-capable local repositories.</summary>
/// <remarks>This is single-partition demo composition, not a production authorization policy or recovery worker.</remarks>
public sealed class FulfillmentProcessBindings
{
    /// <summary>Prepares exact transition links and the finite process interpreter once.</summary>
    /// <param name="orders">Local order authority with atomic receipt support.</param>
    /// <param name="inventory">Local inventory authority with atomic receipt support.</param>
    /// <exception cref="InvalidOperationException">Compilation or required persistence capability is invalid.</exception>
    public FulfillmentProcessBindings(IEntityRepository<Order> orders, IEntityRepository<InventoryItem> inventory)
    {
        var readOptions = new EntityReadOptions(partitionKey: inventory.TransitionOperationCapabilities.PartitionKey
            ?? throw new InvalidOperationException("The demo requires a fixed receipt partition."));
        var hosted = Service.Host(FulfillmentProcess.Definition)
            .Transition(InventoryTransitions.Reserve, inventory)
            .Transition(InventoryTransitions.Release, inventory)
            .Transition(OrderTransitions.Submit, orders)
            .Query(FulfillmentProcess.Stock, async (context, _, input) =>
            {
                var item = await inventory.TryGetEntity(context, input.Sku, readOptions);
                return new InventoryAvailability(item is not null, item?.Available ?? 0);
            })
            .Build(Service.Define(new("fulfillment"), new("1"), FulfillmentProcess.Provenance),
                operationId: "fulfill", authority: "aspire-first", timeout: TimeSpan.FromSeconds(15),
                authorization: new IdentityServiceInvocationAuthorization("demo", new(FulfillmentDomain.PartitionField)));
        Declaration = hosted.Declaration;
        Runtime = hosted.Runtime;
    }

    /// <summary>Service policy declares finite, invocation-local execution.</summary>
    public ExecutionDefinitionDocument Declaration { get; }
    /// <summary>Prepared runtime; invocation identities and state are supplied per call.</summary>
    public ServiceRuntime Runtime { get; }

    /// <summary>Maps the local demonstration route through the canonical service binding.</summary>
    /// <param name="app">Native endpoint builder.</param>
    public void Map(WebApplication app) => app.MapServiceEphemeralProcess(Declaration, _ => Runtime, "fulfill",
        FulfillmentProcess.Definition, new("POST", "/fulfillment", [], new(typeof(FulfillOrder))));

}
