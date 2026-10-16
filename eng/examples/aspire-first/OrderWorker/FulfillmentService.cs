using Cohesive.Api.Execution.Services;
using Cohesive.Execution;

namespace AspireFirst.Orders;

/// <summary>Selected service behavior and query roots; native repositories are attached only at host setup.</summary>
public sealed class FulfillmentService
{
    /// <summary>The shared authoring builder produces a portable service document and retains runtime attachments separately.</summary>
    public ServiceBuilder Definition { get; }

    /// <summary>Composes explicit execution policy and exact dependencies.</summary>
    /// <param name="process">Generated orchestration.</param>
    public FulfillmentService(FulfillmentProcess process)
    {
        Definition = Service.Define(
            new("fulfillment"), new("2"), process.Provenance)
            .Use(new ServiceDependencies()
                .Process(process))
            .Operation("fulfill").Require(new("fulfillment.write")).Run(process.Definition)
            .ReturnAfterAdmission(ProcessExecutionLifetime.Host)
            .Operation("result").Require(new("fulfillment.read")).ReadResultOf(process.Definition);
    }
}
