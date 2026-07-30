using System.Collections.Immutable;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Physical;
using Cohesive.Storage.Materialization;

namespace Cohesive.Tests.Storage;

public sealed class MaterializationProgressStoreTests
{
    static readonly DateTimeOffset FirstCommit = new(2026, 7, 30, 12, 0, 0, TimeSpan.Zero);
    static readonly QualifiedShapeId Shape = new(new("tests"), new("Item"));
    static readonly RelationQueryPhysicalPlanFingerprint PhysicalPlan =
        new("sha256", "tests/physical-plan/v1", "0123456789abcdef");
    static readonly RelationQuerySourcePlacementBinding Placement = new(
        new("placement/source"),
        new("source/items"),
        new("node/source"),
        new("binding/source"),
        Shape,
        new("tests/source"),
        RelationQuerySourcePlacementBindingKind.SourceSet,
        RelationQuerySourceAcquisitionKind.BoundedEnumeration,
        RelationQuerySourcePlacementOrigin.Explicit,
        new RelationQuerySourceIdentityBinding(Shape, "id"));
    static readonly MaterializationSourceScope Scope = new(
        PhysicalPlan,
        Placement,
        new MaterializationSourcePartitionId("tenant-a"),
        new MaterializationOrderingScopeId("tenant-a/feed-0"));
    static readonly MaterializationProgressKey Key = new(
        new("tests/materialization"),
        new("sha256", "execution-definition/v1", "0123456789abcdef"),
        new("generation-1"),
        Scope);

