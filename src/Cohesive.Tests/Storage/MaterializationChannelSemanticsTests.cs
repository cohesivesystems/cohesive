using System.Text.Json;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Acquisition;
using Cohesive.Relations.Physical;
using Cohesive.Storage.Materialization;

namespace Cohesive.Tests.Storage;

public sealed class MaterializationChannelSemanticsTests
{
    static readonly DateTimeOffset ObservedAtUtc =
        new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
    static readonly QualifiedShapeId Shape = new(new("tests/channel"), new("Item"));
    static readonly RelationQuerySourceInstanceId Source = new("tests/channel/source");
    static readonly RelationQueryPhysicalPlanFingerprint PhysicalPlan =
        new("sha256", "tests/channel/canonicalization/v1", "0123456789abcdef");
    static readonly RelationQuerySourcePlacementBinding Placement = new(
        id: new("placement/source"),
        input: new("source/items"),
        node: new("node/source"),
        binding: new("binding/source"),
        shape: Shape,
        source: Source,
        kind: RelationQuerySourcePlacementBindingKind.SourceSet,
        acquisition: RelationQuerySourceAcquisitionKind.BoundedEnumeration,
        origin: RelationQuerySourcePlacementOrigin.Explicit,
        identity: new RelationQuerySourceIdentityBinding(Shape, "id"));
    static readonly MaterializationSourceScope Scope = new(
        physicalPlan: PhysicalPlan,
        placement: Placement,
        partition: new("tenant-a"),
        orderingScope: new("tenant-a/feed-0"));

    [Fact]
    public void SourceScope_ProjectsToExactDeterministicChannelIdentity()
    {
        MaterializationSourceScope restored = new(
            physicalPlan: new(
                PhysicalPlan.Algorithm,
                PhysicalPlan.Canonicalization,
                PhysicalPlan.Value),
            placement: Placement,
            partition: Scope.Partition,
            orderingScope: Scope.OrderingScope);
        MaterializationSourceScope otherPartition = new(
            physicalPlan: PhysicalPlan,
            placement: Placement,
            partition: new("tenant-b"),
            orderingScope: new("tenant-b/feed-0"));
        RelationQuerySourcePlacementBinding otherPlacement = new(
            id: Placement.Id,
            input: Placement.Input,
            node: new("node/source/other"),
            binding: Placement.Binding,
            shape: Placement.Shape,
            source: Placement.Source,
            kind: Placement.Kind,
            acquisition: Placement.Acquisition,
            origin: Placement.Origin,
            identity: Placement.Identity,
            fields: Placement.Fields,
            relationshipKeys: Placement.RelationshipKeys,
            partition: Placement.Partition);
        MaterializationSourceScope otherPlacementScope = new(
            physicalPlan: PhysicalPlan,
            placement: otherPlacement,
            partition: Scope.Partition,
            orderingScope: Scope.OrderingScope);

        var projected = MaterializationChannelSemantics.ToChannelScopeId(Scope);

        Assert.Equal(
            "materialization-channel-scope:v1:sha256:"
            + "3d39366c7c6c29a1c7685df72fda16a8e7f1f1d466f5b52e879841d2f614e55f",
            projected.Value);
        Assert.Equal(projected, MaterializationChannelSemantics.ToChannelScopeId(restored));
        Assert.NotEqual(projected, MaterializationChannelSemantics.ToChannelScopeId(otherPartition));
        Assert.NotEqual(projected, MaterializationChannelSemantics.ToChannelScopeId(otherPlacementScope));
        Assert.Equal(
            Scope.OrderingScope.Value,
            MaterializationChannelSemantics.ToChannelOrderingDomainId(Scope).Value);
    }

