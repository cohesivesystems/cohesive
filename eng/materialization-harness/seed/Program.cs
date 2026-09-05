using Cohesive.Adapters.Sql;
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Text.Json;
using Cohesive.Adapters.Cosmos;
using Cohesive.Adapters.Postgres;
using Cohesive.Execution;
using Cohesive.MaterializationHarness.Model;
using Cohesive.Model;
using Cohesive.Storage;
using Cohesive.Storage.Seeding;
using Microsoft.Azure.Cosmos;
using Npgsql;

namespace Cohesive.MaterializationHarness.Seed;

static class Program
{
    const string PostgresSchema = "freight_harness";
    static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public static async Task<int> Main(string[] args)
    {
        var mode = args switch
        {
            [] => ExecutionMode.SeedCohesive,
            ["--cohesive"] => ExecutionMode.SeedCohesive,
            ["--direct"] => ExecutionMode.SeedDirect,
            ["--validate-only"] => ExecutionMode.Validate,
            ["--verify-only"] => ExecutionMode.VerifyBaseline,
            ["--apply-changes"] => ExecutionMode.ApplyChanges,
            ["--verify-final"] => ExecutionMode.VerifyFinal,
            _ => throw new ArgumentException(
                "The scenario projection accepts --cohesive, --direct, --validate-only, --verify-only, --apply-changes, or --verify-final.",
                nameof(args))
        };
        var options = SeedOptions.FromEnvironment(mode == ExecutionMode.Validate);
        var journal = await FreightScenarioJournal.LoadAsync(options.ScenarioPath);
        if (mode == ExecutionMode.Validate)
        {
            PrintSummary("Validated baseline", journal.Baseline);
            PrintSummary("Validated final state", journal.Final);
            Console.WriteLine(
                $"Validated {journal.MutationTransactions.Length} incremental source transactions after sequence {journal.BaselineThroughSequence}.");
            return 0;
        }
        var state = mode is ExecutionMode.ApplyChanges or ExecutionMode.VerifyFinal
            ? journal.Final
            : journal.Baseline;
        var isSeed = mode is ExecutionMode.SeedDirect or ExecutionMode.SeedCohesive;
        var semantics = isSeed ? FreightOrderMaterializationModel.Create() : null;
        if (isSeed)
        {
            await ResetPostgresChangeFeedSlotsAsync(
                connectionString: options.PostgresConnectionString,
                journal: journal,
                semantics: semantics!);
        }
        if (mode == ExecutionMode.SeedDirect)
        {
            await SeedPostgresDirectAsync(options.PostgresConnectionString, state);
            await SeedCosmosDirectAsync(options.CosmosConnectionString, options.CosmosDatabase, state);
        }
        else if (mode == ExecutionMode.SeedCohesive)
        {
            await SeedPostgresWithRepositoriesAsync(options.PostgresConnectionString, state, semantics!.Storage);
            await SeedCosmosWithRepositoriesAsync(
                options.CosmosConnectionString,
                options.CosmosDatabase,
                state,
                semantics.Storage);
        }
        else if (mode == ExecutionMode.ApplyChanges)
        {
            await FreightScenarioMutationProjection.ApplyAsync(
                postgresConnectionString: options.PostgresConnectionString,
                cosmosConnectionString: options.CosmosConnectionString,
                cosmosDatabase: options.CosmosDatabase,
                journal: journal);
            await VerifyPostgresAsync(options.PostgresConnectionString, state);
            await VerifyCosmosAsync(options.CosmosConnectionString, options.CosmosDatabase, state);
        }
        else
        {
            await VerifyPostgresAsync(options.PostgresConnectionString, state);
            await VerifyCosmosAsync(options.CosmosConnectionString, options.CosmosDatabase, state);
        }
        if (isSeed)
        {
            await CreatePostgresChangeFeedSlotsAsync(
                connectionString: options.PostgresConnectionString,
                journal: journal,
                semantics: semantics!);
        }
        if (mode is ExecutionMode.ApplyChanges or ExecutionMode.VerifyFinal)
        {
            await FreightScenarioMutationProjection.VerifyEvidenceAsync(
                postgresConnectionString: options.PostgresConnectionString,
                cosmosConnectionString: options.CosmosConnectionString,
                cosmosDatabase: options.CosmosDatabase,
                journal: journal);
        }
        await VerifyElasticsearchAsync(options.ElasticsearchEndpoint);
        var action = mode switch
        {
            ExecutionMode.SeedDirect => "Seeded directly",
            ExecutionMode.SeedCohesive => "Seeded through Cohesive.Storage",
            ExecutionMode.ApplyChanges => "Applied incremental mutations to",
            _ => "Verified"
        };
        PrintSummary(action, state);
        return 0;
    }

    static void PrintSummary(string action, FreightScenarioState state) => Console.WriteLine(
        $"{action} scenario '{state.ScenarioId}': {state.Orders.Length} orders, "
        + $"{state.Customers.Length} customers, {state.StopCount} owned stops, "
        + $"{state.Locations.Length} locations across {state.TenantCount} tenants through sequence {state.ThroughSequence}.");

