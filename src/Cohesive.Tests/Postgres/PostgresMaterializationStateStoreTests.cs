using System.Collections.Immutable;
using Cohesive.Adapters.Postgres;
using Cohesive.Control;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Acquisition;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.IR;
using Cohesive.Relations.Physical;
using Cohesive.Storage.Materialization;
using Npgsql;

namespace Cohesive.Tests.Postgres;

public sealed class PostgresMaterializationStateStoreTests
{
    static readonly QualifiedShapeId Shape = new(new("tests"), new("freight-order"));
    static readonly RelationQueryPhysicalPlanFingerprint PhysicalPlan = new(
        "sha256",
        "tests/materialization-state/physical-plan/v1",
        "physical-plan");
    static readonly RelationQuerySourcePlacementBinding Placement = new(
        id: new("placement/orders"),
        input: new("input/orders"),
        node: new("node/orders"),
        binding: new("binding/orders"),
        shape: Shape,
        source: new("source/orders"),
        kind: RelationQuerySourcePlacementBindingKind.SourceSet,
        acquisition: RelationQuerySourceAcquisitionKind.BoundedEnumeration,
        origin: RelationQuerySourcePlacementOrigin.Explicit,
        identity: new(Shape, "id"));
    static readonly MaterializationSourceScope Scope = new(
        physicalPlan: PhysicalPlan,
        placement: Placement,
        partition: new("tenant-a"),
        orderingScope: new("tenant-a/orders"));
    static readonly ExecutionDefinitionFingerprint DefinitionFingerprint = new(
        "sha256",
        "tests/materialization-definition/v1",
        "definition");
    static readonly MaterializationProgressKey ProgressKey = new(
        materialization: new("freight/order-search"),
        definitionFingerprint: DefinitionFingerprint,
        generation: new("generation-a"),
        scope: Scope);
    static readonly MaterializationSynchronizationWorkKey SynchronizationKey = new(
        materialization: ProgressKey.Materialization,
        definitionFingerprint: DefinitionFingerprint,
        rebuildPlanFingerprint: new(
            "sha256",
            "materialization-rebuild-plan/v1",
            "rebuild-plan"),
        impactPlanFingerprint: new(
            "sha256",
            "materialization-impact-plan/v1",
            new string('a', 64)),
        generation: ProgressKey.Generation);

    [Fact]
    public void Options_ReuseSharedPostgresIdentifierValidationAndQuoting()
    {
        var options = new PostgresMaterializationStateStoreOptions(
            authorityId: "authority/materialization-tests",
            schema: "materialization-schema",
            table: "state\"ledgers",
            maximumDocumentBytes: 2048);

        Assert.Equal(2048, options.MaximumDocumentBytes);
        Assert.Equal("\"materialization-schema\"", options.QualifiedSchema);
        Assert.Equal(
            "\"materialization-schema\".\"state\"\"ledgers\"",
            options.QualifiedTable);
        Assert.Throws<ArgumentException>(() => new PostgresMaterializationStateStoreOptions(
            authorityId: "authority/materialization-tests",
            schema: "",
            table: "state_ledgers"));
    }