    [Fact]
    public void EmptyProgressedPage_AdvancesReplayCursorWithoutInventingDelivery()
    {
        MaterializationSourcePosition before = new(
            formatVersion: 1,
            scope: Scope,
            value: "position/10");
        MaterializationChangePage progressed = new(
            deliveries: [],
            throughPosition: new(
                formatVersion: 1,
                scope: Scope,
                value: "position/20"),
            state: MaterializationChangePageState.Progressed);

        var beforeCursor = MaterializationChannelSemantics.ToChannelReplayCursor(before);
        var throughCursor = MaterializationChannelSemantics.ToChannelReplayCursor(progressed.ThroughPosition);

        Assert.Empty(progressed.Deliveries);
        Assert.Equal(MaterializationChangePageState.Progressed, progressed.State);
        Assert.Equal(beforeCursor.Scope, throughCursor.Scope);
        Assert.Equal(beforeCursor.OrderingDomain, throughCursor.OrderingDomain);
        Assert.Equal("position/10", beforeCursor.Value);
        Assert.Equal("position/20", throughCursor.Value);
        Assert.NotEqual(beforeCursor, throughCursor);
    }

    [Fact]
    public void Redelivery_PreservesLogicalAndProviderIdentityAcrossDistinctPhysicalAttempts()
    {
        var delivery = DeleteDelivery(
            deliveryId: "delivery/stable",
            changeId: "change/stable",
            positionValue: "position/20");
        ChannelDeliveryAttemptId firstAttempt = new("attempt/1");
        ChannelDeliveryAttemptId secondAttempt = new("attempt/2");
        ChannelSettlementCouplingId callbackCoupling = new("cosmos/callback/range/shared");
        ChannelSettlementAuthority firstAuthority = new(
            id: new("authority/1"),
            attempt: firstAttempt,
            coupling: callbackCoupling,
            expiresAtUtc: ObservedAtUtc.AddMinutes(1));

        var first = MaterializationChannelSemantics.ToChannelDeliveryAttemptEvidence(
            delivery: delivery,
            attempt: firstAttempt,
            settlementAuthority: firstAuthority);
        var second = MaterializationChannelSemantics.ToChannelDeliveryAttemptEvidence(
            delivery: delivery,
            attempt: secondAttempt);

        Assert.Equal(new MaterializationChangeId("change/stable"), delivery.Change.Id);
        Assert.Equal(new MaterializationDeliveryId("delivery/stable"), delivery.Id);
        Assert.NotEqual(first.Attempt, second.Attempt);
        Assert.Equal(new ChannelProviderDeliveryId("delivery/stable"), first.ProviderDelivery);
        Assert.Equal(first.ProviderDelivery, second.ProviderDelivery);
        Assert.Equal(first.ReplayCursor, second.ReplayCursor);
        Assert.Equal(Scope.OrderingScope.Value, first.ReplayCursor!.OrderingDomain.Value);
        Assert.Equal(callbackCoupling, first.SettlementAuthority!.Coupling);
        Assert.NotEqual(
            first.ReplayCursor.OrderingDomain.Value,
            first.SettlementAuthority.Coupling.Value);
    }

    [Fact]
    public void PositionlessLeasedDelivery_ProjectsAttemptWithoutInventingReplayCursor()
    {
        var delivery = DeleteDelivery(
            deliveryId: "delivery/leased",
            changeId: "change/leased",
            positionValue: null);
        ChannelDeliveryAttemptId attempt = new("attempt/leased/1");
        ChannelSettlementAuthority authority = new(
            id: new("authority/leased/1"),
            attempt: attempt,
            coupling: MaterializationChannelSemantics.ToChannelSettlementCouplingId(Scope),
            expiresAtUtc: ObservedAtUtc.AddMinutes(1));

        var projected = MaterializationChannelSemantics.ToChannelDeliveryAttemptEvidence(
            delivery: delivery,
            attempt: attempt,
            settlementAuthority: authority);

        Assert.Null(delivery.Change.Position);
        Assert.Equal(MaterializationChannelSemantics.ToChannelScopeId(Scope), projected.Scope);
        Assert.Equal(new ChannelProviderDeliveryId("delivery/leased"), projected.ProviderDelivery);
        Assert.Null(projected.ReplayCursor);
        Assert.Equal(authority, projected.SettlementAuthority);
        Assert.Throws<ArgumentException>(() => new MaterializationChangePage(
            deliveries: [delivery],
            throughPosition: Position("position/pull-page"),
            state: MaterializationChangePageState.CaughtUp));
    }

