using AspireFirst.Orders;
using Cohesive.Adapters.AspNet;
using Cohesive.Storage;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRequestOperationContext();
builder.Services.AddCohesiveExceptionHandling();
var connectionString = builder.Configuration.GetConnectionString(OrderStorage.DatabaseName)
    ?? throw new InvalidOperationException("The native Aspire orders database reference is required.");
await using var database = NpgsqlDataSource.Create(connectionString);
IEntityRepository orders = OrderStorage.Bind(database);

// The shared repository owns DML and validation, not schema lifecycle.
using (var schema = new StreamReader(typeof(OrderStorage).Assembly.GetManifestResourceStream("Orders.schema.sql")
    ?? throw new InvalidOperationException("The explicit order schema resource is missing.")))
await using (var initialize = database.CreateCommand(await schema.ReadToEndAsync()))
    await initialize.ExecuteNonQueryAsync();

var app = builder.Build();
app.UseExceptionHandler();
app.UseRequestOperationContext();
OrderEndpoints.Map(app, orders, OrderDetailsQuery.Bind(database));
app.Run();
