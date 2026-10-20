using AspireFirst.Orders;
using AspireFirst.PostgresCosmos;
using Cohesive.Host.Composition;
using Cohesive.Adapters.AspNet;
using Cohesive.Adapters.AspNet.Services;
using Microsoft.Azure.Cosmos;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
var model = ModelActivator.Create<PostgresCosmosApplication>();
builder.Services.AddRequestOperationContext();
builder.Services.AddFixedRequestIdentity(FulfillmentDemoIdentity.Context);
builder.Services.AddCohesiveExceptionHandling();
builder.Services.AddKeyedSingleton<NpgsqlDataSource>(FulfillmentStoragePolicy.OrdersDatabaseName,
    (_, _) => NpgsqlDataSource.Create(builder.Configuration.GetConnectionString(FulfillmentStoragePolicy.OrdersDatabaseName)
        ?? throw new InvalidOperationException("The Aspire orders database reference is required.")));
builder.Services.AddKeyedSingleton<CosmosClient>(PostgresCosmosStorage.CosmosAccount, (_, _) =>
    new CosmosClient(builder.Configuration.GetConnectionString(PostgresCosmosStorage.CosmosAccount)
        ?? throw new InvalidOperationException("The Aspire Cosmos account reference is required."),
        new CosmosClientOptions { Serializer = new Cohesive.Adapters.Cosmos.CosmosSystemTextJsonSerializer(), ConnectionMode = ConnectionMode.Gateway, LimitToEndpoint = true, RequestTimeout = TimeSpan.FromSeconds(10) }));
builder.Services.AddCohesiveServices(model.Endpoints, model.Storage);

await using var app = builder.Build();
app.UseExceptionHandler();
app.UseRequestOperationContext();
app.UseAuthorization();
await app.MapServicesAsync<OrderEndpoints>();
await app.RunAsync();
