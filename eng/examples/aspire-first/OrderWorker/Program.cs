using AspireFirst.Orders;
using Cohesive.Adapters.AspNet;
using Cohesive.Prelude;
using Cohesive.Storage;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRequestOperationContext();
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
app.UseRequestOperationContext();
// Report concurrent modification without retrying a domain decision against changed state.
app.Use(async (http, next) =>
{
    try { await next(http); }
    catch (ObservationConcurrencyConflictException)
    {
        await Results.Conflict(new { error = "Order changed concurrently; reload before submitting." }).ExecuteAsync(http);
    }
});
OrderEndpoints.Map(app, orders);
app.MapPost("/orders", async (OperationContext context) =>
{
    var id = Guid.NewGuid();
    var snapshot = await orders.Upsert(context, OrderStorage.Register(id));
    return Results.Created($"/orders/{id:D}", new { id = snapshot.Entity.EntityId.Value });
});
app.MapGet("/orders/{id:guid}", async (Guid id, OperationContext context) =>
{
    var snapshot = await orders.TryGet(context, id.ToString("D"),
        new EntityReadOptions(partitionKey: OrderStorage.LocalPartition));
    return snapshot is null ? Results.NotFound() : Results.Ok(new { id = snapshot.Entity.EntityId.Value, status = snapshot.Entity.Observation.GetField("status").GetRequiredString() });
});
app.Run();