    [Fact]
    public void ProgressKey_RejectsDefaultValueIdentitiesAsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => new MaterializationProgressKey(
            default,
            Key.DefinitionFingerprint,
            Key.Generation,
            Scope));
        Assert.Throws<ArgumentException>(() => new MaterializationProgressKey(
            Key.Materialization,
            Key.DefinitionFingerprint,
            default,
            Scope));
    }

    [Fact]
    public async Task Mutations_ReplayExactIntentAndRejectIdentityReuse()
    {
        IMaterializationProgressStore store = new InMemoryMaterializationProgressStore();
        var context = OperationContext.Create();

        var claim = await store.AcquireFenceAsync(
            context,
            Key,
            new("claim-1"),
            expectedRevision: null,
            owner: "worker-a");
        var claimReplay = await store.AcquireFenceAsync(
            context,
            Key,
            new("claim-1"),
            expectedRevision: null,
            owner: "worker-a");

        Assert.Equal(MaterializationProgressMutationDisposition.Applied, claim.Disposition);
        Assert.Equal(MaterializationProgressMutationDisposition.Replayed, claimReplay.Disposition);
        var claimed = Assert.IsType<MaterializationProgressSnapshot>(claim.Snapshot);
        Assert.Equal(MaterializationProgressRevision.Initial, claimed.Revision);
        Assert.Equal(MaterializationProgressFence.Initial, claimed.Fence);

        var checkpoint = ChangeCheckpoint("checkpoint-1", "position-1", FirstCommit, "delivery-1");
        var committed = await store.SaveCheckpointAsync(
            context,
            Key,
            new("checkpoint-mutation-1"),
            claimed.Revision,
            "worker-a",
            claimed.Fence,
            checkpoint);
        var replayed = await store.SaveCheckpointAsync(
            context,
            Key,
            new("checkpoint-mutation-1"),
            claimed.Revision,
            "worker-a",
            claimed.Fence,
            checkpoint);
        var conflict = await store.SaveCheckpointAsync(
            context,
            Key,
            new("checkpoint-mutation-1"),
            claimed.Revision,
            "worker-a",
            claimed.Fence,
            ChangeCheckpoint("checkpoint-2", "position-2", FirstCommit.AddSeconds(1), "delivery-2"));

        Assert.Equal(MaterializationProgressMutationDisposition.Applied, committed.Disposition);
        Assert.Equal(MaterializationProgressMutationDisposition.Replayed, replayed.Disposition);
        Assert.Equal(MaterializationProgressMutationDisposition.IdentityConflict, conflict.Disposition);
        var diagnostic = Assert.Single(conflict.Diagnostics);
        Assert.Equal(MaterializationProgressDiagnosticCodes.IdentityConflict, diagnostic.Code);
        Assert.Equal("/progress", diagnostic.Location);
        Assert.Equal("materialization-progress-store", diagnostic.Evidence?.Stage);
        Assert.False(diagnostic.Evidence?.SourceReferences.IsDefaultOrEmpty);
        Assert.False(string.IsNullOrWhiteSpace(diagnostic.Evidence?.Expected));
        Assert.False(string.IsNullOrWhiteSpace(diagnostic.Evidence?.Observed));

        var committedSnapshot = Assert.IsType<MaterializationProgressSnapshot>(committed.Snapshot);
        var checkpointIdentityConflict = await store.SaveCheckpointAsync(
            context,
            Key,
            new("checkpoint-mutation-2"),
            committedSnapshot.Revision,
            "worker-a",
            committedSnapshot.Fence,
            ChangeCheckpoint("checkpoint-1", "position-2", FirstCommit.AddSeconds(1), "delivery-2"));

        Assert.Equal(
            MaterializationProgressMutationDisposition.IdentityConflict,
            checkpointIdentityConflict.Disposition);
        Assert.Equal(checkpoint, (await store.LoadAsync(context, Key))!.LatestCheckpoint);
    }

    [Fact]
    public async Task CheckpointMutationIntent_CoversContinuationReadFingerprint()
    {
        IMaterializationProgressStore store = new InMemoryMaterializationProgressStore();
        var context = OperationContext.Create();
        var claim = Assert.IsType<MaterializationProgressSnapshot>((await store.AcquireFenceAsync(
            context,
            Key,
            new("claim-1"),
            expectedRevision: null,
            owner: "worker-a")).Snapshot);
        MaterializationApplicationCheckpoint checkpoint = new(
            new("checkpoint-1"),
            MaterializationCheckpointKind.BatchContinuation,
            new MaterializationSourceContinuation(
                formatVersion: 1,
                new MaterializationSourceReadFingerprint("sha256", "tests/read/v1", "a"),
                Scope,
                "continuation-1"),
            completion: null,
            position: null,
            [],
            FirstCommit);
        MaterializationApplicationCheckpoint changedFingerprint = new(
            checkpoint.Id,
            checkpoint.Kind,
            new MaterializationSourceContinuation(
                formatVersion: 1,
                new MaterializationSourceReadFingerprint("sha256", "tests/read/v1", "b"),
                Scope,
                "continuation-1"),
            completion: null,
            checkpoint.Position,
            checkpoint.AppliedDeliveries,
            checkpoint.CommittedAtUtc,
            checkpoint.EvidenceReference);

        var committed = await store.SaveCheckpointAsync(
            context,
            Key,
            new("checkpoint-mutation-1"),
            claim.Revision,
            "worker-a",
            claim.Fence,
            checkpoint);
        var conflict = await store.SaveCheckpointAsync(
            context,
            Key,
            new("checkpoint-mutation-1"),
            claim.Revision,
            "worker-a",
            claim.Fence,
            changedFingerprint);

        Assert.Equal(MaterializationProgressMutationDisposition.Applied, committed.Disposition);
        Assert.Equal(MaterializationProgressMutationDisposition.IdentityConflict, conflict.Disposition);
    }

    [Fact]
    public async Task BatchCompletion_PersistsExactScopeAndReadEvidence()
    {
        IMaterializationProgressStore store = new InMemoryMaterializationProgressStore();
        var context = OperationContext.Create();
        var claim = Assert.IsType<MaterializationProgressSnapshot>((await store.AcquireFenceAsync(
            context,
            Key,
            new("claim-1"),
            expectedRevision: null,
            owner: "worker-a")).Snapshot);
        MaterializationSourceReadCompletion completion = new(
            Scope,
            new("sha256", "tests/read/v1", "a"),
            Cohesive.Relations.Acquisition.RelationQuerySourceReadState.Complete,
            "tests/source-complete");
        MaterializationApplicationCheckpoint checkpoint = new(
            new("checkpoint-complete"),
            MaterializationCheckpointKind.BatchCompleted,
            continuation: null,
            completion,
            position: null,
            appliedDeliveries: [],
            FirstCommit);

        var saved = await store.SaveCheckpointAsync(
            context,
            Key,
            new("checkpoint-mutation-complete"),
            claim.Revision,
            "worker-a",
            claim.Fence,
            checkpoint);

        var persisted = Assert.IsType<MaterializationProgressSnapshot>(saved.Snapshot).LatestCheckpoint;
        Assert.Equal(completion, persisted?.Completion);
        Assert.Equal(Scope.Input, persisted?.Completion?.Scope.Input);
    }

    [Fact]
    public async Task SupersedingFence_RejectsPriorWorkerBeforeRevisionComparison()
    {
        IMaterializationProgressStore store = new InMemoryMaterializationProgressStore();
        var context = OperationContext.Create();
        var firstClaim = await store.AcquireFenceAsync(
            context,
            Key,
            new("claim-1"),
            expectedRevision: null,
            owner: "worker-a");
        var first = Assert.IsType<MaterializationProgressSnapshot>(firstClaim.Snapshot);
        var secondClaim = await store.AcquireFenceAsync(
            context,
            Key,
            new("claim-2"),
            first.Revision,
            owner: "worker-b");
        var second = Assert.IsType<MaterializationProgressSnapshot>(secondClaim.Snapshot);

        var stale = await store.SaveCheckpointAsync(
            context,
            Key,
            new("checkpoint-stale"),
            second.Revision,
            "worker-a",
            first.Fence,
            ChangeCheckpoint("checkpoint-1", "position-1", FirstCommit, "delivery-1"));

        Assert.Equal(MaterializationProgressMutationDisposition.StaleFence, stale.Disposition);
        Assert.Equal(MaterializationProgressDiagnosticCodes.StaleFence, Assert.Single(stale.Diagnostics).Code);
        Assert.Null(second.LatestCheckpoint);
    }

    [Fact]
    public async Task Settlement_RequiresAnAlreadyPersistedMatchingCheckpoint()
    {
        IMaterializationProgressStore store = new InMemoryMaterializationProgressStore();
        var context = OperationContext.Create();
        var claim = Assert.IsType<MaterializationProgressSnapshot>((await store.AcquireFenceAsync(
            context,
            Key,
            new("claim-1"),
            expectedRevision: null,
            owner: "worker-a")).Snapshot);
        var position = Position("position-1");
        MaterializationSourceSettlement settlement = new(
            new("settlement-1"),
            new("checkpoint-1"),
            position,
            FirstCommit.AddSeconds(1),
            "tests/ack");

        var missing = await store.SaveSettlementAsync(
            context,
            Key,
            new("settlement-mutation-missing"),
            claim.Revision,
            "worker-a",
            claim.Fence,
            settlement);

        Assert.Equal(MaterializationProgressMutationDisposition.CheckpointNotFound, missing.Disposition);
        Assert.Null(Assert.IsType<MaterializationProgressSnapshot>(missing.Snapshot).LatestSettlement);

        var checkpointResult = await store.SaveCheckpointAsync(
            context,
            Key,
            new("checkpoint-mutation-1"),
            claim.Revision,
            "worker-a",
            claim.Fence,
            ChangeCheckpoint("checkpoint-1", "position-1", FirstCommit, "delivery-1"));
        var checkpointSnapshot = Assert.IsType<MaterializationProgressSnapshot>(checkpointResult.Snapshot);
        MaterializationSourceSettlement wrongPosition = new(
            new("settlement-wrong"),
            new("checkpoint-1"),
            Position("position-2"),
            FirstCommit.AddSeconds(1));
        var mismatch = await store.SaveSettlementAsync(
            context,
            Key,
            new("settlement-mutation-wrong"),
            checkpointSnapshot.Revision,
            "worker-a",
            checkpointSnapshot.Fence,
            wrongPosition);

        Assert.Equal(MaterializationProgressMutationDisposition.CheckpointMismatch, mismatch.Disposition);

        var applied = await store.SaveSettlementAsync(
            context,
            Key,
            new("settlement-mutation-1"),
            checkpointSnapshot.Revision,
            "worker-a",
            checkpointSnapshot.Fence,
            settlement);
        var replay = await store.SaveSettlementAsync(
            context,
            Key,
            new("settlement-mutation-1"),
            checkpointSnapshot.Revision,
            "worker-a",
            checkpointSnapshot.Fence,
            settlement);

        Assert.Equal(MaterializationProgressMutationDisposition.Applied, applied.Disposition);
        Assert.Equal(MaterializationProgressMutationDisposition.Replayed, replay.Disposition);
        var final = Assert.IsType<MaterializationProgressSnapshot>(applied.Snapshot);
        var finalSettlement = Assert.IsType<MaterializationSourceSettlement>(final.LatestSettlement);
        Assert.Equal(position, finalSettlement.Position);
        Assert.Equal("checkpoint-1", finalSettlement.Checkpoint.Value);
    }

    [Fact]
    public async Task Snapshot_ExposesLatestProgressWhileFakeRetainsInternalAuditEvidence()
    {
        IMaterializationProgressStore store = new InMemoryMaterializationProgressStore();
        var context = OperationContext.Create();
        var claim = Assert.IsType<MaterializationProgressSnapshot>((await store.AcquireFenceAsync(
            context,
            Key,
            new("claim-1"),
            expectedRevision: null,
            owner: "worker-a")).Snapshot);
        var firstCheckpoint = ChangeCheckpoint("checkpoint-1", "position-1", FirstCommit, "delivery-1");
        var first = Assert.IsType<MaterializationProgressSnapshot>((await store.SaveCheckpointAsync(
            context,
            Key,
            new("checkpoint-mutation-1"),
            claim.Revision,
            "worker-a",
            claim.Fence,
            firstCheckpoint)).Snapshot);
        var secondCheckpoint = ChangeCheckpoint(
            "checkpoint-2",
            "position-2",
            FirstCommit.AddSeconds(1),
            "delivery-2");
        var second = Assert.IsType<MaterializationProgressSnapshot>((await store.SaveCheckpointAsync(
            context,
            Key,
            new("checkpoint-mutation-2"),
            first.Revision,
            "worker-a",
            first.Fence,
            secondCheckpoint)).Snapshot);

        var priorIdentityReplay = await store.SaveCheckpointAsync(
            context,
            Key,
            new("checkpoint-mutation-3"),
            second.Revision,
            "worker-a",
            second.Fence,
            firstCheckpoint);
        MaterializationSourceSettlement priorSettlement = new(
            new("settlement-1"),
            firstCheckpoint.Id,
            Assert.IsType<MaterializationSourcePosition>(firstCheckpoint.Position),
            FirstCommit.AddSeconds(2));
        var settled = Assert.IsType<MaterializationProgressSnapshot>((await store.SaveSettlementAsync(
            context,
            Key,
            new("settlement-mutation-1"),
            second.Revision,
            "worker-a",
            second.Fence,
            priorSettlement)).Snapshot);

        Assert.Equal(MaterializationProgressMutationDisposition.Replayed, priorIdentityReplay.Disposition);
        Assert.Equal(secondCheckpoint, settled.LatestCheckpoint);
        Assert.Equal(priorSettlement, settled.LatestSettlement);
    }

    [Fact]
    public void Snapshot_RejectsContradictoryLatestState()
    {
        var checkpoint = ChangeCheckpoint("checkpoint-1", "position-1", FirstCommit, "delivery-1");
        MaterializationSourceSettlement settlement = new(
            new("settlement-1"),
            checkpoint.Id,
            Position("position-1"),
            FirstCommit.AddSeconds(1));
        MaterializationSourceSettlement mismatchedSettlement = new(
            new("settlement-2"),
            checkpoint.Id,
            Position("position-2"),
            FirstCommit.AddSeconds(1));
        MaterializationSourceScope otherScope = new(
            PhysicalPlan,
            Placement,
            new("tenant-b"),
            new("tenant-b/feed-0"));
        MaterializationApplicationCheckpoint foreignCheckpoint = new(
            new("checkpoint-foreign"),
            MaterializationCheckpointKind.ChangePosition,
            continuation: null,
            completion: null,
            new MaterializationSourcePosition(formatVersion: 1, otherScope, "position-1"),
            [],
            FirstCommit);

        Assert.Throws<ArgumentException>(() => new MaterializationProgressSnapshot(
            Key,
            default,
            MaterializationProgressFence.Initial,
            "worker-a"));
        Assert.Throws<ArgumentException>(() => new MaterializationProgressSnapshot(
            Key,
            MaterializationProgressRevision.Initial,
            MaterializationProgressFence.Initial,
            "worker-a",
            latestCheckpoint: null,
            settlement));
        Assert.Throws<ArgumentException>(() => new MaterializationProgressSnapshot(
            Key,
            MaterializationProgressRevision.Initial,
            MaterializationProgressFence.Initial,
            "worker-a",
            foreignCheckpoint));
        Assert.Throws<ArgumentException>(() => new MaterializationProgressSnapshot(
            Key,
            MaterializationProgressRevision.Initial,
            MaterializationProgressFence.Initial,
            "worker-a",
            checkpoint,
            mismatchedSettlement));

        var valid = new MaterializationProgressSnapshot(
            Key,
            MaterializationProgressRevision.Initial,
            MaterializationProgressFence.Initial,
            "worker-a",
            checkpoint,
            settlement);
        Assert.Equal(settlement, valid.LatestSettlement);
    }

    [Fact]
    public void MutationResult_RejectsDispositionPayloadContradictions()
    {
        MaterializationProgressSnapshot snapshot = new(
            Key,
            MaterializationProgressRevision.Initial,
            MaterializationProgressFence.Initial,
            "worker-a");
        ImmutableArray<DocumentValidationDiagnostic> diagnostics =
        [
            new(
                MaterializationProgressDiagnosticCodes.RevisionConflict,
                DiagnosticSeverity.Error,
                "The expected progress revision is stale.",
                "/progress",
                Evidence: new(
                    stage: "materialization-progress-store",
                    subject: "tests/progress",
                    sourceReferences: [Key.DefinitionFingerprint.Value],
                    expected: "current revision",
                    observed: "stale revision"))
        ];

        Assert.Throws<ArgumentException>(() => new MaterializationProgressMutationResult(
            MaterializationProgressMutationDisposition.Applied,
            snapshot: null));
        Assert.Throws<ArgumentException>(() => new MaterializationProgressMutationResult(
            MaterializationProgressMutationDisposition.Applied,
            snapshot,
            diagnostics));
        Assert.Throws<ArgumentException>(() => new MaterializationProgressMutationResult(
            MaterializationProgressMutationDisposition.NotFound,
            snapshot,
            diagnostics));
        Assert.Throws<ArgumentException>(() => new MaterializationProgressMutationResult(
            MaterializationProgressMutationDisposition.NotFound,
            snapshot: null));
        Assert.Throws<ArgumentException>(() => new MaterializationProgressMutationResult(
            MaterializationProgressMutationDisposition.RevisionConflict,
            snapshot: null,
            diagnostics));
        Assert.Throws<ArgumentException>(() => new MaterializationProgressMutationResult(
            MaterializationProgressMutationDisposition.RevisionConflict,
            snapshot));
        Assert.Throws<ArgumentException>(() => new MaterializationProgressMutationResult(
            MaterializationProgressMutationDisposition.RevisionConflict,
            snapshot,
            [new("incomplete", DiagnosticSeverity.Error, "missing normative evidence", "/progress")]));

        var applied = new MaterializationProgressMutationResult(
            MaterializationProgressMutationDisposition.Applied,
            snapshot);
        var rejected = new MaterializationProgressMutationResult(
            MaterializationProgressMutationDisposition.RevisionConflict,
            snapshot,
            diagnostics);
        Assert.Empty(applied.Diagnostics);
        Assert.Equal(diagnostics, rejected.Diagnostics);
    }

    [Fact]
    public async Task DefinitionFingerprint_IsPartOfTheDurableProgressIdentity()
    {
        IMaterializationProgressStore store = new InMemoryMaterializationProgressStore();
        var context = OperationContext.Create();
        await store.AcquireFenceAsync(
            context,
            Key,
            new("claim-1"),
            expectedRevision: null,
            owner: "worker-a");
        MaterializationProgressKey changedDefinition = new(
            Key.Materialization,
            new("sha256", "execution-definition/v1", "fedcba9876543210"),
            Key.Generation,
            Key.Scope);

        Assert.Null(await store.LoadAsync(context, changedDefinition));
        Assert.NotNull(await store.LoadAsync(context, Key));
    }

    static MaterializationApplicationCheckpoint ChangeCheckpoint(
        string checkpoint,
        string position,
        DateTimeOffset committedAtUtc,
        params string[] deliveries) => new(
        new(checkpoint),
        MaterializationCheckpointKind.ChangePosition,
        continuation: null,
        completion: null,
        Position(position),
        [.. deliveries.Select(static delivery => new MaterializationDeliveryId(delivery))],
        committedAtUtc,
        "tests/application");

    static MaterializationSourcePosition Position(string value) =>
        new(formatVersion: 1, Key.Scope, value);
}
