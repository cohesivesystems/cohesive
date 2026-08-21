using System.Collections.Immutable;
using Cohesive.Execution;
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
    ImmutableArray<MaterializationHarnessDurableOperationEvidence> DurableOperations,
    ImmutableArray<MaterializationHarnessProgressEvidence> Progress,
    DateTimeOffset CapturedAtUtc);

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
