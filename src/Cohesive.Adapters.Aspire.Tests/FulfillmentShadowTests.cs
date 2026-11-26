using Cohesive.Transitions.Authoring;
using Cohesive.Adapters.Elastic;
using Cohesive.Adapters.Marten;
using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.Builder;
using Cohesive.Adapters.AspNet;
using Cohesive.Adapters.AspNet.Services;
using Cohesive.Api.Execution.Services;
using Cohesive.Relations.Execution;
using System.Text.Json;
using Cohesive.Application;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json.Serialization;
using AspireFirst.Orders;
using AspireFirst.Materialization;
using Cohesive.Adapters.Postgres;
using Cohesive.Prelude;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Physical;
using Cohesive.Storage.Materialization;
using Npgsql;
using Npgsql.Replication;

namespace Cohesive.Adapters.Aspire.Tests;

public sealed class FulfillmentShadowTests
{
    static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    [Fact]
    public void Materialization_model_activation_shares_query_authority_and_freezes_membership()
    {
        var application = ModelActivator.Create<OrderMaterializationApplication>(services => services.AddSingleton(new FulfillmentHistoryPolicy()));
        var model = application.Materializations;
        Assert.Same(model.OrderDetails, Assert.Single(model.Definition));
        Assert.Equal(model.Definition, model.Definition);
        Assert.Same(model.OrderDetails, application.Storage.OrderDetails.Materialization);
        Assert.Same(application.Storage.OrderDetails, Assert.Single(application.Storage.Definition));
        Assert.Same(application.Orders.Queries.OrderDetailsRelation.Definition,
            model.OrderDetails.Document.Definition.Relation.CompilationRequest);
        var builder = new MaterializationModelBuilder();
        Assert.Same(model.OrderDetails, builder.Include(model.OrderDetails));
        Assert.Throws<ArgumentException>(() => builder.Include(model.OrderDetails));
        Assert.Same(model.OrderDetails, Assert.Single(builder.Build()));
        Assert.Throws<InvalidOperationException>(() => builder.Include(model.OrderDetails));
    }

    [Fact]
    public void Activator_completes_materialization_models_without_native_resources()
    {
        var model = ModelActivator.Create<CompletionProbe>();
        Assert.Empty(model.Definition);
        Assert.Throws<InvalidOperationException>(() => model.Add(TestModels.Fulfillment().Queries));
    }

    public sealed class CompletionProbe : MaterializationModel
    {
        public void Add(FulfillmentQueries queries) => Materializations.Include(new FulfillmentMaterializations(queries).OrderDetails);
    }

    [Fact]
    public void Shadow_reads_preserve_the_live_relation_and_map_every_impact_field()
    {
        var application = ModelActivator.Create<OrderMaterializationApplication>(services => services.AddSingleton(new FulfillmentHistoryPolicy()));
        var model = application.Orders;
        var live = model.Queries.OrderDetails.CompilationRequest;
        var definition = application.Materializations;
        var prepared = new PostgresMaterializationRelation(model.Storage.Bindings, model.Queries.OrderDetailsRelation.Definition,
            definition.OrderDetails.Impact, FulfillmentDomain.PartitionField, new(new("test/shadow"), "test/v1", 100, 1000, 1000, 100, 100, 1));
        Assert.Same(live, model.Queries.OrderDetails.CompilationRequest);
        Assert.Equal(RelationQueryCompiledPlanReferenceFingerprinter.Compute(definition.OrderDetails.Document.Definition.Relation.CompiledPlan),
            RelationQueryCompiledPlanReferenceFingerprinter.Compute(prepared.Reads.Baseline.Plan));
        var root = Assert.Single(prepared.Reads.Baseline.Placement.Bindings.Where(binding =>
            binding.Kind == RelationQuerySourcePlacementBindingKind.SourceSet));
        Assert.Equal(RelationQuerySourceAcquisitionKind.BoundedEnumeration, root.Acquisition);
        var impact = prepared.Reads.Impact.Placement.Bindings.Single(binding => binding.Input == root.Input);
        Assert.Equal(RelationQuerySourceAcquisitionKind.BoundedLookup, impact.Acquisition);
        foreach (var binding in prepared.Reads.Impact.Placement.Bindings)
        {
            Assert.Single(prepared.Reads.Impact.Stages.Where(stage => stage.PlacementBinding == binding.Id
                && stage.Kind == RelationQueryPhysicalStageKind.BatchedIdentityLookup));
            var table = prepared.ReadStorage.ResolveTable(binding.Id);
            Assert.All(binding.RelationshipKeys, key => Assert.Contains(table.RelationshipReferences,
                reference => reference.Input == key.Input && reference.SemanticPath == key.SemanticPath));
        }
        Assert.NotNull(RelationQueryPlacement.ForPhysicalReads(prepared.Plan, prepared.Reads.Impact));
    }

    [Fact]
    public void Storage_declaration_rejects_duplicate_slots_before_io()
    {
        var error = Assert.Throws<ArgumentException>(() => DeclareStorage(StorageOptions() with { Slot = _ => "duplicate_slot" }));
        Assert.Contains("distinct", error.Message);
    }

