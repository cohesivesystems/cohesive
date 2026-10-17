using Aspire.Hosting;
using AspireFirst.Orders;
using AspireFirst.PostgresCosmos;
using Cohesive.Host.Composition;
using Cohesive.Model;
using Cohesive.Storage;
using Cohesive.Adapters.Cosmos;
using Cohesive.Adapters.Postgres;
using Cohesive.Api.Execution.Services;
using Cohesive.Relations.Compilation;
using Microsoft.Azure.Cosmos;
using Npgsql;

namespace Cohesive.Adapters.Aspire.Tests;

public sealed class PostgresCosmosCompositionTests
{
    internal const string EmulatorKey = "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==";
    static CosmosClient Client() => new("http://127.0.0.1:1", EmulatorKey, new CosmosClientOptions { ConnectionMode = ConnectionMode.Gateway });

    [Fact]
    public void Both_compositions_share_semantic_contracts_but_only_one_places_inventory_in_Postgres()
    {
        var single = ModelActivator.Create<FulfillmentApplication>();
        var mixed = ModelActivator.Create<PostgresCosmosApplication>();
        Assert.Equal(single.Queries.OrderDetails.CompilationRequest.DefinitionDocument.DefinitionFingerprint,
            mixed.Queries.OrderDetails.CompilationRequest.DefinitionDocument.DefinitionFingerprint);
        Assert.Equal(single.Api.Definition.Endpoints.Select(e => e.Id), mixed.Api.Definition.Endpoints.Select(e => e.Id));
        Assert.Throws<ArgumentException>(() => mixed.Storage.Orders.Mapping(mixed.Domain.Inventory));
        Assert.NotNull(single.Storage.Bindings.Mapping(single.Domain.Inventory));
        Assert.Single(single.Storage.Definition);
        Assert.Equal(2, mixed.Storage.Definition.Length);
    }

    [Fact]
    public void Both_shared_queries_prepare_against_Cosmos_without_IO_and_wrong_partition_is_rejected()
    {
        var model = ModelActivator.Create<PostgresCosmosApplication>();
        using var database = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=orders;Username=test");
        using var client = Client();
        var inventory = client.GetContainer(PostgresCosmosStorage.InventoryDatabase, PostgresCosmosStorage.InventoryContainer);
        var source = CosmosEntityRelationQuerySourceRegistration.Create(model.Domain.Inventory.Definition.StateShape.QualifiedId,
            inventory, inventory.Database.Id, inventory.Id, model.Storage.InventoryPolicy,
            identitySemanticPath: FieldPath.FromField(FulfillmentDomain.InventoryIdField));
        var catalog = new EntityRelationQuerySourceCatalog([source]);
        var orders = model.Storage.Orders.Bind(new(new("orders"), database, "test"));
        Assert.Same(model.Queries.OrderDetails, orders.QueryComposed(model.Queries.OrderDetails, catalog, PostgresCosmosStorage.Policy(1000, 1_000_000)).Definition);
        Assert.Same(model.Queries.ReservationAvailability, orders.QueryComposed(model.Queries.ReservationAvailability, catalog,
            PostgresCosmosStorage.Policy(1000, 1_000_000), model.Queries.ReservationDemandProjection).Definition);
        var missing = Assert.Throws<RelationQueryPreparationException>(() => orders.QueryComposed(model.Queries.OrderDetails,
            new EntityRelationQuerySourceCatalog([]), PostgresCosmosStorage.Policy(1000, 1_000_000)));
        Assert.Equal("postgres.composed.remoteMappingMissing", missing.Code);
        var full = new FulfillmentStorage(model.Domain, new FulfillmentPostgresMappings(model.Domain)).Bind(database);
        var ambiguous = Assert.Throws<RelationQueryPreparationException>(() => full.QueryComposed(model.Queries.OrderDetails,
            catalog, PostgresCosmosStorage.Policy(1000, 1_000_000)));
        Assert.Equal("postgres.composed.placementAmbiguous", ambiguous.Code);
        var wrong = CosmosEntityRelationQuerySourceRegistration.Create(model.Domain.Inventory.Definition.StateShape.QualifiedId,
            inventory, inventory.Database.Id, inventory.Id, new("partitionKey", new("other"), fixedPartitionKey: new PartitionKey("other")));
        var error = Assert.Throws<RelationQueryPreparationException>(() => orders.QueryComposed(model.Queries.OrderDetails,
            new EntityRelationQuerySourceCatalog([wrong]), PostgresCosmosStorage.Policy(1000, 1_000_000)));
        Assert.Equal("postgres.composed.partitionMismatch", error.Code);
    }

    [Fact]
    public void Separate_AppHost_authors_native_Cosmos_and_Postgres_without_starting_resources()
    {
        var model = ModelActivator.Create<PostgresCosmosFulfillmentInfrastructure>();
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { Args = [], DisableDashboard = true });
        var association = model.Configure(builder);
        Assert.True(association.Deployment.IsComplete);
        Assert.Contains(builder.Resources, resource => resource.Name == PostgresCosmosStorage.CosmosAccount);
        Assert.Contains(builder.Resources, resource => resource.Name == FulfillmentStoragePolicy.OrdersDatabaseName);
    }

    [Fact]
    public void Explicit_service_query_selection_is_cached_and_frozen()
    {
        var model = ModelActivator.Create<FulfillmentApplication>();
        using var database = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=orders;Username=test");
        var postgres = model.Storage.Bind(database);
        var calls = 0;
        var builder = new ServicePersistenceBuilder().Query(model.Queries.OrderDetails, (rows, bytes) =>
        { calls++; return postgres.Query(model.Queries.OrderDetails, rows, bytes); });
        var resolver = builder.Build();
        var reader = resolver.Query(model.Queries.OrderDetails, 1000, 1_000_000);
        Assert.Same(reader, resolver.Query(model.Queries.OrderDetails, 1000, 1_000_000));
        Assert.Equal(1, calls);
        Assert.Throws<InvalidOperationException>(() => builder.Query(model.Queries.OrderDetails, (r, b) => reader));
    }
}
