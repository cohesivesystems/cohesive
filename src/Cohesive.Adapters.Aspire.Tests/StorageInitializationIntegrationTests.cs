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
    public async Task Outbox_partial_bootstrap_cannot_repair_an_existing_foreign_key()
    {
        await using var database = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable(ConnectionVariable)!);
        var schema = "outbox_reference_" + Guid.NewGuid().ToString("N");
        var bindings = PostgresPersistenceBindings.Define(new("attachments"), b =>
            b.Outbox(new(schema, "outbox"), PostgresSchemaInitialization.CreateIfMissing));
        try
        {
            await using (var create = database.CreateCommand($"CREATE SCHEMA {schema}")) await create.ExecuteNonQueryAsync();
            await bindings.PrepareOutboxAsync(database);
            await using (var drop = database.CreateCommand($"DROP TABLE {schema}.outbox_commits CASCADE")) await drop.ExecuteNonQueryAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(() => bindings.PrepareOutboxAsync(database));
            await using var missing = database.CreateCommand($"SELECT to_regclass('{schema}.outbox_commits') IS NULL");
            Assert.Equal(true, await missing.ExecuteScalarAsync());
        }
        finally
        {
            await using var cleanup = database.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [PostgresTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Attachment_admission_skips_unchanged_DDL_and_rejects_existing_drift(bool receipts)
    {
        var writes = 0;
        var recording = false;
        using var listener = new System.Diagnostics.ActivityListener
        {
            ShouldListenTo = source => source.Name == "Npgsql",
            Sample = (ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> _) => System.Diagnostics.ActivitySamplingResult.AllData
        };
        System.Diagnostics.ActivitySource.AddActivityListener(listener);
        var builder = new NpgsqlDataSourceBuilder(Environment.GetEnvironmentVariable(ConnectionVariable)!);
        builder.ConfigureTracing(tracing => tracing.ConfigureCommandEnrichmentCallback((_, command) =>
        {
            if (recording && (command.CommandText.Contains("pg_advisory_xact_lock", StringComparison.Ordinal)
                || command.CommandText.Contains("CREATE TABLE", StringComparison.OrdinalIgnoreCase)))
                Interlocked.Increment(ref writes);
        }));
        await using var database = builder.Build();
        var schema = "attachment_" + Guid.NewGuid().ToString("N");
        var bindings = PostgresPersistenceBindings.Define(new("attachments"), b =>
        {
            if (receipts) b.TransitionReceipts(new(schema, "receipts", "local"), PostgresSchemaInitialization.CreateIfMissing);
            else b.Outbox(new(schema, "outbox"), PostgresSchemaInitialization.CreateIfMissing);
        });
        async Task Prepare()
        {
            if (receipts) Assert.NotNull(await bindings.PrepareReceiptsAsync(database));
            else Assert.NotNull(await bindings.PrepareOutboxAsync(database));
        }
        try
        {
            await using (var create = database.CreateCommand($"CREATE SCHEMA {schema}")) await create.ExecuteNonQueryAsync();
            await Prepare();
            recording = true;
            await Prepare();
            recording = false;
            Assert.Equal(0, writes);
            if (!receipts)
            {
                await using (var remove = database.CreateCommand($"DROP TABLE {schema}.outbox_emissions")) await remove.ExecuteNonQueryAsync();
                await Prepare(); // Valid commit table plus missing emissions table is a supported partial bootstrap.
                await using (var remove = database.CreateCommand($"DROP TABLE {schema}.outbox_emissions")) await remove.ExecuteNonQueryAsync();
            }
            var table = receipts ? "receipts" : "outbox_commits";
            await using (var alter = database.CreateCommand($"ALTER TABLE {schema}.{table} ALTER COLUMN content TYPE text USING content::text"))
                await alter.ExecuteNonQueryAsync();
            recording = true;
            await Assert.ThrowsAsync<InvalidOperationException>(Prepare);
            recording = false;
            Assert.Equal(0, writes);
            if (!receipts)
            {
                await using var missing = database.CreateCommand($"SELECT to_regclass('{schema}.outbox_emissions') IS NULL");
                Assert.Equal(true, await missing.ExecuteScalarAsync());
            }
        }
        finally
        {
            recording = false;
            await using var drop = database.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
            await drop.ExecuteNonQueryAsync();
        }
    }

    [PostgresTheory]
    [InlineData(PostgresSchemaInitialization.ValidateOnly, false, false, false, "postgres.schema.tableMissing")]
    [InlineData(PostgresSchemaInitialization.ValidateOnly, true, false, false, "postgres.schema.columnMismatch")]
    [InlineData(PostgresSchemaInitialization.CreateIfMissing, true, false, false, "postgres.schema.columnMismatch")]
    [InlineData(PostgresSchemaInitialization.CreateIfMissing, true, true, false, "postgres.schema.columnMismatch")]
    [InlineData(PostgresSchemaInitialization.CreateIfMissing, true, true, true, "postgres.schema.columnMismatch")]
    public async Task Product_admission_prevents_history_DDL(PostgresSchemaInitialization initialization,
        bool existing, bool missingOrder, bool direct, string code)
    {
        await using var database = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable(ConnectionVariable)!);
        var productSchema = "product_admission_" + Guid.NewGuid().ToString("N");
        var historySchema = "history_admission_" + Guid.NewGuid().ToString("N");
        PostgresEntityRepositoryMapping Relocate<T>(Cohesive.Transitions.Authoring.DomainEntity<T> entity) where T : notnull
        {
            var mapping = Model.Storage.Bindings.Mapping(entity);
            return new(new Cohesive.Adapters.Sql.SqlQualifiedTable(productSchema, mapping.Table.TableName.Value),
                mapping.Fields, mapping.IdentityField, mapping.PartitionField, mapping.VersionColumn.Value);
        }
        var callbacks = 0;
        var bindings = PostgresPersistenceBindings.Define(new("orders"), b => b
            .Entity(Model.Domain.Orders, Relocate(Model.Domain.Orders))
            .Entity(Model.Domain.Inventory, Relocate(Model.Domain.Inventory))
            .Entity(Model.Domain.Reservations, Relocate(Model.Domain.Reservations))
            .Schema(initialization)
            .SchemaPreparation((_, _) => { callbacks++; return Task.CompletedTask; })
            .History(new MartenEntityHistoryConfiguration(initialization: PostgresSchemaInitialization.CreateIfMissing)));
        using var store = DocumentStore.For(options =>
        {
            options.AutoCreateSchemaObjects = JasperFx.AutoCreate.None;
            options.Connection(database);
            options.DatabaseSchemaName = historySchema;
            options.UseCohesiveEntityHistory();
        });
        await using var provider = new ServiceCollection().AddKeyedSingleton("orders", database)
            .AddKeyedSingleton<IDocumentStore>("orders", store).BuildServiceProvider();
        try
        {
            await using (var create = database.CreateCommand($"CREATE SCHEMA {productSchema}")) await create.ExecuteNonQueryAsync();
            if (existing)
            {
                await using var create = database.CreateCommand(bindings.ProductSchema!.CreateSql +
                    $"; ALTER TABLE {productSchema}.cohesive_inventory ALTER COLUMN available TYPE bigint");
                await create.ExecuteNonQueryAsync();
            }
            if (missingOrder)
            {
                await using var remove = database.CreateCommand($"DROP TABLE {productSchema}.cohesive_orders");
                await remove.ExecuteNonQueryAsync();
            }
            var composition = new Cohesive.Adapters.Postgres.Services.PostgresStorageComposition(bindings, Model.Endpoints.Plan);
            var failure = await Assert.ThrowsAsync<PostgresSchemaValidationException>(async () =>
            {
                if (direct) await bindings.PrepareAsync(provider, database, "test");
                else await composition.BindAsync(provider);
            });
            Assert.Equal(code, failure.Code);
            Assert.Equal(0, callbacks);
            await using var retained = database.CreateCommand("SELECT count(*) FROM pg_namespace WHERE nspname=$1");
            retained.Parameters.AddWithValue(historySchema);
            Assert.Equal(0L, await retained.ExecuteScalarAsync());
        }
        finally
        {
            await using var cleanup = database.CreateCommand($"DROP SCHEMA IF EXISTS {productSchema} CASCADE; DROP SCHEMA IF EXISTS {historySchema} CASCADE;");
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [PostgresFact]
    public async Task Embedded_schema_serializes_replica_bootstrap_and_rolls_back_failures()
    {
        await using var database = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable(ConnectionVariable)!);
        PostgresPersistenceBindings Define(string resource) => PostgresPersistenceBindings.Define(new("embedded"),
            b => b.SchemaPreparation(typeof(OrderStorageIntegrationTests).Assembly, resource));
        try
        {
            await Task.WhenAll(Define("ConcurrentSchema.sql").PrepareSchemaAsync(database),
                Define("ConcurrentSchema.sql").PrepareSchemaAsync(database));
            await using (var exists = database.CreateCommand("SELECT to_regclass('public.cohesive_embedded_bootstrap') IS NOT NULL"))
                Assert.Equal(true, await exists.ExecuteScalarAsync());
            var error = await Assert.ThrowsAsync<PostgresException>(() => Define("FailingSchema.sql").PrepareSchemaAsync(database));
            Assert.Equal(PostgresErrorCodes.DivisionByZero, error.SqlState);
            await using var missing = database.CreateCommand("SELECT to_regclass('public.cohesive_failed_embedded_bootstrap') IS NULL");
            Assert.Equal(true, await missing.ExecuteScalarAsync());
        }
        finally
        {
            await using var cleanup = database.CreateCommand("DROP TABLE IF EXISTS public.cohesive_embedded_bootstrap; DROP TABLE IF EXISTS public.cohesive_failed_embedded_bootstrap;");
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [PostgresFact]
    public async Task Reviewed_legacy_migration_preserves_rows_and_satisfies_projected_schema()
    {
        await using var database = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable(ConnectionVariable)!);
        var schema = "migration_" + Guid.NewGuid().ToString("N");
        PostgresEntityRepositoryMapping Relocate<T>(Cohesive.Transitions.Authoring.DomainEntity<T> entity) where T : notnull
        {
            var source = Model.Storage.Bindings.Mapping(entity);
            return new(new Cohesive.Adapters.Sql.SqlQualifiedTable(schema, source.Table.TableName.Value), source.Fields,
                source.IdentityField, source.PartitionField, source.VersionColumn.Value);
        }
        var desired = PostgresPersistenceBindings.Define(new("migration"), b => b
            .Entity(Model.Domain.Orders, Relocate(Model.Domain.Orders))
            .Entity(Model.Domain.Reservations, Relocate(Model.Domain.Reservations))
            .Entity(Model.Domain.Inventory, Relocate(Model.Domain.Inventory))
            .Schema(PostgresSchemaInitialization.ValidateOnly, checks =>
            {
                new AspireFirst.Orders.FulfillmentPostgresMappings(Model.Domain).ConfigureOrderSchema(checks);
                checks.ForeignKey(Model.Domain.ReservationItem).Collation(Model.Domain.Inventory, x => x.Sku, "C")
                    .Check(Model.Domain.Inventory, x => x.Partition, Cohesive.Adapters.Sql.SqlBinaryOperator.Equal, "local")
                    .Check(Model.Domain.Inventory, x => x.Available, Cohesive.Adapters.Sql.SqlBinaryOperator.GreaterThanOrEqual, 0);
            }));
        try
        {
            await using (var legacy = database.CreateCommand($"""
                CREATE SCHEMA {schema};
                CREATE TABLE {schema}.cohesive_orders (order_id text NOT NULL, partition_key text NOT NULL, status text NOT NULL, observation_version bigint NOT NULL, PRIMARY KEY(partition_key,order_id));
                CREATE TABLE {schema}.cohesive_inventory (sku text NOT NULL, partition_key text NOT NULL, available integer NOT NULL, observation_version bigint NOT NULL, PRIMARY KEY(partition_key,sku));
                CREATE TABLE {schema}.cohesive_reservations (reservation_id text NOT NULL, partition_key text NOT NULL, order_id text NOT NULL, sku text NOT NULL, quantity integer NOT NULL, observation_version bigint NOT NULL, PRIMARY KEY(partition_key,reservation_id), FOREIGN KEY(partition_key,order_id) REFERENCES {schema}.cohesive_orders(partition_key,order_id), FOREIGN KEY(partition_key,sku) REFERENCES {schema}.cohesive_inventory(partition_key,sku));
                INSERT INTO {schema}.cohesive_orders VALUES ('order','local','Draft',1);
                INSERT INTO {schema}.cohesive_inventory VALUES ('item','local',3,1);
                INSERT INTO {schema}.cohesive_reservations VALUES ('reservation','local','order','item',2,1);
                """)) await legacy.ExecuteNonQueryAsync();
            await Assert.ThrowsAsync<PostgresSchemaValidationException>(() => desired.PrepareSchemaAsync(database));
            using var resource = new StreamReader(typeof(OrderStorageIntegrationTests).Assembly.GetManifestResourceStream("Migration001.sql")!);
            await using (var migrate = database.CreateCommand((await resource.ReadToEndAsync()).Replace("public.", schema + ".", StringComparison.Ordinal)))
                await migrate.ExecuteNonQueryAsync();
            await desired.PrepareSchemaAsync(database);
            await using var row = database.CreateCommand($"SELECT quantity FROM {schema}.cohesive_reservations WHERE reservation_id='reservation'");
            Assert.Equal(2, await row.ExecuteScalarAsync());
        }
        finally { await using var drop = database.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE"); await drop.ExecuteNonQueryAsync(); }
    }

    [PostgresFact]
    public async Task Concurrent_bootstrap_serializes_overlapping_table_creation()
    {
        await using var database = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable(ConnectionVariable)!);
        var table = "concurrent_" + Guid.NewGuid().ToString("N");
        var source = Model.Storage.Bindings.Mapping(Model.Domain.Inventory);
        var mapping = new PostgresEntityRepositoryMapping(new Cohesive.Adapters.Sql.SqlQualifiedTable("public", table),
            source.Fields, source.IdentityField, source.PartitionField, source.VersionColumn.Value);
        PostgresPersistenceBindings Define() => PostgresPersistenceBindings.Define(new("isolated"), b =>
            b.Entity(Model.Domain.Inventory, mapping).Schema(PostgresSchemaInitialization.CreateIfMissing));
        try
        {
            await Task.WhenAll(Define().PrepareSchemaAsync(database), Define().PrepareSchemaAsync(database));
            await Define().ProductSchema!.ValidateAsync(database);
        }
        finally { await using var drop = database.CreateCommand($"DROP TABLE IF EXISTS public.{table}"); await drop.ExecuteNonQueryAsync(); }
    }

    [PostgresTheory]
    [InlineData("local", 0, true)]
    [InlineData("local", 0, false)]
    [InlineData("quote'and\\slash", -1, true)]
    [InlineData("quote'and\\slash", -1, false)]
    public async Task Typed_checks_validate_exact_predicates_and_reject_drift(string partition, int minimum, bool standardStrings)
    {
        var settings = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable(ConnectionVariable)!)
        { Options = "-c standard_conforming_strings=" + (standardStrings ? "on" : "off") };
        await using var database = NpgsqlDataSource.Create(settings.ConnectionString);
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
            await using (var setting = database.CreateCommand("SHOW standard_conforming_strings"))
                Assert.Equal(standardStrings ? "on" : "off", await setting.ExecuteScalarAsync());
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
                Assert.False(batch.EnableErrorBarriers);
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
            // Partial bootstrap must retain valid existing rows while restoring a missing FK-owning table.
            await using (var partial = database.CreateCommand($"""
                INSERT INTO {schema}.cohesive_inventory (sku,partition_key,available,observation_version)
                VALUES ('preserved','local',7,1);
                DROP TABLE {schema}.cohesive_reservations;
                """)) await partial.ExecuteNonQueryAsync();
            recording = true;
            await create.PrepareSchemaAsync(database);
            recording = false;
            Assert.Equal(2, batches); // Existing-table admission, then complete post-creation validation.
            await using (var retained = database.CreateCommand($"SELECT available FROM {schema}.cohesive_inventory WHERE sku='preserved'"))
                Assert.Equal(7, await retained.ExecuteScalarAsync());
            await validate.ProductSchema!.ValidateAsync(database);
            batches = commands = 0;
            recording = true;
            await create.PrepareSchemaAsync(database);
            recording = false;
            Assert.Equal(1, batches);
            Assert.Equal(0, commands); // No lock/DDL or redundant post-create catalog pass on unchanged startup.
            batches = commands = 0;
            recording = true;
            await validate.PrepareSchemaAsync(database);
            recording = false;
            Assert.Equal(1, batches);
            Assert.Equal(0, commands);
            await using var provider = new ServiceCollection().AddKeyedSingleton("isolated", database).BuildServiceProvider();
            batches = commands = 0;
            recording = true;
            await validate.PrepareAsync(provider, database, "test");
            recording = false;
            Assert.Equal(1, batches);
            Assert.Equal(0, commands);
            var composition = new Cohesive.Adapters.Postgres.Services.PostgresStorageComposition(validate, Model.Endpoints.Plan);
            batches = commands = 0;
            recording = true;
            await composition.BindAsync(provider);
            recording = false;
            Assert.Equal(1, batches);
            Assert.Equal(0, commands);
            var callbacks = 0;
            var withCallback = PostgresPersistenceBindings.Define(new("isolated"), b => b
                .Entity(Model.Domain.Inventory, Relocate(Model.Domain.Inventory))
                .Schema().SchemaPreparation((_, _) => { callbacks++; return Task.CompletedTask; }));
            batches = commands = 0;
            recording = true;
            await withCallback.PrepareAsync(provider, database, "test");
            recording = false;
            Assert.Equal(2, batches); // One admission, one check after the potentially mutating callback.
            Assert.Equal(0, commands);
            Assert.Equal(1, callbacks);
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
        var failure = await Assert.ThrowsAsync<PostgresSchemaValidationException>(() => validate.PrepareOutboxAsync(database));
        Assert.Equal("postgres.schema.tableMissing", failure.Code);
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
            options.UseCohesiveEntityHistory();
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
            await Assert.ThrowsAsync<Weasel.Core.SchemaMigrationException>(() =>
                create.PrepareAsync(provider, "orders", database).AsTask());
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
