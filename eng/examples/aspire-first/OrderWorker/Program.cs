using Cohesive.Host.Composition;
using AspireFirst;
using AspireFirst.Orders;
using Cohesive.Adapters.AspNet;
using Cohesive.Adapters.AspNet.Services;
using Cohesive.Adapters.Postgres;
using Cohesive.Adapters.Postgres.Services;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
var model = ModelActivator.Create<FulfillmentApplication>();
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
var services = model.Endpoints.Build(builder.Services, persistence.ForServices(receiptStorage));

var app = builder.Build();
app.UseExceptionHandler();
app.UseRequestOperationContext();
app.UseAuthorization();
app.MapServices(services);
app.Run();
