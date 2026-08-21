using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Processes.IR;
using Cohesive.Processes.Execution;
using Cohesive.Storage.Materialization;

namespace Cohesive.MaterializationHarness.Control;

sealed record MaterializationHarnessFailureEvidence(
    string Provider,
    string ProcessInstanceId,
    string CurrentAttemptId,
    string ControlRevision,
    ProcessControlMode ControlMode,
    ExecutionTerminalOutcomeKind TerminalOutcome,
    string? CurrentGeneration,
    string? SelectedGeneration,
    string TargetRevision,
    string? ActiveGeneration,
    MaterializationGenerationState? SelectedGenerationState,
    string? SelectedGenerationRevision,
    long? SelectedVisibleItemCount,
    long? SelectedTombstoneCount,
    ImmutableArray<string> SelectedControlEpochs,
    MaterializationHarnessSynchronizationWorkEvidence? SynchronizationWork,
    MaterializationHarnessSynchronizationRunEvidence? LastSynchronization,
    ImmutableArray<MaterializationHarnessDurableOperationEvidence> DurableOperations,
    ImmutableArray<MaterializationHarnessProgressEvidence> Progress,
    ImmutableArray<MaterializationHarnessSourceHeadEvidence> SourceHeads,
    ImmutableArray<string> CanonicalDocuments,
    DateTimeOffset CapturedAtUtc);

sealed record MaterializationHarnessSynchronizationRunEvidence(
    MaterializationSynchronizationRunDisposition Disposition,
    string Generation,
    int FeedCount,
    string? ReceiptFingerprint,
    ImmutableArray<string> DiagnosticCodes)
{
    internal static MaterializationHarnessSynchronizationRunEvidence From(
        MaterializationSynchronizationRunResult result) => new(
        Disposition: result.Disposition,
        Generation: result.Generation.Value,
        FeedCount: result.Feeds.Length,
        ReceiptFingerprint: result.Receipt?.Fingerprint.Value,
        DiagnosticCodes:
        [
            .. result.Diagnostics.Select(static diagnostic => diagnostic.Code)
        ]);
}

sealed record MaterializationHarnessSynchronizationWorkEvidence(
    string Revision,
    string Fence,
    string FenceOwner,
    string NextItemVersion,
    MaterializationHarnessPendingWorkEvidence? PendingWork)
{
    internal static MaterializationHarnessSynchronizationWorkEvidence From(
        MaterializationSynchronizationWorkSnapshot snapshot) => new(
        Revision: snapshot.Revision.Value,
        Fence: snapshot.Fence.Value,
        FenceOwner: snapshot.FenceOwner,
        NextItemVersion: snapshot.NextItemVersion.Value,
        PendingWork: snapshot.PendingWork is { } pending
            ? MaterializationHarnessPendingWorkEvidence.From(pending)
            : null);
}

sealed record MaterializationHarnessPendingWorkEvidence(
    string PreparationId,
    string Feed,
    string Checkpoint,
    int ThroughPositionFormatVersion,
    string ThroughPosition,
    ImmutableArray<string> AppliedDeliveries,
    MaterializationChangePageState PageState,
    string? Version,
    ImmutableArray<MaterializationHarnessPendingMutationEvidence> Mutations)
{
    internal static MaterializationHarnessPendingWorkEvidence From(
        MaterializationPreparedSynchronizationWork pending) => new(
        PreparationId: pending.PreparationId.Value,
        Feed: pending.Page.Feed.Value,
        Checkpoint: pending.Page.Checkpoint.Value,
        ThroughPositionFormatVersion: pending.Page.ThroughPosition.FormatVersion,
        ThroughPosition: pending.Page.ThroughPosition.Value,
        AppliedDeliveries:
        [
            .. pending.Page.AppliedDeliveries.Select(static delivery => delivery.Value)
        ],
        PageState: pending.Page.State,
        Version: pending.Version?.Value,
        Mutations:
        [
            .. pending.Mutations.Select(MaterializationHarnessPendingMutationEvidence.From)
        ]);
}

sealed record MaterializationHarnessPendingMutationEvidence(
    string Item,
    string Mutation,
    MaterializationItemMutationKind Kind,
    string Version)
{
    internal static MaterializationHarnessPendingMutationEvidence From(
        MaterializationItemMutation mutation) => new(
        Item: mutation.ItemId.Value,
        Mutation: mutation.MutationId.Value,
        Kind: mutation.Kind,
        Version: mutation.Version.Value);
}

