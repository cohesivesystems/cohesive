using AspireFirst.Orders;
using Cohesive.Adapters.AspNet;
using Cohesive.Storage;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRequestOperationContext();
builder.Services.AddCohesiveExceptionHandling();
var connectionString = builder.Configuration.GetConnectionString(FulfillmentStorage.DatabaseName)
    ?? throw new InvalidOperationException("The native Aspire orders database reference is required.");
await using var database = NpgsqlDataSource.Create(connectionString);
var persistence = FulfillmentStorage.Bind(database);
IEntityRepository<Order> orders = persistence.Repository(FulfillmentDomain.Orders);

// The shared repository owns DML and validation, not schema lifecycle.
using (var schema = new StreamReader(typeof(FulfillmentStorage).Assembly.GetManifestResourceStream("Orders.schema.sql")
    ?? throw new InvalidOperationException("The explicit order schema resource is missing.")))
await using (var initialize = database.CreateCommand(await schema.ReadToEndAsync()))
    await initialize.ExecuteNonQueryAsync();

// Prepare once and pass the contracts directly to their sole consumer.
var details = persistence.Query(FulfillmentQueries.OrderDetails, maximumRows: 1000, maximumBytes: 1_000_000);
var app = builder.Build();
app.UseExceptionHandler();
app.UseRequestOperationContext();
OrderEndpoints.Map(app, orders, details);
app.Run();
