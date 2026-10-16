using Cohesive.Adapters.AspNet.Entities;
using Cohesive.Adapters.AspNet;
using Cohesive.Storage;
using Cohesive.Relations.Execution;

namespace AspireFirst.Orders;

/// <summary>HTTP declarations bind to the shared load, decide and concurrency-guarded commit implementation.</summary>
public static class OrderEndpoints
{
    /// <summary>Maps entity operations and the joined read through shared API bindings.</summary>
    /// <param name="model">Shared contract and behavior declarations.</param>
    /// <param name="app">Application endpoint builder.</param>
    /// <param name="details">Prepared canonical reader for the canonical joined query.</param>
    /// <param name="availability">Prepared reservation availability reader.</param>
    /// <param name="orders">Caller-owned order repository.</param>
    public static void Map(WebApplication app, FulfillmentApplication model,
        IEntityRepository<Order> orders,
        IRelationQueryReader<string, OrderDetails?> details,
        IRelationQueryReader<string, ReservationAvailability[]> availability
        )
    {
        app.MapEntityApi<Order>(model.Domain.Orders.Definition, orders, partition: FulfillmentDemo.LocalPartition,
            endpoints => endpoints
                .Create(model.Api.Create, EntityCreationPolicy.IfAbsent,
                    initialize: partition => new Order(Guid.NewGuid().ToString("D"), partition),
                    identity: order => order.Id,
                    respond: order => TypedResults.Created($"/orders/{order.Id}", new OrderCreated(order.Id))
                )
                .Get(model.Api.Get, order => TypedResults.Ok(new OrderSummary(order.Id, order.Status)))
                .Transition(model.Api.Submit, model.Orders.Submit)
                    .OnApplied((order, outcome) => TypedResults.Ok(new OrderSummary(order.Id, order.Status)))
                    .OnRejected(outcome => outcome.Outcome, codePrefix: "orders.submit", responses => responses
                        .Conflict(SubmissionOutcome.NotDraft, title: "Order cannot be submitted", detail: "Order must be Draft to submit.")
                        .NotRejected(SubmissionOutcome.Submitted))
        );

        app.MapApiQuery(model.Api.Availability, availability).FromRoute<string>("id", id => id).OkOrNotFound();
        app.MapApiQuery(model.Api.Details, details).FromRoute<string>("id", id => id).OkOrNotFound();
    }

}

/// <summary>Created order identity.</summary>
/// <param name="Id">Server-generated order identity.</param>
public sealed record OrderCreated(string Id);

/// <summary>Public order identity and lifecycle state.</summary>
/// <param name="Id">Order identity.</param>
/// <param name="Status">Current lifecycle state.</param>
public sealed record OrderSummary(string Id, OrderStatus Status);
