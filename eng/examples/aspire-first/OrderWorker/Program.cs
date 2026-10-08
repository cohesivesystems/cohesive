using AspireFirst.Orders;
using Cohesive.Adapters.AspNet;
using Cohesive.Storage;
using Cohesive.Adapters.Postgres;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRequestOperationContext();
builder.Services.AddSingleton<IHttpOperationContextEnricher, FulfillmentDemoIdentity>();
builder.Services.AddCohesiveExceptionHandling();
var connectionString = builder.Configuration.GetConnectionString(FulfillmentStorage.DatabaseName)
    ?? throw new InvalidOperationException("The native Aspire orders database reference is required.");
await using var database = NpgsqlDataSource.Create(connectionString);
var persistence = FulfillmentStorage.Bind(database);
var receipts = new PostgresTransitionReceiptOptions("public", "cohesive_process_receipts", FulfillmentDemo.LocalPartition);
IEntityRepository<Order> orders = persistence.Repository(FulfillmentDomain.Orders, transitionReceipts: receipts);
IEntityRepository<InventoryItem> inventory = persistence.Repository(FulfillmentDomain.Inventory, transitionReceipts: receipts);

// The shared repository owns DML and validation, not schema lifecycle.
using (var schema = new StreamReader(typeof(FulfillmentStorage).Assembly.GetManifestResourceStream("Orders.schema.sql")
    ?? throw new InvalidOperationException("The explicit order schema resource is missing.")))
await using (var initialize = database.CreateCommand(await schema.ReadToEndAsync()))
    await initialize.ExecuteNonQueryAsync();

await using (var initializeReceipts = database.CreateCommand(receipts.SchemaSql))
    await initializeReceipts.ExecuteNonQueryAsync();
await receipts.ValidateSchemaAsync(database);

// Prepare once and pass the contracts directly to their sole consumer.
var details = persistence.Query(FulfillmentQueries.OrderDetails, maximumRows: 1000, maximumBytes: 1_000_000);
var availability = ReservationAvailabilityQueryBindings.BindNative(persistence);
var fulfillment = new FulfillmentProcessBindings(orders, inventory);
var app = builder.Build();
app.UseExceptionHandler();
app.UseRequestOperationContext();
OrderEndpoints.Map(app, orders, details, availability);
fulfillment.Map(app);
app.Run();
