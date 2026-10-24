using AspireFirst.Orders;
using Cohesive.Adapters.AspNet.Services;
using Cohesive.Adapters.Postgres;
using Cohesive.Adapters.Postgres.Services;
using Cohesive.Api;
using Cohesive.Api.Execution.Services;
using Cohesive.Host.Composition;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.Execution;
using Cohesive.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Cohesive.Adapters.Aspire.Tests;

public sealed class ApplicationStoragePlacementTests
{
    sealed class ReadApi<T> : ApiModel where T : class
    {
        public ReadApi(string name) => Read = Endpoints.Query(name).Route("GET", "/" + name + "/{id}")
            .RouteParameter<string>("id").Result(ApiResultKind.NotFound).Build<T>();
        public ApiEndpoint<T> Read { get; }
    }
    sealed class ReadModule<T> : ApiBindingModel where T : class
    {
        public ReadModule(string name, RelationQuery<string, T?> query, RelationQueryConsistency consistency = RelationQueryConsistency.SingleSnapshot) =>
            Bindings.Implement(new ReadApi<T>(name), binding => binding.Bind(api => api.Read, query, new("1"), 1000, 1_000_000,
                route => route.FromRoute<string>("id", value => value).OkOrNotFound(), consistency));
    }
    sealed class Application : ApplicationModel
    {
        public required ApiBindingModel Orders { get; init; }
        public required ApiBindingModel Inventory { get; init; }
        public required StorageModel Storage { get; init; }
    }
    // No endpoint/query plan dependency: the exact same store supports either module or both.
    sealed class NativeStore : StorageModel
    {
        public int Preparations;
        public NativeStore(FulfillmentDomain domain, FulfillmentPostgresMappings mappings, string schema = "public")
        {
            PostgresEntityRepositoryMapping Relocate(PostgresEntityRepositoryMapping mapping) =>
                new(new Cohesive.Adapters.Sql.SqlQualifiedTable(schema, mapping.Table.TableName.Value),
                    mapping.Fields, mapping.IdentityField, mapping.PartitionField, mapping.VersionColumn.Value);
            var bindings = Storage.Define(() => PostgresPersistenceBindings.Define(new("orders"), storage => storage
                .Schema(PostgresSchemaInitialization.CreateIfMissing)
                .SchemaPreparation((_, _) => { Preparations++; return Task.CompletedTask; })
                .Entity(domain.Orders, Relocate(mappings.Orders)).Entity(domain.Reservations, Relocate(mappings.Reservations))
                .Entity(domain.Inventory, Relocate(mappings.Inventory))));
            Storage.Compose(() => new PostgresStorageComposition(bindings));
        }
    }
    static Application Root(ApiBindingModel orders, ApiBindingModel inventory, StorageModel storage) =>
        new ApplicationModelBuilder(new ServiceCollection().AddSingleton(new Application
        { Orders = orders, Inventory = inventory, Storage = storage })).Build().Get<Application>();

