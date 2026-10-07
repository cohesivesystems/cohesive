using AspireFirst.Orders;
using Cohesive.Prelude;
using Cohesive.Storage;
using Npgsql;

namespace Cohesive.Adapters.Aspire.Tests;

public sealed class OrderStorageIntegrationTests
{
    const string ConnectionVariable = "COHESIVE_ORDER_EXAMPLE_TEST_CONNECTION_STRING";

    [PostgresFact]
    public async Task Example_schema_round_trip_and_stale_write_rejection()
    {
        // Only point this opt-in test at a disposable example database.
        await using var database = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable(ConnectionVariable)!);
        using var schema = new StreamReader(typeof(OrderStorage).Assembly.GetManifestResourceStream("Orders.schema.sql")!);
        await using (var command = database.CreateCommand(await schema.ReadToEndAsync()))
            await command.ExecuteNonQueryAsync();
        var repository = OrderStorage.Bind(database);
        var context = OperationContext.Create();
        var id = Guid.NewGuid();
        try
        {
            var first = await repository.Upsert(context, OrderStorage.Register(id));
            var loaded = await repository.TryGet(context, id.ToString("D"), new EntityReadOptions(partitionKey: OrderStorage.LocalPartition));
            Assert.NotNull(loaded);
            Assert.Equal(first.ConcurrencyToken, loaded.ConcurrencyToken);
            var second = await repository.Upsert(context, new EntityWriteRequest(loaded.Entity, loaded.ConcurrencyToken));
            Assert.NotEqual(first.ConcurrencyToken, second.ConcurrencyToken);
            await Assert.ThrowsAsync<ObservationConcurrencyConflictException>(() => repository.Upsert(context,
                new EntityWriteRequest(loaded.Entity, loaded.ConcurrencyToken)));
            var reloaded = await repository.TryGet(context, id.ToString("D"), new EntityReadOptions(partitionKey: OrderStorage.LocalPartition));
            Assert.Equal(second.ConcurrencyToken, reloaded!.ConcurrencyToken);
        }
        finally
        {
            await using var cleanup = database.CreateCommand("DELETE FROM public.cohesive_orders WHERE partition_key = $1 AND order_id = $2");
            cleanup.Parameters.AddWithValue(OrderStorage.LocalPartition);
            cleanup.Parameters.AddWithValue(id.ToString("D"));
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    sealed class PostgresFactAttribute : FactAttribute
    {
        public PostgresFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionVariable)))
                Skip = $"Set {ConnectionVariable} to a disposable PostgreSQL database.";
        }
    }
}
