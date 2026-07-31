using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Physical;
using Cohesive.Storage.Materialization;

namespace Cohesive.Tests.Storage;

public sealed class MaterializationManagedChangeSourceTests
{
    static readonly DateTimeOffset DeliveredAtUtc =
        new(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);
    static readonly QualifiedShapeId Shape = new(new("tests"), new("ManagedItem"));
    static readonly RelationQuerySourceInstanceId Source = new("tests/managed-source");
    static readonly RelationQueryPhysicalPlanFingerprint PhysicalPlan =
        new("sha256", "tests/physical-plan/v1", "0123456789abcdef");
    static readonly RelationQuerySourcePlacementBinding Placement = new(
        new("placement/managed-source"),
        new("input/managed-source"),
        new("node/managed-source"),
        new("binding/managed-source"),
        Shape,
        Source,
        RelationQuerySourcePlacementBindingKind.SourceSet,
        RelationQuerySourceAcquisitionKind.BoundedEnumeration,
        RelationQuerySourcePlacementOrigin.Explicit,
        new RelationQuerySourceIdentityBinding(Shape, "id"));
    static readonly MaterializationSourceScope Scope = new(
        PhysicalPlan,
        Placement,
        new("partition-a"),
        new("partition-a/feed-0"));
    static readonly MaterializationSourceScope OtherScope = new(
        PhysicalPlan,
        Placement,
        new("partition-b"),
        new("partition-b/feed-0"));
    static readonly MaterializationManagedChangeRequest Request = new(
        new("tests/materialization"),
        new ExecutionDefinitionFingerprint(
            "sha256",
            "execution-definition/v1",
            "0123456789abcdef"),
        new("generation-1"));

    [Theory]
    [InlineData(MaterializationProgressMutationDisposition.Applied)]
    [InlineData(MaterializationProgressMutationDisposition.Replayed)]
    public void ExactAppliedOrReplayedCheckpoint_AuthorizesProviderSettlement(
        MaterializationProgressMutationDisposition disposition)
    {
        var page = ChangePage("delivery-2", "delivery-1");
        var progress = Request.CreateProgressKey(page.ThroughPosition.Scope);
        var checkpoint = CheckpointFor(page);
        var result = SuccessfulResult(disposition, progress, checkpoint);

        var authorized = page.RequireDurableCheckpointForSettlement(progress, result);

        Assert.Same(checkpoint, authorized);
        Assert.Equal(
            ["delivery-1", "delivery-2"],
            authorized.AppliedDeliveries.Select(static delivery => delivery.Value));
    }

    [Fact]
    public void RejectedHandlerOrCheckpointProgress_DoesNotAuthorizeProviderSettlement()
    {
        var page = ChangePage("delivery-1");
        var progress = Request.CreateProgressKey(Scope);
        var checkpoint = CheckpointFor(page);
        MaterializationProgressMutationResult handlerRejected = new(
            MaterializationProgressMutationDisposition.NotFound,
            snapshot: null,
            [Diagnostic("tests.managed.handlerRejected")]);
        MaterializationProgressMutationResult checkpointRejected = new(
            MaterializationProgressMutationDisposition.RevisionConflict,
            Snapshot(progress, checkpoint),
            [Diagnostic("tests.managed.checkpointRejected")]);

        Assert.Throws<InvalidOperationException>(() =>
            page.RequireDurableCheckpointForSettlement(progress, handlerRejected));
        Assert.Throws<InvalidOperationException>(() =>
            page.RequireDurableCheckpointForSettlement(progress, checkpointRejected));
    }

