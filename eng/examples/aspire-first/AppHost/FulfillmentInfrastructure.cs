using Aspire.Hosting;
using Cohesive.Adapters.Aspire;
using Cohesive.Adapters.Aspire.Postgres;
using Cohesive.Infra;
using Cohesive.Infra.Realization;
using Cohesive.Model;
using AspireFirst.Orders;
using Cohesive.Adapters.Postgres.Services;

namespace AspireFirst;

/// <summary>A native AppHost with optional Cohesive requirement validation attached to selected resources.</summary>
public sealed class FulfillmentInfrastructure : InfrastructureModel
{
    /// <summary>Canonical order storage identity.</summary>
    public static InfrastructureNodeId Store { get; } = new("resources/orders");
    /// <summary>Canonical order application identity.</summary>
    public static InfrastructureNodeId Worker { get; } = new("workloads/orders");
    /// <summary>Required application execution capability.</summary>
    public static InfrastructureCapabilityId ApplicationExecution { get; } = AspireInfrastructure.DotNetProjectExecution;

    static readonly SourceReference Source = SourceReference.Create("example", "aspire-first/orders/v1");

    /// <summary>Prepares requirements from the same offline composition as the worker.</summary>
    /// <param name="model">Same activated application graph consumed by the worker.</param>
    public FulfillmentInfrastructure(FulfillmentApplication model)
    {
        Define(
            id: new("example/orders"),
            revision: new("4"),
            bindingProfileId: new("example/orders/bindings"),
            infra =>
            {
                infra.Resource(Store).Persistent()
                    .Postgres(model.Storage.Bindings, model.Endpoints.Plan);
                infra.Workload(Worker).Requires(ApplicationExecution).RequiresReady(Store);
            });
    }

    /// <summary>Starts with native resources, then associates only the resources governed by the requirements.</summary>
    /// <param name="builder">Existing AppHost builder.</param>
    /// <returns>Canonical validation and original native objects, without building or starting the AppHost.</returns>
    /// <exception cref="ArgumentNullException">Builder is null.</exception>
    /// <exception cref="ArgumentException">The model already contains conflicting resource names.</exception>
    public AspireInfrastructureAssociation Configure(IDistributedApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var postgres = builder.AddPostgres("postgres").WithDataVolume();
        var database = postgres.AddDatabase(FulfillmentStoragePolicy.OrdersDatabaseName);
        var worker = builder.AddProject<Projects.OrderWorker>("worker")
            .WithHttpEndpoint(env: "ASPNETCORE_HTTP_PORTS")
            .WithReference(database).WaitFor(database);
        _ = builder.AddContainer("native-only", "redis", "7.4");

        // Associate existing Aspire resources with the canonical requirements and implementations.
        return builder.AttachCohesive(
            model: this,
            id: new("example/orders/local"),
            variant: new("development"),
            associations => associations
                .Resource(Store, database, authority: new("aspire/example/orders"), sources: [Source])
                .Workload(Worker, worker, sources: [Source])
            );
    }
}
