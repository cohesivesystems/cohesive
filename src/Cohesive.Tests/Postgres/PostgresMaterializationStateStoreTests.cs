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
