using Cohesive.Adapters.Aspire.Cosmos;
using Cohesive.Adapters.Postgres.Services;
using AspireFirst.Orders;
using Aspire.Hosting;
using Cohesive.Infra;
using Cohesive.Adapters.Aspire;

namespace AspireFirst.PostgresCosmos;

/// <summary>Separate native host: PostgreSQL orders plus Cosmos inventory, with no distributed snapshot claim.</summary>
public sealed class PostgresCosmosFulfillmentInfrastructure : InfrastructureModel
{
    readonly PostgresComposedServicePreparation prepared;
    /// <summary>Canonical shared application workload identity.</summary>
    public static InfrastructureNodeId Worker { get; } = new("workloads/orders");

    /// <summary>Native order database declaration.</summary>
    public static InfrastructureNodeId Orders { get; } = new("resources/orders");
    /// <summary>Native inventory container declaration.</summary>
    public static InfrastructureNodeId Inventory { get; } = new("resources/inventory");

    /// <summary>Activated mixed-store application graph.</summary>
    public PostgresCosmosApplication Application { get; }
    /// <summary>Declares the workload requirement for the shared application.</summary>
    /// <param name="model">Activated application composition.</param>
    public PostgresCosmosFulfillmentInfrastructure(PostgresCosmosApplication model)
    {
        Application = model;
        prepared = model.Storage.RequireComposition<PostgresComposedStorageComposition>().PrepareEvidence(model.Endpoints.Plan.QueryDependencies.Queries);
        Define(
            id: new("example/orders-postgres-cosmos"),
            revision: new("1"),
            bindingProfileId: new("example/orders-postgres-cosmos/bindings"),
            infra =>
            {
                infra.Resource(Orders).Persistent().Prepared(prepared.Postgres);
                infra.Resource(Inventory).Persistent().Prepared(prepared.Sources[model.Storage.Inventory.Query.Binding.Source.Id]);
                var worker = infra.Workload(Worker).Requires(AspireInfrastructure.DotNetProjectExecution)
                    .RequiresReady(Orders).RequiresReady(Inventory);
                foreach (var requirement in prepared.Requirements) worker.Requires(requirement);
            });
    }

    /// <summary>Uses shared offline report projection and complete-deployment validation.</summary>
    /// <param name="association">Association produced for this infrastructure.</param>
    /// <returns>The linked inspection report; this composition has no history attachment.</returns>
    public Cohesive.Application.ApplicationExplanationDocument Explain(AspireInfrastructureAssociation association) =>
        Cohesive.Application.ApplicationExplanation.Create(Application, Definition, association.Deployment,
            "development", Cohesive.Model.SourceReference.Create("example", Definition.Definition.Definition.Id.Value));

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
        var inventory = database.AddContainer(PostgresCosmosStorage.InventoryContainer, Application.Storage.Inventory.PartitionKeyPath);
        var worker = builder.AddProject<Projects.PostgresCosmosWorker>("worker")
            .WithHttpEndpoint(env: "ASPNETCORE_HTTP_PORTS").WithReference(orders).WithReference(cosmos)
            .WaitFor(orders).WaitFor(inventory);
        // Each source supplies acquisition evidence; only the worker composes the independent observations.
        var association = builder.AttachCohesive(
            model: this,
            id: new("example/orders-postgres-cosmos/local"),
            variant: new("development"),
            associations =>
            {
                associations.Resource(Orders, orders, prepared.Postgres.ForSource(orders.Resource.DatabaseName), new("aspire/example/orders"));
                associations.Resource(Inventory, inventory, Application.Storage.Inventory, new("aspire/example/inventory"));
                associations.Workload(Worker, worker, executionEvidence: prepared.ExecutionEvidence);
            });
        if (!association.Deployment.IsComplete) throw new InvalidOperationException(string.Join("; ", association.Deployment.Diagnostics.Select(item => item.Code + ": " + item.Message)));
        return association;
    }
}
