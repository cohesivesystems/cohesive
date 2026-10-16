using Cohesive.Api;
using Cohesive.Api.Execution.Services;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Prelude;
using Cohesive.Relations.Execution;

namespace AspireFirst.Orders;

/// <summary>Caller-owned fulfillment intent and retry identities; keep every field unchanged on retry.</summary>
/// <param name="CommandId">Stable command identity.</param><param name="IdempotencyKey">Stable logical admission key.</param>
/// <param name="InstanceId">Requested process instance.</param><param name="AttemptId">Initial process attempt.</param>
/// <param name="Input">The existing fulfillment process input.</param>
public sealed record AcceptedFulfillOrder(string CommandId, string IdempotencyKey, string InstanceId,
    string AttemptId, FulfillOrder Input);

/// <summary>Optional accepted-execution contribution to the Aspire worker's API.</summary>
/// <remarks>This declares the alternative contract only. The caller must supply qualified durable admission
/// and retained-value bindings before mapping it; the default Program remains ephemeral.</remarks>
public sealed class AcceptedFulfillmentApi : ApiModel
{
    readonly ValueContract resultContract;

    /// <summary>Projects admission and polling over the existing generated fulfillment process.</summary>
    /// <param name="process">The same process used by the default terminal fulfillment endpoint.</param>
    public AcceptedFulfillmentApi(FulfillmentProcess process)
    {
        resultContract = process.Definition.Definition.Result;
        var service = Service.Define(new("fulfillment/accepted"), new("1"), process.Provenance)
            .Use(new ServiceDependencies().Process(process))
            .Operation("fulfill").Require(new("fulfillment.write"))
                .Run(process.Definition).ReturnAfterDurableAdmission()
            .Operation("result").Require(new("fulfillment.read"))
                .ReadResultOf(process.Definition);
        Fulfill = service.AcceptedApi<AcceptedFulfillOrder, FulfillmentResult>(
            startOperationId: "fulfill", resultOperationId: "result",
            startRoute: "/fulfillment/requests", resultRoute: "/fulfillment/results/{instanceId}");
        Endpoints.Include(Fulfill.Start);
        Endpoints.Include(Fulfill.Result);
    }

    /// <summary>Admission receipt and independently authorized terminal result.</summary>
    public ServiceAcceptedProcessApi<AcceptedFulfillOrder, FulfillmentResult> Fulfill { get; }

    // Transport view conversion only: runtime has already authorized and validated the retained result.
    internal FulfillmentResult Project(PortableValue value)
    {
        var decoded = HostedQueryValueAdapter.Decode<FulfillmentResult>(value, resultContract);
        return decoded.Type == ResultType.Success ? decoded.Success!
            : throw new InvalidOperationException("The retained fulfillment result cannot be projected.");
    }
}
