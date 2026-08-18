using System.Collections.Immutable;
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
    const string JournalSchema = "cohesive.materialization-harness/scenario-journal/v1";
    const string PostgresSchema = "freight_harness";
    const string PostgresPublication = "cohesive_freight_harness";
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
            ["--verify-only"] => ExecutionMode.Verify,
            _ => throw new ArgumentException(
                "The seed projection accepts --cohesive, --direct, --validate-only, or --verify-only.",
                nameof(args))
        };
        var options = SeedOptions.FromEnvironment(mode == ExecutionMode.Validate);
        var state = await LoadAsync(options.ScenarioPath);
        Validate(state);
        if (mode == ExecutionMode.Validate)
        {
            PrintSummary("Validated", state);
            return 0;
        }
        if (mode == ExecutionMode.SeedDirect)
        {
            await SeedPostgresDirectAsync(options.PostgresConnectionString, state);
            await SeedCosmosDirectAsync(options.CosmosConnectionString, options.CosmosDatabase, state);
        }
        else if (mode == ExecutionMode.SeedCohesive)
        {
            var semantics = FreightOrderMaterializationModel.Create();
            await SeedPostgresWithRepositoriesAsync(options.PostgresConnectionString, state, semantics.Storage);
            await SeedCosmosWithRepositoriesAsync(
                options.CosmosConnectionString,
                options.CosmosDatabase,
                state,
                semantics.Storage);
        }
        else
        {
            await VerifyPostgresAsync(options.PostgresConnectionString, state);
            await VerifyCosmosAsync(options.CosmosConnectionString, options.CosmosDatabase, state);
        }
        await VerifyElasticsearchAsync(options.ElasticsearchEndpoint);
        var action = mode switch
        {
            ExecutionMode.SeedDirect => "Seeded directly",
            ExecutionMode.SeedCohesive => "Seeded through Cohesive.Storage",
            _ => "Verified"
        };
        PrintSummary(action, state);
        return 0;
    }

    static void PrintSummary(string action, ScenarioState state) => Console.WriteLine(
        $"{action} scenario '{state.ScenarioId}': {state.Orders.Length} orders, "
        + $"{state.Customers.Length} customers, {state.Stops.Length} stops, "
        + $"{state.Locations.Length} locations across {state.TenantCount} tenants.");

    static async Task<ScenarioState> LoadAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        var journal = await JsonSerializer.DeserializeAsync<ScenarioJournal>(stream, JsonOptions)
            ?? throw new InvalidOperationException("The scenario journal is empty.");
        if (!string.Equals(journal.SchemaVersion, JournalSchema, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Unsupported scenario journal schema '{journal.SchemaVersion}'.");
        }
        if (string.IsNullOrWhiteSpace(journal.ScenarioId))
            throw new InvalidOperationException("The scenario journal requires an identity.");
        if (journal.Operations.IsDefaultOrEmpty)
            throw new InvalidOperationException("The scenario journal requires at least one operation.");

        Dictionary<EntityKey, FreightOrder> orders = [];
        Dictionary<EntityKey, CustomerAccount> customers = [];
        Dictionary<EntityKey, OrderStop> stops = [];
        Dictionary<EntityKey, Location> locations = [];
        long expectedSequence = 1;
        foreach (var operation in journal.Operations.OrderBy(static operation => operation.Sequence))
        {
            if (operation.Sequence != expectedSequence)
            {
                throw new InvalidOperationException(
                    $"Scenario operation sequence must be contiguous; expected {expectedSequence} and found {operation.Sequence}.");
            }
            expectedSequence++;
            if (!string.Equals(operation.Operation, "upsert", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Baseline seed operation '{operation.Operation}' is unsupported.");
            }

            switch (operation.Entity)
            {
                case "order":
                    Add(orders, Deserialize<FreightOrder>(operation), static value => value.OrderId);
                    break;
                case "customerAccount":
                    Add(customers, Deserialize<CustomerAccount>(operation), static value => value.CustomerAccountId);
                    break;
                case "orderStop":
                    Add(stops, Deserialize<OrderStop>(operation), static value => value.OrderStopId);
                    break;
                case "location":
                    Add(locations, Deserialize<Location>(operation), static value => value.LocationId);
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Scenario entity '{operation.Entity}' is unsupported.");
            }
        }

        return new(
            journal.ScenarioId,
            [.. orders.Values.OrderBy(static value => value.TenantId, StringComparer.Ordinal)
                .ThenBy(static value => value.OrderId, StringComparer.Ordinal)],
            [.. customers.Values.OrderBy(static value => value.TenantId, StringComparer.Ordinal)
                .ThenBy(static value => value.CustomerAccountId, StringComparer.Ordinal)],
            [.. stops.Values.OrderBy(static value => value.TenantId, StringComparer.Ordinal)
                .ThenBy(static value => value.OrderId, StringComparer.Ordinal)
                .ThenBy(static value => value.Sequence)],
            [.. locations.Values.OrderBy(static value => value.TenantId, StringComparer.Ordinal)
                .ThenBy(static value => value.LocationId, StringComparer.Ordinal)]);

        static T Deserialize<T>(ScenarioOperation operation)
            where T : TenantDocument => operation.Document.Deserialize<T>(JsonOptions)
                ?? throw new InvalidOperationException(
                    $"Scenario operation {operation.Sequence} has no {operation.Entity} document.");

        static void Add<T>(
            IDictionary<EntityKey, T> destination,
            T value,
            Func<T, string> identity)
            where T : TenantDocument
        {
            var key = new EntityKey(value.TenantId, identity(value));
            if (!destination.TryAdd(key, value))
                throw new InvalidOperationException($"Scenario identity '{key}' is repeated.");
        }
    }

    static void Validate(ScenarioState state)
    {
        Require(state.Orders.Length >= 6, "The baseline must cross the two-item root-page boundary.");
        Require(state.Stops.Length >= 12, "The baseline must cross contributor lookup boundaries.");
        Require(state.TenantCount >= 2, "The baseline requires at least two tenants.");
        Require(
            state.Orders.GroupBy(static order => new EntityKey(order.TenantId, order.CustomerAccountId))
                .Any(static group => group.Count() > 1),
            "At least one customer must contribute to multiple orders.");
        Require(
            state.Stops.GroupBy(static stop => new EntityKey(stop.TenantId, stop.LocationId))
                .Any(static group => group.Select(static stop => stop.OrderId).Distinct(StringComparer.Ordinal).Count() > 1),
            "At least one location must contribute to multiple orders.");

        var customers = state.Customers
            .Select(static value => new EntityKey(value.TenantId, value.CustomerAccountId))
            .ToHashSet();
        var locations = state.Locations
            .Select(static value => new EntityKey(value.TenantId, value.LocationId))
            .ToHashSet();
        var orders = state.Orders
            .Select(static value => new EntityKey(value.TenantId, value.OrderId))
            .ToHashSet();
        foreach (var order in state.Orders)
        {
            RequireText(order.TenantId, "Order TenantId");
            RequireText(order.OrderId, "OrderId");
            RequireText(order.OrderNumber, "OrderNumber");
            RequireText(order.EquipmentClass, "EquipmentClass");
            Require(
                customers.Contains(new(order.TenantId, order.CustomerAccountId)),
                $"Order '{order.TenantId}/{order.OrderId}' references a missing tenant-local customer.");
        }
        foreach (var customer in state.Customers)
        {
            RequireText(customer.TenantId, "Customer TenantId");
            RequireText(customer.CustomerAccountId, "CustomerAccountId");
            RequireText(customer.DisplayName, "Customer DisplayName");
        }
        foreach (var location in state.Locations)
        {
            RequireText(location.TenantId, "Location TenantId");
            RequireText(location.LocationId, "LocationId");
            RequireText(location.DisplayName, "Location DisplayName");
            RequireText(location.City, "Location City");
            RequireText(location.Region, "Location Region");
        }
        foreach (var stop in state.Stops)
        {
            RequireText(stop.TenantId, "Stop TenantId");
            RequireText(stop.OrderStopId, "OrderStopId");
            Require(stop.Sequence > 0, "Stop sequence must be positive.");
            Require(
                string.Equals(stop.StopType, "Pickup", StringComparison.Ordinal)
                || string.Equals(stop.StopType, "Drop", StringComparison.Ordinal),
                $"Stop '{stop.TenantId}/{stop.OrderStopId}' has an unsupported type.");
            Require(
                orders.Contains(new(stop.TenantId, stop.OrderId)),
                $"Stop '{stop.TenantId}/{stop.OrderStopId}' references a missing tenant-local order.");
            Require(
                locations.Contains(new(stop.TenantId, stop.LocationId)),
                $"Stop '{stop.TenantId}/{stop.OrderStopId}' references a missing tenant-local location.");
            Require(
                stop.ScheduledStart <= stop.ScheduledEnd,
                $"Stop '{stop.TenantId}/{stop.OrderStopId}' has an inverted schedule.");
        }
        foreach (var group in state.Stops.GroupBy(static stop => new EntityKey(stop.TenantId, stop.OrderId)))
        {
            Require(
                group.Select(static stop => stop.Sequence).Distinct().Count() == group.Count(),
                $"Order '{group.Key}' repeats a stop sequence.");
            Require(
                group.Count(static stop => stop.StopType == "Pickup") == 1,
                $"Order '{group.Key}' must have exactly one unambiguous pickup endpoint.");
            Require(group.Any(static stop => stop.StopType == "Drop"), $"Order '{group.Key}' has no drop.");
        }
        Require(
            state.Stops.Select(static stop => new EntityKey(stop.TenantId, stop.OrderId)).ToHashSet()
                .SetEquals(orders),
            "Every Order must own at least one tenant-local stop sequence.");
    }

    static async Task SeedPostgresDirectAsync(string connectionString, ScenarioState state)
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
                ("id", value.CustomerAccountId),
                ("name", value.DisplayName),
                ("version", 1L));
        }
        foreach (var value in state.Locations)
        {
            await ExecuteAsync(
                connection,
                transaction,
                $"INSERT INTO {PostgresSchema}.locations VALUES (@tenant, @id, @name, @city, @region, @version);",
                ("tenant", value.TenantId),
                ("id", value.LocationId),
                ("name", value.DisplayName),
                ("city", value.City),
                ("region", value.Region),
                ("version", 1L));
        }
        foreach (var value in state.Orders)
        {
            var endpoints = SelectEndpoints(state, value);
            await ExecuteAsync(
                connection,
                transaction,
                $"INSERT INTO {PostgresSchema}.orders VALUES (@tenant, @id, @number, @customer, @equipment, @pickup, @delivery, @origin, @destination, @created, @version);",
                ("tenant", value.TenantId),
                ("id", value.OrderId),
                ("number", value.OrderNumber),
                ("customer", value.CustomerAccountId),
                ("equipment", value.EquipmentClass),
                ("pickup", endpoints.PickupStopId),
                ("delivery", endpoints.DeliveryStopId),
                ("origin", endpoints.OriginLocationId),
                ("destination", endpoints.DestinationLocationId),
                ("created", value.CreatedAt),
                ("version", 1L));
        }
        foreach (var value in state.Stops)
        {
            await ExecuteAsync(
                connection,
                transaction,
                $"INSERT INTO {PostgresSchema}.order_stops VALUES (@tenant, @id, @order, @sequence, @type, @location, @start, @end, @version);",
                ("tenant", value.TenantId),
                ("id", value.OrderStopId),
                ("order", value.OrderId),
                ("sequence", value.Sequence),
                ("type", value.StopType),
                ("location", value.LocationId),
                ("start", value.ScheduledStart),
                ("end", value.ScheduledEnd),
                ("version", 1L));
        }
        await transaction.CommitAsync();

        await VerifyPostgresAsync(connection, state);
    }

    static async Task ResetPostgresSchemaAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction)
    {
        await using var schema = new NpgsqlCommand($$"""
            DROP PUBLICATION IF EXISTS {{PostgresPublication}};
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
                pickup_stop_id text COLLATE "C" NOT NULL,
                delivery_stop_id text COLLATE "C" NOT NULL,
                origin_location_id text COLLATE "C" NOT NULL,
                destination_location_id text COLLATE "C" NOT NULL,
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
                scheduled_start timestamptz NOT NULL,
                scheduled_end timestamptz NOT NULL,
                observation_version bigint NOT NULL,
                PRIMARY KEY (tenant_id, order_stop_id),
                UNIQUE (tenant_id, order_id, sequence),
                FOREIGN KEY (tenant_id, order_id)
                    REFERENCES {{PostgresSchema}}.orders (tenant_id, order_id),
                FOREIGN KEY (tenant_id, location_id)
                    REFERENCES {{PostgresSchema}}.locations (tenant_id, location_id),
                CHECK (scheduled_start <= scheduled_end)
            );

            ALTER TABLE {{PostgresSchema}}.customer_accounts REPLICA IDENTITY FULL;
            ALTER TABLE {{PostgresSchema}}.locations REPLICA IDENTITY FULL;
            ALTER TABLE {{PostgresSchema}}.orders REPLICA IDENTITY FULL;
            ALTER TABLE {{PostgresSchema}}.order_stops REPLICA IDENTITY FULL;
            CREATE PUBLICATION {{PostgresPublication}} FOR TABLE
                {{PostgresSchema}}.customer_accounts,
                {{PostgresSchema}}.locations,
                {{PostgresSchema}}.orders,
                {{PostgresSchema}}.order_stops;
            """, connection, transaction);
        await schema.ExecuteNonQueryAsync();
    }

    static async Task SeedPostgresWithRepositoriesAsync(
        string connectionString,
        ScenarioState state,
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
        var orderRepository = new PostgresEntityRepository(
            storage.Order,
            runtime,
            Mapping(
                "orders",
                "id",
                "order_id",
                ("tenantId", "tenant_id", PostgresRelationQueryScalarType.Text),
                ("orderNumber", "order_number", PostgresRelationQueryScalarType.Text),
                ("customerAccountId", "customer_account_id", PostgresRelationQueryScalarType.Text),
                ("equipmentClass", "equipment_class", PostgresRelationQueryScalarType.Text),
                ("pickupStopId", "pickup_stop_id", PostgresRelationQueryScalarType.Text),
                ("deliveryStopId", "delivery_stop_id", PostgresRelationQueryScalarType.Text),
                ("originLocationId", "origin_location_id", PostgresRelationQueryScalarType.Text),
                ("destinationLocationId", "destination_location_id", PostgresRelationQueryScalarType.Text),
                ("createdAt", "created_at", PostgresRelationQueryScalarType.TimestampWithTimeZone)));
        var stopRepository = new PostgresEntityRepository(
            storage.OrderStop,
            runtime,
            Mapping(
                "order_stops",
                "id",
                "order_stop_id",
                ("tenantId", "tenant_id", PostgresRelationQueryScalarType.Text),
                ("orderId", "order_id", PostgresRelationQueryScalarType.Text),
                ("sequence", "sequence", PostgresRelationQueryScalarType.Int32),
                ("stopType", "stop_type", PostgresRelationQueryScalarType.Text),
                ("locationId", "location_id", PostgresRelationQueryScalarType.Text),
                ("scheduledStart", "scheduled_start", PostgresRelationQueryScalarType.TimestampWithTimeZone),
                ("scheduledEnd", "scheduled_end", PostgresRelationQueryScalarType.TimestampWithTimeZone)));

        var seeder = new GenericRepositorySeedDataService(
            [
                GenericRepositorySeedBinding.For(storage.CustomerAccount, customerRepository),
                GenericRepositorySeedBinding.For(storage.Location, locationRepository),
                GenericRepositorySeedBinding.For(storage.Order, orderRepository),
                GenericRepositorySeedBinding.For(storage.OrderStop, stopRepository)
            ],
            new());
        var seedItems = CreateRepositorySeedItems(state);
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
        await VerifyPostgresAsync(connectionString, state);

        static PostgresEntityRepositoryMapping Mapping(
            string table,
            string identityField,
            string identityColumn,
            params (string Field, string Column, PostgresRelationQueryScalarType Scalar)[] fields) => new(
            new PostgresSqlQualifiedTable(PostgresSchema, table),
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

    static async Task VerifyPostgresAsync(string connectionString, ScenarioState state)
    {
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await using var connection = await dataSource.OpenConnectionAsync();
        await VerifyPostgresAsync(connection, state);
    }

    static async Task VerifyPostgresAsync(NpgsqlConnection connection, ScenarioState state)
    {
        await using var verify = new NpgsqlCommand($$"""
            SELECT
                current_setting('server_version'),
                current_setting('wal_level'),
                (SELECT count(*) FROM {{PostgresSchema}}.orders),
                (SELECT count(*) FROM {{PostgresSchema}}.customer_accounts),
                (SELECT count(*) FROM {{PostgresSchema}}.order_stops),
                (SELECT count(*) FROM {{PostgresSchema}}.locations),
                (SELECT count(*) FROM pg_publication WHERE pubname = '{{PostgresPublication}}');
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
            Require(reader.GetInt64(4) == state.Stops.Length, "PostgreSQL OrderStop count differs from the journal.");
            Require(reader.GetInt64(5) == state.Locations.Length, "PostgreSQL Location count differs from the journal.");
            Require(reader.GetInt64(6) == 1, "The PostgreSQL freight publication is missing.");
        }

        await VerifyPostgresRowsAsync(connection, state);
    }

    static async Task VerifyPostgresRowsAsync(NpgsqlConnection connection, ScenarioState state)
    {
        await VerifyRowsAsync(
            connection,
            $"SELECT tenant_id, customer_account_id, display_name, observation_version FROM {PostgresSchema}.customer_accounts;",
            state.Customers.Select(static value => Row(
                value.TenantId,
                value.CustomerAccountId,
                value.DisplayName,
                1L)),
            static reader => Row(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3)),
            "CustomerAccount");
        await VerifyRowsAsync(
            connection,
            $"SELECT tenant_id, location_id, display_name, city, region, observation_version FROM {PostgresSchema}.locations;",
            state.Locations.Select(static value => Row(
                value.TenantId,
                value.LocationId,
                value.DisplayName,
                value.City,
                value.Region,
                1L)),
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
            $"SELECT tenant_id, order_id, order_number, customer_account_id, equipment_class, pickup_stop_id, delivery_stop_id, origin_location_id, destination_location_id, created_at, observation_version FROM {PostgresSchema}.orders;",
            state.Orders.Select(value =>
            {
                var endpoints = SelectEndpoints(state, value);
                return Row(
                    value.TenantId,
                    value.OrderId,
                    value.OrderNumber,
                    value.CustomerAccountId,
                    value.EquipmentClass,
                    endpoints.PickupStopId,
                    endpoints.DeliveryStopId,
                    endpoints.OriginLocationId,
                    endpoints.DestinationLocationId,
                    value.CreatedAt.ToUniversalTime(),
                    1L);
            }),
            static reader => Row(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8),
                new DateTimeOffset(reader.GetFieldValue<DateTime>(9)).ToUniversalTime(),
                reader.GetInt64(10)),
            "Order");
        await VerifyRowsAsync(
            connection,
            $"SELECT tenant_id, order_stop_id, order_id, sequence, stop_type, location_id, scheduled_start, scheduled_end, observation_version FROM {PostgresSchema}.order_stops;",
            state.Stops.Select(static value => Row(
                value.TenantId,
                value.OrderStopId,
                value.OrderId,
                value.Sequence,
                value.StopType,
                value.LocationId,
                value.ScheduledStart.ToUniversalTime(),
                value.ScheduledEnd.ToUniversalTime(),
                1L)),
            static reader => Row(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.GetString(4),
                reader.GetString(5),
                new DateTimeOffset(reader.GetFieldValue<DateTime>(6)).ToUniversalTime(),
                new DateTimeOffset(reader.GetFieldValue<DateTime>(7)).ToUniversalTime(),
                reader.GetInt64(8)),
            "OrderStop");

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
        ScenarioState state)
    {
        using var client = CreateCosmosClient(connectionString);
        var containers = await ResetCosmosAsync(client, databaseId);
        var database = containers.Database;
        var orders = containers.Orders;
        var customers = containers.Customers;
        var stops = containers.Stops;
        var locations = containers.Locations;
        var occurredAtUtc = DateTimeOffset.UtcNow;
        foreach (var value in state.Orders)
        {
            var endpoints = SelectEndpoints(state, value);
            await orders.UpsertItemAsync(
                new
                {
                    id = $"order/{value.OrderId}",
                    partitionKey = value.TenantId,
                    documentKind = CosmosRelationQuerySourceReader.DefaultEntityDocumentKind,
                    observationType = FreightOrderMaterializationModel.OrderShapeId.ShapeId.Value,
                    observationId = value.OrderId,
                    observationVersion = 1,
                    observation = new
                    {
                        id = value.OrderId,
                        tenantId = value.TenantId,
                        orderNumber = value.OrderNumber,
                        customerAccountId = value.CustomerAccountId,
                        equipmentClass = value.EquipmentClass,
                        pickupStopId = endpoints.PickupStopId,
                        deliveryStopId = endpoints.DeliveryStopId,
                        originLocationId = endpoints.OriginLocationId,
                        destinationLocationId = endpoints.DestinationLocationId,
                        createdAt = value.CreatedAt.ToString("O", CultureInfo.InvariantCulture)
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
                    id = $"customer/{value.CustomerAccountId}",
                    partitionKey = value.TenantId,
                    documentKind = CosmosRelationQuerySourceReader.DefaultEntityDocumentKind,
                    observationType = FreightOrderMaterializationModel.CustomerAccountShapeId.ShapeId.Value,
                    observationId = value.CustomerAccountId,
                    observationVersion = 1,
                    observation = new
                    {
                        id = value.CustomerAccountId,
                        tenantId = value.TenantId,
                        displayName = value.DisplayName
                    },
                    occurredAtUtc
                },
                new(value.TenantId));
        }
        foreach (var value in state.Stops)
        {
            await stops.UpsertItemAsync(
                new
                {
                    id = $"stop/{value.OrderStopId}",
                    partitionKey = value.TenantId,
                    documentKind = CosmosRelationQuerySourceReader.DefaultEntityDocumentKind,
                    observationType = FreightOrderMaterializationModel.OrderStopShapeId.ShapeId.Value,
                    observationId = value.OrderStopId,
                    observationVersion = 1,
                    observation = new
                    {
                        id = value.OrderStopId,
                        tenantId = value.TenantId,
                        orderId = value.OrderId,
                        sequence = value.Sequence,
                        stopType = value.StopType,
                        locationId = value.LocationId,
                        scheduledStart = value.ScheduledStart.ToString("O", CultureInfo.InvariantCulture),
                        scheduledEnd = value.ScheduledEnd.ToString("O", CultureInfo.InvariantCulture)
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
                    id = $"location/{value.LocationId}",
                    partitionKey = value.TenantId,
                    documentKind = CosmosRelationQuerySourceReader.DefaultEntityDocumentKind,
                    observationType = FreightOrderMaterializationModel.LocationShapeId.ShapeId.Value,
                    observationId = value.LocationId,
                    observationVersion = 1,
                    observation = new
                    {
                        id = value.LocationId,
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
            (await database.CreateContainerAsync("orderStops", "/partitionKey")).Container,
            (await database.CreateContainerAsync("locations", "/partitionKey")).Container);
    }

    static async Task SeedCosmosWithRepositoriesAsync(
        string connectionString,
        string databaseId,
        ScenarioState state,
        FreightOrderStorageDefinitions storage)
    {
        using var client = CreateCosmosClient(connectionString);
        var containers = await ResetCosmosAsync(client, databaseId);
        var partition = EntityPartitionKeyPolicy.FromField("tenantId");
        var orderRepository = Repository(storage.Order, containers.Orders, "order");
        var customerRepository = Repository(storage.CustomerAccount, containers.Customers, "customer");
        var stopRepository = Repository(storage.OrderStop, containers.Stops, "stop");
        var locationRepository = Repository(storage.Location, containers.Locations, "location");
        var seeder = new GenericRepositorySeedDataService(
            [
                GenericRepositorySeedBinding.For(storage.CustomerAccount, customerRepository),
                GenericRepositorySeedBinding.For(storage.Location, locationRepository),
                GenericRepositorySeedBinding.For(storage.Order, orderRepository),
                GenericRepositorySeedBinding.For(storage.OrderStop, stopRepository)
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
            itemIdSelector: observation => $"{itemPrefix}/{observation.Id}",
            partitionKeyPolicy: partition);
    }

    static IReadOnlyList<RepositorySeedStateItem> CreateRepositorySeedItems(ScenarioState state)
    {
        List<RepositorySeedStateItem> items = new(
            state.Customers.Length + state.Locations.Length + state.Orders.Length + state.Stops.Length);
        foreach (var value in state.Customers)
        {
            items.Add(new(
                Type: FreightOrderMaterializationModel.CustomerAccountShapeId.ShapeId.Value,
                Id: value.CustomerAccountId,
                State: new FreightCustomerAccount
                {
                    Id = value.CustomerAccountId,
                    TenantId = value.TenantId,
                    DisplayName = value.DisplayName
                },
                Version: 1,
                PartitionKey: value.TenantId));
        }
        foreach (var value in state.Locations)
        {
            items.Add(new(
                Type: FreightOrderMaterializationModel.LocationShapeId.ShapeId.Value,
                Id: value.LocationId,
                State: new FreightLocation
                {
                    Id = value.LocationId,
                    TenantId = value.TenantId,
                    DisplayName = value.DisplayName,
                    City = value.City,
                    Region = value.Region
                },
                Version: 1,
                PartitionKey: value.TenantId));
        }
        foreach (var value in state.Orders)
        {
            var endpoints = SelectEndpoints(state, value);
            items.Add(new(
                Type: FreightOrderMaterializationModel.OrderShapeId.ShapeId.Value,
                Id: value.OrderId,
                State: new Cohesive.MaterializationHarness.Model.FreightOrder
                {
                    Id = value.OrderId,
                    TenantId = value.TenantId,
                    OrderNumber = value.OrderNumber,
                    CustomerAccountId = value.CustomerAccountId,
                    EquipmentClass = value.EquipmentClass,
                    PickupStopId = endpoints.PickupStopId,
                    DeliveryStopId = endpoints.DeliveryStopId,
                    OriginLocationId = endpoints.OriginLocationId,
                    DestinationLocationId = endpoints.DestinationLocationId,
                    CreatedAt = value.CreatedAt
                },
                Version: 1,
                PartitionKey: value.TenantId));
        }
        foreach (var value in state.Stops)
        {
            items.Add(new(
                Type: FreightOrderMaterializationModel.OrderStopShapeId.ShapeId.Value,
                Id: value.OrderStopId,
                State: new FreightOrderStop
                {
                    Id = value.OrderStopId,
                    TenantId = value.TenantId,
                    OrderId = value.OrderId,
                    Sequence = value.Sequence,
                    StopType = value.StopType,
                    LocationId = value.LocationId,
                    ScheduledStart = value.ScheduledStart,
                    ScheduledEnd = value.ScheduledEnd
                },
                Version: 1,
                PartitionKey: value.TenantId));
        }
        return items;
    }

    static async Task VerifyCosmosAsync(
        string connectionString,
        string databaseId,
        ScenarioState state)
    {
        using var client = CreateCosmosClient(connectionString);
        await VerifyCosmosAsync(client.GetDatabase(databaseId), state);
    }

    static async Task VerifyCosmosAsync(Database database, ScenarioState state)
    {
        await VerifyContainerAsync(database, "orders", state.Orders.Length, "Order");
        await VerifyContainerAsync(database, "customerAccounts", state.Customers.Length, "CustomerAccount");
        await VerifyContainerAsync(database, "orderStops", state.Stops.Length, "OrderStop");
        await VerifyContainerAsync(database, "locations", state.Locations.Length, "Location");
        await VerifyCosmosDocumentsAsync(database, state);
    }

    static async Task VerifyCosmosDocumentsAsync(Database database, ScenarioState state)
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
                new QueryDefinition("SELECT * FROM c"),
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
                        document.GetProperty("observationVersion").GetInt64() == 1,
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

    static CosmosClient CreateCosmosClient(string connectionString) => new(
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

    static async Task<int> CountAsync(Container container)
    {
        using var iterator = container.GetItemQueryIterator<int>(
            new QueryDefinition("SELECT VALUE COUNT(1) FROM c"),
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
            await CountAsync(container) == expectedCount,
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

    static void RequireText(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"{name} cannot be empty.");
    }

    static OrderEndpoints SelectEndpoints(ScenarioState state, FreightOrder order)
    {
        var ordered = state.Stops
            .Where(stop => stop.TenantId == order.TenantId && stop.OrderId == order.OrderId)
            .OrderBy(static stop => stop.Sequence)
            .ToArray();
        var pickups = ordered.Where(static stop => stop.StopType == "Pickup").ToArray();
        var drops = ordered.Where(static stop => stop.StopType == "Drop").ToArray();
        Require(
            pickups.Length == 1,
            $"Order '{order.TenantId}/{order.OrderId}' has {pickups.Length} pickup endpoints; exactly one is required.");
        Require(
            drops.Length > 0,
            $"Order '{order.TenantId}/{order.OrderId}' has no delivery endpoint.");
        return new(
            pickups[0].OrderStopId,
            drops[^1].OrderStopId,
            pickups[0].LocationId,
            drops[^1].LocationId);
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
        Verify
    }

    sealed record ScenarioJournal(
        string SchemaVersion,
        string ScenarioId,
        ImmutableArray<ScenarioOperation> Operations);

    sealed record ScenarioOperation(
        long Sequence,
        string Entity,
        string Operation,
        JsonElement Document);

    abstract record TenantDocument(string TenantId);

    sealed record FreightOrder(
        string TenantId,
        string OrderId,
        string OrderNumber,
        string CustomerAccountId,
        string EquipmentClass,
        DateTimeOffset CreatedAt) : TenantDocument(TenantId);

    sealed record CustomerAccount(
        string TenantId,
        string CustomerAccountId,
        string DisplayName) : TenantDocument(TenantId);

    sealed record OrderStop(
        string TenantId,
        string OrderStopId,
        string OrderId,
        int Sequence,
        string StopType,
        string LocationId,
        DateTimeOffset ScheduledStart,
        DateTimeOffset ScheduledEnd) : TenantDocument(TenantId);

    sealed record Location(
        string TenantId,
        string LocationId,
        string DisplayName,
        string City,
        string Region) : TenantDocument(TenantId);

    readonly record struct EntityKey(string TenantId, string Id)
    {
        public override string ToString() => $"{TenantId}/{Id}";
    }

    readonly record struct OrderEndpoints(
        string PickupStopId,
        string DeliveryStopId,
        string OriginLocationId,
        string DestinationLocationId);

    sealed record CosmosSeedContainers(
        Database Database,
        Container Orders,
        Container Customers,
        Container Stops,
        Container Locations);

    sealed record ExpectedCosmosDocument(
        string Container,
        string ItemId,
        string PartitionKey,
        string ObservationType,
        string ObservationId,
        JsonElement State)
    {
        public static ExpectedCosmosDocument From(RepositorySeedStateItem item)
        {
            var (container, prefix) = item.Type switch
            {
                "order" => ("orders", "order"),
                "customer-account" => ("customerAccounts", "customer"),
                "order-stop" => ("orderStops", "stop"),
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
                JsonSerializer.SerializeToElement(
                    state.Fields ?? throw new InvalidOperationException("Cosmos seed state is not an object."),
                    CosmosSystemTextJsonSerializer.CreateDefaultOptions()));
        }
    }

    sealed record ScenarioState(
        string ScenarioId,
        ImmutableArray<FreightOrder> Orders,
        ImmutableArray<CustomerAccount> Customers,
        ImmutableArray<OrderStop> Stops,
        ImmutableArray<Location> Locations)
    {
        public int TenantCount => Orders.Select(static order => order.TenantId)
            .Distinct(StringComparer.Ordinal)
            .Count();
    }
}
