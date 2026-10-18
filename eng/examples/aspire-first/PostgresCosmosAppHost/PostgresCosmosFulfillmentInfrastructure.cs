using AspireFirst.Orders;
using Aspire.Hosting;
using Cohesive.Infra;
using Cohesive.Adapters.Aspire;

namespace AspireFirst.PostgresCosmos;

/// <summary>Separate native host: PostgreSQL orders plus Cosmos inventory, with no distributed snapshot claim.</summary>
public sealed class PostgresCosmosFulfillmentInfrastructure : InfrastructureModel
{
    /// <summary>Canonical shared application workload identity.</summary>
    public static InfrastructureNodeId Worker { get; } = new("workloads/orders");

    /// <summary>Activated mixed-store application graph.</summary>
    public PostgresCosmosApplication Application { get; }
    /// <summary>Declares the workload requirement for the shared application.</summary>
    /// <param name="model">Activated application composition.</param>
    public PostgresCosmosFulfillmentInfrastructure(PostgresCosmosApplication model)
    {
        Application = model;
        Define(
            id: new("example/orders-postgres-cosmos"),
            revision: new("1"),
            bindingProfileId: new("example/orders-postgres-cosmos/bindings"),
            infra => infra.Workload(Worker).Requires(AspireInfrastructure.DotNetProjectExecution));
    }

    /// <summary>Attaches native local resources and the inferred .NET workload implementation.</summary>
    /// <param name="builder">Native Aspire host under construction.</param>
    /// <returns>Association evidence for the governed workload.</returns>
    /// <exception cref="ArgumentNullException">Builder is null.</exception>
    /// <exception cref="InvalidOperationException">The workload association is incomplete.</exception>
    public AspireInfrastructureAssociation Configure(IDistributedApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var postgres = builder.AddPostgres("postgres").WithDataVolume();
        var orders = postgres.AddDatabase(FulfillmentStoragePolicy.OrdersDatabaseName);
#pragma warning disable ASPIRECOSMOSDB001 // Local example deliberately uses the cross-platform preview emulator.
        var cosmos = builder.AddAzureCosmosDB(PostgresCosmosStorage.CosmosAccount).RunAsPreviewEmulator();
#pragma warning restore ASPIRECOSMOSDB001
        var database = cosmos.AddCosmosDatabase(PostgresCosmosStorage.InventoryDatabase);
        var inventory = database.AddContainer(PostgresCosmosStorage.InventoryContainer, PostgresCosmosStorage.PartitionPath);
        var worker = builder.AddProject<Projects.PostgresCosmosWorker>("worker")
            .WithHttpEndpoint(env: "ASPNETCORE_HTTP_PORTS").WithReference(orders).WithReference(cosmos)
            .WaitFor(orders).WaitFor(inventory);
        // Storage stays native: the composed reads do not claim a cross-store snapshot.
        var association = builder.AttachCohesive(
            model: this,
            id: new("example/orders-postgres-cosmos/local"),
            variant: new("development"),
            associations => associations.Workload(Worker, worker));
        if (!association.Deployment.IsComplete) throw new InvalidOperationException("Incomplete native resource associations.");
        return association;
    }
}
