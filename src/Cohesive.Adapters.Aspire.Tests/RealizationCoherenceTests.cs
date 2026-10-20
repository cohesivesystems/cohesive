using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using AspireFirst;
using AspireFirst.Orders;
using AspireFirst.PostgresCosmos;
using Cohesive.Adapters.Aspire.Cosmos;
using Cohesive.Adapters.Cosmos;
using Cohesive.Adapters.Marten;
using Cohesive.Host.Composition;
using Cohesive.Storage;
using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Cohesive.Adapters.Aspire.Tests;

public sealed class RealizationCoherenceTests
{
    [Theory]
    [InlineData(EntityStateAuthority.State, EntityReconstitutionMode.FullReplay, "orders")]
    [InlineData(EntityStateAuthority.Events, EntityReconstitutionMode.CheckpointAndTail, "orders_events")]
    public async Task Both_executables_select_the_same_model_and_native_database(EntityStateAuthority authority,
        EntityReconstitutionMode reconstitution, string databaseName)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["EntityState:Authority"] = authority.ToString(),
            ["EntityState:Reconstitution"] = reconstitution.ToString()
        }).Build();
        var hostPolicy = FulfillmentHistoryPolicy.FromConfiguration(configuration);
        var workerPolicy = FulfillmentHistoryPolicy.FromConfiguration(configuration);
        var host = ModelActivator.Create<FulfillmentInfrastructure>(services => services.AddSingleton(hostPolicy));
        var worker = ModelActivator.Create<FulfillmentApplication>(services => services.AddSingleton(workerPolicy));
        Assert.Equal(databaseName, worker.Storage.Bindings.Database.Value);
        var history = Assert.IsType<MartenEntityHistoryConfiguration>(worker.Storage.Bindings.HistoryConfiguration);
        Assert.Equal(authority, history.Authority);
        Assert.Equal(reconstitution, history.Reconstitution);
        Assert.Same(workerPolicy.History, history);
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { Args = [], DisableDashboard = true });
        var association = host.Configure(builder);
        Assert.True(association.Deployment.IsComplete);
        Assert.Equal(databaseName, Assert.IsType<PostgresDatabaseResource>(association.Resources[FulfillmentInfrastructure.Store]).DatabaseName);
        var nativeWorker = association.Resources[FulfillmentInfrastructure.Worker];
        var environment = new Dictionary<string, object>();
        var context = new EnvironmentCallbackContext(builder.ExecutionContext, nativeWorker, environment, CancellationToken.None);
        foreach (var callback in nativeWorker.Annotations.OfType<EnvironmentCallbackAnnotation>())
            await callback.Callback(context);
        Assert.Equal(authority.ToString(), environment["EntityState__Authority"]);
        Assert.Equal(reconstitution.ToString(), environment["EntityState__Reconstitution"]);
        using var app = builder.Build();
    }

    [Theory]
    [InlineData("EntityState:Authority", "unknown")]
    [InlineData("EntityState:Authority", "99")]
    [InlineData("EntityState:Reconstitution", "unknown")]
    public void Invalid_policy_fails_at_shared_configuration_boundary(string key, string value)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [key] = value }).Build();
        Assert.Contains(key, Assert.Throws<ArgumentException>(() => FulfillmentHistoryPolicy.FromConfiguration(configuration)).Message);
    }

    [Fact]
    public void Environment_policy_is_never_silently_default_constructed()
    {
        Assert.Contains(nameof(FulfillmentHistoryPolicy), Assert.Throws<InvalidOperationException>(() =>
            ModelActivator.Create<FulfillmentApplication>()).Message);
        Assert.Contains(nameof(FulfillmentHistoryPolicy), Assert.Throws<InvalidOperationException>(() =>
            ModelActivator.Create<FulfillmentInfrastructure>()).Message);
        Assert.NotNull(ModelActivator.Create<FulfillmentApplication>(services => services.AddSingleton(new FulfillmentHistoryPolicy())));
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("event-schema")]
    [InlineData("auto-create")]
    [InlineData("event-type")]
    [InlineData("stream-id")]
    public async Task Marten_expectations_reject_mismatch_before_database_IO(string mismatch)
    {
        await using var database = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=unused;Username=unused;Timeout=1");
        var policy = new MartenEntityHistoryConfiguration(schemaName: "expected_history");
        using var store = DocumentStore.For(options =>
        {
            options.Connection(database);
            if (mismatch == "event-type")
            {
                options.AutoCreateSchemaObjects = JasperFx.AutoCreate.None;
                options.DatabaseSchemaName = policy.SchemaName;
                options.Events.StreamIdentity = JasperFx.Events.StreamIdentity.AsString;
            }
            else policy.Configure(options);
            if (mismatch == "schema") options.DatabaseSchemaName = "other";
            if (mismatch == "event-schema") options.Events.DatabaseSchemaName = "other";
            if (mismatch == "auto-create") options.AutoCreateSchemaObjects = JasperFx.AutoCreate.CreateOrUpdate;
            if (mismatch == "stream-id") options.Events.StreamIdentity = JasperFx.Events.StreamIdentity.AsGuid;
        });
        await using var provider = new ServiceCollection().AddKeyedSingleton<IDocumentStore>("orders", store).BuildServiceProvider();
        await Assert.ThrowsAsync<ArgumentException>(() => policy.PrepareAsync(provider, "orders", database).AsTask());
    }

    [Theory]
    [InlineData("account")]
    [InlineData("database")]
    [InlineData("container")]
    [InlineData("partition")]
    [InlineData("source")]
    public void Cosmos_association_rejects_wrong_native_placement_or_prepared_source(string mismatch)
    {
        var model = ModelActivator.Create<PostgresCosmosFulfillmentInfrastructure>();
        var storage = model.Application.Storage.Inventory;
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { Args = [], DisableDashboard = true });
        var account = builder.AddAzureCosmosDB(mismatch == "account" ? "wrong" : storage.Query.Account);
        var database = account.AddCosmosDatabase(mismatch == "database" ? "wrong" : storage.Query.Database);
        var container = database.AddContainer(mismatch == "container" ? "wrong" : storage.Query.Container,
            mismatch == "partition" ? "/wrong" : storage.PartitionKeyPath);
        if (mismatch == "source")
            storage = new CosmosEntityStorageBinding(model.Application.Domain.Inventory.Definition, FulfillmentDomain.InventoryIdField,
                storage.Query.Account, storage.Query.Database, storage.Query.Container, new(FulfillmentStoragePolicy.LogicalPartition),
                FulfillmentStoragePolicy.LocalPartition, FulfillmentDomain.PartitionField, maximumEnumerationRows: 7);
        Assert.Throws<ArgumentException>(() => builder.AttachCohesive(model, new("test/association"), new("test"),
            associations => associations.Resource(PostgresCosmosFulfillmentInfrastructure.Inventory, container, storage)));
        using var app = builder.Build();
    }
}
