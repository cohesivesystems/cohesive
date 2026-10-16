using Cohesive.Api;
using Cohesive.Api.Execution.Services;
using Microsoft.AspNetCore.Mvc;

namespace AspireFirst.Orders;

/// <summary>Portable order API declarations, independent of ASP.NET handlers and repository instances.</summary>
public sealed class OrderApi : ApiModel
{
    /// <summary>Declares the public contract using the exact transition reference.</summary>
    /// <param name="service">Execution contract.</param>
    /// <param name="process">Exact process authority.</param>
    /// <param name="transitions">Order behavior declarations, without runtime bindings.</param>
    public OrderApi(OrderTransitions transitions, FulfillmentService service, FulfillmentProcess process)
    {
        Fulfill = Endpoints.Process(service.Definition, "fulfill", process).Route("POST", "/fulfillment");
        var orders = Endpoints.Entity<Order>();
        Create = orders.Command("Create")
            .Route("POST", "/orders").Build<OrderCreated>(ApiResultKind.Created);
        Get = orders.Query("Get")
            .Route("GET", "/orders/{id}").RouteParameter<string>("id")
            .Result(ApiResultKind.NotFound).Build<OrderSummary>();
        Submit = orders.Command("Submit")
            .Route("POST", "/orders/{id}/submit").RouteParameter<string>("id")
            .Result<ProblemDetails>(ApiResultKind.Conflict).Result(ApiResultKind.NotFound)
            .Transition(transitions.Submit.Reference).Build<OrderSummary>();
        Details = orders.Query("Details").Route("GET", "/orders/{id}/details")
            .RouteParameter<string>("id").Result(ApiResultKind.NotFound).Build<OrderDetails>();
        Availability = orders.Query("availability").Route("GET", "/orders/{id}/availability")
            .RouteParameter<string>("id").Result(ApiResultKind.NotFound).Build<ReservationAvailability[]>();
    }

    /// <summary>Runs fulfillment to terminal completion before returning.</summary>
    public ServiceProcessEndpoint<FulfillOrder, FulfillmentResult> Fulfill { get; }
    /// <summary>Creates a fresh order.</summary>
    public ApiEndpoint<OrderCreated> Create { get; }
    /// <summary>Reads an existing order.</summary>
    public ApiEndpoint<OrderSummary> Get { get; }
    /// <summary>Submits an existing order using the exact declared transition.</summary>
    public ApiEndpoint<OrderSummary> Submit { get; }
    /// <summary>Queries order details through the canonical fulfillment relation.</summary>
    public ApiEndpoint<OrderDetails> Details { get; }
    /// <summary>Current joined reservation demand and inventory observations.</summary>
    public ApiEndpoint<ReservationAvailability[]> Availability { get; }
}
