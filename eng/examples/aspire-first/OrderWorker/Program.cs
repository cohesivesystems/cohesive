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
await using var database = NpgsqlDataSource.Create(connectionString);
var persistence = model.Storage.Bindings.Bind(database, authority: "aspire-first/apphost");
var receipts = new PostgresTransitionReceiptOptions("public", "cohesive_process_receipts", FulfillmentStoragePolicy.LocalPartition);

// The shared repository owns DML and validation, not schema lifecycle.
using (var schema = new StreamReader(typeof(FulfillmentApplication).Assembly.GetManifestResourceStream("Orders.schema.sql")
    ?? throw new InvalidOperationException("The explicit order schema resource is missing.")))
await using (var initialize = database.CreateCommand(await schema.ReadToEndAsync()))
    await initialize.ExecuteNonQueryAsync();

await using (var initializeReceipts = database.CreateCommand(receipts.SchemaSql))
    await initializeReceipts.ExecuteNonQueryAsync();
var receiptStorage = await receipts.BindAsync(database);
// Authority is a storage choice. Commands, transitions, queries and API contracts stay unchanged.
// Use a fresh database when changing authority; an existing history requires explicit migration.
var authority = builder.Configuration.GetValue("EntityState:Authority", EntityStateAuthority.State);
var replay = builder.Configuration.GetValue("EntityState:Reconstitution", EntityReconstitutionMode.CheckpointAndTail);
using var eventStore = DocumentStore.For(options =>
{
    options.AutoCreateSchemaObjects = JasperFx.AutoCreate.None;
                options.Connection(database);
    options.DatabaseSchemaName = "cohesive_events";
    options.UseCohesiveEntityHistory();
});
await eventStore.Storage.ApplyAllConfiguredChangesToDatabaseAsync();
var historyStorage = await MartenEntityHistoryStorage.BindAsync(database, eventStore, authority, replay);
var outbox = new PostgresEntityOutboxOptions("public", "cohesive_transition_outbox");
await using (var initializeOutbox = database.CreateCommand(outbox.SchemaSql))
    await initializeOutbox.ExecuteNonQueryAsync();
var outboxStorage = await outbox.BindAsync(database);
var services = model.Endpoints.Build(builder.Services, persistence.ForServices(receiptStorage, historyStorage, outboxStorage));

var app = builder.Build();
app.UseExceptionHandler();
app.UseRequestOperationContext();
app.UseAuthorization();
app.MapServices(services);
app.Run();