    static async Task SeedPostgresDirectAsync(string connectionString, FreightScenarioState state)
    {
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await ResetPostgresSchemaAsync(connection, transaction);

        foreach (var value in state.Customers)
        {
            await ExecuteAsync(
                connection,
                transaction,
                $"INSERT INTO {PostgresSchema}.customer_accounts VALUES (@tenant, @id, @name, @version);",
                ("tenant", value.TenantId),
                ("id", value.Id),
                ("name", value.DisplayName),
                ("version", state.GetVersion(FreightScenarioEntityKind.CustomerAccount, value.TenantId, value.Id)));
        }
        foreach (var value in state.Locations)
        {
            await ExecuteAsync(
                connection,
                transaction,
                $"INSERT INTO {PostgresSchema}.locations VALUES (@tenant, @id, @name, @city, @region, @version);",
                ("tenant", value.TenantId),
                ("id", value.Id),
                ("name", value.DisplayName),
                ("city", value.City),
                ("region", value.Region),
                ("version", state.GetVersion(FreightScenarioEntityKind.Location, value.TenantId, value.Id)));
        }
        foreach (var value in state.Orders)
        {
            await ExecuteAsync(
                connection,
                transaction,
                $"INSERT INTO {PostgresSchema}.orders VALUES (@tenant, @id, @number, @customer, @equipment, @created, @version);",
                ("tenant", value.TenantId),
                ("id", value.Id),
                ("number", value.OrderNumber),
                ("customer", value.CustomerAccountId),
                ("equipment", value.EquipmentClass),
                ("created", value.CreatedAt),
                ("version", state.GetVersion(FreightScenarioEntityKind.Order, value.TenantId, value.Id)));
            foreach (var stop in value.Stops)
            {
                await ExecuteAsync(
                    connection,
                    transaction,
                    $"INSERT INTO {PostgresSchema}.order_stops VALUES (@tenant, @id, @order, @sequence, @type, @location, @version);",
                    ("tenant", value.TenantId),
                    ("id", stop.Id),
                    ("order", value.Id),
                    ("sequence", stop.Sequence),
                    ("type", stop.StopType),
                    ("location", stop.LocationId),
                    ("version", state.GetVersion(FreightScenarioEntityKind.Order, value.TenantId, value.Id)));
            }
        }
        await transaction.CommitAsync();

        await VerifyPostgresAsync(connection, state);
    }

