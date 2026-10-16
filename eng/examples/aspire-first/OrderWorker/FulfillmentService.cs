using Cohesive.Api.Execution.Services;

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
            new("fulfillment"), new("1"), process.Provenance)
            .Use(new ServiceDependencies()
                .Process(process))
            .Operation("fulfill").Run(process.Definition)
            .ExecuteEphemerally(TimeSpan.FromSeconds(15));
    }
}
