using Cohesive.Adapters.Postgres;
using Npgsql.Replication;
using AspireFirst.Materialization;
using Cohesive.Adapters.Marten;
using Marten;
using Cohesive.Application;
using AspireFirst;
using AspireFirst.Orders;
using Cohesive.Adapters.AspNet;
using Cohesive.Adapters.AspNet.Services;
using Npgsql;
using Cohesive.Storage;

var builder = WebApplication.CreateBuilder(args);
var policy = FulfillmentHistoryPolicy.FromConfiguration(builder.Configuration);
var reads = builder.Configuration.GetSection("OrderReads").Get<OrderReadPolicy>() ?? new();
reads.Validate();
var model = ModelActivator.Create<FulfillmentApplication>(services => services.AddSingleton(policy).AddSingleton(reads));
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
// Native clients remain DI-owned; the storage model owns read admission and selection.
if (reads.Source == OrderDetailsReadSource.Materialized)
{
    builder.Services.AddKeyedSingleton<PostgresLogicalReplicationConnectionFactory>(policy.DatabaseName,
        (_, _) => () => new LogicalReplicationConnection(connectionString));
    builder.Services.AddKeyedSingleton<HttpClient>(ShadowElastic.Resource, (_, _) => new HttpClient
    {
        BaseAddress = new Uri(builder.Configuration["Shadow:Elasticsearch"]
            ?? throw new InvalidOperationException("Shadow:Elasticsearch is required for materialized reads."))
    });
}
builder.Services.AddCohesiveServices(model);

await using var app = builder.Build();
app.Logger.LogInformation("OrderDetails read source: {Source}; consistency: {Consistency}. Selection is fixed for this host lifetime.", reads.Source, reads.Consistency);
app.UseExceptionHandler();
app.UseRequestOperationContext();
app.UseAuthorization();
await app.MapServicesAsync();
await app.RunAsync();
