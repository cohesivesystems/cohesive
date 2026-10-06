using Cohesive.Api;

namespace AspireFirst.Orders;

/// <summary>Portable order API declarations, independent of ASP.NET handlers and repository instances.</summary>
public static class OrderApi
{
    static OrderApi()
    {
        var orders = Api.Define().Entity<Order>();
        Create = orders.Command("Create")
            .Route("POST", "/orders").Returns<OrderCreated>().Build();
        Get = orders.Query("Get")
            .Route("GET", "/orders/{id:guid}").RouteParameter<string>("id")
            .Returns<OrderSummary>().Build();
        Submit = orders.Command("Submit")
            .Route("POST", "/orders/{id}/submit").RouteParameter<string>("id")
            .Returns<OrderSummary>().Result<SubmitOrderResult>(ApiResultKind.Conflict)
            .Transition(OrderTransitions.Submit.Reference).Build();
        Definition = orders.Build();
    }

    /// <summary>Creates a fresh order.</summary>
    public static ApiEndpoint Create { get; }
    /// <summary>Reads an existing order.</summary>
    public static ApiEndpoint Get { get; }
    /// <summary>Submits an existing order using the exact declared transition.</summary>
    public static ApiEndpoint Submit { get; }
    /// <summary>The complete portable surface, suitable for other API projections.</summary>
    public static ApiDefinition Definition { get; }
}