    [Fact]
    public void PositionedCheckpoint_ProjectsCursorFloorWithoutInventingProviderPendingState()
    {
        var position = Position("position/30");
        MaterializationApplicationCheckpoint checkpoint = new(
            id: new("checkpoint/change/1"),
            kind: MaterializationCheckpointKind.ChangeProgress,
            continuation: null,
            completion: null,
            position: position,
            appliedDeliveries:
            [
                new MaterializationDeliveryId("delivery/2"),
                new MaterializationDeliveryId("delivery/1")
            ],
            committedAtUtc: ObservedAtUtc,
            evidenceReference: "tests/application-progress",
            channelProgress: MaterializationChannelSemantics.CreatePositionedDurableProgress(position));

        var projected = MaterializationChannelSemantics.ToChannelDurableProgress(checkpoint);
        var cursorFloor = Assert.IsType<ChannelReplayCursorProgressFloor>(projected.Floor);

        Assert.Equal(MaterializationChannelSemantics.ToChannelReplayCursor(position), projected.ReplayCursor);
        Assert.Equal(projected.ReplayCursor, cursorFloor.Cursor);
        Assert.Null(projected.Pending);
        Assert.Equal(
            ["delivery/1", "delivery/2"],
            checkpoint.AppliedDeliveries.Select(static delivery => delivery.Value));
    }

    [Fact]
    public void PositionlessCheckpoint_ProjectsProviderFloorAndIndividualSettlement()
    {
        var channelScope = MaterializationChannelSemantics.ToChannelScopeId(Scope);
        var orderingDomain = MaterializationChannelSemantics.ToChannelOrderingDomainId(Scope);
        ChannelProviderDeliveryId providerFloor = new("delivery/leased/floor");
        ChannelProviderDeliveryId providerPending = new("delivery/leased/provider-pending");
        MaterializationDeliveryId appliedDelivery = new("delivery/leased/applied");
        ChannelDurableProgressEvidence progress = new(
            replayCursor: null,
            floor: new ChannelProviderDeliveryProgressFloor(
                scope: channelScope,
                orderingDomain: orderingDomain,
                delivery: providerFloor),
            pending: new ChannelStableDeliverySetProgress(
                scope: channelScope,
                deliveries: [providerPending]));
        MaterializationApplicationCheckpoint checkpoint = new(
            id: new("checkpoint/leased/1"),
            kind: MaterializationCheckpointKind.ChangeProgress,
            continuation: null,
            completion: null,
            position: null,
            appliedDeliveries: [appliedDelivery],
            committedAtUtc: ObservedAtUtc,
            evidenceReference: "tests/leased-progress",
            channelProgress: progress);
        MaterializationSourceSettlement settlement = new(
            id: new("settlement/leased/1"),
            checkpoint: checkpoint.Id,
            scope: Scope,
            kind: ChannelSettlementKind.Individual,
            position: null,
            deliveries: [appliedDelivery],
            settledAtUtc: ObservedAtUtc.AddSeconds(1),
            evidenceReference: "tests/leased-settlement");

        var snapshot = new MaterializationProgressSnapshot(
            key: ProgressKey(),
            revision: MaterializationProgressRevision.Initial,
            fence: MaterializationProgressFence.Initial,
            fenceOwner: "worker/leased",
            latestCheckpoint: checkpoint,
            latestSettlement: settlement);
        MaterializationChangeSettlementObservation observation = new(
            progress: snapshot,
            settlement: settlement);
        var projectedProgress = MaterializationChannelSemantics.ToChannelDurableProgress(checkpoint);
        var receipt = MaterializationChannelSemantics.ToChannelSettlementReceipt(observation);

        Assert.Same(progress, projectedProgress);
        Assert.Null(checkpoint.Position);
        Assert.Equal(appliedDelivery, Assert.Single(checkpoint.AppliedDeliveries));
        Assert.Equal(
            providerPending,
            Assert.Single(Assert.IsType<ChannelStableDeliverySetProgress>(projectedProgress.Pending).Deliveries));
        Assert.Equal(ChannelSettlementKind.Individual, receipt.Kind);
        Assert.Equal(ChannelSettlementCouplingKind.PerDelivery, receipt.CouplingKind);
        Assert.Null(receipt.ThroughCursor);
        Assert.Equal(
            new ChannelProviderDeliveryId(appliedDelivery.Value),
            Assert.Single(receipt.Deliveries));
        Assert.Equal(channelScope, receipt.ApplicationProgress.Scope);
        Assert.Equal(checkpoint.Id.Value, receipt.ApplicationProgress.Value);
        Assert.Equal(settlement, snapshot.LatestSettlement);

        MaterializationSourceSettlement uncovered = new(
            id: new("settlement/leased/uncovered"),
            checkpoint: checkpoint.Id,
            scope: Scope,
            kind: ChannelSettlementKind.Individual,
            position: null,
            deliveries: [new("delivery/leased/other")],
            settledAtUtc: ObservedAtUtc.AddSeconds(1));
        Assert.Throws<ArgumentException>(() => new MaterializationProgressSnapshot(
            key: ProgressKey(),
            revision: MaterializationProgressRevision.Initial,
            fence: MaterializationProgressFence.Initial,
            fenceOwner: "worker/leased",
            latestCheckpoint: checkpoint,
            latestSettlement: uncovered));
    }

