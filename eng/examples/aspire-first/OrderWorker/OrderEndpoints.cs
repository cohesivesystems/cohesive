using Cohesive.Adapters.AspNet.Entities;
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
        app.MapEntityApi<Order>(OrderStorage.Entity, orders, OrderStorage.LocalPartition,
            endpoints => endpoints
                .Create(OrderApi.Create,
                    initialize: () => new Order(Guid.NewGuid().ToString("D"), OrderStorage.LocalPartition),
                    identity: order => order.Id,
                    respond: order => TypedResults.Created($"/orders/{order.Id}", new OrderCreated(order.Id)))
                .Get(OrderApi.Get, order => TypedResults.Ok(new OrderSummary(order.Id, order.Status)))
                .Transition(OrderApi.Submit, OrderTransitions.Submit)
                    .Input(id => new SubmitOrder(id))
                    .OnApplied((order, outcome) => TypedResults.Ok(new OrderSummary(order.Id, order.Status)))
                    .OnRejected(outcome => TypedResults.Conflict(outcome)));
    }

}

/// <summary>Created order identity.</summary>
/// <param name="Id">Server-generated order identity.</param>
public sealed record OrderCreated(string Id);

/// <summary>Public order identity and lifecycle state.</summary>
/// <param name="Id">Order identity.</param>
/// <param name="Status">Current lifecycle state.</param>
public sealed record OrderSummary(string Id, string Status);