    [PostgresFact]
    public async Task Existing_publication_rejects_new_unpublished_table_and_rolls_back_identity_change()
    {
        await using var database = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("COHESIVE_POSTGRES_TEST_CONNECTION_STRING")!);
        var name = "coverage_" + Guid.NewGuid().ToString("N");
        await using var setup = database.CreateCommand($"CREATE SCHEMA {name}; CREATE TABLE {name}.original(id text); CREATE TABLE {name}.added(id text); CREATE PUBLICATION {name} FOR TABLE {name}.original");
        await setup.ExecuteNonQueryAsync();
        try
        {
            var state = new PostgresMaterializationStateStore(database, new PostgresMaterializationStateStoreOptions(name));
            await PostgresLogicalReplicationPreparation.InitializeAsync(database,
                [new Cohesive.Adapters.Sql.SqlQualifiedTable(name, "original")], name, [], state);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                PostgresLogicalReplicationPreparation.InitializeAsync(database,
                    [new Cohesive.Adapters.Sql.SqlQualifiedTable(name, "original"), new(name, "added")], name, [], state));
            Assert.Contains("does not cover table", error.Message);
            await using var identity = database.CreateCommand($"SELECT relreplident::text FROM pg_class WHERE oid = '{name}.added'::regclass");
            Assert.Equal("d", await identity.ExecuteScalarAsync());
        }
        finally
        {
            await using var cleanup = database.CreateCommand($"DROP PUBLICATION {name}; DROP SCHEMA {name} CASCADE");
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [PostgresFact]
    public async Task Operator_lease_releases_and_broken_unlock_does_not_mask_failure()
    {
        var connection = Environment.GetEnvironmentVariable("COHESIVE_POSTGRES_TEST_CONNECTION_STRING")!;
        var name = "lease-" + Guid.NewGuid().ToString("N");
        await using var database = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(connection) { ApplicationName = name }.ConnectionString);
        await using var admin = NpgsqlDataSource.Create(connection);
        const long key = 394823419;
        await using (var lease = await PostgresSessionLease.AcquireAsync(database, key))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => PostgresSessionLease.AcquireAsync(database, key));
            await using var terminate = admin.CreateCommand("SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE application_name=$1");
            terminate.Parameters.AddWithValue(name);
            await terminate.ExecuteNonQueryAsync();
        }
        await using var restored = await PostgresSessionLease.AcquireAsync(database, key);
    }

    [PostgresFact]
    public async Task Nested_document_survives_jsonb_reordering_but_rejects_content_changes()
    {
        await using var database = NpgsqlDataSource.Create(
            Environment.GetEnvironmentVariable("COHESIVE_POSTGRES_TEST_CONNECTION_STRING")!);
        var options = new PostgresMaterializationStateStoreOptions("canonical-test",
            schema: $"canonical_{Guid.NewGuid():N}");
        var authority = new PostgresMaterializationDocumentAuthority(database, options);
        var context = OperationContext.Create();
        var original = System.Text.Json.Nodes.JsonNode.Parse(
            """{"longProperty":{"zz":1,"a":2},"sequence":[3,1,2]}""")!;
        try
        {
            await authority.EnsureCreatedAsync(context);
            async Task<System.Text.Json.Nodes.JsonNode> Read() =>
                await authority.AccessAsync(context, "test", original,
                    json => System.Text.Json.Nodes.JsonNode.Parse(json)!,
                    node => node.ToJsonString(),
                    (node, _) => Task.FromResult((node, node)));
            Assert.False(await new PostgresMaterializationStateStore(database, options).HasStoredStateAsync());
            Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(original, await Read()));
            Assert.True(await new PostgresMaterializationStateStore(database, options).HasStoredStateAsync());
            Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(original, await Read()));
            // Re-sign exactly the serialized loaded document using the legacy algorithm, then prove upgrade.
            await using (var load = database.CreateCommand($"SELECT document::text FROM {options.QualifiedTable}"))
            {
                var loaded = System.Text.Json.Nodes.JsonNode.Parse((string)(await load.ExecuteScalarAsync())!)!.ToJsonString();
                var legacy = "sha256-v1:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(loaded)));
                await using var old = database.CreateCommand($"UPDATE {options.QualifiedTable} SET document_fingerprint=$1");
                old.Parameters.AddWithValue(legacy);
                await old.ExecuteNonQueryAsync();
            }
            Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(original, await Read()));
            await using (var upgraded = database.CreateCommand($"SELECT document_fingerprint FROM {options.QualifiedTable}"))
                Assert.StartsWith("sha256-canonical-v2:", (string)(await upgraded.ExecuteScalarAsync())!);
            await using var tamper = database.CreateCommand($$"""
                UPDATE {{options.QualifiedTable}}
                SET document = jsonb_set(document, '{sequence}', '[1,3,2]'::jsonb)
                """);
            await tamper.ExecuteNonQueryAsync();
            await Assert.ThrowsAsync<InvalidDataException>(() => Read());
        }
        finally
        {
            await using var cleanup = database.CreateCommand($"DROP SCHEMA {options.QualifiedSchema} CASCADE");
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [PostgresFact]
    public async Task LocalPostgres_RetainsAllMaterializationAuthoritiesAcrossAdapterReconstruction()
    {
        var connectionString = Environment.GetEnvironmentVariable("COHESIVE_POSTGRES_TEST_CONNECTION_STRING")
            ?? throw new InvalidOperationException(
                "The PostgreSQL integration-test connection string disappeared after test discovery.");
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var schema = $"ari403_materialization_{Guid.NewGuid():N}";
        var options = new PostgresMaterializationStateStoreOptions(
            authorityId: $"authority/materialization-restart/{Guid.NewGuid():N}",
            schema: schema);
        var context = OperationContext.Create();

        try
        {
            var firstHostStore = new PostgresMaterializationStateStore(dataSource, options);
            await firstHostStore.EnsureCreatedAsync(context);
            var progress = await firstHostStore.AcquireFenceAsync(
                context,
                ProgressKey,
                mutationId: new("progress/acquire"),
                expectedRevision: null,
                owner: "worker/first-host");
            var synchronization = await ((IMaterializationSynchronizationWorkStore)firstHostStore)
                .AcquireFenceAsync(
                    context,
                    SynchronizationKey,
                    mutationId: new("synchronization/acquire"),
                    expectedRevision: null,
                    owner: "worker/first-host");
            var controlKey = ControlKey();
            var controlState = ControlState(controlKey);
            var control = await firstHostStore.CreateAsync(
                context,
                controlKey,
                mutationId: "control/create",
                mutationFingerprint: "control/create/fingerprint",
                state: controlState);

            var restartedHostStore = new PostgresMaterializationStateStore(dataSource, options);
            var restoredProgress = await restartedHostStore.LoadAsync(context, ProgressKey);
            var restoredSynchronization = await ((IMaterializationSynchronizationWorkStore)restartedHostStore)
                .LoadAsync(context, SynchronizationKey);
            var restoredControl = await restartedHostStore.ReadAsync(context, controlKey);
            var replayedProgress = await restartedHostStore.AcquireFenceAsync(
                context,
                ProgressKey,
                mutationId: new("progress/acquire"),
                expectedRevision: null,
                owner: "worker/first-host");
            var replayedControl = await restartedHostStore.CreateAsync(
                context,
                controlKey,
                mutationId: "control/create",
                mutationFingerprint: "control/create/fingerprint",
                state: controlState);

            Assert.Equal(MaterializationProgressMutationDisposition.Applied, progress.Disposition);
            Assert.Equal(progress.Snapshot, restoredProgress);
            Assert.Equal(MaterializationProgressMutationDisposition.Replayed, replayedProgress.Disposition);
            Assert.Equal(
                MaterializationSynchronizationWorkMutationDisposition.Applied,
                synchronization.Disposition);
            Assert.Equal(synchronization.Snapshot, restoredSynchronization);
            Assert.Equal(MaterializationIndexSyncControlWriteDisposition.Applied, control.Disposition);
            Assert.Equal(controlState, restoredControl);
            Assert.Equal(MaterializationIndexSyncControlWriteDisposition.Replayed, replayedControl.Disposition);
        }
        finally
        {
            await using var cleanup = dataSource.CreateCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE;");
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    static MaterializationIndexSyncControlStateKey ControlKey() => new(
        materializationId: ProgressKey.Materialization,
        definitionFingerprint: DefinitionFingerprint,
        controlDefinitionFingerprint: new(
            "sha256",
            "cohesive-control-definition/v1",
            "control-definition"),
        planFingerprint: SynchronizationKey.RebuildPlanFingerprint,
        targetId: new("elastic/freight-order-search"),
        generationId: ProgressKey.Generation,
        workload: MaterializationIndexSyncWorkloadKind.Rebuild,
        loopId: new("index-sync/target-batch"));

    static ControlLoopState ControlState(MaterializationIndexSyncControlStateKey key) => new(
        schemaVersion: ControlLoopDefinition.CurrentSchemaVersion,
        loopId: key.LoopId,
        target: key.MaterializationId.Value,
        epoch: key.Epoch,
        revision: ControlRevision.Initial,
        definitionFingerprint: key.ControlDefinitionFingerprint,
        operatingPoint: new([
            new(
                ControlActuatorKind.BatchItems,
                new(8, ControlUnit.Count))
        ]),
        healthyObservationCount: 0,
        createdAtUtc: DateTimeOffset.UnixEpoch,
        updatedAtUtc: DateTimeOffset.UnixEpoch);

    sealed class PostgresFactAttribute : FactAttribute
    {
        public PostgresFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(
                    Environment.GetEnvironmentVariable("COHESIVE_POSTGRES_TEST_CONNECTION_STRING")))
            {
                Skip = "Set COHESIVE_POSTGRES_TEST_CONNECTION_STRING or run the materialization harness.";
            }
        }
    }
}
