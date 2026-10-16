using Cohesive.Adapters.AspNet.Services;
using Cohesive.Api.Execution.Services;
using Cohesive.Storage;
using Cohesive.Model;

namespace AspireFirst.Orders;

/// <summary>HTTP declarations bind to the shared load, decide and concurrency-guarded commit implementation.</summary>
public static class OrderEndpoints
{
    /// <summary>Contributes entity, query and process implementations to the same public API.</summary>
    /// <param name="model">Canonical application declarations.</param><returns>Offline implementation recipes.</returns>
    public static ServiceModelBuilder Define(FulfillmentApplication model) => new ServiceModelBuilder()
        .Implement(model.Api, bindings => ConfigureOrders(bindings, model))
        .Implement(model.Api, bindings => bindings.BindHosted(
            api => api.Fulfill, model.Service.Definition, model.Process,
            authority: "aspire-first", authorization: new IdentityServiceInvocationAuthorization("demo", new(FulfillmentDomain.PartitionField)),
            maximumExecutions: 1000,
            bind: (_, command) => (
                new(command.CommandId), new(command.IdempotencyKey),
                new(new(command.InstanceId), new(command.AttemptId)),
                ObservationValue.FromObject(command.Input)),
            project: value =>
            {
                var decoded = Cohesive.Relations.Execution.HostedQueryValueAdapter.Decode<FulfillmentResult>(
                    value, model.Process.Definition.Definition.Result);
                return decoded.Type == Cohesive.Prelude.ResultType.Success ? decoded.Success!
                    : throw new InvalidOperationException("The retained fulfillment result cannot be projected.");
            }, logPrivateFailures: true, authorizationPolicyResolver: (_, requirement) => requirement.Id));

    /// <summary>Contributes order operations independently of fulfillment execution.</summary>
    /// <param name="bindings">Shared API implementation contribution.</param><param name="model">Canonical application declarations.</param>
    public static void ConfigureOrders(ServiceApiImplementation<OrderApi> bindings, FulfillmentApplication model)
    {
        bindings.Entity(model.Domain.Orders, partition: FulfillmentDemo.LocalPartition, (api, endpoints) => endpoints
            .Create(api.Create, EntityCreationPolicy.IfAbsent,
                initialize: partition => new Order(Guid.NewGuid().ToString("D"), partition),
                identity: order => order.Id,
                respond: order => TypedResults.Created($"/orders/{order.Id}", new OrderCreated(order.Id)))
            .Get(api.Get, order => TypedResults.Ok(new OrderSummary(order.Id, order.Status)))
            .Transition(api.Submit, model.Orders.Submit)
                .OnApplied((order, outcome) => TypedResults.Ok(new OrderSummary(order.Id, order.Status)))
                .OnRejected(outcome => outcome.Outcome, codePrefix: "orders.submit", responses => responses
                    .Conflict(SubmissionOutcome.NotDraft, title: "Order cannot be submitted", detail: "Order must be Draft to submit.")
                    .NotRejected(SubmissionOutcome.Submitted)));
        bindings.Bind(api => api.Details, model.Queries.OrderDetails, new("1"), maximumRows: 1000, maximumBytes: 1_000_000,
            configure: query => query.FromRoute<string>("id", id => id).OkOrNotFound());
        bindings.Bind(api => api.Availability, model.Queries.ReservationAvailability, new("1"), maximumRows: 1000, maximumBytes: 1_000_000,
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
