using Cohesive.Adapters.AspNet.Entities;
using Cohesive.Adapters.AspNet;
using Cohesive.Adapters.Postgres;
using Cohesive.Model;
using Cohesive.Relations.IR;
using Cohesive.Storage;

namespace AspireFirst.Orders;

/// <summary>HTTP declarations bind to the shared load, decide and concurrency-guarded commit implementation.</summary>
public static class OrderEndpoints
{
    /// <summary>Maps entity operations and the joined read through shared API bindings.</summary>
    /// <param name="app">Application endpoint builder.</param>
    /// <param name="details">Prepared native reader for the canonical joined query.</param>
    /// <param name="orders">Caller-owned order repository.</param>
    public static void Map(WebApplication app, IEntityRepository orders, PostgresQueryRowsReader details)
    {
        app.MapEntityApi<Order>(OrderStorage.Entity, orders, OrderStorage.LocalPartition,
            endpoints => endpoints
                .Create(OrderApi.Create,
                    initialize: () => new Order(Guid.NewGuid().ToString("D"), OrderStorage.LocalPartition),
                    identity: order => order.Id,
                    respond: order => TypedResults.Created($"/orders/{order.Id}", new OrderCreated(order.Id)))
                .Get(OrderApi.Get, order => TypedResults.Ok(new OrderSummary(order.Id, order.Status)))
                .Transition(OrderApi.Submit, OrderTransitions.Submit)
                    .Input(request => new SubmitOrder(request.RequiredEntityId))
                    .OnApplied((order, outcome) => TypedResults.Ok(new OrderSummary(order.Id, order.Status)))
                    .OnRejected(outcome => TypedResults.Problem(statusCode: StatusCodes.Status409Conflict,
                        title: "Order cannot be submitted", detail: outcome.Reason,
                        extensions: new Dictionary<string, object?> { ["code"] = "orders.submit.rejected" })));
        app.MapApiEndpoint(OrderApi.Details, async (Guid id, CancellationToken cancellationToken) =>
        {
            var observations = await details.ReadAsync(new Dictionary<QueryParameterId, ObservationValue>
            {
                [new(OrderDetailsQuery.OrderIdParameter)] = ObservationValue.FromString(id.ToString("D"))
            }, cancellationToken);
            if (observations.IsEmpty) return Results.NotFound();
            var rows = observations.Select(value => value.Deserialize<OrderDetailRow>()!).ToArray();
            // Presentation nesting only; correlation and filtering are compiled from the canonical query.
            return Results.Ok(new OrderDetails(rows[0].Id, rows[0].Status,
                rows.Where(row => row.ReservationId is not null).OrderBy(row => row.ReservationId, StringComparer.Ordinal)
                    .Select(row => new ReservationSummary(row.ReservationId!, row.Sku!, row.Quantity!.Value, row.AvailableStock!.Value)).ToArray()));
        });
    }

}

/// <summary>Created order identity.</summary>
/// <param name="Id">Server-generated order identity.</param>
public sealed record OrderCreated(string Id);

/// <summary>Public order identity and lifecycle state.</summary>
/// <param name="Id">Order identity.</param>
/// <param name="Status">Current lifecycle state.</param>
public sealed record OrderSummary(string Id, string Status);

/// <summary>Joined order view with zero or more reservations.</summary>
/// <param name="Id">Order identity.</param>
/// <param name="Status">Current lifecycle state.</param>
/// <param name="Reservations">Joined reservations ordered by identity.</param>
public sealed record OrderDetails(string Id, string Status, IReadOnlyList<ReservationSummary> Reservations);
/// <summary>One reservation and its current inventory availability, not an event-sourced snapshot.</summary>
/// <param name="Id">Reservation identity.</param>
/// <param name="Sku">Inventory identity.</param>
/// <param name="Quantity">Reserved quantity.</param>
/// <param name="AvailableStock">Current availability in the query snapshot.</param>
public sealed record ReservationSummary(string Id, string Sku, int Quantity, int AvailableStock);