    static async Task ResetPostgresSchemaAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction)
    {
        await using var schema = new NpgsqlCommand($$"""
            DROP PUBLICATION IF EXISTS {{FreightMaterializationChangeFeedConventions.PostgresPublicationName}};
            DROP SCHEMA IF EXISTS {{PostgresSchema}} CASCADE;
            CREATE SCHEMA {{PostgresSchema}};

            CREATE TABLE {{PostgresSchema}}.customer_accounts (
                tenant_id text COLLATE "C" NOT NULL,
                customer_account_id text COLLATE "C" NOT NULL
                    CONSTRAINT ck_freight_harness_customer_id_ascii
                    CHECK (customer_account_id ~ '^[ -~]+$'),
                display_name text NOT NULL,
                observation_version bigint NOT NULL,
                PRIMARY KEY (tenant_id, customer_account_id)
            );
            CREATE TABLE {{PostgresSchema}}.locations (
                tenant_id text COLLATE "C" NOT NULL,
                location_id text COLLATE "C" NOT NULL
                    CONSTRAINT ck_freight_harness_location_id_ascii
                    CHECK (location_id ~ '^[ -~]+$'),
                display_name text NOT NULL,
                city text NOT NULL,
                region text NOT NULL,
                observation_version bigint NOT NULL,
                PRIMARY KEY (tenant_id, location_id)
            );
            CREATE TABLE {{PostgresSchema}}.orders (
                tenant_id text COLLATE "C" NOT NULL,
                order_id text COLLATE "C" NOT NULL
                    CONSTRAINT ck_freight_harness_order_id_ascii
                    CHECK (order_id ~ '^[ -~]+$'),
                order_number text COLLATE "C" NOT NULL,
                customer_account_id text COLLATE "C" NOT NULL,
                equipment_class text NOT NULL,
                created_at timestamptz NOT NULL,
                observation_version bigint NOT NULL,
                PRIMARY KEY (tenant_id, order_id),
                FOREIGN KEY (tenant_id, customer_account_id)
                    REFERENCES {{PostgresSchema}}.customer_accounts (tenant_id, customer_account_id)
            );
            CREATE TABLE {{PostgresSchema}}.order_stops (
                tenant_id text COLLATE "C" NOT NULL,
                order_stop_id text COLLATE "C" NOT NULL
                    CONSTRAINT ck_freight_harness_stop_id_ascii
                    CHECK (order_stop_id ~ '^[ -~]+$'),
                order_id text COLLATE "C" NOT NULL,
                sequence integer NOT NULL CHECK (sequence > 0),
                stop_type text NOT NULL CHECK (stop_type IN ('Pickup', 'Drop')),
                location_id text COLLATE "C" NOT NULL,
                observation_version bigint NOT NULL,
                CONSTRAINT pk_freight_harness_order_stops
                    PRIMARY KEY (tenant_id, order_id, order_stop_id),
                CONSTRAINT uq_freight_harness_order_stops_sequence
                    UNIQUE (tenant_id, order_id, sequence),
                CONSTRAINT fk_freight_harness_order_stops_order
                    FOREIGN KEY (tenant_id, order_id)
                    REFERENCES {{PostgresSchema}}.orders (tenant_id, order_id)
                    ON DELETE CASCADE,
                FOREIGN KEY (tenant_id, location_id)
                    REFERENCES {{PostgresSchema}}.locations (tenant_id, location_id)
            );
            CREATE TABLE {{PostgresSchema}}.scenario_mutations (
                operation_id text COLLATE "C" PRIMARY KEY,
                scenario_id text COLLATE "C" NOT NULL,
                sequence bigint NOT NULL UNIQUE,
                transaction_id text COLLATE "C" NOT NULL,
                entity_kind text NOT NULL,
                tenant_id text COLLATE "C" NOT NULL,
                entity_id text COLLATE "C" NOT NULL,
                entity_version bigint NOT NULL CHECK (entity_version > 0),
                operation text NOT NULL,
                fingerprint text COLLATE "C" NOT NULL,
                occurred_at_utc timestamptz NOT NULL,
                before_state jsonb,
                after_state jsonb
            );

            ALTER TABLE {{PostgresSchema}}.customer_accounts REPLICA IDENTITY FULL;
            ALTER TABLE {{PostgresSchema}}.locations REPLICA IDENTITY FULL;
            ALTER TABLE {{PostgresSchema}}.orders REPLICA IDENTITY FULL;
            ALTER TABLE {{PostgresSchema}}.order_stops REPLICA IDENTITY FULL;
            CREATE PUBLICATION {{FreightMaterializationChangeFeedConventions.PostgresPublicationName}} FOR TABLE
                {{PostgresSchema}}.customer_accounts,
                {{PostgresSchema}}.locations,
                {{PostgresSchema}}.orders,
                {{PostgresSchema}}.order_stops
                WITH (publish = 'insert, update, delete', publish_via_partition_root = false);
            """, connection, transaction);
        await schema.ExecuteNonQueryAsync();
    }

    static async Task ResetPostgresChangeFeedSlotsAsync(
        string connectionString,
        FreightScenarioJournal journal,
        FreightOrderMaterializationSemantics semantics)
    {
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await using var connection = await dataSource.OpenConnectionAsync().ConfigureAwait(false);
        foreach (var slotName in PostgresChangeFeedSlots(journal, semantics))
        {
            await using var command = new NpgsqlCommand(
                "SELECT pg_drop_replication_slot(@slot_name) WHERE EXISTS (SELECT 1 FROM pg_replication_slots WHERE slot_name = @slot_name);",
                connection);
            command.Parameters.AddWithValue("slot_name", slotName);
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
    }

    static async Task CreatePostgresChangeFeedSlotsAsync(
        string connectionString,
        FreightScenarioJournal journal,
        FreightOrderMaterializationSemantics semantics)
    {
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await using var connection = await dataSource.OpenConnectionAsync().ConfigureAwait(false);
        foreach (var slotName in PostgresChangeFeedSlots(journal, semantics))
        {
            await using var command = new NpgsqlCommand(
                "SELECT slot_name FROM pg_create_logical_replication_slot(@slot_name, 'pgoutput');",
                connection);
            command.Parameters.AddWithValue("slot_name", slotName);
            _ = await command.ExecuteScalarAsync().ConfigureAwait(false);
        }
    }

    static IEnumerable<string> PostgresChangeFeedSlots(
        FreightScenarioJournal journal,
        FreightOrderMaterializationSemantics semantics) =>
        from tenant in journal.Baseline.Orders
            .Select(static order => order.TenantId)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
        from source in semantics.Definition.Sources.OrderBy(static source => source.Input.Value, StringComparer.Ordinal)
        select FreightMaterializationChangeFeedConventions.PostgresSlotName(tenant, source.Input);

    static async Task SeedPostgresWithRepositoriesAsync(
        string connectionString,
        FreightScenarioState state,
        FreightOrderStorageDefinitions storage)
    {
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await using (var connection = await dataSource.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await ResetPostgresSchemaAsync(connection, transaction);
            await transaction.CommitAsync();
        }

        var runtime = new PostgresNpgsqlRuntimeBinding(
            new("cohesive/materialization-harness/postgres"),
            dataSource,
            "cohesive.materialization-harness.seed");
        var customerRepository = new PostgresEntityRepository(
            storage.CustomerAccount,
            runtime,
            Mapping(
                "customer_accounts",
                "id",
                "customer_account_id",
                ("tenantId", "tenant_id", PostgresRelationQueryScalarType.Text),
                ("displayName", "display_name", PostgresRelationQueryScalarType.Text)));
        var locationRepository = new PostgresEntityRepository(
            storage.Location,
            runtime,
            Mapping(
                "locations",
                "id",
                "location_id",
                ("tenantId", "tenant_id", PostgresRelationQueryScalarType.Text),
                ("displayName", "display_name", PostgresRelationQueryScalarType.Text),
                ("city", "city", PostgresRelationQueryScalarType.Text),
                ("region", "region", PostgresRelationQueryScalarType.Text)));
        var seeder = new GenericRepositorySeedDataService(
            [
                GenericRepositorySeedBinding.For(storage.CustomerAccount, customerRepository),
                GenericRepositorySeedBinding.For(storage.Location, locationRepository)
            ],
            new());
        var seedItems = CreateRepositorySeedItems(state, includeOrders: false);
        var result = await seeder.Seed(
            OperationContext.Create(),
            seedItems,
            new(Atomicity: EntityBatchAtomicity.AllOrNothing));
        Require(result.WrittenCount == result.Items.Count, "PostgreSQL repository seeding did not write every item.");
        var replay = await seeder.Seed(
            OperationContext.Create(),
            seedItems,
            new(Atomicity: EntityBatchAtomicity.AllOrNothing));
        Require(
            replay.Items.All(static item => item.Status == RepositorySeedItemStatuses.Replaced),
            "PostgreSQL repository replay did not exercise replacement semantics for every item.");
        await SeedPostgresOrderAggregatesAsync(dataSource, state);
        await SeedPostgresOrderAggregatesAsync(dataSource, state);
        await VerifyPostgresAsync(connectionString, state);

        static PostgresEntityRepositoryMapping Mapping(
            string table,
            string identityField,
            string identityColumn,
            params (string Field, string Column, PostgresRelationQueryScalarType Scalar)[] fields) => new(
            new SqlQualifiedTable(PostgresSchema, table),
            [
                new(identityField, identityColumn, PostgresRelationQueryScalarType.Text),
                .. fields.Select(static field => new PostgresEntityRepositoryFieldBinding(
                    field.Field,
                    field.Column,
                    field.Scalar))
            ],
            identityField,
            partitionField: "tenantId");
    }

    static async Task SeedPostgresOrderAggregatesAsync(
        NpgsqlDataSource dataSource,
        FreightScenarioState state)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        foreach (var order in state.Orders)
        {
            var version = state.GetVersion(FreightScenarioEntityKind.Order, order.TenantId, order.Id);
            await FreightScenarioMutationProjection.ExecutePostgresAsync(
                connection: connection,
                transaction: transaction,
                template: FreightScenarioMutationProjection.PostgresCommands.UpsertOrder,
                cancellationToken: CancellationToken.None,
                parameters:
                [
                    ("tenant_id", order.TenantId),
                    ("order_id", order.Id),
                    ("order_number", order.OrderNumber),
                    ("customer_account_id", order.CustomerAccountId),
                    ("equipment_class", order.EquipmentClass),
                    ("created_at", order.CreatedAt),
                    ("observation_version", version)
                ]);
            await FreightScenarioMutationProjection.ExecutePostgresAsync(
                connection: connection,
                transaction: transaction,
                template: FreightScenarioMutationProjection.PostgresCommands.DeleteOrderStops,
                cancellationToken: CancellationToken.None,
                parameters:
                [
                    ("tenant_id", order.TenantId),
                    ("order_id", order.Id)
                ]);
            foreach (var stop in order.Stops)
            {
                await FreightScenarioMutationProjection.ExecutePostgresAsync(
                    connection: connection,
                    transaction: transaction,
                    template: FreightScenarioMutationProjection.PostgresCommands.InsertStop,
                    cancellationToken: CancellationToken.None,
                    parameters:
                    [
                        ("tenant_id", order.TenantId),
                        ("order_stop_id", stop.Id),
                        ("order_id", order.Id),
                        ("sequence", stop.Sequence),
                        ("stop_type", stop.StopType),
                        ("location_id", stop.LocationId),
                        ("observation_version", version)
                    ]);
            }
        }
        await transaction.CommitAsync();
    }

    static async Task VerifyPostgresAsync(string connectionString, FreightScenarioState state)
    {
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await using var connection = await dataSource.OpenConnectionAsync();
        await VerifyPostgresAsync(connection, state);
    }

    static async Task VerifyPostgresAsync(NpgsqlConnection connection, FreightScenarioState state)
    {
        await using var verify = new NpgsqlCommand($$"""
            SELECT
                current_setting('server_version'),
                current_setting('wal_level'),
                (SELECT count(*) FROM {{PostgresSchema}}.orders),
                (SELECT count(*) FROM {{PostgresSchema}}.customer_accounts),
                (SELECT count(*) FROM {{PostgresSchema}}.order_stops),
                (SELECT count(*) FROM {{PostgresSchema}}.locations),
                (SELECT count(*) FROM pg_publication WHERE pubname = '{{FreightMaterializationChangeFeedConventions.PostgresPublicationName}}');
            """, connection);
        await using (var reader = await verify.ExecuteReaderAsync())
        {
            Require(await reader.ReadAsync(), "PostgreSQL verification returned no row.");
            Require(
                reader.GetString(0).StartsWith("17.10", StringComparison.Ordinal),
                "The PostgreSQL server version differs from the pinned harness image.");
            Require(reader.GetString(1) == "logical", "PostgreSQL logical WAL is not enabled.");
            Require(reader.GetInt64(2) == state.Orders.Length, "PostgreSQL Order count differs from the journal.");
            Require(reader.GetInt64(3) == state.Customers.Length, "PostgreSQL CustomerAccount count differs from the journal.");
            Require(reader.GetInt64(4) == state.StopCount, "PostgreSQL owned Order.Stop count differs from the journal.");
            Require(reader.GetInt64(5) == state.Locations.Length, "PostgreSQL Location count differs from the journal.");
            Require(reader.GetInt64(6) == 1, "The PostgreSQL freight publication is missing.");
        }

        await VerifyPostgresRowsAsync(connection, state);
    }

    static async Task VerifyPostgresRowsAsync(NpgsqlConnection connection, FreightScenarioState state)
    {
        await VerifyRowsAsync(
            connection,
            $"SELECT tenant_id, customer_account_id, display_name, observation_version FROM {PostgresSchema}.customer_accounts;",
            state.Customers.Select(value => Row(
                value.TenantId,
                value.Id,
                value.DisplayName,
                state.GetVersion(FreightScenarioEntityKind.CustomerAccount, value.TenantId, value.Id))),
            static reader => Row(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3)),
            "CustomerAccount");
        await VerifyRowsAsync(
            connection,
            $"SELECT tenant_id, location_id, display_name, city, region, observation_version FROM {PostgresSchema}.locations;",
            state.Locations.Select(value => Row(
                value.TenantId,
                value.Id,
                value.DisplayName,
                value.City,
                value.Region,
                state.GetVersion(FreightScenarioEntityKind.Location, value.TenantId, value.Id))),
            static reader => Row(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt64(5)),
            "Location");
        await VerifyRowsAsync(
            connection,
            $"SELECT tenant_id, order_id, order_number, customer_account_id, equipment_class, created_at, observation_version FROM {PostgresSchema}.orders;",
            state.Orders.Select(value => Row(
                value.TenantId,
                value.Id,
                value.OrderNumber,
                value.CustomerAccountId,
                value.EquipmentClass,
                value.CreatedAt.ToUniversalTime(),
                state.GetVersion(FreightScenarioEntityKind.Order, value.TenantId, value.Id))),
            static reader => Row(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                new DateTimeOffset(reader.GetFieldValue<DateTime>(5)).ToUniversalTime(),
                reader.GetInt64(6)),
            "Order");
        await VerifyRowsAsync(
            connection,
            $"SELECT tenant_id, order_stop_id, order_id, sequence, stop_type, location_id, observation_version FROM {PostgresSchema}.order_stops;",
            state.Orders.SelectMany(order => order.Stops.Select(stop => Row(
                order.TenantId,
                stop.Id,
                order.Id,
                stop.Sequence,
                stop.StopType,
                stop.LocationId,
                state.GetVersion(FreightScenarioEntityKind.Order, order.TenantId, order.Id)))),
            static reader => Row(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetInt64(6)),
            "owned Order.Stop");

        static string Row(params object[] values) => JsonSerializer.Serialize(values, JsonOptions);
    }

    static async Task VerifyRowsAsync(
        NpgsqlConnection connection,
        string sql,
        IEnumerable<string> expectedRows,
        Func<NpgsqlDataReader, string> project,
        string entity)
    {
        var expected = expectedRows.ToHashSet(StringComparer.Ordinal);
        HashSet<string> actual = new(StringComparer.Ordinal);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            Require(actual.Add(project(reader)), $"PostgreSQL {entity} contains a duplicate canonical row.");
        Require(actual.SetEquals(expected), $"PostgreSQL {entity} rows differ from the canonical journal projection.");
    }

    static async Task ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var parameter in parameters)
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        await command.ExecuteNonQueryAsync();
    }

    static async Task SeedCosmosDirectAsync(
        string connectionString,
        string databaseId,
        FreightScenarioState state)
    {
        using var client = CreateCosmosClient(connectionString);
        var containers = await ResetCosmosAsync(client, databaseId);
        var database = containers.Database;
        var orders = containers.Orders;
        var customers = containers.Customers;
        var locations = containers.Locations;
        var occurredAtUtc = state.OccurredAtUtc;
        foreach (var value in state.Orders)
        {
            await orders.UpsertItemAsync(
                new
                {
                    id = $"order/{value.Id}",
                    partitionKey = value.TenantId,
                    documentKind = CosmosRelationQuerySourceReader.DefaultEntityDocumentKind,
                    observationType = FreightOrderMaterializationModel.OrderShapeId.ShapeId.Value,
                    observationId = value.Id,
                    observationVersion = state.GetVersion(FreightScenarioEntityKind.Order, value.TenantId, value.Id),
                    observation = new
                    {
                        id = value.Id,
                        tenantId = value.TenantId,
                        orderNumber = value.OrderNumber,
                        customerAccountId = value.CustomerAccountId,
                        equipmentClass = value.EquipmentClass,
                        createdAt = value.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
                        stops = value.Stops.Select(static stop => new
                        {
                            id = stop.Id,
                            sequence = stop.Sequence,
                            stopType = stop.StopType,
                            locationId = stop.LocationId
                        }).ToArray()
                    },
                    occurredAtUtc
                },
                new(value.TenantId));
        }
        foreach (var value in state.Customers)
        {
            await customers.UpsertItemAsync(
                new
                {
                    id = $"customer/{value.Id}",
                    partitionKey = value.TenantId,
                    documentKind = CosmosRelationQuerySourceReader.DefaultEntityDocumentKind,
                    observationType = FreightOrderMaterializationModel.CustomerAccountShapeId.ShapeId.Value,
                    observationId = value.Id,
                    observationVersion = state.GetVersion(FreightScenarioEntityKind.CustomerAccount, value.TenantId, value.Id),
                    observation = new
                    {
                        id = value.Id,
                        tenantId = value.TenantId,
                        displayName = value.DisplayName
                    },
                    occurredAtUtc
                },
                new(value.TenantId));
        }
        foreach (var value in state.Locations)
        {
            await locations.UpsertItemAsync(
                new
                {
                    id = $"location/{value.Id}",
                    partitionKey = value.TenantId,
                    documentKind = CosmosRelationQuerySourceReader.DefaultEntityDocumentKind,
                    observationType = FreightOrderMaterializationModel.LocationShapeId.ShapeId.Value,
                    observationId = value.Id,
                    observationVersion = state.GetVersion(FreightScenarioEntityKind.Location, value.TenantId, value.Id),
                    observation = new
                    {
                        id = value.Id,
                        tenantId = value.TenantId,
                        displayName = value.DisplayName,
                        city = value.City,
                        region = value.Region
                    },
                    occurredAtUtc
                },
                new(value.TenantId));
        }

        await VerifyCosmosAsync(database, state);
    }

    static async Task<CosmosSeedContainers> ResetCosmosAsync(CosmosClient client, string databaseId)
    {
        var prior = client.GetDatabase(databaseId);
        try
        {
            await prior.DeleteAsync();
        }
        catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
        }

        var database = (await client.CreateDatabaseAsync(databaseId)).Database;
        return new(
            database,
            (await database.CreateContainerAsync("orders", "/partitionKey")).Container,
            (await database.CreateContainerAsync("customerAccounts", "/partitionKey")).Container,
            (await database.CreateContainerAsync("locations", "/partitionKey")).Container);
    }

    static async Task SeedCosmosWithRepositoriesAsync(
        string connectionString,
        string databaseId,
        FreightScenarioState state,
        FreightOrderStorageDefinitions storage)
    {
        using var client = CreateCosmosClient(connectionString);
        var containers = await ResetCosmosAsync(client, databaseId);
        var partition = EntityPartitionKeyPolicy.FromField("tenantId");
        var orderRepository = Repository(storage.Order, containers.Orders, "order");
        var customerRepository = Repository(storage.CustomerAccount, containers.Customers, "customer");
        var locationRepository = Repository(storage.Location, containers.Locations, "location");
        var seeder = new GenericRepositorySeedDataService(
            [
                GenericRepositorySeedBinding.For(storage.CustomerAccount, customerRepository),
                GenericRepositorySeedBinding.For(storage.Location, locationRepository),
                GenericRepositorySeedBinding.For(storage.Order, orderRepository)
            ],
            new());
        var seedItems = CreateRepositorySeedItems(state);
        var result = await seeder.Seed(
            OperationContext.Create(),
            seedItems,
            new(Atomicity: EntityBatchAtomicity.None));
        Require(result.WrittenCount == result.Items.Count, "Cosmos repository seeding did not write every item.");
        var replay = await seeder.Seed(
            OperationContext.Create(),
            seedItems,
            new(Atomicity: EntityBatchAtomicity.None));
        Require(
            replay.Items.All(static item => item.Status == RepositorySeedItemStatuses.Replaced),
            "Cosmos repository replay did not exercise replacement semantics for every item.");
        await VerifyCosmosAsync(containers.Database, state);

        CosmosEntityOutboxRepository Repository(
            Cohesive.Transitions.Model.EntityDefinition definition,
            Container container,
            string itemPrefix) => new(
            definition,
            container,
            itemIdSelector: observation => $"{itemPrefix}/{observation.EntityId.Value}",
            partitionKeyPolicy: partition);
    }

    static IReadOnlyList<RepositorySeedStateItem> CreateRepositorySeedItems(
        FreightScenarioState state,
        bool includeOrders = true)
    {
        List<RepositorySeedStateItem> items = new(
            state.Customers.Length + state.Locations.Length + (includeOrders ? state.Orders.Length : 0));
        foreach (var value in state.Customers)
        {
            items.Add(new(
                Type: FreightOrderMaterializationModel.CustomerAccountShapeId.ShapeId.Value,
                Id: value.Id,
                State: value,
                Version: state.GetVersion(FreightScenarioEntityKind.CustomerAccount, value.TenantId, value.Id),
                PartitionKey: value.TenantId));
        }
        foreach (var value in state.Locations)
        {
            items.Add(new(
                Type: FreightOrderMaterializationModel.LocationShapeId.ShapeId.Value,
                Id: value.Id,
                State: value,
                Version: state.GetVersion(FreightScenarioEntityKind.Location, value.TenantId, value.Id),
                PartitionKey: value.TenantId));
        }
        foreach (var value in includeOrders ? state.Orders : [])
        {
            items.Add(new(
                Type: FreightOrderMaterializationModel.OrderShapeId.ShapeId.Value,
                Id: value.Id,
                State: value,
                Version: state.GetVersion(FreightScenarioEntityKind.Order, value.TenantId, value.Id),
                PartitionKey: value.TenantId));
        }
        return items;
    }

    static async Task VerifyCosmosAsync(
        string connectionString,
        string databaseId,
        FreightScenarioState state)
    {
        using var client = CreateCosmosClient(connectionString);
        await VerifyCosmosAsync(client.GetDatabase(databaseId), state);
    }

    static async Task VerifyCosmosAsync(Database database, FreightScenarioState state)
    {
        await VerifyContainerAsync(database, "orders", state.Orders.Length, "Order");
        await VerifyContainerAsync(database, "customerAccounts", state.Customers.Length, "CustomerAccount");
        await VerifyContainerAsync(database, "locations", state.Locations.Length, "Location");
        await VerifyCosmosDocumentsAsync(database, state);
    }

    static async Task VerifyCosmosDocumentsAsync(Database database, FreightScenarioState state)
    {
        var expected = CreateRepositorySeedItems(state)
            .Select(item => ExpectedCosmosDocument.From(item))
            .GroupBy(static item => item.Container, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.ToDictionary(
                    static item => new EntityKey(item.PartitionKey, item.ObservationId)),
                StringComparer.Ordinal);

        foreach (var (containerName, expectedDocuments) in expected)
        {
            var container = database.GetContainer(containerName);
            using var iterator = container.GetItemQueryIterator<JsonElement>(
                new QueryDefinition("SELECT * FROM c WHERE c.documentKind = @documentKind")
                    .WithParameter("@documentKind", CosmosRelationQuerySourceReader.DefaultEntityDocumentKind),
                requestOptions: new QueryRequestOptions { MaxItemCount = 32 });
            HashSet<EntityKey> observed = [];
            while (iterator.HasMoreResults)
            {
                foreach (var document in await iterator.ReadNextAsync())
                {
                    var partitionKey = document.GetProperty("partitionKey").GetString()
                        ?? throw new InvalidOperationException("Cosmos entity envelope has no partition key.");
                    var observationId = document.GetProperty("observationId").GetString()
                        ?? throw new InvalidOperationException("Cosmos entity envelope has no observation id.");
                    var key = new EntityKey(partitionKey, observationId);
                    Require(observed.Add(key), $"Cosmos container '{containerName}' repeats observation '{key}'.");
                    if (!expectedDocuments.TryGetValue(key, out var expectedDocument))
                    {
                        throw new InvalidOperationException(
                            $"Cosmos container '{containerName}' contains unexpected observation '{key}'.");
                    }
                    Require(
                        document.GetProperty("id").GetString() == expectedDocument.ItemId,
                        $"Cosmos observation '{key}' has a non-canonical item id.");
                    Require(
                        document.GetProperty("documentKind").GetString() == CosmosRelationQuerySourceReader.DefaultEntityDocumentKind,
                        $"Cosmos observation '{key}' has a non-entity discriminator.");
                    Require(
                        document.GetProperty("observationType").GetString() == expectedDocument.ObservationType,
                        $"Cosmos observation '{key}' has a different semantic type.");
                    Require(
                        document.GetProperty("observationVersion").GetInt64() == expectedDocument.Version,
                        $"Cosmos observation '{key}' has a different semantic version.");
                    Require(
                        document.TryGetProperty("occurredAtUtc", out var occurred)
                        && occurred.TryGetDateTimeOffset(out _),
                        $"Cosmos observation '{key}' has no valid persistence instant.");
                    var actualState = document.GetProperty("observation");
                    if (!JsonElement.DeepEquals(actualState, expectedDocument.State))
                    {
                        throw new InvalidOperationException(
                            $"Cosmos observation '{key}' differs from the canonical journal projection. "
                            + $"Expected {expectedDocument.State.GetRawText()}; actual {actualState.GetRawText()}.");
                    }
                    foreach (var optional in new[]
                    {
                        "streamName",
                        "subjectType",
                        "subjectId",
                        "subjectVersion",
                        "correlationId",
                        "traceId",
                        "spanId",
                        "envelope",
                        "envelopeFingerprint"
                    })
                    {
                        Require(
                            !document.TryGetProperty(optional, out _),
                            $"Cosmos entity observation '{key}' unexpectedly contains outbox field '{optional}'.");
                    }
                }
            }
            Require(
                observed.SetEquals(expectedDocuments.Keys),
                $"Cosmos container '{containerName}' differs from the canonical journal projection.");
        }
    }

    internal static CosmosClient CreateCosmosClient(string connectionString) => new(
        connectionString,
        new CosmosClientOptions
        {
            ConnectionMode = ConnectionMode.Gateway,
            HttpClientFactory = CreateCosmosHttpClient,
            LimitToEndpoint = true,
            Serializer = new CosmosSystemTextJsonSerializer()
        });

    static HttpClient CreateCosmosHttpClient()
    {
        HttpClientHandler handler = new();
        handler.ServerCertificateCustomValidationCallback = static (request, _, _, errors) =>
            request.RequestUri?.IsLoopback == true || errors == SslPolicyErrors.None;
        return new(handler, disposeHandler: true);
    }

    static async Task<int> CountEntitiesAsync(Container container)
    {
        using var iterator = container.GetItemQueryIterator<int>(
            new QueryDefinition("SELECT VALUE COUNT(1) FROM c WHERE c.documentKind = @documentKind")
                .WithParameter("@documentKind", CosmosRelationQuerySourceReader.DefaultEntityDocumentKind),
            requestOptions: new QueryRequestOptions { MaxItemCount = 1 });
        var total = 0;
        while (iterator.HasMoreResults)
        {
            foreach (var count in await iterator.ReadNextAsync())
                total += count;
        }
        return total;
    }

    static async Task VerifyContainerAsync(
        Database database,
        string containerId,
        int expectedCount,
        string entity)
    {
        var container = database.GetContainer(containerId);
        var properties = (await container.ReadContainerAsync()).Resource;
        Require(
            string.Equals(properties.PartitionKeyPath, "/partitionKey", StringComparison.Ordinal),
            $"Cosmos {entity} partition path is not /partitionKey.");
        Require(
            await CountEntitiesAsync(container) == expectedCount,
            $"Cosmos {entity} count differs from the journal.");
    }

    static async Task VerifyElasticsearchAsync(Uri endpoint)
    {
        using HttpClient client = new() { BaseAddress = endpoint };
        using var root = await client.GetAsync("/");
        root.EnsureSuccessStatusCode();
        await using (var stream = await root.Content.ReadAsStreamAsync())
        using (var document = await JsonDocument.ParseAsync(stream))
        {
            var version = document.RootElement.GetProperty("version").GetProperty("number").GetString();
            Require(
                string.Equals(version, "8.19.13", StringComparison.Ordinal),
                "The Elasticsearch server version differs from the pinned harness image.");
        }
        using var response = await client.GetAsync("/_cluster/health?wait_for_status=yellow&timeout=30s");
        response.EnsureSuccessStatusCode();
    }

    static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    sealed record SeedOptions(
        string ScenarioPath,
        string PostgresConnectionString,
        string CosmosConnectionString,
        string CosmosDatabase,
        Uri ElasticsearchEndpoint)
    {
        public static SeedOptions FromEnvironment(bool validateOnly)
        {
            var scenario = Required("COHESIVE_MATERIALIZATION_SCENARIO_PATH");
            if (validateOnly)
            {
                return new(
                    scenario,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    new("http://localhost", UriKind.Absolute));
            }
            return new(
                scenario,
                Required("COHESIVE_MATERIALIZATION_POSTGRES_CONNECTION_STRING"),
                Required("COHESIVE_MATERIALIZATION_COSMOS_CONNECTION_STRING"),
                Required("COHESIVE_MATERIALIZATION_COSMOS_DATABASE"),
                new(Required("COHESIVE_MATERIALIZATION_ELASTIC_ENDPOINT"), UriKind.Absolute));
        }

        static string Required(string name) =>
            Environment.GetEnvironmentVariable(name) is { } value && !string.IsNullOrWhiteSpace(value)
                ? value
                : throw new InvalidOperationException($"Set {name} before running the seed projection.");
    }

    enum ExecutionMode
    {
        SeedCohesive,
        SeedDirect,
        Validate,
        VerifyBaseline,
        ApplyChanges,
        VerifyFinal
    }

    readonly record struct EntityKey(string TenantId, string Id)
    {
        public override string ToString() => $"{TenantId}/{Id}";
    }

    sealed record CosmosSeedContainers(
        Database Database,
        Container Orders,
        Container Customers,
        Container Locations);

    sealed record ExpectedCosmosDocument(
        string Container,
        string ItemId,
        string PartitionKey,
        string ObservationType,
        string ObservationId,
        long Version,
        JsonElement State)
    {
        public static ExpectedCosmosDocument From(RepositorySeedStateItem item)
        {
            var (container, prefix) = item.Type switch
            {
                "order" => ("orders", "order"),
                "customer-account" => ("customerAccounts", "customer"),
                "location" => ("locations", "location"),
                _ => throw new InvalidOperationException($"Unsupported Cosmos seed type '{item.Type}'.")
            };
            var state = ObservationValue.FromObject(item.State);
            return new(
                container,
                $"{prefix}/{item.Id}",
                item.PartitionKey ?? throw new InvalidOperationException("Cosmos seed item has no partition key."),
                item.Type,
                item.Id,
                item.Version ?? throw new InvalidOperationException("Cosmos seed item has no semantic version."),
                JsonSerializer.SerializeToElement(
                    state.Fields ?? throw new InvalidOperationException("Cosmos seed state is not an object."),
                    CosmosSystemTextJsonSerializer.CreateDefaultOptions()));
        }
    }

}
