using Cohesive.Identity;
using Cohesive.Adapters.FeatureManagement;
using Cohesive.Relations.Execution;
using Cohesive.Execution;
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
using Cohesive.Adapters.DurableTask;
using Microsoft.DurableTask.Client.AzureManaged;
using Microsoft.DurableTask.Worker.AzureManaged;
using Cohesive.Storage;

var builder = WebApplication.CreateBuilder(args);
var policy = FulfillmentHistoryPolicy.FromConfiguration(builder.Configuration);
var reads = builder.Configuration.GetSection("OrderReads").Get<RelationQueryReadPolicy>() ?? new();
reads.Validate();
var model = ModelActivator.Create<FulfillmentApplication>(services => services.AddSingleton(policy).AddSingleton(reads).AddSingleton(new FulfillmentExecutionPolicy { Lifetime = ProcessExecutionLifetime.Durable }));
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
if (reads.UsesMaterializedReads)
{
    builder.Services.AddKeyedSingleton<PostgresLogicalReplicationConnectionFactory>(policy.DatabaseName,
        (_, _) => () => new LogicalReplicationConnection(connectionString));
    builder.Services.AddKeyedSingleton<HttpClient>(ShadowElastic.Resource, (_, _) => new HttpClient
    {
        BaseAddress = new Uri(builder.Configuration["Shadow:Elasticsearch"]
            ?? throw new InvalidOperationException("Shadow:Elasticsearch is required for materialized reads."))
    });
}
var scheduler = builder.Configuration.GetConnectionString(FulfillmentExecutionPolicy.SchedulerResource)
    ?? throw new InvalidOperationException("The native Aspire fulfillment scheduler reference is required.");
builder.Services.AddCohesiveDurableTaskServices(
    configureClient: client => client.UseDurableTaskScheduler(scheduler),
    configureWorker: worker => worker.UseDurableTaskScheduler(scheduler));
builder.Services.AddCohesiveFeatureManagement<IdentityContext>();
builder.Services.AddCohesiveServices(model);

await using var app = builder.Build();
app.Logger.LogInformation("OrderDetails read source: {Source}. An optional feature flag is evaluated per read.", reads.Source);
app.UseExceptionHandler();
app.UseRequestOperationContext();
app.UseAuthorization();
await app.MapServicesAsync();
await app.RunAsync();
