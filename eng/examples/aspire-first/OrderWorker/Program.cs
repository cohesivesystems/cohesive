using AspireFirst;
using AspireFirst.Orders;
using Cohesive.Adapters.AspNet;
using Cohesive.Adapters.AspNet.Services;
using Cohesive.Host.Services;
using Cohesive.Storage;
using Cohesive.Adapters.Postgres;
using Cohesive.Adapters.Postgres.Services;
using Cohesive.Api.Execution.Services;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
var model = new FulfillmentApplication();
builder.Services.AddRequestOperationContext();
builder.Services.AddFixedRequestIdentity(FulfillmentDemoIdentity.Context);
builder.Services.AddCohesiveExceptionHandling();
var connectionString = builder.Configuration.GetConnectionString(FulfillmentResources.DatabaseName)
    ?? throw new InvalidOperationException("The native Aspire orders database reference is required.");
await using var database = NpgsqlDataSource.Create(connectionString);
var persistence = model.Storage.Bind(database);
var receipts = new PostgresTransitionReceiptOptions("public", "cohesive_process_receipts", FulfillmentDemo.LocalPartition);

// The shared repository owns DML and validation, not schema lifecycle.
using (var schema = new StreamReader(typeof(FulfillmentStorage).Assembly.GetManifestResourceStream("Orders.schema.sql")
    ?? throw new InvalidOperationException("The explicit order schema resource is missing.")))
await using (var initialize = database.CreateCommand(await schema.ReadToEndAsync()))
    await initialize.ExecuteNonQueryAsync();

await using (var initializeReceipts = database.CreateCommand(receipts.SchemaSql))
    await initializeReceipts.ExecuteNonQueryAsync();
var receiptStorage = await receipts.BindAsync(database);
IEntityRepository<Order> orders = persistence.Repository(model.Domain.Orders, transitionReceipts: receiptStorage);
IEntityRepository<InventoryItem> inventory = persistence.Repository(model.Domain.Inventory, transitionReceipts: receiptStorage);


// Prepare once and pass the contracts directly to their sole consumer.
var details = persistence.Query(model.Service.Definition, model.Queries.OrderDetails);
var availability = persistence.Query(model.Service.Definition, model.Queries.ReservationAvailability);
var fulfillment = model.Service.Definition.Bind(model.Process.Definition, orders, inventory)
    .Build(authority: "aspire-first", authorization: new IdentityServiceInvocationAuthorization("demo", new(FulfillmentDomain.PartitionField)));
// Explicit opt-in: private native failure details belong only in protected operator logs.
builder.Services.AddCohesiveTransitionFailureLogging(fulfillment);

var app = builder.Build();
app.UseExceptionHandler();
app.UseRequestOperationContext();
OrderEndpoints.Map(app, model, orders, details, availability);
app.MapServiceEphemeralProcess(fulfillment,
    route: "/fulfillment");
app.Run();
