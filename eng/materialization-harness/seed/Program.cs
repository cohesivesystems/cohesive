using System.Collections.Immutable;
using System.Net;
using System.Net.Security;
using System.Text.Json;
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
            [] => ExecutionMode.Seed,
            ["--validate-only"] => ExecutionMode.Validate,
            ["--verify-only"] => ExecutionMode.Verify,
            _ => throw new ArgumentException(
                "The seed projection accepts only --validate-only or --verify-only.",
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
        if (mode == ExecutionMode.Seed)
        {
            await SeedPostgresAsync(options.PostgresConnectionString, state);
            await SeedCosmosAsync(options.CosmosConnectionString, options.CosmosDatabase, state);
        }
        else
        {
            await VerifyPostgresAsync(options.PostgresConnectionString, state);
            await VerifyCosmosAsync(options.CosmosConnectionString, options.CosmosDatabase, state);
        }
        await VerifyElasticsearchAsync(options.ElasticsearchEndpoint);
        PrintSummary(mode == ExecutionMode.Seed ? "Seeded" : "Verified", state);
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
            Require(group.Any(static stop => stop.StopType == "Pickup"), $"Order '{group.Key}' has no pickup.");
            Require(group.Any(static stop => stop.StopType == "Drop"), $"Order '{group.Key}' has no drop.");
        }
        Require(
            state.Stops.Select(static stop => new EntityKey(stop.TenantId, stop.OrderId)).ToHashSet()
                .SetEquals(orders),
            "Every Order must own at least one tenant-local stop sequence.");
    }

    static async Task SeedPostgresAsync(string connectionString, ScenarioState state)
    {
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var schema = new NpgsqlCommand($$"""
            DROP PUBLICATION IF EXISTS {{PostgresPublication}};
            DROP SCHEMA IF EXISTS {{PostgresSchema}} CASCADE;
            CREATE SCHEMA {{PostgresSchema}};

            CREATE TABLE {{PostgresSchema}}.customer_accounts (
                tenant_id text COLLATE "C" NOT NULL,
                customer_account_id text COLLATE "C" NOT NULL,
                display_name text NOT NULL,
                PRIMARY KEY (tenant_id, customer_account_id)
            );
            CREATE TABLE {{PostgresSchema}}.locations (
                tenant_id text COLLATE "C" NOT NULL,
                location_id text COLLATE "C" NOT NULL,
                display_name text NOT NULL,
                city text NOT NULL,
                region text NOT NULL,
                PRIMARY KEY (tenant_id, location_id)
            );
            CREATE TABLE {{PostgresSchema}}.orders (
                tenant_id text COLLATE "C" NOT NULL,
                order_id text COLLATE "C" NOT NULL,
                order_number text COLLATE "C" NOT NULL,
                customer_account_id text COLLATE "C" NOT NULL,
                equipment_class text NOT NULL,
                created_at timestamptz NOT NULL,
                PRIMARY KEY (tenant_id, order_id),
                FOREIGN KEY (tenant_id, customer_account_id)
                    REFERENCES {{PostgresSchema}}.customer_accounts (tenant_id, customer_account_id)
            );
            CREATE TABLE {{PostgresSchema}}.order_stops (
                tenant_id text COLLATE "C" NOT NULL,
                order_stop_id text COLLATE "C" NOT NULL,
                order_id text COLLATE "C" NOT NULL,
                sequence integer NOT NULL CHECK (sequence > 0),
                stop_type text NOT NULL CHECK (stop_type IN ('Pickup', 'Drop')),
                location_id text COLLATE "C" NOT NULL,
                scheduled_start timestamptz NOT NULL,
                scheduled_end timestamptz NOT NULL,
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
            """, connection, transaction))
        {
            await schema.ExecuteNonQueryAsync();
        }

        foreach (var value in state.Customers)
        {
            await ExecuteAsync(
                connection,
                transaction,
                $"INSERT INTO {PostgresSchema}.customer_accounts VALUES (@tenant, @id, @name);",
                ("tenant", value.TenantId),
                ("id", value.CustomerAccountId),
                ("name", value.DisplayName));
        }
        foreach (var value in state.Locations)
        {
            await ExecuteAsync(
                connection,
                transaction,
                $"INSERT INTO {PostgresSchema}.locations VALUES (@tenant, @id, @name, @city, @region);",
                ("tenant", value.TenantId),
                ("id", value.LocationId),
                ("name", value.DisplayName),
                ("city", value.City),
                ("region", value.Region));
        }
        foreach (var value in state.Orders)
        {
            await ExecuteAsync(
                connection,
                transaction,
                $"INSERT INTO {PostgresSchema}.orders VALUES (@tenant, @id, @number, @customer, @equipment, @created);",
                ("tenant", value.TenantId),
                ("id", value.OrderId),
                ("number", value.OrderNumber),
                ("customer", value.CustomerAccountId),
                ("equipment", value.EquipmentClass),
                ("created", value.CreatedAt));
        }
        foreach (var value in state.Stops)
        {
            await ExecuteAsync(
                connection,
                transaction,
                $"INSERT INTO {PostgresSchema}.order_stops VALUES (@tenant, @id, @order, @sequence, @type, @location, @start, @end);",
                ("tenant", value.TenantId),
                ("id", value.OrderStopId),
                ("order", value.OrderId),
                ("sequence", value.Sequence),
                ("type", value.StopType),
                ("location", value.LocationId),
                ("start", value.ScheduledStart),
                ("end", value.ScheduledEnd));
        }
        await transaction.CommitAsync();

        await VerifyPostgresAsync(connection, state);
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
        await using var reader = await verify.ExecuteReaderAsync();
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

    static async Task SeedCosmosAsync(
        string connectionString,
        string databaseId,
        ScenarioState state)
    {
        using var client = CreateCosmosClient(connectionString);
        var prior = client.GetDatabase(databaseId);
        try
        {
            await prior.DeleteAsync();
        }
        catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
        }

        var database = (await client.CreateDatabaseAsync(databaseId)).Database;
        var orders = (await database.CreateContainerAsync("orders", "/tenantId")).Container;
        var customers = (await database.CreateContainerAsync("customerAccounts", "/tenantId")).Container;
        var stops = (await database.CreateContainerAsync("orderStops", "/tenantId")).Container;
        var locations = (await database.CreateContainerAsync("locations", "/tenantId")).Container;
        foreach (var value in state.Orders)
        {
            await orders.UpsertItemAsync(
                new
                {
                    id = value.OrderId,
                    tenantId = value.TenantId,
                    orderId = value.OrderId,
                    orderNumber = value.OrderNumber,
                    customerAccountId = value.CustomerAccountId,
                    equipmentClass = value.EquipmentClass,
                    createdAt = value.CreatedAt
                },
                new(value.TenantId));
        }
        foreach (var value in state.Customers)
        {
            await customers.UpsertItemAsync(
                new
                {
                    id = value.CustomerAccountId,
                    tenantId = value.TenantId,
                    customerAccountId = value.CustomerAccountId,
                    displayName = value.DisplayName
                },
                new(value.TenantId));
        }
        foreach (var value in state.Stops)
        {
            await stops.UpsertItemAsync(
                new
                {
                    id = value.OrderStopId,
                    tenantId = value.TenantId,
                    orderStopId = value.OrderStopId,
                    orderId = value.OrderId,
                    sequence = value.Sequence,
                    stopType = value.StopType,
                    locationId = value.LocationId,
                    scheduledStart = value.ScheduledStart,
                    scheduledEnd = value.ScheduledEnd
                },
                new(value.TenantId));
        }
        foreach (var value in state.Locations)
        {
            await locations.UpsertItemAsync(
                new
                {
                    id = value.LocationId,
                    tenantId = value.TenantId,
                    locationId = value.LocationId,
                    displayName = value.DisplayName,
                    city = value.City,
                    region = value.Region
                },
                new(value.TenantId));
        }

        await VerifyCosmosAsync(database, state);
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
    }

    static CosmosClient CreateCosmosClient(string connectionString) => new(
        connectionString,
        new CosmosClientOptions
        {
            ConnectionMode = ConnectionMode.Gateway,
            HttpClientFactory = CreateCosmosHttpClient,
            LimitToEndpoint = true
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
            string.Equals(properties.PartitionKeyPath, "/tenantId", StringComparison.Ordinal),
            $"Cosmos {entity} partition path is not /tenantId.");
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
        Seed,
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
