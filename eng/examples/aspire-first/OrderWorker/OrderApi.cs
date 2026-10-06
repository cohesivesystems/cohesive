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
            .Route("POST", "/orders").Returns<OrderCreated>(ApiResultKind.Created).Build();
        Get = orders.Query("Get")
            .Route("GET", "/orders/{id:guid}").RouteParameter<string>("id")
            .Returns<OrderSummary>().Result(ApiResultKind.NotFound).Build();
        Submit = orders.Command("Submit")
            .Route("POST", "/orders/{id}/submit").RouteParameter<string>("id")
            .Returns<OrderSummary>().Result<ProblemDetails>(ApiResultKind.Conflict).Result(ApiResultKind.NotFound)
            .Transition(OrderTransitions.Submit.Reference).Build();
        Details = orders.Query("Details").Route("GET", "/orders/{id:guid}/details")
            .RouteParameter<string>("id").Returns<OrderDetails>().Result(ApiResultKind.NotFound).Build();
        Definition = orders.Build();
    }

    /// <summary>Creates a fresh order.</summary>
    public static ApiEndpoint Create { get; }
    /// <summary>Reads an existing order.</summary>
    public static ApiEndpoint Get { get; }
    /// <summary>Submits an existing order using the exact declared transition.</summary>
    public static ApiEndpoint Submit { get; }
    /// <summary>Queries order details through the canonical fulfillment relation.</summary>
    public static ApiEndpoint Details { get; }
    /// <summary>The complete portable surface, suitable for other API projections.</summary>
    public static ApiDefinition Definition { get; }
}
