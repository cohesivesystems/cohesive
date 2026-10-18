using AspireFirst.Orders;
using AspireFirst.PostgresCosmos;
using Cohesive.Host.Composition;
using Cohesive.Adapters.AspNet;
using Cohesive.Adapters.AspNet.Services;
using Cohesive.Api.Execution.Services;
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
// Explicit demo migration; composition binding validates receipt and container metadata afterward.
builder.Services.AddCohesiveServices(model.Endpoints, async (services, cancellationToken) =>
{
    var orders = services.GetRequiredKeyedService<NpgsqlDataSource>(FulfillmentStoragePolicy.OrdersDatabaseName);
    using (var schema = new StreamReader(typeof(PostgresCosmosStorage).Assembly.GetManifestResourceStream("Orders.PostgresCosmos.schema.sql")!))
    await using (var initialize = orders.CreateCommand(await schema.ReadToEndAsync(cancellationToken)))
        await initialize.ExecuteNonQueryAsync(cancellationToken);
    return await model.Storage.RequireComposition<IServiceStorageComposition>().BindAsync(services, cancellationToken);
});
await using var app = builder.Build();
app.UseExceptionHandler();
app.UseRequestOperationContext();
app.UseAuthorization();
await app.MapServicesAsync<OrderEndpoints>();
await app.RunAsync();
