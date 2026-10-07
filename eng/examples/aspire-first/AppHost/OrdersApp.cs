using Aspire.Hosting;
using Cohesive.Adapters.Aspire;
using Cohesive.Infra;
using Cohesive.Infra.Realization;
using Cohesive.Model;
using static Cohesive.Infra.InfrastructureTargetImplementation;

namespace AspireFirst;

/// <summary>A native AppHost with optional Cohesive requirement validation attached to selected resources.</summary>
public static class OrdersApp
{
    /// <summary>Canonical order storage identity.</summary>
    public static InfrastructureNodeId Store { get; } = new("resources/orders");
    /// <summary>Canonical order application identity.</summary>
    public static InfrastructureNodeId Worker { get; } = new("workloads/orders");
    /// <summary>Required relational persistence capability.</summary>
    public static InfrastructureCapabilityId RelationalStorage { get; } = new("example/relational-storage");
    /// <summary>Required application execution capability.</summary>
    public static InfrastructureCapabilityId ApplicationExecution { get; } = new("example/application-execution");
    static readonly SourceReference Source = SourceReference.Create("example", "aspire-first/orders/v1");

    /// <summary>Canonical requirements, independent of native Aspire resource names and provider options.</summary>
    public static InfrastructureAuthoringResult Definition { get; } = Infrastructure.Define(
        id: new("example/orders"),
        revision: new("1"),
        bindingProfileId: new("example/orders/bindings"),
        infra =>
        {
            infra.Resource(Store).Persistent().Requires(RelationalStorage);
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
        var database = postgres.AddDatabase("orders");
        var worker = builder.AddProject<Projects.OrderWorker>("worker")
            .WithHttpEndpoint(env: "ASPNETCORE_HTTP_PORTS")
            .WithReference(database).WaitFor(database);
        _ = builder.AddContainer("native-only", "redis", "7.4");

        // Declare PostgreSQL as the implementation of relational storage.
        var postgresImplementation = ResourceImplementation(
            id: new("example/postgres"),
            evidence:
            [
                new InfrastructureCapabilityEvidence(
                    id: new("example/postgres/relational"),
                    capability: RelationalStorage,
                    realization: CapabilityRealizationKind.Native,
                    sourceReferences: [Source])
            ]);
        // Declare the .NET project as the implementation of application execution.
        var dotnetProjectImplementation = WorkloadImplementation(
            id: new("example/dotnet-project"),
            evidence:
            [
                new InfrastructureCapabilityEvidence(
                    id: new("example/dotnet/execution"),
                    capability: ApplicationExecution,
                    realization: CapabilityRealizationKind.Native,
                    sourceReferences: [Source])
            ]);

        // Associate existing Aspire resources with the canonical requirements and implementations.
        return AspireInfrastructureAssociation.Attach(
            application: builder,
            definition: Definition,
            id: new("example/orders/local"),
            variant: new("development"),
            associations => associations
                .Resource(Store, database, postgresImplementation, new("aspire/example/orders"), [Source])
                .Workload(Worker, worker, dotnetProjectImplementation, [Source]));
    }
}
