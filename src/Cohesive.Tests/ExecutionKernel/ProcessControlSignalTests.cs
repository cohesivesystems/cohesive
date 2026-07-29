using Cohesive.Execution;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class ProcessControlSignalTests
{
    [Fact]
    public void RunningProcess_AdmitsAValidatedSignalForActiveConsumption()
    {
        var fixture = ProcessControlTestFixture.Create();
        var state = fixture.State();
        var command = fixture.SignalCommand(state);

        var admitted = fixture.Executor.Apply(
            state,
            command,
            state.UpdatedAtUtc.AddMinutes(1));

        Assert.Equal(ProcessControlDecisionDisposition.SignalAccepted, admitted.Disposition);
        Assert.Equal(state.Revision, admitted.State.Revision);
        Assert.Equal(ProcessControlReceiptDisposition.SignalAccepted, admitted.Receipt?.Disposition);
        var admission = Assert.Single(admitted.State.SignalAdmissions);
        Assert.Equal(ProcessSignalAdmissionDisposition.Active, admission.Disposition);
        Assert.Equal(command.Signal, admission.Signal);
        Assert.Equal(command.Context.CommandId, admission.CommandId);
        Assert.Equal(admission, Assert.IsType<ProcessSignalAdmissionIntent>(admitted.Intent).Admission);
    }

    [Fact]
    public void PausedAndPausingProcesses_BufferSignalsWithoutStartingWork()
    {
        var fixture = ProcessControlTestFixture.Create();
        var initial = fixture.State();
        var paused = fixture.Executor.Apply(
            initial,
            fixture.Pause(initial),
            initial.UpdatedAtUtc.AddMinutes(1)).State;

        var pausedSignal = fixture.Executor.Apply(
            paused,
            fixture.SignalCommand(paused, id: "signal-command/paused"),
            paused.UpdatedAtUtc.AddMinutes(1));

        Assert.Equal(ProcessControlDecisionDisposition.SignalBuffered, pausedSignal.Disposition);
        Assert.Equal(ProcessControlMode.Paused, pausedSignal.State.Mode);
        Assert.Equal(ProcessSignalAdmissionDisposition.Buffered, Assert.Single(
            pausedSignal.State.SignalAdmissions).Disposition);
        Assert.Equal(ProcessControlExecutionPhase.Ready, pausedSignal.State.CurrentAttempt.Phase);

        var activation = fixture.BeginActivation(fixture.State()).State;
        var pausing = fixture.Executor.Apply(
            activation,
            fixture.Pause(activation, id: "pause/deferred"),
            activation.UpdatedAtUtc.AddMinutes(1)).State;
        var pausingSignal = fixture.Executor.Apply(
            pausing,
            fixture.SignalCommand(
                pausing,
                id: "signal-command/pausing",
                emissionId: "emission/signal/pausing"),
            pausing.UpdatedAtUtc.AddMinutes(1));

        Assert.Equal(ProcessControlDecisionDisposition.SignalBuffered, pausingSignal.Disposition);
        Assert.Equal(ProcessControlMode.PauseRequested, pausingSignal.State.Mode);
        Assert.Equal(ProcessControlExecutionPhase.InActivation, pausingSignal.State.CurrentAttempt.Phase);
        Assert.Equal(ProcessSignalAdmissionDisposition.Buffered, Assert.Single(
            pausingSignal.State.SignalAdmissions).Disposition);
    }

    [Fact]
    public void LogicalSignalReplay_AddsAReceiptButNeverDuplicatesAdmissionOrIntent()
    {
        var fixture = ProcessControlTestFixture.Create();
        var initial = fixture.State();
        var firstCommand = fixture.SignalCommand(initial, id: "signal-command/original");
        var first = fixture.Executor.Apply(
            initial,
            firstCommand,
            initial.UpdatedAtUtc.AddMinutes(1));
        var paused = fixture.Executor.Apply(
            first.State,
            fixture.Pause(first.State),
            first.State.UpdatedAtUtc.AddMinutes(1)).State;
        var duplicateCommand = fixture.SignalCommand(
            paused,
            id: "signal-command/retry",
            emissionId: firstCommand.Signal.Context.EmissionId.Value,
            signalIdempotencyKey: firstCommand.Signal.Context.IdempotencyKey.Value);

        var duplicate = fixture.Executor.Apply(
            paused,
            duplicateCommand,
            paused.UpdatedAtUtc.AddMinutes(1));

        Assert.Equal(ProcessControlDecisionDisposition.SignalDuplicate, duplicate.Disposition);
        Assert.Equal(ProcessControlReceiptDisposition.SignalDuplicate, duplicate.Receipt?.Disposition);
        Assert.Null(duplicate.Intent);
        Assert.Single(duplicate.State.SignalAdmissions);
        Assert.Equal(3, duplicate.State.Receipts.Length);
        Assert.Equal(paused.Revision, duplicate.State.Revision);
        Assert.Equal(ProcessControlMode.Paused, duplicate.State.Mode);
    }

    [Fact]
    public void ReusedSignalIdentityWithDifferentContent_IsAConflict()
    {
        var fixture = ProcessControlTestFixture.Create();
        var initial = fixture.State();
        var first = fixture.Executor.Apply(
            initial,
            fixture.SignalCommand(initial),
            initial.UpdatedAtUtc.AddMinutes(1));

        var conflict = fixture.Executor.Apply(
            first.State,
            fixture.SignalCommand(
                first.State,
                id: "signal-command/conflict",
                payload: "different"),
            first.State.UpdatedAtUtc.AddMinutes(1));

        Assert.Equal(ProcessControlDecisionDisposition.SignalConflict, conflict.Disposition);
        Assert.Equal(ProcessControlDiagnosticCodes.SignalConflict, Assert.Single(conflict.Diagnostics).Code);
        Assert.Same(first.State, conflict.State);
        Assert.Single(conflict.State.SignalAdmissions);
        Assert.Single(conflict.State.Receipts);
    }

    [Fact]
    public void SignalTargetingAnotherAttempt_IsRejectedAsStale()
    {
        var fixture = ProcessControlTestFixture.Create();
        var state = fixture.State();

        var rejected = fixture.Executor.Apply(
            state,
            fixture.SignalCommand(
                state,
                targetAttemptId: new("process-attempt/old")),
            state.UpdatedAtUtc.AddMinutes(1));

        Assert.Equal(ProcessControlDecisionDisposition.StaleAttempt, rejected.Disposition);
        Assert.Equal(ProcessControlDiagnosticCodes.StaleAttempt, Assert.Single(rejected.Diagnostics).Code);
        Assert.Same(state, rejected.State);
    }

    [Fact]
    public void SignalTargetingAnotherInstance_IsRejectedAsTargetMismatch()
    {
        var fixture = ProcessControlTestFixture.Create();
        var state = fixture.State();
        var target = new ProcessTokenInteractionTarget(
            new(new("process-instance/other"), state.CurrentAttempt.AttemptId),
            new("token/control-input"));

        var rejected = fixture.Executor.Apply(
            state,
            Retarget(fixture.SignalCommand(state), target),
            state.UpdatedAtUtc.AddMinutes(1));

        Assert.Equal(ProcessControlDecisionDisposition.TargetMismatch, rejected.Disposition);
        Assert.Equal(ProcessControlDiagnosticCodes.TargetMismatch, Assert.Single(rejected.Diagnostics).Code);
        Assert.Same(state, rejected.State);
    }

    [Fact]
    public void SignalTargetingATransition_IsRejectedAsInvalidCommand()
    {
        var fixture = ProcessControlTestFixture.Create();
        var state = fixture.State();
        var target = new TransitionInteractionTarget(
            state.Definition,
            new("node/transition-continuation"),
            new(new("entity/order"), new("order/1")));

        var rejected = fixture.Executor.Apply(
            state,
            Retarget(fixture.SignalCommand(state), target),
            state.UpdatedAtUtc.AddMinutes(1));

        Assert.Equal(ProcessControlDecisionDisposition.InvalidCommand, rejected.Disposition);
        Assert.Equal(ProcessControlDiagnosticCodes.InvalidCommand, Assert.Single(rejected.Diagnostics).Code);
        Assert.Same(state, rejected.State);
    }

    [Fact]
    public void SignalsCannotEnterAnAttemptThatIsRestartingCancellingOrTerminal()
    {
        var fixture = ProcessControlTestFixture.Create();
        var restartActivation = fixture.BeginActivation(fixture.State()).State;
        var restarting = fixture.Executor.Apply(
            restartActivation,
            fixture.Restart(restartActivation),
            restartActivation.UpdatedAtUtc.AddMinutes(1)).State;
        var restartingSignal = fixture.Executor.Apply(
            restarting,
            fixture.SignalCommand(
                restarting,
                id: "signal-command/restarting",
                emissionId: "emission/signal/restarting"),
            restarting.UpdatedAtUtc.AddMinutes(1));

        Assert.Equal(ProcessControlDecisionDisposition.InvalidState, restartingSignal.Disposition);
        Assert.Same(restarting, restartingSignal.State);

        var cancelActivation = fixture.BeginActivation(fixture.State()).State;
        var cancelling = fixture.Executor.Apply(
            cancelActivation,
            fixture.Cancel(cancelActivation),
            cancelActivation.UpdatedAtUtc.AddMinutes(1)).State;
        var cancellingSignal = fixture.Executor.Apply(
            cancelling,
            fixture.SignalCommand(
                cancelling,
                id: "signal-command/cancelling",
                emissionId: "emission/signal/cancelling"),
            cancelling.UpdatedAtUtc.AddMinutes(1));

        Assert.Equal(ProcessControlDecisionDisposition.InvalidState, cancellingSignal.Disposition);
        Assert.Same(cancelling, cancellingSignal.State);

        var terminal = fixture.Executor.Apply(
            fixture.State(),
            fixture.Terminate(fixture.State()),
            ProcessControlTestFixture.CreatedAtUtc.AddMinutes(1)).State;
        var terminalSignal = fixture.Executor.Apply(
            terminal,
            fixture.SignalCommand(
                terminal,
                id: "signal-command/terminal",
                emissionId: "emission/signal/terminal"),
            terminal.UpdatedAtUtc.AddMinutes(1));

        Assert.Equal(ProcessControlDecisionDisposition.InvalidState, terminalSignal.Disposition);
        Assert.Same(terminal, terminalSignal.State);
    }

    [Fact]
    public void BufferedSignalsRemainBoundToTheSameAttemptAcrossContinue()
    {
        var fixture = ProcessControlTestFixture.Create();
        var initial = fixture.State();
        var paused = fixture.Executor.Apply(
            initial,
            fixture.Pause(initial),
            initial.UpdatedAtUtc.AddMinutes(1)).State;
        var buffered = fixture.Executor.Apply(
            paused,
            fixture.SignalCommand(paused),
            paused.UpdatedAtUtc.AddMinutes(1)).State;

        var continued = fixture.Executor.Apply(
            buffered,
            fixture.Continue(buffered),
            buffered.UpdatedAtUtc.AddMinutes(1));

        Assert.Equal(initial.CurrentAttempt.AttemptId, continued.State.CurrentAttempt.AttemptId);
        var admission = Assert.Single(continued.State.SignalAdmissions);
        var target = Assert.IsType<ProcessTokenInteractionTarget>(admission.Signal.Target);
        Assert.Equal(initial.CurrentAttempt.AttemptId, target.Continuation.ProcessAttemptId);
        Assert.Equal(ProcessSignalAdmissionDisposition.Buffered, admission.Disposition);
    }

    [Fact]
    public void RetiringAndTerminalStates_RejectFreshLogicalDuplicatesButReplayTheOriginalCommand()
    {
        var fixture = ProcessControlTestFixture.Create();
        var initial = fixture.State();
        var original = fixture.SignalCommand(initial, id: "signal-command/original-stable");
        var signalled = fixture.Executor.Apply(
            initial,
            original,
            initial.UpdatedAtUtc.AddMinutes(1)).State;
        var activation = fixture.BeginActivation(signalled).State;
        var restarting = fixture.Executor.Apply(
            activation,
            fixture.Restart(activation, id: "restart/after-signal"),
            activation.UpdatedAtUtc.AddMinutes(1)).State;

        var retiringReplay = fixture.Executor.Apply(
            restarting,
            original,
            restarting.UpdatedAtUtc.AddMinutes(1));
        var retiringDuplicate = fixture.Executor.Apply(
            restarting,
            fixture.SignalCommand(
                restarting,
                id: "signal-command/fresh-retiring-duplicate",
                emissionId: original.Signal.Context.EmissionId.Value,
                signalIdempotencyKey: original.Signal.Context.IdempotencyKey.Value),
            restarting.UpdatedAtUtc.AddMinutes(1));

        Assert.Equal(ProcessControlDecisionDisposition.Replayed, retiringReplay.Disposition);
        Assert.Same(restarting, retiringReplay.State);
        Assert.Equal(original.Context.CommandId, retiringReplay.Receipt?.Command.Context.CommandId);
        Assert.Equal(ProcessControlDecisionDisposition.InvalidState, retiringDuplicate.Disposition);
        Assert.Same(restarting, retiringDuplicate.State);

        var terminated = fixture.Executor.Apply(
            signalled,
            fixture.Terminate(signalled, id: "terminate/after-signal"),
            signalled.UpdatedAtUtc.AddMinutes(1)).State;
        var terminalReplay = fixture.Executor.Apply(
            terminated,
            original,
            terminated.UpdatedAtUtc.AddMinutes(1));
        var terminalDuplicate = fixture.Executor.Apply(
            terminated,
            fixture.SignalCommand(
                terminated,
                id: "signal-command/fresh-terminal-duplicate",
                emissionId: original.Signal.Context.EmissionId.Value,
                signalIdempotencyKey: original.Signal.Context.IdempotencyKey.Value),
            terminated.UpdatedAtUtc.AddMinutes(1));

        Assert.Equal(ProcessControlDecisionDisposition.Replayed, terminalReplay.Disposition);
        Assert.Same(terminated, terminalReplay.State);
        Assert.Equal(original.Context.CommandId, terminalReplay.Receipt?.Command.Context.CommandId);
        Assert.Equal(ProcessControlDecisionDisposition.InvalidState, terminalDuplicate.Disposition);
        Assert.Same(terminated, terminalDuplicate.State);
    }

    static SignalProcessCommand Retarget(
        SignalProcessCommand command,
        InteractionTarget target)
    {
        var signal = command.Signal;
        return new(
            command.SchemaVersion,
            command.Context,
            command.Expectation!,
            new(
                signal.SchemaVersion,
                signal.Context,
                signal.Contract,
                signal.Payload,
                target));
    }
}
