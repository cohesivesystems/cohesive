using Cohesive.Adapters.AspNet.Entities;
using Cohesive.Api;
using Cohesive.Storage;

namespace AspireFirst.Orders;

/// <summary>HTTP declarations bind to the shared load, decide and concurrency-guarded commit implementation.</summary>
public static class OrderEndpoints
{
    /// <summary>Maps order creation, lookup and submission through the shared entity API bindings.</summary>
    /// <param name="app">Application endpoint builder.</param>
    /// <param name="orders">Caller-owned order repository.</param>
    public static void Map(WebApplication app, IEntityRepository orders)
    {
        var submit = OrderTransitions.Submit;
        var api = Api.Define().Entity<Order>()
            .Command("Create").Route("POST", "/orders").Returns<OrderCreated>().Done()
            .Query("Get").Route("GET", "/orders/{id:guid}")
                .RouteParameter<Guid>("id").Returns<OrderSummary>().Done()
            .Command("Submit").Route("POST", "/orders/{id}/submit")
                .RouteParameter<string>("id").Returns<OrderSummary>().Transition(submit.Reference).Done().Build();
        app.MapEntityApiDefinition(api, new EntityApiEndpointOptions
        {
            Entity = OrderStorage.Entity,
            RepositoryResolver = (_, _) => orders,
            ReadPartitionKeyResolver = _ => OrderStorage.LocalPartition
        }
        .Bind(EntityApiOperationBinding.Create("Create",
            createState: (_, _) => OrderStorage.Entity.CreateState(OrderStorage.Register(Guid.NewGuid()).Entity),
            createResult: (_, snapshot) => Results.Created($"/orders/{snapshot.Entity.EntityId.Value}",
                new OrderCreated(snapshot.Entity.EntityId.Value))))
        .Bind(EntityApiOperationBinding.Get("Get", (_, snapshot) => Results.Ok(ToSummary(snapshot))))
        .Bind(EntityApiOperationBinding.Transition("Submit", submit,
            createTransitionInput: (context, _) => new SubmitOrder(context.EntityId),
            createResult: (context, snapshot) => context.Decision!.GuaranteeDemands.CommitRequired
                ? Results.Ok(ToSummary(snapshot))
                : Results.Conflict(new { error = "Order must be Draft to submit." }))));
    }

    static OrderSummary ToSummary(EntitySnapshot snapshot) => new(snapshot.Entity.EntityId.Value,
        snapshot.Entity.Observation.GetField("status").GetRequiredString());
}

/// <summary>Created order identity.</summary>
/// <param name="Id">Server-generated order identity.</param>
public sealed record OrderCreated(string Id);

/// <summary>Public order identity and lifecycle state.</summary>
/// <param name="Id">Order identity.</param>
/// <param name="Status">Current lifecycle state.</param>
public sealed record OrderSummary(string Id, string Status);
