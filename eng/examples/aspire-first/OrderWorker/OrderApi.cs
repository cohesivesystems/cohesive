using Cohesive.Api;
using Microsoft.AspNetCore.Mvc;

namespace AspireFirst.Orders;

/// <summary>Portable order API declarations, independent of ASP.NET handlers and repository instances.</summary>
public static class OrderApi
{
    static OrderApi()
    {
        var orders = Api.Define().Entity<Order>();
        Create = orders.Command("Create")
            .Route("POST", "/orders").Build<OrderCreated>(ApiResultKind.Created);
        Get = orders.Query("Get")
            .Route("GET", "/orders/{id:guid}").RouteParameter<string>("id")
            .Result(ApiResultKind.NotFound).Build<OrderSummary>();
        Submit = orders.Command("Submit")
            .Route("POST", "/orders/{id}/submit").RouteParameter<string>("id")
            .Result<ProblemDetails>(ApiResultKind.Conflict).Result(ApiResultKind.NotFound)
            .Transition(OrderTransitions.Submit.Reference).Build<OrderSummary>();
        Details = orders.Query("Details").Route("GET", "/orders/{id:guid}/details")
            .RouteParameter<string>("id").Result(ApiResultKind.NotFound).Build<OrderDetails>();
        Definition = orders.Build();
    }

    /// <summary>Creates a fresh order.</summary>
    public static ApiEndpoint<OrderCreated> Create { get; }
    /// <summary>Reads an existing order.</summary>
    public static ApiEndpoint<OrderSummary> Get { get; }
    /// <summary>Submits an existing order using the exact declared transition.</summary>
    public static ApiEndpoint<OrderSummary> Submit { get; }
    /// <summary>Queries order details through the canonical fulfillment relation.</summary>
    public static ApiEndpoint<OrderDetails> Details { get; }
    /// <summary>The complete portable surface, suitable for other API projections.</summary>
    public static ApiDefinition Definition { get; }
}
