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
        new("example/orders"), new("1"), new("example/orders/bindings"), infra =>
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
        // Ordinary native authoring remains authoritative for provider configuration and wiring.
        var postgres = builder.AddPostgres("postgres").WithDataVolume();
        var database = postgres.AddDatabase("orders");
        var worker = builder.AddProject<Projects.OrderWorker>("worker")
            .WithHttpEndpoint(env: "ASPNETCORE_HTTP_PORTS")
            .WithReference(database).WaitFor(database);
        _ = builder.AddContainer("native-only", "redis", "7.4");

        var storage = ResourceImplementation(new("example/postgres"),
            [new(new("example/postgres/relational"), RelationalStorage, CapabilityRealizationKind.Native,
                sourceReferences: [Source])]);
        var process = WorkloadImplementation(new("example/dotnet-project"),
            [new(new("example/dotnet/execution"), ApplicationExecution, CapabilityRealizationKind.Native,
                sourceReferences: [Source])]);

        return AspireInfrastructureAssociation.Attach(builder, Definition,
            new("example/orders/local"), new("development"), associations => associations
                .Resource(Store, database, storage, new("aspire/example/orders"), [Source])
                .Workload(Worker, worker, process, [Source]));
    }
}
