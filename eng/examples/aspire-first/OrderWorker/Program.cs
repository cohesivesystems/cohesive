using Cohesive.Adapters.Marten;
using Marten;
using JasperFx.Events;
using Cohesive.Host.Composition;
using AspireFirst;
using AspireFirst.Orders;
using Cohesive.Adapters.AspNet;
using Cohesive.Adapters.AspNet.Services;
using Cohesive.Adapters.Postgres;
using Cohesive.Adapters.Postgres.Services;
using Npgsql;
using Cohesive.Storage;

var builder = WebApplication.CreateBuilder(args);
var model = ModelActivator.Create<FulfillmentApplication>();
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
// The application explicitly owns demo schema initialization; library binding only validates it.
builder.Services.AddCohesiveServices(model.Endpoints, async (services, cancellationToken) =>
{
    var database = services.GetRequiredKeyedService<NpgsqlDataSource>(FulfillmentStoragePolicy.OrdersDatabaseName);
    var receipts = model.Storage.Bindings.ReceiptOptions!;

    // The shared repository owns DML and validation, not schema lifecycle.
    using (var schema = new StreamReader(typeof(FulfillmentApplication).Assembly.GetManifestResourceStream("Orders.schema.sql")
        ?? throw new InvalidOperationException("The explicit order schema resource is missing.")))
    await using (var initialize = database.CreateCommand(await schema.ReadToEndAsync(cancellationToken)))
        await initialize.ExecuteNonQueryAsync(cancellationToken);

    await using (var initializeReceipts = database.CreateCommand(receipts.SchemaSql))
        await initializeReceipts.ExecuteNonQueryAsync(cancellationToken);
    // Authority is a storage choice. Commands, transitions, queries and API contracts stay unchanged.
    // Use a fresh database when changing authority; an existing history requires explicit migration.
    var authority = builder.Configuration.GetValue("EntityState:Authority", EntityStateAuthority.State);
    var replay = builder.Configuration.GetValue("EntityState:Reconstitution", EntityReconstitutionMode.CheckpointAndTail);
    var eventStore = services.GetRequiredKeyedService<IDocumentStore>(FulfillmentStoragePolicy.OrdersDatabaseName);
    await eventStore.Storage.ApplyAllConfiguredChangesToDatabaseAsync();
    var historyStorage = await MartenEntityHistoryStorage.BindAsync(database, eventStore, authority, replay);
    var outbox = new PostgresEntityOutboxOptions("public", "cohesive_transition_outbox");
    await using (var initializeOutbox = database.CreateCommand(outbox.SchemaSql))
        await initializeOutbox.ExecuteNonQueryAsync(cancellationToken);
    var outboxStorage = await outbox.BindAsync(database);
    return await model.Storage.RequireComposition<PostgresStorageComposition>().BindAsync(services, historyStorage, outboxStorage, cancellationToken);
});

await using var app = builder.Build();
app.UseExceptionHandler();
app.UseRequestOperationContext();
app.UseAuthorization();
await app.MapServicesAsync<OrderEndpoints>();
await app.RunAsync();
