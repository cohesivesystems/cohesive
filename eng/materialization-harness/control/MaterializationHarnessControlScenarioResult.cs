using System.Collections.Immutable;
using Cohesive.Api;
using Cohesive.Control;
using Cohesive.Execution;
using Cohesive.Processes.Runtime;
using Cohesive.Storage.Materialization;

namespace Cohesive.MaterializationHarness.Control;

enum MaterializationHarnessControlTransportKind
{
    Sdk = 0,
    Http = 1
}

sealed record MaterializationHarnessControlOperationObservation(
    string Operation,
    ApiResultKind ResultKind,
    string Disposition,
    long? BeforeRevision,
    long? AfterRevision,
    long? CurrentRevision,
    ProcessControlMode? ControlMode);

sealed record MaterializationHarnessControlScenarioResult(
    int SchemaVersion,
    MaterializationHarnessControlTransportKind Transport,
    string Provider,
    ImmutableArray<MaterializationHarnessControlOperationObservation> Operations,
    string InitialAttempt,
    string InitialGeneration,
    string PausedAttempt,
    string PausedGeneration,
    string ContinuedAttempt,
    string ContinuedGeneration,
    string RestartedAttempt,
    string RestartedGeneration,
    string CancelledAttempt,
    string CancelledGeneration,
    bool LimitUpdateBoundExactGeneration,
    MaterializationGenerationState? InterruptedGenerationState,
    MaterializationGenerationState? CancelledGenerationState,
    string? ActiveGeneration,
    ProcessControlMode FinalControlMode,
    ExecutionTerminalOutcomeKind FinalTerminalOutcome)
{
    internal const int CurrentSchemaVersion = 1;

    internal MaterializationHarnessControlScenarioSemantics ValidateAndProjectSemantics()
    {
        if (SchemaVersion != CurrentSchemaVersion)
            throw new InvalidOperationException("The control scenario result uses an unsupported schema version.");
        if (string.IsNullOrWhiteSpace(Provider))
            throw new InvalidOperationException("The control scenario result has no provider.");
        var expectedOperations = new[]
        {
            ProcessStartWireNames.Start,
            ExecutionControlWireNames.Pause,
            ExecutionControlWireNames.Inspect,
            ExecutionExplainWireNames.Explain,
            ProcessExecutionTraceWireNames.Read,
            ExecutionControlWireNames.Continue,
            ControlLimitUpdateWireNames.UpdateLimits,
            ExecutionControlWireNames.RestartAttempt,
            ExecutionControlWireNames.Cancel
        };
        if (!Operations.Select(static operation => operation.Operation).SequenceEqual(expectedOperations))
            throw new InvalidOperationException("The control scenario did not observe the canonical operation sequence.");
        if (Operations.Any(static operation => string.IsNullOrWhiteSpace(operation.Disposition)))
            throw new InvalidOperationException("Every control operation requires an explicit safe disposition.");
        if (!string.Equals(InitialAttempt, PausedAttempt, StringComparison.Ordinal)
            || !string.Equals(InitialAttempt, ContinuedAttempt, StringComparison.Ordinal)
            || !string.Equals(InitialGeneration, PausedGeneration, StringComparison.Ordinal)
            || !string.Equals(InitialGeneration, ContinuedGeneration, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Pause and Continue must retain the exact attempt and generation.");
        }
        if (string.Equals(InitialAttempt, RestartedAttempt, StringComparison.Ordinal)
            || string.Equals(InitialGeneration, RestartedGeneration, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("RestartAttempt must allocate a fresh attempt and generation.");
        }
        if (!string.Equals(RestartedAttempt, CancelledAttempt, StringComparison.Ordinal)
            || !string.Equals(RestartedGeneration, CancelledGeneration, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Cancel must close the replacement attempt and its generation.");
        }
        if (!LimitUpdateBoundExactGeneration)
            throw new InvalidOperationException("The limit update did not bind the exact continued generation epoch.");
        if (InterruptedGenerationState is not (null or MaterializationGenerationState.Retired)
            || CancelledGenerationState is not (null or MaterializationGenerationState.Retired)
            || ActiveGeneration is not null)
        {
            throw new InvalidOperationException("Cancelled control scenarios must retire both candidates and route none.");
        }
        if (FinalControlMode != ProcessControlMode.Cancelled
            || FinalTerminalOutcome != ExecutionTerminalOutcomeKind.Cancelled)
        {
            throw new InvalidOperationException("Cancel must leave terminal canonical cancellation evidence.");
        }

        return new(
            Operations:
            [
                .. Operations.Select(static operation => new MaterializationHarnessControlOperationSemantics(
                    Operation: operation.Operation,
                    ResultKind: operation.ResultKind,
                    Disposition: operation.Disposition,
                    RevisionAdvance: operation.BeforeRevision.HasValue && operation.AfterRevision.HasValue
                        ? operation.AfterRevision.Value - operation.BeforeRevision.Value
                        : null,
                    ControlMode: operation.ControlMode))
            ],
            PauseRetainedAttempt: true,
            PauseRetainedGeneration: true,
            ContinueRetainedAttempt: true,
            ContinueRetainedGeneration: true,
            RestartReplacedAttempt: true,
            RestartReplacedGeneration: true,
            LimitUpdateBoundExactGeneration: true,
            CancelRetainedReplacementAttempt: true,
            InterruptedGenerationVisible: false,
            CancelledGenerationVisible: false,
            HasActiveGeneration: false,
            FinalControlMode: FinalControlMode,
            FinalTerminalOutcome: FinalTerminalOutcome);
    }
}

sealed record MaterializationHarnessControlOperationSemantics(
    string Operation,
    ApiResultKind ResultKind,
    string Disposition,
    long? RevisionAdvance,
    ProcessControlMode? ControlMode);

sealed record MaterializationHarnessControlScenarioSemantics(
    ImmutableArray<MaterializationHarnessControlOperationSemantics> Operations,
    bool PauseRetainedAttempt,
    bool PauseRetainedGeneration,
    bool ContinueRetainedAttempt,
    bool ContinueRetainedGeneration,
    bool RestartReplacedAttempt,
    bool RestartReplacedGeneration,
    bool LimitUpdateBoundExactGeneration,
    bool CancelRetainedReplacementAttempt,
    bool InterruptedGenerationVisible,
    bool CancelledGenerationVisible,
    bool HasActiveGeneration,
    ProcessControlMode FinalControlMode,
    ExecutionTerminalOutcomeKind FinalTerminalOutcome)
{
    internal bool IsEquivalentTo(MaterializationHarnessControlScenarioSemantics other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Operations.SequenceEqual(other.Operations)
            && PauseRetainedAttempt == other.PauseRetainedAttempt
            && PauseRetainedGeneration == other.PauseRetainedGeneration
            && ContinueRetainedAttempt == other.ContinueRetainedAttempt
            && ContinueRetainedGeneration == other.ContinueRetainedGeneration
            && RestartReplacedAttempt == other.RestartReplacedAttempt
            && RestartReplacedGeneration == other.RestartReplacedGeneration
            && LimitUpdateBoundExactGeneration == other.LimitUpdateBoundExactGeneration
            && CancelRetainedReplacementAttempt == other.CancelRetainedReplacementAttempt
            && InterruptedGenerationVisible == other.InterruptedGenerationVisible
            && CancelledGenerationVisible == other.CancelledGenerationVisible
            && HasActiveGeneration == other.HasActiveGeneration
            && FinalControlMode == other.FinalControlMode
            && FinalTerminalOutcome == other.FinalTerminalOutcome;
    }
}