    [Fact]
    public void HybridCheckpoint_PreservesReplayProviderFloorAndUnresolvedGaps()
    {
        var position = Position("position/hybrid/40");
        var cursor = MaterializationChannelSemantics.ToChannelReplayCursor(position);
        ChannelDurableProgressEvidence progress = new(
            replayCursor: cursor,
            floor: new ChannelProviderDeliveryProgressFloor(
                scope: cursor.Scope,
                orderingDomain: cursor.OrderingDomain,
                delivery: new("delivery/hybrid/floor")),
            pending: new ChannelUnresolvedGapProgress(
                scope: cursor.Scope,
                deliveries:
                [
                    new ChannelProviderDeliveryId("delivery/hybrid/gap-2"),
                    new ChannelProviderDeliveryId("delivery/hybrid/gap-1")
                ]));
        MaterializationApplicationCheckpoint checkpoint = new(
            id: new("checkpoint/hybrid/1"),
            kind: MaterializationCheckpointKind.ChangeProgress,
            continuation: null,
            completion: null,
            position: position,
            appliedDeliveries: [],
            committedAtUtc: ObservedAtUtc,
            channelProgress: progress);

        var projected = MaterializationChannelSemantics.ToChannelDurableProgress(checkpoint);
        var floor = Assert.IsType<ChannelProviderDeliveryProgressFloor>(projected.Floor);
        var gaps = Assert.IsType<ChannelUnresolvedGapProgress>(projected.Pending);

        Assert.Same(progress, projected);
        Assert.Equal(cursor, projected.ReplayCursor);
        Assert.Equal("delivery/hybrid/floor", floor.Delivery.Value);
        Assert.Equal(
            ["delivery/hybrid/gap-1", "delivery/hybrid/gap-2"],
            gaps.Deliveries.Select(static item => item.Value));
        Assert.Empty(checkpoint.AppliedDeliveries);
        Assert.True(checkpoint.CoversReplayPosition(position));
    }

