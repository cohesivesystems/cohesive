using AspireFirst.Orders;
using Cohesive.Prelude;
using Cohesive.Storage;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
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
app.MapPost("/orders/{id:guid}", async (Guid id, CancellationToken cancellation) =>
{
    var snapshot = await orders.Upsert(OperationContext.Create(cancellationToken: cancellation), OrderStorage.Register(id));
    return Results.Created($"/orders/{id:D}", new { id = snapshot.Entity.EntityId.Value });
});
app.MapGet("/orders/{id:guid}", async (Guid id, CancellationToken cancellation) =>
{
    var snapshot = await orders.TryGet(OperationContext.Create(cancellationToken: cancellation), id.ToString("D"),
        new EntityReadOptions(partitionKey: OrderStorage.LocalPartition));
    return snapshot is null ? Results.NotFound() : Results.Ok(new { id = snapshot.Entity.EntityId.Value });
});
app.Run();
