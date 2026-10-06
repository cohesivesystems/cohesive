using Cohesive.Adapters.AspNet.Entities;
using Cohesive.Api;
using Cohesive.Storage;

namespace AspireFirst.Orders;

/// <summary>HTTP declarations bind to the shared load, decide and concurrency-guarded commit implementation.</summary>
public static class OrderEndpoints
{
    /// <summary>Maps Submit; repeated submissions return conflict and unknown orders return not found.</summary>
    /// <param name="app">Application endpoint builder.</param>
    /// <param name="orders">Caller-owned order repository.</param>
    public static void Map(WebApplication app, IEntityRepository orders)
    {
        var submit = OrderTransitions.Submit;
        var api = Api.Define().Entity<Order>()
            .Command("Submit").Route("POST", "/orders/{id}/submit")
                .RouteParameter<string>("id").Returns<Order>().Transition(submit.Reference).Done().Build();
        app.MapEntityApiDefinition(api, new EntityApiEndpointOptions
        {
            Entity = OrderStorage.Entity,
            RepositoryResolver = (_, _) => orders,
            ReadPartitionKeyResolver = _ => OrderStorage.LocalPartition
        }.Bind(EntityApiOperationBinding.Transition("Submit", submit,
            createTransitionInput: (context, _) => new SubmitOrder(context.EntityId),
            createResult: (context, snapshot) => context.Decision!.GuaranteeDemands.CommitRequired
                ? Results.Ok(new { id = snapshot.Entity.EntityId.Value, status = "Submitted" })
                : Results.Conflict(new { error = "Order must be Draft to submit." }))));
    }
}
