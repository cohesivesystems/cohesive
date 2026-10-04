using Npgsql;

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("orders")
    ?? throw new InvalidOperationException("The native Aspire orders database reference is required.");
await using var database = NpgsqlDataSource.Create(connectionString);
await using (var initialize = database.CreateCommand("CREATE TABLE IF NOT EXISTS orders (id uuid PRIMARY KEY)"))
    await initialize.ExecuteNonQueryAsync();

var app = builder.Build();
app.MapPost("/orders/{id:guid}", async (Guid id, CancellationToken cancellation) =>
{
    await using var insert = database.CreateCommand("INSERT INTO orders (id) VALUES ($1) ON CONFLICT DO NOTHING");
    insert.Parameters.AddWithValue(id);
    await insert.ExecuteNonQueryAsync(cancellation);
    return Results.Created($"/orders/{id}", new { id });
});
app.MapGet("/orders/{id:guid}", async (Guid id, CancellationToken cancellation) =>
{
    await using var read = database.CreateCommand("SELECT EXISTS (SELECT 1 FROM orders WHERE id = $1)");
    read.Parameters.AddWithValue(id);
    return await read.ExecuteScalarAsync(cancellation) is true ? Results.Ok(new { id }) : Results.NotFound();
});
app.Run();