    [Fact]
    public void PositionlessCheckpoint_PreservesTargetManagedFloorAndPendingSnapshot()
    {
        var channelScope = MaterializationChannelSemantics.ToChannelScopeId(Scope);
        ChannelDurableProgressEvidence progress = new(
            replayCursor: null,
            floor: new ChannelTargetManagedProgressFloor(
                formatVersion: 2,
                scope: channelScope,
                value: "provider/floor/snapshot"),
            pending: new ChannelTargetManagedPendingProgress(
                formatVersion: 3,
                scope: channelScope,
                value: "provider/pending/snapshot"));
        MaterializationApplicationCheckpoint checkpoint = new(
            id: new("checkpoint/target-managed/1"),
            kind: MaterializationCheckpointKind.ChangeProgress,
            continuation: null,
            completion: null,
            position: null,
            appliedDeliveries: [],
            committedAtUtc: ObservedAtUtc,
            channelProgress: progress);

        var projected = MaterializationChannelSemantics.ToChannelDurableProgress(checkpoint);

        Assert.Same(progress, projected);
        Assert.Equal(
            "provider/floor/snapshot",
            Assert.IsType<ChannelTargetManagedProgressFloor>(projected.Floor).Value);
        Assert.Equal(
            "provider/pending/snapshot",
            Assert.IsType<ChannelTargetManagedPendingProgress>(projected.Pending).Value);
        Assert.Empty(checkpoint.AppliedDeliveries);
        var snapshot = new MaterializationProgressSnapshot(
            key: ProgressKey(),
            revision: MaterializationProgressRevision.Initial,
            fence: MaterializationProgressFence.Initial,
            fenceOwner: "worker/target-managed",
            latestCheckpoint: checkpoint);
        Assert.Same(checkpoint, snapshot.LatestCheckpoint);
    }

