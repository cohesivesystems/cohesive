using Cohesive.Adapters.Marten;
using Marten;
using JasperFx.Events;
using Cohesive.Host.Composition;
using AspireFirst;
using AspireFirst.Orders;
using Cohesive.Adapters.AspNet;
using Cohesive.Adapters.AspNet.Services;
using Npgsql;
using Cohesive.Storage;

var builder = WebApplication.CreateBuilder(args);
var model = ModelActivator.Create<FulfillmentApplication>(services => services.AddSingleton(new FulfillmentHistoryPolicy
{
    Authority = builder.Configuration.GetValue("EntityState:Authority", EntityStateAuthority.State),
    Reconstitution = builder.Configuration.GetValue("EntityState:Reconstitution", EntityReconstitutionMode.CheckpointAndTail)
}));
builder.Services.AddRequestOperationContext();
builder.Services.AddFixedRequestIdentity(FulfillmentDemoIdentity.Context);
builder.Services.AddCohesiveExceptionHandling();
var connectionString = builder.Configuration.GetConnectionString(FulfillmentStoragePolicy.OrdersDatabaseName)
    ?? throw new InvalidOperationException("The native Aspire orders database reference is required.");
builder.Services.AddKeyedSingleton<NpgsqlDataSource>(FulfillmentStoragePolicy.OrdersDatabaseName,
    (_, _) => NpgsqlDataSource.Create(connectionString));
builder.Services.AddKeyedSingleton<IDocumentStore>(FulfillmentStoragePolicy.OrdersDatabaseName, (services, _) => DocumentStore.For(options =>
{
    options.AutoCreateSchemaObjects = JasperFx.AutoCreate.None;
    options.Connection(services.GetRequiredKeyedService<NpgsqlDataSource>(FulfillmentStoragePolicy.OrdersDatabaseName));
    options.DatabaseSchemaName = "cohesive_events";
    options.Events.StreamIdentity = StreamIdentity.AsString;
    options.Events.AddEventType<EntityStateEvent>();
}));
builder.Services.AddCohesiveServices(model.Endpoints, model.Storage);

await using var app = builder.Build();
app.UseExceptionHandler();
app.UseRequestOperationContext();
app.UseAuthorization();
await app.MapServicesAsync<OrderEndpoints>();
await app.RunAsync();
