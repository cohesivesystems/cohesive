using System.Collections.Immutable;
using System.Text.Json;
using Cohesive.Execution;
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
        Assert.Equal(checkpoint, (await store.LoadAsync(context, Key))!.LatestChangeCheckpoint);
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
            FirstCommit,
            batchPageOrdinal: 1);
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
            checkpoint.EvidenceReference,
            batchPageOrdinal: checkpoint.BatchPageOrdinal);
        MaterializationApplicationCheckpoint changedPageOrdinal = new(
            checkpoint.Id,
            checkpoint.Kind,
            checkpoint.Continuation,
            checkpoint.Completion,
            checkpoint.Position,
            checkpoint.AppliedDeliveries,
            checkpoint.CommittedAtUtc,
            checkpoint.EvidenceReference,
            batchPageOrdinal: 2);

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
        var pageOrdinalConflict = await store.SaveCheckpointAsync(
            context,
            Key,
            new("checkpoint-mutation-1"),
            claim.Revision,
            "worker-a",
            claim.Fence,
            changedPageOrdinal);

        Assert.Equal(MaterializationProgressMutationDisposition.Applied, committed.Disposition);
        Assert.Equal(MaterializationProgressMutationDisposition.IdentityConflict, conflict.Disposition);
        Assert.Equal(
            MaterializationProgressMutationDisposition.IdentityConflict,
            pageOrdinalConflict.Disposition);
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
            FirstCommit,
            batchPageOrdinal: 1);

        var saved = await store.SaveCheckpointAsync(
            context,
            Key,
            new("checkpoint-mutation-complete"),
            claim.Revision,
            "worker-a",
            claim.Fence,
            checkpoint);

        var persisted = Assert.IsType<MaterializationProgressSnapshot>(saved.Snapshot).LatestBatchCheckpoint;
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
        Assert.Null(second.LatestBatchCheckpoint);
        Assert.Null(second.LatestChangeCheckpoint);
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
    public async Task IndividualSettlement_RequiresPositionlessDeliveryCoverageInDurableCheckpoint()
    {
        IMaterializationProgressStore store = new InMemoryMaterializationProgressStore();
        var context = OperationContext.Create();
        var claim = Assert.IsType<MaterializationProgressSnapshot>((await store.AcquireFenceAsync(
            context,
            Key,
            new("claim-individual"),
            expectedRevision: null,
            owner: "worker-a")).Snapshot);
        var channelScope = MaterializationChannelSemantics.ToChannelScopeId(Scope);
        MaterializationDeliveryId appliedDelivery = new("delivery-applied");
        ChannelProviderDeliveryId providerFloor = new("delivery-provider-floor");
        ChannelProviderDeliveryId providerPending = new("delivery-provider-pending");
        MaterializationApplicationCheckpoint checkpoint = new(
            id: new("checkpoint-individual"),
            kind: MaterializationCheckpointKind.ChangeProgress,
            continuation: null,
            completion: null,
            position: null,
            appliedDeliveries: [appliedDelivery],
            committedAtUtc: FirstCommit,
            channelProgress: new(
                replayCursor: null,
                floor: new ChannelProviderDeliveryProgressFloor(
                    scope: channelScope,
                    orderingDomain: MaterializationChannelSemantics.ToChannelOrderingDomainId(Scope),
                    delivery: providerFloor),
                pending: new ChannelStableDeliverySetProgress(
                    scope: channelScope,
                    deliveries: [providerPending])));
        var checkpointResult = await store.SaveCheckpointAsync(
            context,
            Key,
            new("checkpoint-mutation-individual"),
            claim.Revision,
            "worker-a",
            claim.Fence,
            checkpoint);
        var checkpointSnapshot = Assert.IsType<MaterializationProgressSnapshot>(checkpointResult.Snapshot);
        MaterializationSourceSettlement uncovered = new(
            id: new("settlement-individual-uncovered"),
            checkpoint: checkpoint.Id,
            scope: Scope,
            kind: ChannelSettlementKind.Individual,
            position: null,
            deliveries: [new(providerPending.Value)],
            settledAtUtc: FirstCommit.AddSeconds(1));

        var mismatch = await store.SaveSettlementAsync(
            context,
            Key,
            new("settlement-mutation-individual-uncovered"),
            checkpointSnapshot.Revision,
            "worker-a",
            checkpointSnapshot.Fence,
            uncovered);

        Assert.Equal(MaterializationProgressMutationDisposition.CheckpointMismatch, mismatch.Disposition);
        Assert.Null(Assert.IsType<MaterializationProgressSnapshot>(mismatch.Snapshot).LatestSettlement);

        MaterializationSourceSettlement covered = new(
            id: new("settlement-individual-covered"),
            checkpoint: checkpoint.Id,
            scope: Scope,
            kind: ChannelSettlementKind.Individual,
            position: null,
            deliveries: [appliedDelivery],
            settledAtUtc: FirstCommit.AddSeconds(1),
            evidenceReference: "tests/individual-ack");
        var applied = await store.SaveSettlementAsync(
            context,
            Key,
            new("settlement-mutation-individual-covered"),
            checkpointSnapshot.Revision,
            "worker-a",
            checkpointSnapshot.Fence,
            covered);
        var replayed = await store.SaveSettlementAsync(
            context,
            Key,
            new("settlement-mutation-individual-covered"),
            checkpointSnapshot.Revision,
            "worker-a",
            checkpointSnapshot.Fence,
            covered);

        Assert.Equal(MaterializationProgressMutationDisposition.Applied, applied.Disposition);
        Assert.Equal(MaterializationProgressMutationDisposition.Replayed, replayed.Disposition);
        var persisted = Assert.IsType<MaterializationSourceSettlement>(
            Assert.IsType<MaterializationProgressSnapshot>(applied.Snapshot).LatestSettlement);
        Assert.Equal(ChannelSettlementKind.Individual, persisted.Kind);
        Assert.Null(persisted.Position);
        Assert.Equal(
            appliedDelivery,
            Assert.Single(persisted.Deliveries));
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
        Assert.Equal(secondCheckpoint, settled.LatestChangeCheckpoint);
        Assert.Equal(priorSettlement, settled.LatestSettlement);
    }

    [Fact]
    public async Task BatchAndChangeProgress_RemainIndependentAndSettlementCitesChangeAudit()
    {
        IMaterializationProgressStore store = new InMemoryMaterializationProgressStore();
        var context = OperationContext.Create();
        var claim = Assert.IsType<MaterializationProgressSnapshot>((await store.AcquireFenceAsync(
            context,
            Key,
            new("claim-independent-tracks"),
            expectedRevision: null,
            owner: "worker-a")).Snapshot);
        var changeCheckpoint = ChangeCheckpoint(
            "checkpoint-change-cut",
            "position-change-cut",
            FirstCommit,
            "delivery-change-cut");
        var changeProgress = Assert.IsType<MaterializationProgressSnapshot>((await store.SaveCheckpointAsync(
            context,
            Key,
            new("checkpoint-mutation-change-cut"),
            claim.Revision,
            "worker-a",
            claim.Fence,
            changeCheckpoint)).Snapshot);
        MaterializationApplicationCheckpoint batchCheckpoint = new(
            id: new("checkpoint-batch-continuation"),
            kind: MaterializationCheckpointKind.BatchContinuation,
            continuation: new MaterializationSourceContinuation(
                formatVersion: 1,
                readFingerprint: new("sha256", "tests/read/v1", "0123456789abcdef"),
                scope: Scope,
                value: "continuation-after-change-cut"),
            completion: null,
            position: null,
            appliedDeliveries: [],
            committedAtUtc: FirstCommit.AddSeconds(1),
            batchPageOrdinal: 1);
        var batchProgress = Assert.IsType<MaterializationProgressSnapshot>((await store.SaveCheckpointAsync(
            context,
            Key,
            new("checkpoint-mutation-batch-continuation"),
            changeProgress.Revision,
            "worker-a",
            changeProgress.Fence,
            batchCheckpoint)).Snapshot);
        MaterializationSourceSettlement settlement = new(
            id: new("settlement-change-cut"),
            checkpoint: changeCheckpoint.Id,
            position: Assert.IsType<MaterializationSourcePosition>(changeCheckpoint.Position),
            settledAtUtc: FirstCommit.AddSeconds(2));

        var settled = Assert.IsType<MaterializationProgressSnapshot>((await store.SaveSettlementAsync(
            context,
            Key,
            new("settlement-mutation-change-cut"),
            batchProgress.Revision,
            "worker-a",
            batchProgress.Fence,
            settlement)).Snapshot);

        Assert.Equal(batchCheckpoint, settled.LatestBatchCheckpoint);
        Assert.Equal(changeCheckpoint, settled.LatestChangeCheckpoint);
        Assert.Equal(settlement, settled.LatestSettlement);

        var json = JsonSerializer.Serialize(settled, StrictDocumentJson.CreateOptions());
        var restored = Assert.IsType<MaterializationProgressSnapshot>(
            JsonSerializer.Deserialize<MaterializationProgressSnapshot>(
                json,
                StrictDocumentJson.CreateOptions()));

        Assert.Contains("\"latestBatchCheckpoint\":", json);
        Assert.Contains("\"latestChangeCheckpoint\":", json);
        Assert.DoesNotContain("\"latestCheckpoint\":", json);
        Assert.Equal(batchCheckpoint.Id, restored.LatestBatchCheckpoint?.Id);
        Assert.Equal(
            batchCheckpoint.Continuation?.Value,
            restored.LatestBatchCheckpoint?.Continuation?.Value);
        Assert.Equal(changeCheckpoint.Id, restored.LatestChangeCheckpoint?.Id);
        Assert.Equal(
            changeCheckpoint.ChannelProgress?.ReplayCursor?.Value,
            restored.LatestChangeCheckpoint?.ChannelProgress?.ReplayCursor?.Value);
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
        MaterializationSourcePosition foreignPosition = new(
            formatVersion: 1,
            scope: otherScope,
            value: "position-1");
        MaterializationApplicationCheckpoint foreignCheckpoint = new(
            id: new("checkpoint-foreign"),
            kind: MaterializationCheckpointKind.ChangeProgress,
            continuation: null,
            completion: null,
            position: foreignPosition,
            appliedDeliveries: [],
            committedAtUtc: FirstCommit,
            channelProgress: MaterializationChannelSemantics.CreatePositionedDurableProgress(foreignPosition));

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
            latestSettlement: settlement));
        Assert.Throws<ArgumentException>(() => new MaterializationProgressSnapshot(
            Key,
            MaterializationProgressRevision.Initial,
            MaterializationProgressFence.Initial,
            "worker-a",
            latestChangeCheckpoint: foreignCheckpoint));
        Assert.Throws<ArgumentException>(() => new MaterializationProgressSnapshot(
            Key,
            MaterializationProgressRevision.Initial,
            MaterializationProgressFence.Initial,
            "worker-a",
            latestChangeCheckpoint: checkpoint,
            latestSettlement: mismatchedSettlement));

        var valid = new MaterializationProgressSnapshot(
            Key,
            MaterializationProgressRevision.Initial,
            MaterializationProgressFence.Initial,
            "worker-a",
            latestChangeCheckpoint: checkpoint,
            latestSettlement: settlement);
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
        params string[] deliveries)
    {
        var checkpointPosition = Position(position);
        return new(
            id: new(checkpoint),
            kind: MaterializationCheckpointKind.ChangeProgress,
            continuation: null,
            completion: null,
            position: checkpointPosition,
            appliedDeliveries: [.. deliveries.Select(static delivery => new MaterializationDeliveryId(delivery))],
            committedAtUtc: committedAtUtc,
            evidenceReference: "tests/application",
            channelProgress: MaterializationChannelSemantics.CreatePositionedDurableProgress(checkpointPosition));
    }

    static MaterializationSourcePosition Position(string value) =>
        new(formatVersion: 1, Key.Scope, value);
}