    [Fact]
    public void ChangeProgressCheckpoint_RoundTripsCompleteChannelAuthorityWithStrictKindEncoding()
    {
        var channelScope = MaterializationChannelSemantics.ToChannelScopeId(Scope);
        MaterializationDeliveryId appliedDelivery = new("delivery/persisted/applied");
        MaterializationApplicationCheckpoint checkpoint = new(
            id: new("checkpoint/persisted/1"),
            kind: MaterializationCheckpointKind.ChangeProgress,
            continuation: null,
            completion: null,
            position: null,
            appliedDeliveries: [appliedDelivery],
            committedAtUtc: ObservedAtUtc,
            evidenceReference: "tests/persisted-progress",
            channelProgress: new(
                replayCursor: null,
                floor: new ChannelTargetManagedProgressFloor(
                    formatVersion: 2,
                    scope: channelScope,
                    value: "provider/floor/persisted"),
                pending: new ChannelTargetManagedPendingProgress(
                    formatVersion: 3,
                    scope: channelScope,
                    value: "provider/pending/persisted")));
        var options = StrictDocumentJson.CreateOptions();

        var json = JsonSerializer.Serialize(checkpoint, options);
        var restored = Assert.IsType<MaterializationApplicationCheckpoint>(
            JsonSerializer.Deserialize<MaterializationApplicationCheckpoint>(json, options));

        Assert.Contains("\"kind\":\"ChangeProgress\"", json);
        Assert.Equal(MaterializationCheckpointKind.ChangeProgress, restored.Kind);
        Assert.Null(restored.Position);
        Assert.Equal(appliedDelivery, Assert.Single(restored.AppliedDeliveries));
        Assert.Equal(
            "provider/floor/persisted",
            Assert.IsType<ChannelTargetManagedProgressFloor>(restored.ChannelProgress!.Floor).Value);
        Assert.Equal(
            "provider/pending/persisted",
            Assert.IsType<ChannelTargetManagedPendingProgress>(restored.ChannelProgress.Pending).Value);
        Assert.Equal(
            "\"ChangeProgress\"",
            JsonSerializer.Serialize(MaterializationCheckpointKind.ChangeProgress));
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<MaterializationCheckpointKind>("2"));
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<MaterializationCheckpointKind>("\"ChangePosition\""));
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<MaterializationCheckpointKind>("\"changeProgress\""));
    }

    [Fact]
    public void ChangeProgressCheckpoint_RejectsMissingAuthorityOrDivergentPositionProjection()
    {
        var retainedPosition = Position("position/retained");
        ChannelDurableProgressEvidence divergentProgress = new(
            replayCursor: MaterializationChannelSemantics.ToChannelReplayCursor(
                Position("position/divergent")));

        Assert.Throws<ArgumentException>(() => new MaterializationApplicationCheckpoint(
            id: new("checkpoint/missing-positioned-progress"),
            kind: MaterializationCheckpointKind.ChangeProgress,
            continuation: null,
            completion: null,
            position: retainedPosition,
            appliedDeliveries: [],
            committedAtUtc: ObservedAtUtc));
        Assert.Throws<ArgumentException>(() => new MaterializationApplicationCheckpoint(
            id: new("checkpoint/missing-positionless-progress"),
            kind: MaterializationCheckpointKind.ChangeProgress,
            continuation: null,
            completion: null,
            position: null,
            appliedDeliveries: [],
            committedAtUtc: ObservedAtUtc));
        Assert.Throws<ArgumentException>(() => new MaterializationApplicationCheckpoint(
            id: new("checkpoint/divergent-progress"),
            kind: MaterializationCheckpointKind.ChangeProgress,
            continuation: null,
            completion: null,
            position: retainedPosition,
            appliedDeliveries: [],
            committedAtUtc: ObservedAtUtc,
            channelProgress: divergentProgress));
    }

    [Fact]
    public void BatchCheckpoints_DoNotProjectToChannelDeliveryProgress()
    {
        MaterializationSourceReadFingerprint fingerprint = new(
            algorithm: "sha256",
            canonicalization: "tests/read/v1",
            value: "abcdef");
        MaterializationApplicationCheckpoint continuationCheckpoint = new(
            id: new("checkpoint/batch/continuation"),
            kind: MaterializationCheckpointKind.BatchContinuation,
            continuation: new(
                formatVersion: 1,
                readFingerprint: fingerprint,
                scope: Scope,
                value: "continuation/1"),
            completion: null,
            position: null,
            appliedDeliveries: [],
            committedAtUtc: ObservedAtUtc);
        MaterializationApplicationCheckpoint completionCheckpoint = new(
            id: new("checkpoint/batch/completed"),
            kind: MaterializationCheckpointKind.BatchCompleted,
            continuation: null,
            completion: new(
                scope: Scope,
                readFingerprint: fingerprint,
                evidenceState: RelationQuerySourceReadState.Complete,
                evidenceReference: "tests/read-complete"),
            position: null,
            appliedDeliveries: [],
            committedAtUtc: ObservedAtUtc);

        Assert.Throws<ArgumentException>(() =>
            MaterializationChannelSemantics.ToChannelDurableProgress(continuationCheckpoint));
        Assert.Throws<ArgumentException>(() =>
            MaterializationChannelSemantics.ToChannelDurableProgress(completionCheckpoint));
    }

    [Fact]
    public void SettlementReceipt_CitesDurableCheckpointAndUsesCumulativeCoverage()
    {
        var position = Position("position/40");
        MaterializationApplicationCheckpoint checkpoint = new(
            id: new("checkpoint/change/1"),
            kind: MaterializationCheckpointKind.ChangeProgress,
            continuation: null,
            completion: null,
            position: position,
            appliedDeliveries: [],
            committedAtUtc: ObservedAtUtc,
            channelProgress: MaterializationChannelSemantics.CreatePositionedDurableProgress(position));
        MaterializationSourceSettlement settlement = new(
            id: new("settlement/1"),
            checkpoint: checkpoint.Id,
            position: position,
            settledAtUtc: ObservedAtUtc.AddSeconds(1),
            evidenceReference: "tests/provider-settlement");
        MaterializationProgressSnapshot progress = new(
            key: ProgressKey(),
            revision: MaterializationProgressRevision.Initial,
            fence: MaterializationProgressFence.Initial,
            fenceOwner: "worker/cumulative",
            latestCheckpoint: checkpoint,
            latestSettlement: settlement);
        MaterializationChangeSettlementObservation observation = new(
            progress: progress,
            settlement: settlement);

        var receipt = MaterializationChannelSemantics.ToChannelSettlementReceipt(observation);

        Assert.Equal(ChannelSettlementKind.CumulativePrefix, receipt.Kind);
        Assert.Equal(ChannelSettlementCouplingKind.OrderingScope, receipt.CouplingKind);
        Assert.Equal(
            new ChannelApplicationProgressReference(
                scope: MaterializationChannelSemantics.ToChannelScopeId(Scope),
                value: "checkpoint/change/1"),
            receipt.ApplicationProgress);
        Assert.Equal(MaterializationChannelSemantics.ToChannelReplayCursor(position), receipt.ThroughCursor);
        Assert.Equal(
            MaterializationChannelSemantics.ToChannelSettlementCouplingId(Scope),
            receipt.Coupling);
        Assert.Empty(receipt.Deliveries);
        Assert.Equal(settlement.SettledAtUtc, receipt.SettledAtUtc);
        Assert.Equal(settlement.EvidenceReference, receipt.EvidenceReference);
    }

    [Fact]
    public void ProviderSettlement_RemainsGuardedByExactDurableApplicationCheckpoint()
    {
        var delivery = DeleteDelivery(
            deliveryId: "delivery/1",
            changeId: "change/1",
            positionValue: "position/41");
        MaterializationChangePage page = new(
            deliveries: [delivery],
            throughPosition: Position("position/50"),
            state: MaterializationChangePageState.CaughtUp);
        MaterializationProgressKey progress = new(
            materialization: new("tests/materialization"),
            definitionFingerprint: new(
                algorithm: "sha256",
                canonicalization: "execution-definition/v1",
                value: "0123456789abcdef"),
            generation: new("generation/1"),
            scope: Scope);
        MaterializationProgressMutationResult missingCheckpoint = new(
            disposition: MaterializationProgressMutationDisposition.NotFound,
            snapshot: null,
            diagnostics:
            [
                new DocumentValidationDiagnostic(
                    Code: "tests.materialization.checkpointMissing",
                    Severity: DiagnosticSeverity.Error,
                    Message: "Application progress is not durable.",
                    Location: "/progress",
                    SchemaLocation: "materialization/channel-settlement",
                    Evidence: new DocumentDiagnosticEvidence(
                        stage: "settlement-authorization",
                        subject: "materialization-progress",
                        sourceReferences: ["tests/materialization"],
                        expected: "an applied or replayed durable checkpoint",
                        observed: "no progress aggregate"))
            ]);

        Assert.Throws<InvalidOperationException>(() =>
            page.RequireDurableCheckpointForSettlement(progress, missingCheckpoint));

        MaterializationApplicationCheckpoint checkpoint = new(
            id: new("checkpoint/change/authorized"),
            kind: MaterializationCheckpointKind.ChangeProgress,
            continuation: null,
            completion: null,
            position: page.ThroughPosition,
            appliedDeliveries: [delivery.Id],
            committedAtUtc: ObservedAtUtc.AddSeconds(1),
            channelProgress: MaterializationChannelSemantics.CreatePositionedDurableProgress(page.ThroughPosition));
        MaterializationProgressSnapshot snapshot = new(
            key: progress,
            revision: MaterializationProgressRevision.Initial,
            fence: MaterializationProgressFence.Initial,
            fenceOwner: "worker/1",
            latestCheckpoint: checkpoint);
        MaterializationProgressMutationResult durable = new(
            disposition: MaterializationProgressMutationDisposition.Applied,
            snapshot: snapshot);

        Assert.Same(
            checkpoint,
            page.RequireDurableCheckpointForSettlement(progress, durable));
    }

    static MaterializationSourcePosition Position(string value) => new(
        formatVersion: 1,
        scope: Scope,
        value: value);

    static MaterializationProgressKey ProgressKey() => new(
        materialization: new("tests/materialization"),
        definitionFingerprint: new(
            algorithm: "sha256",
            canonicalization: "execution-definition/v1",
            value: "0123456789abcdef"),
        generation: new("generation/1"),
        scope: Scope);

    static MaterializationChangeDelivery DeleteDelivery(
        string deliveryId,
        string changeId,
        string? positionValue)
    {
        MaterializationChangeEnvelope change = new(
            id: new(changeId),
            subjectIdentity: "item/1",
            scope: Scope,
            shape: Shape,
            position: positionValue is null ? null : Position(positionValue),
            kind: MaterializationChangeKind.Delete,
            before: null,
            after: null,
            occurredAtUtc: ObservedAtUtc,
            observedAtUtc: ObservedAtUtc,
            evidenceReference: "tests/change");
        return new(
            id: new(deliveryId),
            change: change,
            deliveredAtUtc: ObservedAtUtc,
            evidenceReference: "tests/delivery");
    }
}
