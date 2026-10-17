using Cohesive.Adapters.AspNet.Services;
using Cohesive.Api.Execution.Services;
using Cohesive.Storage;

namespace AspireFirst.Orders;

/// <summary>HTTP declarations bind to the shared load, decide and concurrency-guarded commit implementation.</summary>
public sealed class OrderEndpoints : ApiBindingModel
{
    /// <summary>Authors offline bindings using the same declaration instances as the rest of the application.</summary>
    /// <param name="api">API contract.</param><param name="domain">Canonical entities.</param>
    /// <param name="queries">Declared queries.</param><param name="transitions">Order transition contracts.</param>
    public OrderEndpoints(OrderApi api, FulfillmentDomain domain, FulfillmentQueries queries, OrderTransitions transitions)
    {
        Bindings.Implement(api, bindings => ConfigureOrders(bindings, domain, queries, transitions))
            .Implement(api, bindings => bindings.BindHosted(
                endpoint => endpoint.Fulfill,
                authority: "aspire-first", authorization: new IdentityServiceInvocationAuthorization("demo", new(FulfillmentDomain.PartitionField)),
                maximumExecutions: 1000, logPrivateFailures: true));
    }

    /// <summary>Contributes order operations independently of fulfillment execution.</summary>
    /// <param name="bindings">Shared API implementation contribution.</param><param name="domain">Entities.</param><param name="queries">Queries.</param><param name="transitions">Order transitions.</param>
    public static void ConfigureOrders(ServiceApiImplementation<OrderApi> bindings, FulfillmentDomain domain, FulfillmentQueries queries, OrderTransitions transitions)
    {
        bindings.Entity(domain.Orders, partition: FulfillmentDemo.LocalPartition, (api, endpoints) => endpoints
            .Create(api.Create, EntityCreationPolicy.IfAbsent,
                initialize: partition => new Order(Guid.NewGuid().ToString("D"), partition),
                identity: order => order.Id,
                respond: order => TypedResults.Created($"/orders/{order.Id}", new OrderCreated(order.Id)))
            .Get(api.Get, order => TypedResults.Ok(new OrderSummary(order.Id, order.Status)))
            .Transition(api.Submit, transitions.Submit)
                .OnApplied((order, outcome) => TypedResults.Ok(new OrderSummary(order.Id, order.Status)))
                .OnRejected(outcome => outcome.Outcome, codePrefix: "orders.submit", responses => responses
                    .Conflict(SubmissionOutcome.NotDraft, title: "Order cannot be submitted", detail: "Order must be Draft to submit.")
                    .NotRejected(SubmissionOutcome.Submitted)));
        bindings.Bind(api => api.Details, queries.OrderDetails, new("1"), maximumRows: 1000, maximumBytes: 1_000_000,
            configure: query => query.FromRoute<string>("id", id => id).OkOrNotFound());
        bindings.Bind(api => api.Availability, queries.ReservationAvailability, new("1"), maximumRows: 1000, maximumBytes: 1_000_000,
            configure: query => query.FromRoute<string>("id", id => id).OkOrNotFound());
    }

}

/// <summary>Created order identity.</summary>
/// <param name="Id">Server-generated order identity.</param>
public sealed record OrderCreated(string Id);

/// <summary>Public order identity and lifecycle state.</summary>
/// <param name="Id">Order identity.</param>
/// <param name="Status">Current lifecycle state.</param>
public sealed record OrderSummary(string Id, OrderStatus Status);
