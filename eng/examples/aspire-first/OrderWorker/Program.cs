using AspireFirst.Orders;
using Cohesive.Adapters.AspNet;
using Cohesive.Adapters.AspNet.Services;
using Cohesive.Host.Services;
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

// The shared repository owns DML and validation, not schema lifecycle.
using (var schema = new StreamReader(typeof(FulfillmentStorage).Assembly.GetManifestResourceStream("Orders.schema.sql")
    ?? throw new InvalidOperationException("The explicit order schema resource is missing.")))
await using (var initialize = database.CreateCommand(await schema.ReadToEndAsync()))
    await initialize.ExecuteNonQueryAsync();

await using (var initializeReceipts = database.CreateCommand(receipts.SchemaSql))
    await initializeReceipts.ExecuteNonQueryAsync();
var receiptStorage = await receipts.BindAsync(database);
IEntityRepository<Order> orders = persistence.Repository(FulfillmentDomain.Orders, transitionReceipts: receiptStorage);
IEntityRepository<InventoryItem> inventory = persistence.Repository(FulfillmentDomain.Inventory, transitionReceipts: receiptStorage);


// Prepare once and pass the contracts directly to their sole consumer.
var details = persistence.Query(FulfillmentQueries.OrderDetails, maximumRows: 1000, maximumBytes: 1_000_000);
var availability = ReservationAvailabilityQueryBindings.BindNative(persistence);
var fulfillment = FulfillmentProcessBindings.Bind(orders, inventory);
// Explicit opt-in: private native failure details belong only in protected operator logs.
builder.Services.AddCohesiveTransitionFailureLogging(fulfillment);
var app = builder.Build();
app.UseExceptionHandler();
app.UseRequestOperationContext();
OrderEndpoints.Map(app, orders, details, availability);
app.MapServiceEphemeralProcess(fulfillment.Declaration, _ => fulfillment.Runtime, "fulfill",
    FulfillmentProcess.Definition, new("POST", "/fulfillment", [], new(typeof(FulfillOrder))));
app.Run();
