using Cohesive.Adapters.AspNet.Entities;
using Cohesive.Adapters.AspNet;
using Cohesive.Storage;
using Cohesive.Relations.Execution;

namespace AspireFirst.Orders;

/// <summary>HTTP declarations bind to the shared load, decide and concurrency-guarded commit implementation.</summary>
public static class OrderEndpoints
{
    /// <summary>Maps entity operations and the joined read through shared API bindings.</summary>
    /// <param name="app">Application endpoint builder.</param>
    /// <param name="details">Prepared canonical reader for the canonical joined query.</param>
    /// <param name="availability">Prepared reservation availability reader.</param>
    /// <param name="orders">Caller-owned order repository.</param>
    public static void Map(WebApplication app, IEntityRepository<Order> orders, IRelationQueryReader<string, OrderDetails?> details,
        IRelationQueryReader<string, ReservationAvailability[]> availability)
    {
        app.MapEntityApi<Order>(FulfillmentDomain.Orders.Definition, orders, partition: FulfillmentDemo.LocalPartition,
            endpoints => endpoints
                .CreateIfAbsent(OrderApi.Create,
                    initialize: partition => new Order(Guid.NewGuid().ToString("D"), partition),
                    identity: order => order.Id,
                    respond: order => TypedResults.Created($"/orders/{order.Id}", new OrderCreated(order.Id))
                )
                .Get(OrderApi.Get, order => TypedResults.Ok(new OrderSummary(order.Id, order.Status)))
                .Transition(OrderApi.Submit, OrderTransitions.Submit)
                    .Input(request => new SubmitOrder(request.RequiredEntityId))
                    .OnApplied((order, outcome) => TypedResults.Ok(new OrderSummary(order.Id, order.Status)))
                    .OnRejected(outcome => TypedResults.Problem(statusCode: StatusCodes.Status409Conflict,
                        title: "Order cannot be submitted", detail: outcome.Reason,
                        extensions: new Dictionary<string, object?> { ["code"] = "orders.submit.rejected" }
                    )
                )
        );

        app.MapApiQuery(OrderApi.Availability, availability).FromRoute<Guid>("id", id => id.ToString("D")).OkOrNotFound();
        app.MapApiQuery(OrderApi.Details, details).FromRoute<Guid>("id", id => id.ToString("D")).OkOrNotFound();
    }

}

/// <summary>Created order identity.</summary>
/// <param name="Id">Server-generated order identity.</param>
public sealed record OrderCreated(string Id);

/// <summary>Public order identity and lifecycle state.</summary>
/// <param name="Id">Order identity.</param>
/// <param name="Status">Current lifecycle state.</param>
public sealed record OrderSummary(string Id, string Status);
