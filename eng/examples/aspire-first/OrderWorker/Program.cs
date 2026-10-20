using Cohesive.Adapters.Marten;
using Marten;
using Cohesive.Host.Composition;
using AspireFirst;
using AspireFirst.Orders;
using Cohesive.Adapters.AspNet;
using Cohesive.Adapters.AspNet.Services;
using Npgsql;
using Cohesive.Storage;

var builder = WebApplication.CreateBuilder(args);
var policy = FulfillmentHistoryPolicy.FromConfiguration(builder.Configuration);
var model = ModelActivator.Create<FulfillmentApplication>(services => services.AddSingleton(policy));
builder.Services.AddRequestOperationContext();
builder.Services.AddFixedRequestIdentity(FulfillmentDemoIdentity.Context);
builder.Services.AddCohesiveExceptionHandling();
var connectionString = builder.Configuration.GetConnectionString(FulfillmentStoragePolicy.OrdersDatabaseName)
    ?? throw new InvalidOperationException("The native Aspire orders database reference is required.");
builder.Services.AddKeyedSingleton<NpgsqlDataSource>(policy.DatabaseName,
    (_, _) => NpgsqlDataSource.Create(connectionString));
builder.Services.AddKeyedSingleton<IDocumentStore>(policy.DatabaseName, (services, _) => DocumentStore.For(options =>
{
    options.Connection(services.GetRequiredKeyedService<NpgsqlDataSource>(policy.DatabaseName));
    policy.History.Configure(options);
}));
builder.Services.AddCohesiveServices(model.Endpoints, model.Storage);

await using var app = builder.Build();
app.UseExceptionHandler();
app.UseRequestOperationContext();
app.UseAuthorization();
await app.MapServicesAsync<OrderEndpoints>();
await app.RunAsync();