    [Fact]
    public void SettlementAuthorization_RejectsMismatchedProgressPositionAndDeliverySet()
    {
        var page = ChangePage("delivery-1", "delivery-2");
        var progress = Request.CreateProgressKey(Scope);
        var otherProgress = Request.CreateProgressKey(OtherScope);
        var otherScopeCheckpoint = CheckpointFor(
            page,
            position: new MaterializationSourcePosition(1, OtherScope, "through/2"));
        var otherPositionCheckpoint = CheckpointFor(
            page,
            position: new MaterializationSourcePosition(1, Scope, "through/other"));
        var incompleteDeliveryCheckpoint = CheckpointFor(
            page,
            appliedDeliveries: [new MaterializationDeliveryId("delivery-1")]);
        var changedDeliveryCheckpoint = CheckpointFor(
            page,
            appliedDeliveries:
            [
                new MaterializationDeliveryId("delivery-1"),
                new MaterializationDeliveryId("delivery-other")
            ]);

        Assert.Throws<InvalidOperationException>(() => page.RequireDurableCheckpointForSettlement(
            progress,
            SuccessfulResult(
                MaterializationProgressMutationDisposition.Applied,
                otherProgress,
                otherScopeCheckpoint)));
        Assert.Throws<InvalidOperationException>(() => page.RequireDurableCheckpointForSettlement(
            progress,
            SuccessfulResult(
                MaterializationProgressMutationDisposition.Applied,
                progress,
                otherPositionCheckpoint)));
        Assert.Throws<InvalidOperationException>(() => page.RequireDurableCheckpointForSettlement(
            progress,
            SuccessfulResult(
                MaterializationProgressMutationDisposition.Applied,
                progress,
                incompleteDeliveryCheckpoint)));
        Assert.Throws<InvalidOperationException>(() => page.RequireDurableCheckpointForSettlement(
            progress,
            SuccessfulResult(
                MaterializationProgressMutationDisposition.Applied,
                progress,
                changedDeliveryCheckpoint)));
    }

    [Fact]
    public void EmptyProgressedPage_RequiresDurableThroughPositionWithNoAppliedDeliveries()
    {
        MaterializationChangePage page = new(
            deliveries: [],
            new MaterializationSourcePosition(1, Scope, "through/filtered-input"),
            MaterializationChangePageState.Progressed);
        var progress = Request.CreateProgressKey(Scope);
        var exact = CheckpointFor(page, appliedDeliveries: []);
        var inventedDelivery = CheckpointFor(
            page,
            appliedDeliveries: [new MaterializationDeliveryId("delivery-not-on-page")]);

        Assert.Same(
            exact,
            page.RequireDurableCheckpointForSettlement(
                progress,
                SuccessfulResult(MaterializationProgressMutationDisposition.Applied, progress, exact)));
        Assert.Throws<InvalidOperationException>(() => page.RequireDurableCheckpointForSettlement(
            progress,
            SuccessfulResult(
                MaterializationProgressMutationDisposition.Applied,
                progress,
                inventedDelivery)));
    }

    [Fact]
    public void ExactReplayCheckpointPredatingRedelivery_StillAuthorizesProviderSettlement()
    {
        var page = ChangePage("delivery-1");
        var progress = Request.CreateProgressKey(Scope);
        var checkpoint = CheckpointFor(page, committedAtUtc: DeliveredAtUtc.AddTicks(-1));
        var result = SuccessfulResult(
            MaterializationProgressMutationDisposition.Replayed,
            progress,
            checkpoint);

        Assert.Same(checkpoint, page.RequireDurableCheckpointForSettlement(progress, result));
    }

    [Fact]
    public void ManagedRequestAndLagObservation_RetainGenerationWithoutInventingPerScopePrecision()
    {
        var progress = Request.CreateProgressKey(Scope);
        MaterializationChangeLagObservation sourceWide = new(
            Request,
            Source,
            scope: null,
            MaterializationChangeLagEstimateState.Estimated,
            estimatedPendingProviderWork: 42,
            DeliveredAtUtc,
            "tests/managed-estimator");
        MaterializationChangeLagObservation unavailable = new(
            Request,
            Source,
            Scope,
            MaterializationChangeLagEstimateState.Unavailable,
            estimatedPendingProviderWork: null,
            DeliveredAtUtc);

        Assert.Equal(Request.Materialization, progress.Materialization);
        Assert.Equal(Request.DefinitionFingerprint, progress.DefinitionFingerprint);
        Assert.Equal(Request.Generation, progress.Generation);
        Assert.Equal(Scope, progress.Scope);
        Assert.Null(sourceWide.Scope);
        Assert.Equal(42L, sourceWide.EstimatedPendingProviderWork);
        Assert.Null(unavailable.EstimatedPendingProviderWork);
        Assert.Throws<ArgumentException>(() => new MaterializationChangeLagObservation(
            Request,
            new RelationQuerySourceInstanceId("tests/other-source"),
            OtherScope,
            MaterializationChangeLagEstimateState.Estimated,
            estimatedPendingProviderWork: 1,
            DeliveredAtUtc));
    }