    [Fact]
    public void Storage_declaration_rejects_missing_partition_before_io()
    {
        var error = Assert.Throws<ArgumentException>(() => DeclareStorage(StorageOptions() with { Reads = new(100, 1000, 100, 1_048_576) }));
        Assert.Contains("partition scope", error.Message);
    }

    [Fact]
    public async Task Missing_keyed_replication_factory_fails_before_native_io()
    {
        var storage = DeclareStorage(StorageOptions());
        await using var database = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=orders;Username=test");
        using var services = new ServiceCollection().AddKeyedSingleton<NpgsqlDataSource>("orders", database).BuildServiceProvider();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => storage.BindAsync(services,
            UnusedTarget(), new byte[32], "test/v1"));
        Assert.Contains(nameof(PostgresLogicalReplicationConnectionFactory), error.Message);
    }

    [Fact]
    public async Task Missing_replication_factory_fails_without_opening_pool()
    {
        var storage = DeclareStorage(StorageOptions());
        await using var database = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=unreachable;Username=test");
        var error = await Assert.ThrowsAsync<ArgumentNullException>(() => storage.BindAsync(database, null!, UnusedTarget(), new byte[32], "test/v1"));
        Assert.Equal("replicationConnectionFactory", error.ParamName);
    }

    [Fact]
    public async Task Wrong_database_pool_fails_native_affinity_before_opening_connections()
    {
        var storage = DeclareStorage(StorageOptions());
        await using var database = NpgsqlDataSource.Create(
            "Host=127.0.0.1;Port=1;Database=wrong_database;Username=test");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => storage.BindAsync(
            database,
            () => new LogicalReplicationConnection("Host=127.0.0.1;Port=1;Database=orders;Username=test"),
            UnusedTarget(), new byte[32], "test/v1"));
        Assert.Contains("does not match", error.Message);
        Assert.DoesNotContain("wrong_database", error.Message);
    }

    // Neither admission failure should reach target capability admission or perform target IO.
    static InMemoryMaterializationTarget UnusedTarget() => new(new(
        new("test/unused-target"), new("test/materialization"),
        new(new("test/unused-profile"), MaterializationEndpointRole.Target, "test/unused-target", [])));

    static PostgresMaterializationStorage DeclareStorage(PostgresMaterializationStorageOptions options)
    {
        var application = ModelActivator.Create<OrderMaterializationApplication>(services => services.AddSingleton(new FulfillmentHistoryPolicy()));
        return new(application.Orders.Storage.Bindings, application.Materializations.OrderDetails, options);
    }

    static PostgresMaterializationStorageOptions StorageOptions() => new()
    {
        PartitionField = FulfillmentDomain.PartitionField,
        Planning = new(new("test/storage"), "test/v1", 100, 1000, 1000, 100, 100, 1),
        Reads = new(100, 1000, 100, 1_048_576, partitionScope: new(new("local"), FulfillmentDomain.PartitionField, "local")),
        Rebuild = new(100, 1_048_576, 100, 1_048_576, 128, 1, 1, 16),
        Publication = "test_publication",
        Slot = input => "test_" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(input.Value)))[..16],
        Replication = new(),
        Checkpoints = new("test/storage"),
        Provider = "test/storage",
        Capacity = new(new("test/capacity"), 1, ["test/single-writer"]),
        Provenance = new(new("test/storage", "1"), new("test/storage"))
    };

    sealed class RowsFactAttribute : FactAttribute
    {
        public RowsFactAttribute()
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("COHESIVE_SHADOW_POSTGRES")))
                Skip = "Requires an isolated logical-replication PostgreSQL orders database.";
        }
    }

    [RowsFact]
    public async Task Rooted_rows_worker_republishes_both_orders_sharing_stock_and_preserves_unrelated_row()
    {
        var connection = Environment.GetEnvironmentVariable("COHESIVE_SHADOW_POSTGRES")!;
        await using var database = NpgsqlDataSource.Create(connection);
        var app = ModelActivator.Create<OrderMaterializationApplication>(services => services.AddSingleton(new FulfillmentHistoryPolicy()));
        var bindings = app.Orders.Storage.Bindings;
        await bindings.PrepareSchemaAsync(database);
        var id = Guid.NewGuid().ToString("N");
        var a = "A" + id;
        var b = "B" + id;
        var u = "U" + id;
        var sku = "S" + id;
        var table = "rooted_" + id;
        async Task Execute(string sql, params string[] values)
        {
            await using var cmd = database.CreateCommand(sql);
            foreach (var value in values) cmd.Parameters.AddWithValue(value);
            await cmd.ExecuteNonQueryAsync();
        }
        async Task<string> Version(string root)
        {
            await using var cmd = database.CreateCommand($"SELECT xmin::text FROM {table} WHERE order_id = $1");
            cmd.Parameters.AddWithValue(root);
            return (string)(await cmd.ExecuteScalarAsync())!;
        }
        try
        {
            await Execute("INSERT INTO cohesive_orders(order_id,partition_key,status,observation_version) VALUES ($1,'local','Draft',0),($2,'local','Draft',0),($3,'local','Draft',0)", a,b,u);
            await Execute("INSERT INTO cohesive_inventory(sku,partition_key,available,observation_version) VALUES ($1,'local',10,0),($2,'local',5,0)", sku,u);
            await Execute("INSERT INTO cohesive_reservations(reservation_id,order_id,sku,partition_key,quantity,observation_version) VALUES ($1,$1,$4,'local',1,0),($2,$2,$4,'local',1,0),($3,$3,$3,'local',1,0)", a,b,u,sku);
            await Execute("INSERT INTO cohesive_orders(order_id,partition_key,status,observation_version) VALUES ($1,'local','Draft',0)", "empty"+id);
            var target = bindings.Bind(database, "tests/rows").MaterializeAsyncRows(AvailabilityRows(app.Orders.Domain),
                table, new(new("tenant/local"), FulfillmentDomain.PartitionField, "local"), new()
                {
                    ScheduleName = table,
                    ConfigureStorage = options => options with
                    {
                        Publication = table,
                        Slot = input => table + "_" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
                            System.Text.Encoding.UTF8.GetBytes(input.Value)))[..12]
                    }
                });
            await target.InitializeAsync();
            var schedule = target.Schedule;
            Task<PostgresRootedRowsWorker<ReservationAvailability>> Bind() => target.BindWorkerAsync(
                () => new LogicalReplicationConnection(connection), new byte[32], "test-slots/v1");
            using var worker = await Bind();
            await worker.RunOnceAsync();
            Assert.Null(Assert.Single(await target.ReadAsync("empty"+id)).ReservationId);
            var vu = await Version(u);
            var va = await Version(a);
            var vb = await Version(b);
            await Execute("UPDATE cohesive_inventory SET available=8,observation_version=observation_version+1 WHERE sku=$1", sku);
            using var restarted = await Bind();
            await restarted.RunOnceAsync();
            Assert.NotEqual(va, await Version(a));
            Assert.NotEqual(vb, await Version(b));
            Assert.Equal(vu, await Version(u));
            var byOrder = target;
            Assert.Equal(8, Assert.Single(await byOrder.ReadAsync(a)).Available);
            Assert.Equal(8, Assert.Single(await byOrder.ReadAsync(b)).Available);
            Assert.Equal(5, Assert.Single(await byOrder.ReadAsync(u)).Available);
            await Execute($"DELETE FROM {table} WHERE order_id=$1", a);
            var requested = await schedule.RequestResyncAsync();
            await restarted.RunOnceAsync();
            Assert.Equal(requested, (await schedule.CheckpointAsync()).Applied);
            Assert.Equal(8, Assert.Single(await byOrder.ReadAsync(a)).Available);
        }
        finally
        {
            await Execute("SELECT pg_drop_replication_slot(slot_name) FROM pg_replication_slots WHERE starts_with(slot_name,$1)", table+"_");
            await Execute($"DROP PUBLICATION IF EXISTS {table}");
            await Execute($"DROP FUNCTION IF EXISTS {table}_changed() CASCADE; DROP TABLE IF EXISTS {table}_progress,{table}_request,{table}");
            await Execute("DELETE FROM cohesive_reservations WHERE sku IN ($1,$2)", sku,u);
            await Execute("DELETE FROM cohesive_inventory WHERE sku IN ($1,$2)", sku,u);
            await Execute("DELETE FROM cohesive_orders WHERE order_id IN ($1,$2,$3,$4)", a,b,u,"empty"+id);
        }
    }

    // Each database has independent source rows and progress; logical slot names share a cluster namespace.
    static async Task InDatabase(Func<NpgsqlDataSource, string, Task> test)
    {
        var settings = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("COHESIVE_SHADOW_POSTGRES")!);
        await using var admin = NpgsqlDataSource.Create(settings.ConnectionString);
        var name = "rooted_" + Guid.NewGuid().ToString("N");
        await using (var create = admin.CreateCommand($"CREATE DATABASE {name}"))
            await create.ExecuteNonQueryAsync();
        try
        {
            settings.Database = name;
            await using var database = NpgsqlDataSource.Create(settings.ConnectionString);
            await test(database, settings.ConnectionString);
        }
        finally
        {
            await using var drop = admin.CreateCommand($"DROP DATABASE {name} WITH (FORCE)");
            await drop.ExecuteNonQueryAsync();
        }
    }

    static async Task<PostgresAsyncRows<ReservationAvailability>> ScopedAvailability(NpgsqlDataSource database,
        PostgresAsyncRowsOptions? options = null)
    {
        var domain = new FulfillmentDomain();
        var mappings = new FulfillmentPostgresMappings(domain);
        using var connection = database.CreateConnection();
        var bindings = PostgresPersistenceBindings.Define(new(connection.Database), storage =>
        {
            storage.Schema(PostgresSchemaInitialization.CreateIfMissing, _ => { });
            storage.Entity(domain.Orders, mappings.Orders);
            storage.Entity(domain.Inventory, mappings.Inventory);
            storage.Entity(domain.Reservations, mappings.Reservations);
        });
        await bindings.PrepareSchemaAsync(database);
        return bindings.Bind(database, "scope-test").MaterializeAsyncRows(AvailabilityRows(domain),
            "availability_rows", new(new("tenant/local"), FulfillmentDomain.PartitionField, "local"), options);
    }

    [RowsFact]
    public Task Unfiltered_projection_scopes_root_and_join_scans_during_repair_and_catchup() =>
        InDatabase(async (database, connection) =>
        {
            var target = await ScopedAvailability(database);
            await using var seed = database.CreateCommand("""
                INSERT INTO cohesive_orders(order_id,partition_key,status,observation_version)
                VALUES ('A','local','Draft',0),('B','other','Draft',0);
                INSERT INTO cohesive_inventory(sku,partition_key,available,observation_version) VALUES ('X','other',9,0);
                INSERT INTO cohesive_reservations(reservation_id,order_id,sku,partition_key,quantity,released,observation_version)
                VALUES ('r','A','X','local',1,false,0),('foreign','A','X','other',1,false,0);
                """);
            await seed.ExecuteNonQueryAsync();
            await target.InitializeAsync();
            using var worker = await target.BindWorkerAsync(() => new LogicalReplicationConnection(connection), new byte[32]);
            await worker.RunOnceAsync();
            Assert.Empty(await target.ReadAsync("B"));
            Assert.Null(Assert.Single(await target.ReadAsync("A")).Available);
            await using var change = database.CreateCommand("""
                UPDATE cohesive_inventory SET available=99, observation_version=1 WHERE sku='X';
                UPDATE cohesive_reservations SET quantity=3, observation_version=1 WHERE reservation_id='r';
                INSERT INTO cohesive_orders(order_id,partition_key,status,observation_version) VALUES ('C','other','Draft',0);
                """);
            await change.ExecuteNonQueryAsync();
            await worker.RunOnceAsync();
            var row = Assert.Single(await target.ReadAsync("A"));
            Assert.Equal(3, row.Quantity);
            Assert.Null(row.Available);
            Assert.Empty(await target.ReadAsync("C"));
            await target.Schedule.RequestResyncAsync();
            await worker.RunOnceAsync();
            Assert.Empty(await target.ReadAsync("B"));
            Assert.Empty(await target.ReadAsync("C"));
        });

    [RowsFact]
    public Task Same_projection_in_two_databases_uses_distinct_slots_and_checks_existing_slot_ownership() =>
        InDatabase(async (first, _) => await InDatabase(async (second, _) =>
        {
            static async Task<string[]> Slots(NpgsqlDataSource db)
            {
                await using var cmd = db.CreateCommand("SELECT array_agg(slot_name::text ORDER BY slot_name) FROM pg_replication_slots WHERE database=current_database()");
                return (string[])(await cmd.ExecuteScalarAsync())!;
            }
            await (await ScopedAvailability(first)).InitializeAsync();
            var firstSlots = await Slots(first);
            Assert.Equal(3, firstSlots.Length);
            var target = await ScopedAvailability(second);
            await target.InitializeAsync();
            await target.InitializeAsync();
            var secondSlots = await Slots(second);
            Assert.Equal(3, secondSlots.Length);
            Assert.Empty(firstSlots.Intersect(secondSlots));

            async Task RejectSlot(string name)
            {
                var index = 0;
                var bad = await ScopedAvailability(second, new() { ConfigureStorage = native => native with
                    { Slot = input => index++ == 0 ? name : native.Slot(input) } });
                Assert.Contains("permanent pgoutput slot in the current database",
                    (await Assert.ThrowsAsync<InvalidOperationException>(() => bad.InitializeAsync())).Message);
            }
            await RejectSlot(firstSlots[0]);
            var wrongPlugin = "wrong_" + Guid.NewGuid().ToString("N");
            await using var create = second.CreateCommand("SELECT pg_create_logical_replication_slot($1,'test_decoding')");
            create.Parameters.AddWithValue(wrongPlugin);
            await create.ExecuteNonQueryAsync();
            await RejectSlot(wrongPlugin);
        }));

    sealed record OrderKey(string Id);

    sealed class StartupRead(Func<Task> read) : IHostedService
    {
        public Task StartAsync(CancellationToken token) => read();
        public Task StopAsync(CancellationToken token) => Task.CompletedTask;
    }

    [RowsFact]
    public Task Root_only_worker_backfills_and_publishes_a_new_root() =>
        InDatabase(async (database, connection) =>
        {
            var domain = new FulfillmentDomain();
            var mappings = new FulfillmentPostgresMappings(domain);
            using var native = database.CreateConnection();
            var bindings = PostgresPersistenceBindings.Define(new(native.Database), storage =>
            {
                storage.Schema(PostgresSchemaInitialization.CreateIfMissing, _ => { });
                storage.Entity(domain.Orders, mappings.Orders);
            });
            await bindings.PrepareSchemaAsync(database);
            var query = RelationQuery.Expression();
            var orders = query.Source(domain.Orders);
            var ids = query.RowsByRoot<OrderKey>().From(orders).Key(orders, o => o.Id, row => row.Id)
                .BuildRows(new("test/ids"));
            var target = bindings.Bind(database, "ids").MaterializeAsyncRows(ids, "order_ids",
                new(new("tenant/local"), FulfillmentDomain.PartitionField, "local"));
            async Task Insert(string id)
            {
                await using var insert = database.CreateCommand("INSERT INTO cohesive_orders(order_id,partition_key,status,observation_version) VALUES ($1,'local','Draft',0)");
                insert.Parameters.AddWithValue(id);
                await insert.ExecuteNonQueryAsync();
            }
            await Insert("before");
            var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
            // Even an earlier registered service must see the published baseline in StartAsync.
            builder.Services.AddSingleton<IHostedService>(new StartupRead(async () =>
                Assert.Equal(new OrderKey("before"), Assert.Single(await target.ReadAsync("before")))));
            builder.Services.AddPostgresRowsWorker(target, () => new LogicalReplicationConnection(connection), new byte[32]);
            using var host = builder.Build();
            await host.StartAsync();
            await Insert("after");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while ((await target.ReadAsync("after", deadline.Token)).Length == 0)
                await Task.Delay(50, deadline.Token);
            await host.StopAsync();
            Assert.Equal(new OrderKey("after"), Assert.Single(await target.ReadAsync("after")));
        });

    [Fact]
    public async Task Async_rows_derive_routes_from_the_projection_and_reject_an_unrelated_key()
    {
        var app = ModelActivator.Create<OrderMaterializationApplication>(services => services.AddSingleton(new FulfillmentHistoryPolicy()));
        await using var database = NpgsqlDataSource.Create("Host=localhost;Database=orders");
        var persistence = app.Orders.Storage.Bindings.Bind(database, "tests/rows");
        var partition = new PostgresRelationQueryPartitionScope(new("tenant/local"), FulfillmentDomain.PartitionField, "local");
        var query = RelationQuery.Expression();
        var orders = query.Source(app.Orders.Domain.Orders);
        var ids = query.RowsByRoot<OrderKey>().From(orders).Key(orders, o => o.Id, row => row.Id).BuildRows(new("test/ids"));
        Assert.Single(persistence.MaterializeAsyncRows(ids, "order_ids", partition).Impact.Routes);
        var availability = persistence.MaterializeAsyncRows(AvailabilityRows(app.Orders.Domain), "order_availability", partition);
        Assert.Equal(3, availability.Impact.Routes.Length);
        var wrong = query.RowsByRoot<OrderKey>().From(orders).Key(orders, o => o.Partition, row => row.Id).BuildRows(new("test/wrong"));
        Assert.Contains("mapped identity", Assert.Throws<ArgumentException>(() =>
            persistence.MaterializeAsyncRows(wrong, "invalid", partition)).Message);
    }

    static RootedRowProjection<ReservationAvailability> AvailabilityRows(FulfillmentDomain domain)
    {
        var query = RelationQuery.Expression();
        var orders = query.Source(domain.Orders);
        var reservations = orders.TraverseInverse(domain.ReservationOrder);
        var inventory = reservations.Traverse(domain.ReservationItem);
        return query.RowsByRoot<ReservationAvailability>().From(inventory)
            .Key(orders, order => order.Id, row => row.OrderId)
            .Field(row => row.ReservationId, reservations, reservation => reservation.Id)
            .Field(row => row.Sku, inventory, item => item.Sku)
            .Field(row => row.Quantity, reservations, reservation => reservation.Quantity)
            .Field(row => row.Available, inventory, item => item.Available)
            .BuildRows(new("fulfillment/availability"));
    }

    [ShadowFact]
    public async Task Native_shadow_resumes_then_tracks_updates_reparenting_and_deletes_without_changing_live_reads()
    {
        const int MaximumQuiescentComparisons = 5;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var ct = timeout.Token;
        var application = ModelActivator.Create<OrderMaterializationApplication>(services => services.AddSingleton(new FulfillmentHistoryPolicy())
            .AddSingleton(new RelationQueryReadPolicy { Source = RelationQueryReadSource.Materialized, AcceptEventualConsistency = true }));
        var model = application.Orders;
        var connection = Environment.GetEnvironmentVariable("COHESIVE_SHADOW_POSTGRES")!;
        await using var database = NpgsqlDataSource.Create(connection);
        using var http = new HttpClient { BaseAddress = new(Environment.GetEnvironmentVariable("COHESIVE_SHADOW_ELASTIC")!) };
        var definition = application.Materializations;
        await ShadowBootstrap.InitializeAsync(application, database, http, ct);
        var target = await ShadowElastic.BindAsync(http, definition.OrderDetails, ct);
        var runtime = await application.Storage.OrderDetails.BindAsync(database,
            () => new LogicalReplicationConnection(connection), target, new byte[32], "test-slots/v1", ct);
        Assert.Equal("tenant/" + FulfillmentStoragePolicy.LocalPartition, Assert.Single(runtime.Compilation.Plan.Shards).Id.Value);
        var id = Guid.NewGuid().ToString("N");
        var first = "first-" + id;
        var second = "second-" + id;
        var reservation = "reservation-" + id;
        var sku = "sku-" + id;
        var attempt = new MaterializationRebuildAttempt(new(new(id), new(id)), DateTimeOffset.UtcNow);
        var context = OperationContext.Create(cancellationToken: ct);
        var index = target.Binding.GetGenerationIndexName(MaterializationRebuildIdentities.Generation(runtime.Compilation.Plan, attempt));
        var live = model.Storage.Bindings.Bind(database, "tests/shadow/live")
            .Query(model.Queries.OrderDetails, maximumRows: 1000, maximumBytes: 1_000_000);
        MaterializationConvergenceReceipt? convergence = null;
        try
        {
            await Execute("INSERT INTO cohesive_orders(order_id,partition_key,status,observation_version) VALUES ($1,'local','Draft',0),($2,'local','Draft',0)", first, second);
            await Execute("INSERT INTO cohesive_inventory(sku,partition_key,available,observation_version) VALUES ($1,'local',10,0)", sku);
            await Execute("INSERT INTO cohesive_reservations(reservation_id,order_id,sku,partition_key,quantity,observation_version) VALUES ($1,$2,$3,'local',2,0)", reservation, first, sku);
            var rebuild = new MaterializationRebuildExecutor(runtime.Resolved);
            Assert.Equal(MaterializationRebuildInitializationDisposition.Ready, (await rebuild.BeginAttemptAsync(context, attempt)).Disposition);
            var interruption = new InterruptAfterWrite();
            await Assert.ThrowsAsync<TestInterruption>(() => new MaterializationRebuildExecutor(runtime.Resolved, interruption)
                .RunShardAsync(context, attempt, Assert.Single(runtime.Compilation.Plan.Shards).Id));
            // Rebind all native interpretations, simulating restart with the same fingerprint, attempt and key.
            runtime = await application.Storage.OrderDetails.BindAsync(database, () => new LogicalReplicationConnection(connection), target, new byte[32], "test-slots/v1", ct);
            rebuild = new(runtime.Resolved);
            Assert.Equal(MaterializationRebuildInitializationDisposition.Ready, (await rebuild.BeginAttemptAsync(context, attempt)).Disposition);
            Assert.Equal(MaterializationRebuildShardDisposition.BaselineCompleteCatchUpRequired,
                (await rebuild.RunShardAsync(context, attempt, Assert.Single(runtime.Compilation.Plan.Shards).Id)).Disposition);
            await CatchUp("baseline");
            var approvedComparison = await AssertMatchesLive(first, second);
            // CLI JSON is the operator artifact consumed by worker admission.
            approvedComparison = JsonSerializer.Deserialize<MaterializationShadowComparison>(JsonSerializer.Serialize(approvedComparison))!;
            var native = new ServicePersistenceBuilder().Query(model.Queries.OrderDetails, (_, _) => live).Build();
            var selected = new ServicePersistenceBuilder(native).Query(model.Queries.OrderDetails,
                await ElasticMaterializationReadSelection.PrepareAsync(model.Queries.OrderDetails, model.Queries.OrderDetailsRelation, runtime, approvedComparison, ct)).Build();
            Assert.Throws<ServiceBindingValidationException>(() => selected.Query(model.Queries.OrderDetails, 1000, 1_000_000, RelationQueryConsistency.SingleSnapshot));
            var otherIncarnation = await application.Storage.OrderDetails.BindAsync(database,
                () => new LogicalReplicationConnection(connection), target, new byte[32], "different-incarnation", ct);
            await Assert.ThrowsAsync<InvalidOperationException>(() => ElasticMaterializationReadSelection.PrepareAsync(model.Queries.OrderDetails, model.Queries.OrderDetailsRelation, otherIncarnation, approvedComparison, ct));
            await Assert.ThrowsAsync<Cohesive.Adapters.Elastic.ElasticMaterializationTransportException>(() => target.ReadValueAsync(context, approvedComparison.Convergence.Generation,
                definition.OrderDetails.Document.DefinitionFingerprint, MaterializationItemId.FromRootIdentity(first), maximumBytes: 1));
            var eventual = selected.Query(model.Queries.OrderDetails, 1000, 1_000_000, RelationQueryConsistency.Eventual);
            var selectedBefore = JsonSerializer.Serialize(await eventual.ReadAsync(first, ct), Json);
            Assert.Equal(JsonSerializer.Serialize(await live.ReadAsync(first, ct), Json), selectedBefore);
            Assert.Null(await eventual.ReadAsync("absent-" + id, ct));
            await AssertHttpRead(approvedComparison, first, await live.ReadAsync(first, ct));
            await Assert.ThrowsAsync<InvalidOperationException>(() => target.ReadValueAsync(context, new("missing-" + id),
                definition.OrderDetails.Document.DefinitionFingerprint, MaterializationItemId.FromRootIdentity(first)));
            await Execute("UPDATE cohesive_inventory SET available=8,observation_version=1 WHERE sku=$1", sku);
            var stale = await Compare();
            Assert.False(stale.Matches);
            Assert.Single(stale.Changed);
            Assert.Equal(selectedBefore, JsonSerializer.Serialize(await eventual.ReadAsync(first, ct), Json));
            // Rollback resolves the original live reader; it does not query, mutate or delete the generation.
            var rolledBack = native.Query(model.Queries.OrderDetails, 1000, 1_000_000, RelationQueryConsistency.SingleSnapshot);
            Assert.Same(live, rolledBack);
            Assert.NotEqual(selectedBefore, JsonSerializer.Serialize(await rolledBack.ReadAsync(first, ct), Json));
            await AssertHttpRead(null, first, await rolledBack.ReadAsync(first, ct));
            Assert.Throws<InvalidOperationException>(() => MaterializationReadSelection.Validate(stale, definition.OrderDetails.Document, DateTimeOffset.UtcNow));
            await Execute("UPDATE cohesive_reservations SET order_id=$1,observation_version=1 WHERE reservation_id=$2", second, reservation);
            await CatchUp("reparent");
            await AssertMatchesLive(first, second);
            var moving = await MaterializationShadowVerification.CompareAsync(runtime, context, convergence!, async (generation, limit, token) =>
            {
                var values = await target.ReadValuesAsync(OperationContext.Create(cancellationToken: token), generation, limit);
                await Execute("UPDATE cohesive_inventory SET available=7,observation_version=2 WHERE sku=$1", sku);
                return values;
            });
            Assert.False(moving.SourceStable);
            Assert.False(moving.Matches);
            await CatchUp("after-movement");
            await AssertMatchesLive(first, second);
            // A WAL cut is cluster-wide: unrelated WAL can invalidate an otherwise equal read.
            // Force that race once and require a new, fully stable comparison before accepting parity.
            var comparisonReads = 0;
            var quiescent = await CompareQuiescent(async (generation, limit, token) =>
            {
                var values = await target.ReadValuesAsync(OperationContext.Create(cancellationToken: token), generation, limit);
                if (++comparisonReads == 1)
                    // Transactional emission commits (and flushes) before the verifier captures its after-cut.
                    await Execute("SELECT pg_logical_emit_message(true, 'shadow-test', 'unrelated-wal')");
                return values;
            });
            Assert.True(quiescent.Matches, JsonSerializer.Serialize(quiescent));
            Assert.InRange(comparisonReads, 2, MaximumQuiescentComparisons);
            await Assert.ThrowsAsync<InvalidOperationException>(() => MaterializationShadowVerification.CompareAsync(runtime, context, convergence!,
                (generation, limit, token) => target.ReadValuesAsync(OperationContext.Create(cancellationToken: token), generation, limit), maximumItems: 1));
            var beforeReplay = await Snapshot();
            await CatchUp("after-movement");
            Assert.True(JsonElement.DeepEquals(beforeReplay, await Snapshot()));
            await Execute("DELETE FROM cohesive_reservations WHERE reservation_id=$1", reservation);
            await Execute("DELETE FROM cohesive_orders WHERE order_id=$1", first);
            await CatchUp("delete");
            await AssertMatchesLive(second);
            Assert.Null(await live.ReadAsync(first, ct));
            Assert.DoesNotContain((await Snapshot()).EnumerateArray(), row =>
                !row.GetProperty("_source").GetProperty("_cohesive").GetProperty("deleted").GetBoolean()
                && row.GetProperty("_source").GetProperty("value").GetProperty("Id").GetString() == first);
            await Execute("DELETE FROM cohesive_orders WHERE order_id=$1", second);
            await CatchUp("empty");
            var empty = await CompareQuiescent();
            Assert.True(empty.Matches);
            Assert.Equal(0, empty.LiveCount);
            Assert.Equal(0, empty.ShadowCount);
            using var aliases = await http.GetAsync("/_alias/orders-shadow", ct);
            Assert.Equal(System.Net.HttpStatusCode.NotFound, aliases.StatusCode);
        }
        finally
        {
            await Execute("DELETE FROM cohesive_reservations WHERE reservation_id=$1", reservation);
            await Execute("DELETE FROM cohesive_orders WHERE order_id IN ($1,$2)", first, second);
            await Execute("DELETE FROM cohesive_inventory WHERE sku=$1", sku);
        }

        async Task Execute(string sql, params object[] parameters)
        {
            await using var command = database.CreateCommand(sql);
            foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter);
            await command.ExecuteNonQueryAsync(ct);
        }
        async Task CatchUp(string invocation)
        {
            var result = await MaterializationShadowExecution.RunAsync(runtime,
                invocation == "baseline" ? MaterializationShadowOperation.Rebuild : MaterializationShadowOperation.CatchUp,
                context, attempt, new(id + "/" + invocation), new("test-worker"));
            Assert.True(result.Disposition == MaterializationSynchronizationRunDisposition.Converged, JsonSerializer.Serialize(result.Diagnostics));
            convergence = Assert.IsType<MaterializationConvergenceReceipt>(result.Receipt);
        }
        async Task<JsonElement> Snapshot()
        {
            using var refresh = await http.PostAsync("/" + index + "/_refresh", null, ct);
            refresh.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await http.GetStringAsync("/" + index + "/_search?size=1000&sort=_doc", ct));
            return document.RootElement.GetProperty("hits").GetProperty("hits").Clone();
        }
        async Task AssertHttpRead(MaterializationShadowComparison? selection, string order, OrderDetails? expected)
        {
            var hostedModel = ModelActivator.Create<FulfillmentApplication>(services => services
                .AddSingleton(new FulfillmentHistoryPolicy())
                .AddSingleton(new RelationQueryReadPolicy { Source = selection is null ? RelationQueryReadSource.Live : RelationQueryReadSource.Materialized,
                    AcceptEventualConsistency = selection is not null }));
            var builder = WebApplication.CreateBuilder();
            var artifact = Path.GetTempFileName();
            await File.WriteAllTextAsync(artifact, JsonSerializer.Serialize(selection), ct);
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OrderReads:ComparisonFile"] = artifact,
                ["Shadow:PositionKey"] = Convert.ToBase64String(new byte[32]),
                ["Shadow:SlotGeneration"] = "test-slots/v1"
            });
            var historyPolicy = new FulfillmentHistoryPolicy();
            builder.Services.AddKeyedSingleton<NpgsqlDataSource>(historyPolicy.DatabaseName, database);
            builder.Services.AddKeyedSingleton<IDocumentStore>(historyPolicy.DatabaseName, (_, _) => DocumentStore.For(options =>
            {
                options.Connection(database);
                historyPolicy.History.Configure(options);
            }));
            if (selection is not null)
            {
                builder.Services.AddKeyedSingleton<HttpClient>(ShadowElastic.Resource, http);
                builder.Services.AddKeyedSingleton<PostgresLogicalReplicationConnectionFactory>(historyPolicy.DatabaseName,
                    (_, _) => () => new LogicalReplicationConnection(connection));
            }
            builder.Services.AddRequestOperationContext();
            builder.Services.AddFixedRequestIdentity(FulfillmentDemoIdentity.Context);
            builder.Services.AddCohesiveServices(hostedModel);
            await using var app = builder.Build();
            app.UseRequestOperationContext();
            app.UseAuthorization();
            await app.MapServicesAsync();
            app.Urls.Add("http://127.0.0.1:0");
            await app.StartAsync(ct);
            try
            {
                using var client = new HttpClient { BaseAddress = new(app.Urls.Single()) };
                using var response = await client.GetAsync("/orders/" + order + "/details", ct);
                response.EnsureSuccessStatusCode();
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                // ASP.NET uses camelCase; compare through the same typed output rather than duplicating a DTO.
                var actual = body.RootElement.Deserialize<OrderDetails>(new JsonSerializerOptions(JsonSerializerDefaults.Web)
                    { Converters = { new JsonStringEnumConverter() } });
                Assert.Equal(JsonSerializer.Serialize(expected, Json), JsonSerializer.Serialize(actual, Json));
            }
            finally { await app.StopAsync(ct); File.Delete(artifact); }
        }
        Task<MaterializationShadowComparison> Compare() => MaterializationShadowVerification.CompareAsync(runtime, context, convergence!,
            (generation, limit, token) => target.ReadValuesAsync(OperationContext.Create(cancellationToken: token), generation, limit));
        async Task<MaterializationShadowComparison> CompareQuiescent(
            Func<MaterializationGenerationId, int, CancellationToken, Task<IReadOnlyDictionary<string, Cohesive.Model.ObservationValue>>>? readCandidate = null)
        {
            // Keep the production verifier conservative. Retry only an otherwise equal, fresh read
            // invalidated by WAL movement, and never carry evidence between comparison attempts.
            for (var attempt = 1; ; attempt++)
            {
                var comparison = await MaterializationShadowVerification.CompareAsync(runtime, context, convergence!,
                    readCandidate ?? ((generation, limit, token) => target.ReadValuesAsync(OperationContext.Create(cancellationToken: token), generation, limit)));
                if (comparison.SourceStable || !comparison.Fresh || !comparison.Missing.IsEmpty
                    || !comparison.Extra.IsEmpty || !comparison.Changed.IsEmpty || attempt == MaximumQuiescentComparisons)
                    return comparison;
                await Task.Delay(TimeSpan.FromMilliseconds(50), ct);
            }
        }
        async Task<MaterializationShadowComparison> AssertMatchesLive(params string[] orders)
        {
            var comparison = await CompareQuiescent();
            Assert.True(comparison.Matches, JsonSerializer.Serialize(comparison));
            var rows = await Snapshot();
            foreach (var order in orders)
            {
                var expected = JsonSerializer.SerializeToElement(await live.ReadAsync(order, ct), Json);
                var actual = rows.EnumerateArray().Select(row => row.GetProperty("_source"))
                    .Where(row => !row.GetProperty("_cohesive").GetProperty("deleted").GetBoolean())
                    .Select(row => row.GetProperty("value")).Single(value => value.GetProperty("Id").GetString() == order);
                Assert.True(JsonElement.DeepEquals(expected, actual), $"Live: {expected}; shadow: {actual}");
            }
            return comparison;
        }
    }

    sealed class TestInterruption : Exception;
    sealed class InterruptAfterWrite : IMaterializationExecutionBoundaryObserver
    {
        public ValueTask ObserveAsync(OperationContext context, MaterializationExecutionBoundaryObservation observation) =>
            observation.Point == MaterializationExecutionBoundaryPoint.AfterTargetBatch
                ? ValueTask.FromException(new TestInterruption()) : ValueTask.CompletedTask;
    }
    sealed class ShadowFactAttribute : FactAttribute
    {
        public ShadowFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("COHESIVE_SHADOW_POSTGRES") is null || Environment.GetEnvironmentVariable("COHESIVE_SHADOW_ELASTIC") is null)
                Skip = "Requires dedicated disposable COHESIVE_SHADOW_POSTGRES and COHESIVE_SHADOW_ELASTIC resources.";
        }
    }
}
