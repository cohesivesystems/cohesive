using Aspire.Hosting;
using Cohesive.Infra;
using Cohesive.Adapters.Aspire;

namespace AspireFirst.PostgresCosmos;

/// <summary>Separate native host: PostgreSQL orders plus Cosmos inventory, with no distributed snapshot claim.</summary>
public sealed class PostgresCosmosApp : InfrastructureModel
{
    /// <summary>Activated mixed-store application graph.</summary>
    public PostgresCosmosApplication Application { get; }
    /// <summary>Declares the workload requirement for the shared application.</summary>
    /// <param name="model">Activated application composition.</param>
    public PostgresCosmosApp(PostgresCosmosApplication model)
    {
        Application = model;
        Define(new("example/orders-postgres-cosmos"), new("1"), new("example/orders-postgres-cosmos/bindings"), infra =>
        {
            infra.Workload(new("workloads/orders")).Requires(AspireInfrastructure.DotNetProjectExecution);
        });
    }

    /// <summary>Attaches native local resources and the inferred .NET workload implementation.</summary>
    /// <param name="builder">Native Aspire host under construction.</param>
    /// <returns>Association evidence for the governed workload.</returns>
    /// <exception cref="InvalidOperationException">The workload association is incomplete.</exception>
    public AspireInfrastructureAssociation Configure(IDistributedApplicationBuilder builder)
    {
        var postgres = builder.AddPostgres("postgres").WithDataVolume();
        var orders = postgres.AddDatabase(PostgresCosmosStorage.OrdersDatabase);
#pragma warning disable ASPIRECOSMOSDB001 // Local example deliberately uses the cross-platform preview emulator.
        var cosmos = builder.AddAzureCosmosDB(PostgresCosmosStorage.CosmosAccount).RunAsPreviewEmulator();
#pragma warning restore ASPIRECOSMOSDB001
        var database = cosmos.AddCosmosDatabase(PostgresCosmosStorage.InventoryDatabase);
        var inventory = database.AddContainer(PostgresCosmosStorage.InventoryContainer, PostgresCosmosStorage.PartitionPath);
        var worker = builder.AddProject<Projects.PostgresCosmosWorker>("worker")
            .WithHttpEndpoint(env: "ASPNETCORE_HTTP_PORTS").WithReference(orders).WithReference(cosmos)
            .WaitFor(orders).WaitFor(inventory);
        var association = AspireInfrastructureAssociation.Attach(builder, Definition,
            new("example/orders-postgres-cosmos/local"), new("development"), bindings => bindings
                .Workload(new("workloads/orders"), worker));
        if (!association.Deployment.IsComplete) throw new InvalidOperationException("Incomplete native resource associations.");
        return association;
    }
}