sealed record MaterializationHarnessSourceHeadEvidence(
    string Feed,
    string Input,
    string Partition,
    int FormatVersion,
    string Position);

sealed record MaterializationHarnessIncompatibleReplayProbeResult(
    string Provider,
    string Generation,
    string PreparationId,
    string OriginalPosition,
    string ConflictingPosition,
    MaterializationSynchronizationWorkMutationDisposition Disposition,
    ProcessRecoveryPolicy RequiredControlAction,
    string BeforeRevision,
    string AfterRevision,
    string BeforeFence,
    string AfterFence,
    bool PendingWorkPreserved);

sealed record MaterializationHarnessTargetOrderingProbeResult(
    string Provider,
    string Generation,
    string StaleWorkerFence,
    string CurrentWorkerFence,
    string Item,
    string CurrentItemVersion,
    string SubmittedStaleItemVersion,
    MaterializationBatchDisposition StaleWorkerDisposition,
    MaterializationItemOutcomeDisposition StaleWorkerItemDisposition,
    MaterializationBatchDisposition StaleVersionDisposition,
    MaterializationItemOutcomeDisposition StaleVersionItemDisposition,
    string? StaleVersionCode,
    bool LogicalDocumentsUnchanged);

sealed record MaterializationHarnessDurableOperationEvidence(
    string OperationId,
    string Contract,
    string? OriginAttemptId,
    DurableOperationStatus Status,
    DurableOperationRecoveryRequirement RecoveryRequirement,
    DurableOperationAttemptStage? CurrentAttemptStage,
    DateTimeOffset? CurrentClaimExpiresAtUtc,
    string? CurrentFailureCode,
    int AttemptCount,
    int ReconciliationCount)
{
    internal static MaterializationHarnessDurableOperationEvidence From(DurableOperationState operation) => new(
        OperationId: operation.OperationId.Value,
        Contract: operation.Request.Contract.Definition.DefinitionId.Value,
        OriginAttemptId: operation.Request.Context.Origin is ProcessInteractionOrigin origin
            ? origin.Continuation.ProcessAttemptId.Value
            : null,
        Status: operation.Status,
        RecoveryRequirement: operation.RecoveryRequirement,
        CurrentAttemptStage: operation.CurrentAttempt?.Stage,
        CurrentClaimExpiresAtUtc: operation.CurrentAttempt?.Claim.ExpiresAtUtc,
        CurrentFailureCode: operation.CurrentAttempt?.Failure?.Code,
        AttemptCount: operation.Attempts.Length,
        ReconciliationCount: operation.Reconciliations.Length);
}

sealed record MaterializationHarnessProgressEvidence(
    string Input,
    string Partition,
    string? Revision,
    string? FenceOwner,
    string? Fence,
    string? BaselineCheckpoint,
    MaterializationCheckpointKind? BaselineCheckpointKind,
    long? BaselinePageOrdinal,
    string? ChangeCheckpoint,
    string? ChangePosition,
    int AppliedDeliveryCount,
    string? Settlement,
    string? SettledCheckpoint)
{
    internal static MaterializationHarnessProgressEvidence From(
        MaterializationSourceScope scope,
        MaterializationProgressSnapshot? snapshot) => new(
        Input: scope.Input.Value,
        Partition: scope.Partition.Value,
        Revision: snapshot?.Revision.Value,
        FenceOwner: snapshot?.FenceOwner,
        Fence: snapshot?.Fence.Value,
        BaselineCheckpoint: snapshot?.LatestBatchCheckpoint?.Id.Value,
        BaselineCheckpointKind: snapshot?.LatestBatchCheckpoint?.Kind,
        BaselinePageOrdinal: snapshot?.LatestBatchCheckpoint?.BatchPageOrdinal,
        ChangeCheckpoint: snapshot?.LatestChangeCheckpoint?.Id.Value,
        ChangePosition: snapshot?.LatestChangeCheckpoint?.Position?.Value,
        AppliedDeliveryCount: snapshot?.LatestChangeCheckpoint?.AppliedDeliveries.Length ?? 0,
        Settlement: snapshot?.LatestSettlement?.Id.Value,
        SettledCheckpoint: snapshot?.LatestSettlement?.Checkpoint.Value);
}
