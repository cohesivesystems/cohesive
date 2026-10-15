using Aspire.Hosting;
using Cohesive.Adapters.Aspire;
using Cohesive.Adapters.Aspire.Postgres;
using Cohesive.Infra;
using Cohesive.Infra.Realization;
using Cohesive.Model;
using AspireFirst.Orders;
using Cohesive.Adapters.Postgres.Infra;
using Cohesive.Relations.Infra;

namespace AspireFirst;

/// <summary>A native AppHost with optional Cohesive requirement validation attached to selected resources.</summary>
public static class OrdersApp
{
    /// <summary>Canonical order storage identity.</summary>
    public static InfrastructureNodeId Store { get; } = new("resources/orders");
    /// <summary>Canonical order application identity.</summary>
    public static InfrastructureNodeId Worker { get; } = new("workloads/orders");
    /// <summary>Prepare the declared query set once; requirements and native evidence share this authority.</summary>
    public static PostgresPreparedQueryEvidence StorageQueries { get; } = PostgresInfrastructure.Prepare(
        new("example/postgres"), FulfillmentStorage.Bindings,
        new QuerySnapshotRequirement("orders/details", "1", FulfillmentQueries.OrderDetails.CompilationRequest),
        new QuerySnapshotRequirement("orders/availability", "1", FulfillmentQueries.ReservationAvailability.CompilationRequest));
    /// <summary>Required application execution capability.</summary>
    public static InfrastructureCapabilityId ApplicationExecution { get; } = AspireInfrastructure.DotNetProjectExecution;

    static readonly SourceReference Source = SourceReference.Create("example", "aspire-first/orders/v1");

    /// <summary>Canonical requirements, independent of native Aspire resource names and provider options.</summary>
    public static InfrastructureAuthoringResult Definition { get; } = Infrastructure.Define(
        id: new("example/orders"),
        revision: new("3"),
        bindingProfileId: new("example/orders/bindings"),
        infra =>
        {
            var storage = infra.Resource(Store).Persistent();
            foreach (var requirement in StorageQueries.Requirements) storage.Requires(requirement);
            infra.Workload(Worker).Requires(ApplicationExecution).RequiresReady(Store);
        });

    /// <summary>Starts with native resources, then associates only the resources governed by the requirements.</summary>
    /// <param name="builder">Existing AppHost builder.</param>
    /// <returns>Canonical validation and original native objects, without building or starting the AppHost.</returns>
    /// <exception cref="ArgumentNullException">Builder is null.</exception>
    /// <exception cref="ArgumentException">The model already contains conflicting resource names.</exception>
    public static AspireInfrastructureAssociation Configure(IDistributedApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var postgres = builder.AddPostgres("postgres").WithDataVolume();
        var database = postgres.AddDatabase(FulfillmentResources.DatabaseName);
        var worker = builder.AddProject<Projects.OrderWorker>("worker")
            .WithHttpEndpoint(env: "ASPNETCORE_HTTP_PORTS")
            .WithReference(database).WaitFor(database);
        _ = builder.AddContainer("native-only", "redis", "7.4");

        // Associate existing Aspire resources with the canonical requirements and implementations.
        return AspireInfrastructureAssociation.Attach(
            application: builder,
            definition: Definition,
            id: new("example/orders/local"),
            variant: new("development"),
            associations => associations
                .Resource(Store, database, StorageQueries, new("aspire/example/orders"), [Source])
                .Workload(Worker, worker, sources: [Source])
            );
    }
}
