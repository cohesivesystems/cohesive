using AspireFirst.Orders;
using AspireFirst.PostgresCosmos;
using Cohesive.Host.Composition;
using Cohesive.Adapters.AspNet;
using Cohesive.Adapters.AspNet.Services;
using Cohesive.Adapters.Postgres;
using Microsoft.Azure.Cosmos;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
var model = ModelActivator.Create<PostgresCosmosApplication>();
builder.Services.AddRequestOperationContext();
builder.Services.AddFixedRequestIdentity(FulfillmentDemoIdentity.Context);
builder.Services.AddCohesiveExceptionHandling();
await using var orders = NpgsqlDataSource.Create(builder.Configuration.GetConnectionString(FulfillmentStoragePolicy.OrdersDatabaseName)
    ?? throw new InvalidOperationException("The Aspire orders database reference is required."));
using var cosmos = new CosmosClient(builder.Configuration.GetConnectionString(PostgresCosmosStorage.CosmosAccount)
    ?? throw new InvalidOperationException("The Aspire Cosmos account reference is required."),
    new CosmosClientOptions { Serializer = new Cohesive.Adapters.Cosmos.CosmosSystemTextJsonSerializer(), ConnectionMode = ConnectionMode.Gateway, LimitToEndpoint = true, RequestTimeout = TimeSpan.FromSeconds(10) });
var inventory = cosmos.GetContainer(PostgresCosmosStorage.InventoryDatabase, PostgresCosmosStorage.InventoryContainer);
var metadata = await inventory.ReadContainerAsync();
if (metadata.Resource.PartitionKeyPath != PostgresCosmosStorage.PartitionPath)
    throw new InvalidOperationException("Inventory must use the declared /partitionKey container path.");
using (var schema = new StreamReader(typeof(PostgresCosmosStorage).Assembly.GetManifestResourceStream("Orders.PostgresCosmos.schema.sql")!))
await using (var initialize = orders.CreateCommand(await schema.ReadToEndAsync()))
    await initialize.ExecuteNonQueryAsync();
var receipts = new PostgresTransitionReceiptOptions("public", "cohesive_process_receipts", FulfillmentStoragePolicy.LocalPartition);
await using (var initialize = orders.CreateCommand(receipts.SchemaSql)) await initialize.ExecuteNonQueryAsync();
var persistence = model.Storage.Bind(orders, inventory, await receipts.BindAsync(orders));
var bindings = model.Endpoints.Build(builder.Services, persistence);
var app = builder.Build();
app.UseExceptionHandler();
app.UseRequestOperationContext();
app.UseAuthorization();
app.MapServices(bindings);
app.Run();
