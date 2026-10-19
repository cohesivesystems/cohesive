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
    [PostgresTheory]
    [InlineData("local", 0)]
    [InlineData("quote'and\\slash", -1)]
    public async Task Typed_checks_validate_exact_predicates_and_reject_drift(string partition, int minimum)
    {
        await using var database = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable(ConnectionVariable)!);
        var table = "checks_" + Guid.NewGuid().ToString("N");
        var source = Model.Storage.Bindings.Mapping(Model.Domain.Inventory);
        var mapping = new PostgresEntityRepositoryMapping(new Cohesive.Adapters.Sql.SqlQualifiedTable("public", table),
            source.Fields, source.IdentityField, source.PartitionField, source.VersionColumn.Value);
        var bindings = PostgresPersistenceBindings.Define(new("isolated"), b => b.Entity(Model.Domain.Inventory, mapping)
            .Schema(PostgresSchemaInitialization.CreateIfMissing, s => s
                .Check(Model.Domain.Inventory, x => x.Partition, Cohesive.Adapters.Sql.SqlBinaryOperator.Equal, partition)
                .Check(Model.Domain.Inventory, x => x.Available, Cohesive.Adapters.Sql.SqlBinaryOperator.GreaterThanOrEqual, minimum)));
        try
        {
            await bindings.PrepareSchemaAsync(database);
            await using (var insert = database.CreateCommand($"INSERT INTO public.{table} (available, partition_key, sku, observation_version) VALUES ($1, $2, $3, $4)"))
            {
                insert.Parameters.AddWithValue(minimum - 1); insert.Parameters.AddWithValue(partition);
                insert.Parameters.AddWithValue("item"); insert.Parameters.AddWithValue(0L);
                var failure = await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync());
                Assert.Equal(PostgresErrorCodes.CheckViolation, failure.SqlState);
            }
            string constraint;
            await using (var lookup = database.CreateCommand($"SELECT conname FROM pg_constraint WHERE conrelid='public.{table}'::regclass AND contype='c' AND pg_get_expr(conbin,conrelid) LIKE '%available%'"))
                constraint = (string)(await lookup.ExecuteScalarAsync())!;
            await using (var alter = database.CreateCommand($"ALTER TABLE public.{table} DROP CONSTRAINT \"{constraint}\", ADD CONSTRAINT \"{constraint}\" CHECK (available >= -100)"))
                await alter.ExecuteNonQueryAsync();
            var drift = await Assert.ThrowsAsync<PostgresSchemaValidationException>(() => bindings.ProductSchema!.ValidateAsync(database));
            Assert.Equal("postgres.schema.checkMismatch", drift.Code);
            Assert.Equal("available", drift.Column);
        }
        finally { await using var drop = database.CreateCommand($"DROP TABLE IF EXISTS public.{table}"); await drop.ExecuteNonQueryAsync(); }
    }

    [PostgresFact]
    public async Task Native_schema_additions_cannot_invalidate_projected_contract_before_admission()
    {
        await using var database = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable(ConnectionVariable)!);
        var table = "override_" + Guid.NewGuid().ToString("N");
        var source = Model.Storage.Bindings.Mapping(Model.Domain.Inventory);
        var mapping = new PostgresEntityRepositoryMapping(new Cohesive.Adapters.Sql.SqlQualifiedTable("public", table),
            source.Fields, source.IdentityField, source.PartitionField, source.VersionColumn.Value);
        var bindings = PostgresPersistenceBindings.Define(new("isolated"), b => b.Entity(Model.Domain.Inventory, mapping)
            .Schema(PostgresSchemaInitialization.CreateIfMissing)
            .SchemaPreparation(async (native, token) =>
            {
                await using var change = native.CreateCommand($"ALTER TABLE public.{table} ALTER COLUMN available TYPE bigint");
                await change.ExecuteNonQueryAsync(token);
            }));
        try
        {
            var failure = await Assert.ThrowsAsync<PostgresSchemaValidationException>(() => bindings.PrepareSchemaAsync(database));
            Assert.Equal("postgres.schema.columnMismatch", failure.Code);
        }
        finally { await using var drop = database.CreateCommand($"DROP TABLE IF EXISTS public.{table}"); await drop.ExecuteNonQueryAsync(); }
    }

    [PostgresTheory]
    [InlineData("ADD COLUMN unexpected text", "postgres.schema.undeclaredColumns")]
    [InlineData("DROP COLUMN available", "postgres.schema.columnMismatch")]
    [InlineData("DROP CONSTRAINT {table}_pkey", "postgres.schema.keyMismatch")]
    [InlineData("ALTER COLUMN sku TYPE text COLLATE \"default\"", "postgres.schema.columnMismatch")]
    [InlineData("ALTER COLUMN available TYPE bigint", "postgres.schema.columnMismatch")]
    public async Task Projected_schema_rejects_key_collation_type_and_extra_column_drift(string mutation, string code)
    {
        await using var database = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable(ConnectionVariable)!);
        var table = "projection_" + Guid.NewGuid().ToString("N");
        var source = Model.Storage.Bindings.Mapping(Model.Domain.Inventory);
        var mapping = new PostgresEntityRepositoryMapping(new Cohesive.Adapters.Sql.SqlQualifiedTable("public", table),
            source.Fields, source.IdentityField, source.PartitionField, source.VersionColumn.Value);
        var bindings = PostgresPersistenceBindings.Define(new("isolated"), b => b.Entity(Model.Domain.Inventory, mapping)
            .Schema(PostgresSchemaInitialization.CreateIfMissing, s => s.Collation(Model.Domain.Inventory, x => x.Sku, "C")));
        try
        {
            await bindings.PrepareSchemaAsync(database);
            await using (var change = database.CreateCommand($"ALTER TABLE public.{table} {mutation.Replace("{table}", table, StringComparison.Ordinal)}"))
                await change.ExecuteNonQueryAsync();
            var failure = await Assert.ThrowsAsync<PostgresSchemaValidationException>(() => bindings.PrepareSchemaAsync(database));
            Assert.Equal(code, failure.Code);
        }
        finally { await using var drop = database.CreateCommand($"DROP TABLE IF EXISTS public.{table}"); await drop.ExecuteNonQueryAsync(); }
    }

    [PostgresFact]
    public async Task Projected_schema_validates_without_creation_and_rejects_native_drift()
    {
        var batches = 0;
        var commands = 0;
        var recording = false;
        using var listener = new System.Diagnostics.ActivityListener
        {
            ShouldListenTo = source => source.Name == "Npgsql",
            Sample = (ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> _) => System.Diagnostics.ActivitySamplingResult.AllData
        };
        System.Diagnostics.ActivitySource.AddActivityListener(listener);
        var source = new NpgsqlDataSourceBuilder(Environment.GetEnvironmentVariable(ConnectionVariable)!);
        source.ConfigureTracing(tracing => tracing
            .ConfigureBatchEnrichmentCallback((_, batch) =>
            {
                if (!recording) return;
                Interlocked.Increment(ref batches);
                Assert.NotEqual(true, batch.EnableErrorBarriers);
            })
            .ConfigureCommandEnrichmentCallback((_, _) => { if (recording) Interlocked.Increment(ref commands); }));
        await using var database = source.Build();
        var schema = "projected_" + Guid.NewGuid().ToString("N");
        await using (var createNamespace = database.CreateCommand($"CREATE SCHEMA {schema}")) await createNamespace.ExecuteNonQueryAsync();
        PostgresEntityRepositoryMapping Relocate<T>(Cohesive.Transitions.Authoring.DomainEntity<T> entity) where T : notnull
        {
            var source = Model.Storage.Bindings.Mapping(entity);
            return new(new Cohesive.Adapters.Sql.SqlQualifiedTable(schema, source.Table.TableName.Value), source.Fields,
                source.IdentityField, source.PartitionField, source.VersionColumn.Value);
        }
        PostgresPersistenceBindings Bindings(PostgresSchemaInitialization initialization) => PostgresPersistenceBindings.Define(new("isolated"), b => b
            .Entity(Model.Domain.Reservations, Relocate(Model.Domain.Reservations))
            .Entity(Model.Domain.Orders, Relocate(Model.Domain.Orders))
            .Entity(Model.Domain.Inventory, Relocate(Model.Domain.Inventory))
            .Schema(initialization, s => s.ForeignKey(Model.Domain.ReservationOrder).ForeignKey(Model.Domain.ReservationItem)));
        var validate = Bindings(PostgresSchemaInitialization.ValidateOnly);
        var create = Bindings(PostgresSchemaInitialization.CreateIfMissing);
        try
        {
            var missing = await Assert.ThrowsAsync<PostgresSchemaValidationException>(() => validate.PrepareSchemaAsync(database));
            Assert.Equal("postgres.schema.tableMissing", missing.Code);
            await using (var count = database.CreateCommand("SELECT count(*) FROM information_schema.tables WHERE table_schema=$1"))
            {
                count.Parameters.AddWithValue(schema);
                Assert.Equal(0L, await count.ExecuteScalarAsync());
            }
            await create.PrepareSchemaAsync(database);
            recording = true;
            await validate.PrepareSchemaAsync(database);
            recording = false;
            Assert.Equal(1, batches);
            Assert.Equal(0, commands);
            await using (var change = database.CreateCommand($"ALTER TABLE {schema}.cohesive_inventory ALTER COLUMN available DROP NOT NULL"))
                await change.ExecuteNonQueryAsync();
            var drift = await Assert.ThrowsAsync<PostgresSchemaValidationException>(() => create.PrepareSchemaAsync(database));
            Assert.Equal("postgres.schema.columnMismatch", drift.Code);
            Assert.Equal("available", drift.Column);
            await using (var repair = database.CreateCommand($"ALTER TABLE {schema}.cohesive_inventory ALTER COLUMN available SET NOT NULL"))
                await repair.ExecuteNonQueryAsync();
            string constraint;
            await using (var name = database.CreateCommand($"SELECT conname FROM pg_constraint WHERE conrelid='{schema}.cohesive_reservations'::regclass AND contype='f' LIMIT 1"))
                constraint = (string)(await name.ExecuteScalarAsync())!;
            await using (var dropReference = database.CreateCommand($"ALTER TABLE {schema}.cohesive_reservations DROP CONSTRAINT \"{constraint}\""))
                await dropReference.ExecuteNonQueryAsync();
            var reference = await Assert.ThrowsAsync<PostgresSchemaValidationException>(() => create.PrepareSchemaAsync(database));
            Assert.Equal("postgres.schema.referenceMismatch", reference.Code);
        }
        finally
        {
            await using var cleanup = database.CreateCommand($"DROP SCHEMA {schema} CASCADE");
            await cleanup.ExecuteNonQueryAsync();
        }
    }

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
