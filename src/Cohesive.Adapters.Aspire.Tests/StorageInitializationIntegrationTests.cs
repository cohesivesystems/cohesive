using Cohesive.Adapters.Marten;
using Cohesive.Adapters.Postgres;
using Cohesive.Storage;
using JasperFx.Events;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Cohesive.Adapters.Aspire.Tests;

public sealed partial class OrderStorageIntegrationTests
{
    [PostgresFact]
    public async Task Outbox_model_defaults_to_validation_and_opt_in_creates_without_repairing_drift()
    {
        await using var database = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable(ConnectionVariable)!);
        var prefix = "outbox_" + Guid.NewGuid().ToString("N");
        var options = new PostgresEntityOutboxOptions("public", prefix);
        var validate = PostgresPersistenceBindings.Define(new("orders"), b => b.Outbox(options));
        var create = PostgresPersistenceBindings.Define(new("orders"), b => b.Outbox(options, PostgresSchemaInitialization.CreateIfMissing));
        await Assert.ThrowsAsync<InvalidOperationException>(() => validate.PrepareOutboxAsync(database));
        await using (var missing = database.CreateCommand("SELECT to_regclass($1) IS NULL"))
        {
            missing.Parameters.AddWithValue("public." + prefix + "_commits");
            Assert.Equal(true, await missing.ExecuteScalarAsync());
        }
        try
        {
            Assert.NotNull(await create.PrepareOutboxAsync(database));
            Assert.NotNull(await validate.PrepareOutboxAsync(database));
            await using (var alter = database.CreateCommand($"ALTER TABLE public.{prefix}_commits DROP COLUMN content_hash"))
                await alter.ExecuteNonQueryAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(() => create.PrepareOutboxAsync(database));
        }
        finally
        {
            await using var drop = database.CreateCommand($"DROP TABLE IF EXISTS public.{prefix}_emissions; DROP TABLE IF EXISTS public.{prefix}_commits");
            await drop.ExecuteNonQueryAsync();
        }
    }

    [PostgresFact]
    public async Task History_model_defaults_to_validation_and_checks_native_identity_before_creation()
    {
        await using var database = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable(ConnectionVariable)!);
        await using var other = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable(ConnectionVariable)!);
        var schema = "history_" + Guid.NewGuid().ToString("N");
        using var store = DocumentStore.For(options =>
        {
            options.AutoCreateSchemaObjects = JasperFx.AutoCreate.None;
            options.Connection(database);
            options.DatabaseSchemaName = schema;
            options.Events.StreamIdentity = StreamIdentity.AsString;
            options.Events.AddEventType<EntityStateEvent>();
        });
        var services = new ServiceCollection().AddKeyedSingleton<IDocumentStore>("orders", store);
        await using var provider = services.BuildServiceProvider();
        var validate = new MartenEntityHistoryConfiguration();
        var create = new MartenEntityHistoryConfiguration(initialization: PostgresSchemaInitialization.CreateIfMissing);
        await Assert.ThrowsAsync<Weasel.Core.Migrations.DatabaseValidationException>(() => validate.PrepareAsync(provider, "orders", database).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => create.PrepareAsync(provider, "orders", other).AsTask());
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => create.PrepareAsync(provider, "orders", database, canceled.Token).AsTask());
        await using (var missing = database.CreateCommand("SELECT NOT EXISTS (SELECT 1 FROM pg_namespace WHERE nspname=$1)"))
        {
            missing.Parameters.AddWithValue(schema);
            Assert.Equal(true, await missing.ExecuteScalarAsync());
        }
        try
        {
            Assert.NotNull(await create.PrepareAsync(provider, "orders", database));
            Assert.NotNull(await validate.PrepareAsync(provider, "orders", database));
            await using (var alter = database.CreateCommand($"ALTER TABLE {schema}.mt_events ADD COLUMN unexpected text"))
                await alter.ExecuteNonQueryAsync();
            var failure = await Record.ExceptionAsync(() => create.PrepareAsync(provider, "orders", database).AsTask());
            Assert.True(failure is Weasel.Core.SchemaMigrationException or Weasel.Core.Migrations.DatabaseValidationException,
                failure?.GetType().FullName ?? "Expected schema rejection.");
            await using var retained = database.CreateCommand("SELECT count(*) FROM information_schema.columns WHERE table_schema=$1 AND table_name='mt_events' AND column_name='unexpected'");
            retained.Parameters.AddWithValue(schema);
            Assert.Equal(1L, await retained.ExecuteScalarAsync());
        }
        finally
        {
            await using var drop = database.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
            await drop.ExecuteNonQueryAsync();
        }
    }
}
