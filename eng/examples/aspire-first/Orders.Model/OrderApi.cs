using Cohesive.Relations.Execution;
using Cohesive.Api;
using Cohesive.Execution;
using Cohesive.Api.Execution.Services;
using Microsoft.AspNetCore.Mvc;
using Cohesive.Adapters.AspNet.Services;
using Cohesive.Storage;

namespace AspireFirst.Orders;

/// <summary>Portable order API declarations, independent of ASP.NET handlers and repository instances.</summary>
public sealed class OrderApi : ApiModel
{
    /// <summary>Declares the public contract using the exact transition reference.</summary>
    /// <param name="process">Execution contract.</param>
    /// <param name="domain">Canonical entities and order behavior, without runtime bindings.</param>
    public OrderApi(FulfillmentDomain domain, FulfillmentProcess process)
    {
        Fulfill = Endpoints.Process(process).Accepted<ServiceProcessCommand<FulfillOrder>>(
            id: new("fulfillment"), revision: new("3"),
            configure: service => service
                .Operation("fulfill").Require(new("fulfillment.write")).Run(process.Definition)
                .ReturnAfterAdmission(ProcessExecutionLifetime.Host)
                .Operation("result").Require(new("fulfillment.read")).ReadResultOf(process.Definition),
            startOperationId: "fulfill", resultOperationId: "result",
            startRoute: "/fulfillment", resultRoute: "/fulfillment/results/{instanceId}");
        var orders = Endpoints.Entity<Order>();
        Create = orders.Command("Create")
            .Route("POST", "/orders").Build<OrderCreated>(ApiResultKind.Created);
        Get = orders.Query("Get")
            .Route("GET", "/orders/{id}").RouteParameter<string>("id")
            .Result(ApiResultKind.NotFound).Build<OrderSummary>();
        Submit = orders.Command("Submit")
            .Route("POST", "/orders/{id}/submit").RouteParameter<string>("id")
            .Result<ProblemDetails>(ApiResultKind.Conflict).Result(ApiResultKind.NotFound)
            .Transition(domain.OrderTransitions.Submit.Reference).Build<OrderSummary>();
        Details = orders.Query("Details").Route("GET", "/orders/{id}/details")
            .RouteParameter<string>("id").Result(ApiResultKind.NotFound).Build<OrderDetails>();
        Availability = orders.Query("availability").Route("GET", "/orders/{id}/availability")
            .RouteParameter<string>("id").Result(ApiResultKind.NotFound).Build<ReservationAvailability[]>();
    }

    /// <summary>Acknowledges fulfillment and exposes its independently authorized result.</summary>
    public ColocatedProcessApi<ServiceProcessCommand<FulfillOrder>, FulfillOrder, FulfillmentResult> Fulfill { get; }
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

/// <summary>HTTP declarations bind to the shared load, decide and concurrency-guarded commit implementation.</summary>
public sealed class OrderEndpoints : ApiBindingModel
{
    /// <summary>Authors offline bindings using the same declaration instances as the rest of the application.</summary>
    /// <param name="api">API contract.</param><param name="domain">Canonical entities.</param>
    /// <param name="queries">Declared queries.</param>
    public OrderEndpoints(OrderApi api, FulfillmentDomain domain, FulfillmentQueries queries)
    {
        Bindings.Implement(api, bindings =>
        {
            bindings.Entity(domain.Orders, partition: FulfillmentStoragePolicy.LocalPartition, (api, endpoints) => endpoints
                .Create(api.Create, EntityCreationPolicy.IfAbsent,
                    initialize: partition => new Order(Guid.NewGuid().ToString("D"), partition),
                    identity: order => order.Id,
                    respond: order => TypedResults.Created($"/orders/{order.Id}", new OrderCreated(order.Id)))
                .Get(api.Get, order => TypedResults.Ok(new OrderSummary(order.Id, order.Status)))
                .Transition(api.Submit, domain.OrderTransitions.Submit)
                    .OnApplied((order, outcome) => TypedResults.Ok(new OrderSummary(order.Id, order.Status)))
                    .OnRejected(outcome => outcome.Outcome, codePrefix: "orders.submit", responses => responses
                        .Conflict(SubmissionOutcome.NotDraft, title: "Order cannot be submitted", detail: "Order must be Draft to submit.")
                        .NotRejected(SubmissionOutcome.Submitted)));
            bindings.Bind(api => api.Details, queries.OrderDetails, new("1"), maximumRows: 1000, maximumBytes: 1_000_000,
                configure: query => query.FromRoute<string>("id", id => id).OkOrNotFound(),
                consistency: RelationQueryConsistency.IndependentSources);
            bindings.Bind(api => api.Availability, queries.ReservationAvailability, new("1"), maximumRows: 1000, maximumBytes: 1_000_000,
                configure: query => query.FromRoute<string>("id", id => id).OkOrNotFound(),
                consistency: RelationQueryConsistency.IndependentSources);
            bindings.BindHosted(endpoint => endpoint.Fulfill,
                authority: "aspire-first", authorization: new IdentityServiceInvocationAuthorization("demo", new(FulfillmentDomain.PartitionField)),
                maximumExecutions: 1000, logPrivateFailures: true);
        });
    }
}

/// <summary>Created order identity.</summary>
/// <param name="Id">Server-generated order identity.</param>
public sealed record OrderCreated(string Id);

/// <summary>Public order identity and lifecycle state.</summary>
/// <param name="Id">Order identity.</param>
/// <param name="Status">Current lifecycle state.</param>
public sealed record OrderSummary(string Id, OrderStatus Status);
