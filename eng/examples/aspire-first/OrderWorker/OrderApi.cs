using Cohesive.Api;

namespace AspireFirst.Orders;

/// <summary>Portable order API declarations, independent of ASP.NET handlers and repository instances.</summary>
public static class OrderApi
{
    /// <summary>Creates a fresh order.</summary>
    public static ApiEndpoint Create { get; } = Api.Define().Entity<Order>()
        .Command("Create").Route("POST", "/orders").Returns<OrderCreated>().Build();
    /// <summary>Reads an existing order.</summary>
    public static ApiEndpoint Get { get; } = Api.Define().Entity<Order>()
        .Query("Get").Route("GET", "/orders/{id:guid}").RouteParameter<string>("id").Returns<OrderSummary>().Build();
    /// <summary>Submits an existing order using the exact declared transition.</summary>
    public static ApiEndpoint Submit { get; } = Api.Define().Entity<Order>()
        .Command("Submit").Route("POST", "/orders/{id}/submit").RouteParameter<string>("id")
        .Returns<OrderSummary>().Result<SubmitOrderResult>(ApiResultKind.Conflict).Transition(OrderTransitions.Submit.Reference).Build();
    /// <summary>The complete portable surface, suitable for other API projections.</summary>
    public static ApiDefinition Definition { get; } = ApiDefinition.From(Create, Get, Submit);
}