    [Fact]
    public void SettlementObservation_RequiresExactCheckpointPositionScopeAndChronology()
    {
        var page = ChangePage("delivery-1");
        var progressKey = Request.CreateProgressKey(Scope);
        var checkpoint = CheckpointFor(page, committedAtUtc: DeliveredAtUtc.AddTicks(1));
        var progress = Snapshot(progressKey, checkpoint);
        MaterializationSourceSettlement settlement = new(
            new("settlement-1"),
            checkpoint.Id,
            page.ThroughPosition,
            DeliveredAtUtc.AddTicks(2),
            "tests/provider-settlement");

        MaterializationChangeSettlementObservation observation = new(progress, settlement);

        Assert.Same(progress, observation.Progress);
        Assert.Same(settlement, observation.Settlement);
        Assert.Throws<ArgumentException>(() => new MaterializationChangeSettlementObservation(
            progress,
            new MaterializationSourceSettlement(
                new("settlement-2"),
                new MaterializationCheckpointId("checkpoint-other"),
                page.ThroughPosition,
                DeliveredAtUtc.AddTicks(2))));
        Assert.Throws<ArgumentException>(() => new MaterializationChangeSettlementObservation(
            progress,
            new MaterializationSourceSettlement(
                new("settlement-3"),
                checkpoint.Id,
                new MaterializationSourcePosition(1, Scope, "through/other"),
                DeliveredAtUtc.AddTicks(2))));
        Assert.Throws<ArgumentException>(() => new MaterializationChangeSettlementObservation(
            progress,
            new MaterializationSourceSettlement(
                new("settlement-4"),
                checkpoint.Id,
                new MaterializationSourcePosition(1, OtherScope, "through/2"),
                DeliveredAtUtc.AddTicks(2))));
        Assert.Throws<ArgumentException>(() => new MaterializationChangeSettlementObservation(
            progress,
            new MaterializationSourceSettlement(
                new("settlement-5"),
                checkpoint.Id,
                page.ThroughPosition,
                DeliveredAtUtc)));
    }

    static MaterializationChangePage ChangePage(params string[] deliveryIds) => new(
        [.. deliveryIds.Select(ChangeDelivery)],
        new MaterializationSourcePosition(1, Scope, "through/2"),
        MaterializationChangePageState.CaughtUp);

    static MaterializationChangeDelivery ChangeDelivery(string deliveryId) => new(
        new(deliveryId),
        new MaterializationChangeEnvelope(
            new($"change/{deliveryId}"),
            $"subject/{deliveryId}",
            Scope,
            Shape,
            new MaterializationSourcePosition(1, Scope, $"position/{deliveryId}"),
            MaterializationChangeKind.Delete,
            before: null,
            after: null,
            DeliveredAtUtc,
            DeliveredAtUtc,
            "tests/change"),
        DeliveredAtUtc,
        "tests/delivery");

    static MaterializationApplicationCheckpoint CheckpointFor(
        MaterializationChangePage page,
        MaterializationSourcePosition? position = null,
        ImmutableArray<MaterializationDeliveryId> appliedDeliveries = default,
        DateTimeOffset? committedAtUtc = null)
    {
        if (appliedDeliveries.IsDefault)
        {
            appliedDeliveries =
            [
                .. page.Deliveries.Select(static delivery => delivery.Id)
            ];
        }

        return new(
            new("checkpoint-1"),
            MaterializationCheckpointKind.ChangePosition,
            continuation: null,
            completion: null,
            position ?? page.ThroughPosition,
            appliedDeliveries,
            committedAtUtc ?? DeliveredAtUtc.AddTicks(1),
            "tests/application-commit");
    }

    static MaterializationProgressMutationResult SuccessfulResult(
        MaterializationProgressMutationDisposition disposition,
        MaterializationProgressKey progress,
        MaterializationApplicationCheckpoint checkpoint) =>
        new(disposition, Snapshot(progress, checkpoint));

    static MaterializationProgressSnapshot Snapshot(
        MaterializationProgressKey progress,
        MaterializationApplicationCheckpoint checkpoint) => new(
        progress,
        MaterializationProgressRevision.Initial,
        MaterializationProgressFence.Initial,
        "worker-a",
        checkpoint);

    static DocumentValidationDiagnostic Diagnostic(string code) => new(
        code,
        DiagnosticSeverity.Error,
        "Managed change progress was rejected.",
        "/progress",
        "managed-change-progress",
        new DocumentDiagnosticEvidence(
            stage: "managed-change-test",
            subject: "progress",
            sourceReferences: ["tests/managed-source"],
            expected: "applied or replayed",
            observed: "rejected"));
}