    [OrderStorageIntegrationTests.PostgresFact]
    public async Task Independent_modules_share_unchanged_placement_and_prepare_database_once()
    {
        var schema = "module_placement_" + Guid.NewGuid().ToString("N");
        await using var database = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("COHESIVE_ORDER_EXAMPLE_TEST_CONNECTION_STRING")!);
        try
        {
            await using (var create = database.CreateCommand($"CREATE SCHEMA {schema}"))
                await create.ExecuteNonQueryAsync();
            var domain = new FulfillmentDomain();
            var queries = new FulfillmentQueries(domain);
            var storage = new NativeStore(domain, new FulfillmentPostgresMappings(domain), schema);
            var orders = new ReadModule<OrderDetails>("details", queries.OrderDetails);
            var inventory = new ReadModule<ReservationAvailability[]>("availability", queries.ReservationAvailability);
            var model = Root(orders, inventory, storage);
            var builder = WebApplication.CreateBuilder();
            builder.Services.AddKeyedSingleton<NpgsqlDataSource>("orders", database);
            builder.Services.AddCohesiveServices(model);
            Assert.Equal(0, storage.Preparations);
            await using var app = builder.Build();
            await app.MapServicesAsync();
            Assert.Equal(1, storage.Preparations);
            app.Urls.Add("http://127.0.0.1:0"); await app.StartAsync();
            using var client = new HttpClient { BaseAddress = new(app.Urls.Single()) };
            var id = Guid.NewGuid().ToString("D");
            Assert.Equal(System.Net.HttpStatusCode.NotFound, (await client.GetAsync("/details/" + id)).StatusCode);
            Assert.Equal("[]", await client.GetStringAsync("/availability/" + id));
            Assert.Equal(1, storage.Preparations);
            await app.StopAsync();
        }
        finally
        {
            await using var cleanup = database.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public void Rebuilt_query_is_owned_by_the_same_canonical_placement()
    {
        var domain = new FulfillmentDomain();
        var first = new FulfillmentQueries(domain).OrderDetails;
        var rebuilt = new FulfillmentQueries(domain).OrderDetails;
        Assert.NotSame(first.CompilationRequest, rebuilt.CompilationRequest);
        var storage = new NativeStore(domain, new FulfillmentPostgresMappings(domain));
        var model = Root(new ReadModule<OrderDetails>("first", first), new ReadModule<OrderDetails>("second", rebuilt), storage);
        new ServiceCollection().AddCohesiveServices(model);
        Assert.Equal(0, storage.Preparations);
    }

    [Fact]
    public void Composed_placement_cannot_weaken_a_modules_snapshot_requirement()
    {
        var mixed = ModelActivator.Create<AspireFirst.PostgresCosmos.PostgresCosmosApplication>();
        var model = Root(new ReadModule<OrderDetails>("details", mixed.Queries.OrderDetails),
            new ReadModule<ReservationAvailability[]>("availability", mixed.Queries.ReservationAvailability), mixed.Storage);
        var services = new ServiceCollection(); // Deliberately no native clients.
        var error = Assert.Throws<ServiceBindingValidationException>(() => services.AddCohesiveServices(model));
        Assert.Contains(error.Validation.Diagnostics, item => item.Code == ServiceBindingDiagnosticCodes.QueryPlacementUnsupported);
        Assert.Empty(services);
    }

    [Fact]
    public void Incidental_transitive_storage_does_not_participate_in_root_ownership()
    {
        var domain = new FulfillmentDomain(); var mappings = new FulfillmentPostgresMappings(domain);
        var queries = new FulfillmentQueries(domain);
        var store = new NativeStore(domain, mappings); var incidental = new NativeStore(domain, mappings);
        var root = new Application { Orders = new ReadModule<OrderDetails>("first", queries.OrderDetails),
            Inventory = new ReadModule<ReservationAvailability[]>("second", queries.ReservationAvailability), Storage = store };
        var model = new ApplicationModelBuilder(new ServiceCollection().AddSingleton(root)
            .AddSingleton(incidental)).Build().Get<Application>();
        new ServiceCollection().AddCohesiveServices(model);
        Assert.Equal(0, incidental.Preparations);
        Assert.Same(store, Assert.Single(model.Components<StorageModel>()));
    }

    [Fact]
    public void Missing_process_references_report_every_exact_dependency_before_native_preparation()
    {
        var model = TestModels.Fulfillment();
        var error = Assert.Throws<ServiceBindingValidationException>(() =>
            new ServiceDependencies().RepositoryRequirements(model.Process.Definition.Definition));
        Assert.True(error.Validation.Diagnostics.Length > 1);
        Assert.All(error.Validation.Diagnostics, diagnostic =>
        {
            Assert.Contains("Missing exact dependency:", diagnostic.Message);
            Assert.Contains("@", diagnostic.Message);
            Assert.Contains("[", diagnostic.Message);
        });
    }
}
