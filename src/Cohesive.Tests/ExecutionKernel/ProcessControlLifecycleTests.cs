using Cohesive.Execution;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class ProcessControlLifecycleTests
{
    [Fact]
    public void Inspect_IsReadOnlyAndDoesNotCreateADurableReceipt()
    {
        var fixture = ProcessControlTestFixture.Create();
        var state = fixture.State();

        var inspected = fixture.Executor.Apply(
            state,
            fixture.Inspect(state),
            state.UpdatedAtUtc);

        Assert.Equal(ProcessControlDecisionDisposition.Inspected, inspected.Disposition);
        Assert.Same(state, inspected.State);
        Assert.Null(inspected.Receipt);
        Assert.Null(inspected.Intent);
        Assert.Empty(inspected.Diagnostics);
        Assert.Empty(state.Receipts);
    }

    [Fact]
    public void PauseAndContinue_RetainTheCurrentAttemptAndItsAffinity()
    {
        var fixture = ProcessControlTestFixture.Create();
        var bound = fixture.BindAffinity(fixture.State());
        var beforePause = bound.State;

        var paused = fixture.Executor.Apply(
            beforePause,
            fixture.Pause(beforePause),
            beforePause.UpdatedAtUtc.AddMinutes(1));
        var continued = fixture.Executor.Apply(
            paused.State,
            fixture.Continue(paused.State),
            paused.State.UpdatedAtUtc.AddMinutes(1));

        Assert.Equal(ProcessControlDecisionDisposition.AffinityBound, bound.Disposition);
        Assert.Equal(ProcessControlMode.Paused, paused.State.Mode);
        Assert.Equal(ProcessControlMode.Running, continued.State.Mode);
        Assert.Equal(beforePause.ProcessInstanceId, continued.State.ProcessInstanceId);
        Assert.Equal(beforePause.CurrentAttempt.AttemptId, continued.State.CurrentAttempt.AttemptId);
        Assert.Equal(beforePause.CurrentAttempt.AffinityBindings, continued.State.CurrentAttempt.AffinityBindings);
        Assert.Equal("generation/1", continued.State.CurrentAttempt.FindAffinity(
            new("node/index-generation"))?.Value.Value?.String);
        Assert.Equal(new ProcessControlRevision("4"), continued.State.Revision);
    }

    [Fact]
    public void Restart_AbandonsTheCurrentAttemptAndStartsAnUnboundStableReplacement()
    {
        var fixture = ProcessControlTestFixture.Create();
        var bound = fixture.BindAffinity(fixture.State()).State;
        var command = fixture.Restart(bound, newAttemptId: "process-attempt/replacement");

        var restarted = fixture.Executor.Apply(
            bound,
            command,
            bound.UpdatedAtUtc.AddMinutes(1));

        Assert.Equal(ProcessControlDecisionDisposition.Applied, restarted.Disposition);
        Assert.Equal(ProcessControlMode.Running, restarted.State.Mode);
        Assert.Equal(bound.ProcessInstanceId, restarted.State.ProcessInstanceId);
        Assert.Equal(new ProcessAttemptId("process-attempt/replacement"), restarted.State.CurrentAttempt.AttemptId);
        Assert.Empty(restarted.State.CurrentAttempt.AffinityBindings);
        Assert.Equal(ProcessControlExecutionPhase.Ready, restarted.State.CurrentAttempt.Phase);
        var abandoned = restarted.State.Attempts[0];
        Assert.Equal(ProcessControlAttemptDisposition.Abandoned, abandoned.Disposition);
        Assert.Equal(ProcessControlExecutionPhase.Stopped, abandoned.Phase);
        Assert.Equal(bound.CurrentAttempt.AffinityBindings, abandoned.AffinityBindings);
        Assert.Equal(command.Context.CommandId, abandoned.Closure?.CommandId);
        Assert.Equal(command.Plan.NewAttemptId, restarted.State.CurrentAttempt.AttemptId);
        var intent = Assert.IsType<ProcessAttemptRestartIntent>(restarted.Intent);
        Assert.Equal(bound.CurrentAttempt.AttemptId, intent.AbandonedAttemptId);
        Assert.Equal(command.Plan.NewAttemptId, intent.ReplacementAttemptId);
    }

    [Fact]
    public void PauseDuringActivation_DefersUntilTheExactSafePoint()
    {
        var fixture = ProcessControlTestFixture.Create();
        var activation = fixture.BeginActivation(fixture.State()).State;
        var pause = fixture.Pause(activation);

        var deferred = fixture.Executor.Apply(
            activation,
            pause,
            activation.UpdatedAtUtc.AddMinutes(1));

        Assert.Equal(ProcessControlDecisionDisposition.DeferredToSafePoint, deferred.Disposition);
        Assert.Equal(ProcessControlMode.PauseRequested, deferred.State.Mode);
        Assert.Equal(pause.Context.CommandId, deferred.State.PendingCommandId);
        var intent = Assert.IsType<ProcessReachSafePointIntent>(deferred.Intent);
        Assert.Equal(ProcessControlPendingAction.Pause, intent.Action);

        var reached = fixture.ReachSafePoint(deferred.State);

        Assert.Equal(ProcessControlDecisionDisposition.SafePointReached, reached.Disposition);
        Assert.Equal(ProcessControlMode.Paused, reached.State.Mode);
        Assert.Null(reached.State.PendingCommandId);
        Assert.Equal(ProcessControlExecutionPhase.AtSafePoint, reached.State.CurrentAttempt.Phase);
        Assert.Equal(activation.CurrentAttempt.AttemptId, reached.State.CurrentAttempt.AttemptId);
        Assert.Equal(new ProcessSafePointId("safe-point/1"), reached.State.CurrentAttempt.LastSafePoint?.SafePointId);
    }

    [Fact]
    public void RestartDuringActivation_SelectsOneReplacementAndRealizesItAtTheSafePoint()
    {
        var fixture = ProcessControlTestFixture.Create();
        var activation = fixture.BeginActivation(fixture.State()).State;
        var restart = fixture.Restart(
            activation,
            newAttemptId: "process-attempt/stable-replacement");
        var deferred = fixture.Executor.Apply(
            activation,
            restart,
            activation.UpdatedAtUtc.AddMinutes(1));

        var reached = fixture.ReachSafePoint(deferred.State);
        var replay = fixture.Executor.Apply(
            reached.State,
            restart,
            reached.State.UpdatedAtUtc.AddMinutes(1));

        Assert.Equal(ProcessControlMode.RestartRequested, deferred.State.Mode);
        Assert.Equal(ProcessControlDecisionDisposition.SafePointReached, reached.Disposition);
        Assert.Equal(ProcessControlMode.Running, reached.State.Mode);
        Assert.Equal(new ProcessAttemptId("process-attempt/stable-replacement"), reached.State.CurrentAttempt.AttemptId);
        Assert.Equal(2, reached.State.Attempts.Length);
        Assert.IsType<ProcessAttemptRestartIntent>(reached.Intent);
        Assert.Equal(ProcessControlDecisionDisposition.Replayed, replay.Disposition);
        Assert.Same(reached.State, replay.State);
        Assert.Null(replay.Intent);
        Assert.Equal(2, replay.State.Attempts.Length);
    }

    [Fact]
    public void CancelDuringActivation_IsCooperativeWhileTerminateIsImmediate()
    {
        var fixture = ProcessControlTestFixture.Create();
        var cancellationActivation = fixture.BeginActivation(fixture.State()).State;
        var cancellation = fixture.Executor.Apply(
            cancellationActivation,
            fixture.Cancel(cancellationActivation),
            cancellationActivation.UpdatedAtUtc.AddMinutes(1));

        Assert.Equal(ProcessControlMode.CancellationRequested, cancellation.State.Mode);
        Assert.Equal(ProcessControlExecutionPhase.InActivation, cancellation.State.CurrentAttempt.Phase);
        Assert.IsType<ProcessReachSafePointIntent>(cancellation.Intent);

        var cancelled = fixture.ReachSafePoint(cancellation.State);

        Assert.Equal(ProcessControlMode.Cancelled, cancelled.State.Mode);
        Assert.True(cancelled.State.IsTerminal);
        Assert.Equal(ProcessControlAttemptDisposition.Cancelled, cancelled.State.CurrentAttempt.Disposition);
        Assert.Equal(ProcessControlExecutionPhase.Stopped, cancelled.State.CurrentAttempt.Phase);
        Assert.IsType<ProcessCancellationIntent>(cancelled.Intent);

        var terminationActivation = fixture.BeginActivation(fixture.State()).State;
        var terminated = fixture.Executor.Apply(
            terminationActivation,
            fixture.Terminate(terminationActivation),
            terminationActivation.UpdatedAtUtc.AddMinutes(1));

        Assert.Equal(ProcessControlDecisionDisposition.Applied, terminated.Disposition);
        Assert.Equal(ProcessControlMode.Terminated, terminated.State.Mode);
        Assert.Equal(ProcessControlAttemptDisposition.Terminated, terminated.State.CurrentAttempt.Disposition);
        Assert.Equal(ProcessControlExecutionPhase.Stopped, terminated.State.CurrentAttempt.Phase);
        Assert.Null(terminated.State.PendingCommandId);
        Assert.IsType<ProcessTerminationIntent>(terminated.Intent);
    }

    [Theory]
    [InlineData(ExecutionTerminalOutcomeKind.Cancelled)]
    [InlineData(ExecutionTerminalOutcomeKind.Failed)]
    public void AuthoredCancellation_RemainsCancellingUntilExactFinalizationEvidence(
        ExecutionTerminalOutcomeKind outcome)
    {
        var fixture = ProcessControlTestFixture.Create();
        var state = fixture.State();
        var command = fixture.Cancel(state, id: $"cancel/authored/{outcome}");
        var requested = fixture.Executor.Apply(
            state,
            command,
            state.UpdatedAtUtc.AddMinutes(1),
            ProcessCancellationCompletionPolicy.AuthoredFinalization);

        Assert.Equal(ProcessControlMode.Cancelling, requested.State.Mode);
        Assert.False(requested.State.IsTerminal);
        Assert.Equal(ProcessControlAttemptDisposition.Current, requested.State.CurrentAttempt.Disposition);
        Assert.Null(requested.State.CurrentAttempt.Closure);
        var intent = Assert.IsType<ProcessCancellationIntent>(requested.Intent);
        Assert.Equal(command.Context.CommandId, intent.CommandId);

        var observation = new ProcessCancellationFinalizationObservation(
            intent,
            outcome,
            requested.State.UpdatedAtUtc.AddMinutes(1));
        var finalized = fixture.Executor.CompleteCancellationFinalization(
            requested.State,
            observation);
        var replayed = fixture.Executor.CompleteCancellationFinalization(
            finalized.State,
            observation);

        Assert.Equal(ProcessControlDecisionDisposition.CancellationFinalized, finalized.Disposition);
        Assert.Equal(
            outcome == ExecutionTerminalOutcomeKind.Cancelled
                ? ProcessControlMode.Cancelled
                : ProcessControlMode.CancellationFailed,
            finalized.State.Mode);
        Assert.Equal(
            outcome == ExecutionTerminalOutcomeKind.Cancelled
                ? ProcessControlAttemptDisposition.Cancelled
                : ProcessControlAttemptDisposition.CancellationFailed,
            finalized.State.CurrentAttempt.Disposition);
        Assert.Equal(observation, finalized.State.CancellationFinalization);
        Assert.Equal(ProcessControlDecisionDisposition.Replayed, replayed.Disposition);
        Assert.Same(finalized.State, replayed.State);
    }

    [Fact]
    public void AuthoredCancellationDeferredDuringActivation_EntersCancellingAtTheExactSafePoint()
    {
        var fixture = ProcessControlTestFixture.Create();
        var active = fixture.BeginActivation(fixture.State()).State;
        var command = fixture.Cancel(active, id: "cancel/authored-deferred");
        var deferred = fixture.Executor.Apply(
            active,
            command,
            active.UpdatedAtUtc.AddMinutes(1),
            ProcessCancellationCompletionPolicy.AuthoredFinalization);
        var cutTime = deferred.State.UpdatedAtUtc.AddMinutes(1);
        var safePoint = new ProcessSafePointObservation(
            new("safe-point/authored-cancellation"),
            fixture.Expectation(deferred.State),
            deferred.State.CurrentAttempt.ActiveActivationId
                ?? throw new InvalidOperationException("Expected an active cancellation boundary."),
            new("node/authored-cancellation"),
            cutTime);

        var reached = fixture.Executor.ReachSafePoint(
            deferred.State,
            safePoint,
            ProcessCancellationCompletionPolicy.AuthoredFinalization);

        Assert.Equal(ProcessControlMode.CancellationRequested, deferred.State.Mode);
        Assert.Equal(ProcessControlDecisionDisposition.SafePointReached, reached.Disposition);
        Assert.Equal(ProcessControlMode.Cancelling, reached.State.Mode);
        Assert.Equal(ProcessControlExecutionPhase.AtSafePoint, reached.State.CurrentAttempt.Phase);
        Assert.Equal(ProcessControlAttemptDisposition.Current, reached.State.CurrentAttempt.Disposition);
        Assert.Null(reached.State.CurrentAttempt.Closure);
        Assert.Null(reached.State.PendingCommandId);
        var intent = Assert.IsType<ProcessCancellationIntent>(reached.Intent);
        Assert.Equal(command.Context.CommandId, intent.CommandId);

        var finalized = fixture.Executor.CompleteCancellationFinalization(
            reached.State,
            new(intent, ExecutionTerminalOutcomeKind.Cancelled, cutTime.AddMinutes(1)));
        Assert.Equal(ProcessControlMode.Cancelled, finalized.State.Mode);
        Assert.Equal(ProcessControlAttemptDisposition.Cancelled, finalized.State.CurrentAttempt.Disposition);
    }

    [Fact]
    public void AuthoredCancellation_RetainsOrdinaryActivationCutsBeforeFinalization()
    {
        var fixture = ProcessControlTestFixture.Create();
        var state = fixture.State();
        var command = fixture.Cancel(state, id: "cancel/authored-multiple-cuts");
        var requested = fixture.Executor.Apply(
            state,
            command,
            state.UpdatedAtUtc.AddMinutes(1),
            ProcessCancellationCompletionPolicy.AuthoredFinalization);
        var intent = Assert.IsType<ProcessCancellationIntent>(requested.Intent);

        var firstActivation = fixture.Executor.BeginActivation(
            requested.State,
            new(
                fixture.Expectation(requested.State),
                new("activation/cancellation/1"),
                requested.State.UpdatedAtUtc.AddMinutes(1)));
        var firstCut = fixture.Executor.ReachSafePoint(
            firstActivation.State,
            new(
                new("safe-point/cancellation/1"),
                fixture.Expectation(firstActivation.State),
                new("activation/cancellation/1"),
                new("node/cancellation/1"),
                firstActivation.State.UpdatedAtUtc.AddMinutes(1)),
            ProcessCancellationCompletionPolicy.AuthoredFinalization);
        var secondActivation = fixture.Executor.BeginActivation(
            firstCut.State,
            new(
                fixture.Expectation(firstCut.State),
                new("activation/cancellation/2"),
                firstCut.State.UpdatedAtUtc.AddMinutes(1)));
        var secondCut = fixture.Executor.ReachSafePoint(
            secondActivation.State,
            new(
                new("safe-point/cancellation/2"),
                fixture.Expectation(secondActivation.State),
                new("activation/cancellation/2"),
                new("node/cancellation/2"),
                secondActivation.State.UpdatedAtUtc.AddMinutes(1)),
            ProcessCancellationCompletionPolicy.AuthoredFinalization);

        var finalized = fixture.Executor.CompleteCancellationFinalization(
            secondCut.State,
            new(
                intent,
                ExecutionTerminalOutcomeKind.Cancelled,
                secondCut.State.UpdatedAtUtc.AddMinutes(1)));

        Assert.Equal(ProcessControlMode.Cancelling, firstCut.State.Mode);
        Assert.Equal(ProcessControlMode.Cancelling, secondCut.State.Mode);
        Assert.Equal(2, finalized.State.CurrentAttempt.SafePoints.Length);
        Assert.Equal(ProcessControlMode.Cancelled, finalized.State.Mode);
    }

    [Fact]
    public void DeferredCancellation_AllowsAPreCutNoOpAtTheSameTimestampAsTheSafePoint()
    {
        var fixture = ProcessControlTestFixture.Create();
        var activation = fixture.BeginActivation(fixture.State()).State;
        var deferred = fixture.Executor.Apply(
            activation,
            fixture.Cancel(activation, id: "cancel/deferred"),
            activation.UpdatedAtUtc.AddMinutes(1)).State;
        var cutTime = deferred.UpdatedAtUtc.AddMinutes(1);
        var alreadyRequested = fixture.Executor.Apply(
            deferred,
            fixture.Cancel(deferred, id: "cancel/already-requested"),
            cutTime);

        var cancelled = fixture.ReachSafePoint(
            alreadyRequested.State,
            observedAtUtc: cutTime);

        Assert.Equal(ProcessControlDecisionDisposition.AlreadyRequested, alreadyRequested.Disposition);
        Assert.Equal(ProcessControlMode.Cancelled, cancelled.State.Mode);
        Assert.Equal(cutTime, cancelled.State.CurrentAttempt.EndedAtUtc);
        Assert.Equal(2, cancelled.State.Receipts.Length);
    }

    [Fact]
    public void Terminate_PreemptsAPendingPauseAndClearsTheSafePointRequest()
    {
        var fixture = ProcessControlTestFixture.Create();
        var activation = fixture.BeginActivation(fixture.State()).State;
        var pausing = fixture.Executor.Apply(
            activation,
            fixture.Pause(activation),
            activation.UpdatedAtUtc.AddMinutes(1)).State;

        var terminated = fixture.Executor.Apply(
            pausing,
            fixture.Terminate(pausing),
            pausing.UpdatedAtUtc.AddMinutes(1));

        Assert.Equal(ProcessControlMode.Terminated, terminated.State.Mode);
        Assert.Null(terminated.State.PendingCommandId);
        Assert.Equal(ProcessControlExecutionPhase.Stopped, terminated.State.CurrentAttempt.Phase);
        Assert.Equal(2, terminated.State.Receipts.Length);
    }

    [Fact]
    public void IllegalCrossTerminalAndPendingActions_AreRejectedWithoutMutation()
    {
        var fixture = ProcessControlTestFixture.Create();
        var cancelled = fixture.Executor.Apply(
            fixture.State(),
            fixture.Cancel(fixture.State()),
            ProcessControlTestFixture.CreatedAtUtc.AddMinutes(1)).State;
        var terminateCancelled = fixture.Executor.Apply(
            cancelled,
            fixture.Terminate(cancelled),
            cancelled.UpdatedAtUtc.AddMinutes(1));

        Assert.Equal(ProcessControlDecisionDisposition.InvalidState, terminateCancelled.Disposition);
        Assert.Same(cancelled, terminateCancelled.State);
        Assert.Equal(ProcessControlDiagnosticCodes.InvalidState, Assert.Single(terminateCancelled.Diagnostics).Code);

        var activation = fixture.BeginActivation(fixture.State()).State;
        var restarting = fixture.Executor.Apply(
            activation,
            fixture.Restart(activation),
            activation.UpdatedAtUtc.AddMinutes(1)).State;
        var cancelRestart = fixture.Executor.Apply(
            restarting,
            fixture.Cancel(restarting),
            restarting.UpdatedAtUtc.AddMinutes(1));

        Assert.Equal(ProcessControlDecisionDisposition.InvalidState, cancelRestart.Disposition);
        Assert.Same(restarting, cancelRestart.State);
    }

    [Fact]
    public void CooperativeCancellation_DoesNotSupersedeAPendingPause()
    {
        var fixture = ProcessControlTestFixture.Create();
        var activation = fixture.BeginActivation(fixture.State()).State;
        var pause = fixture.Pause(activation, id: "pause/winning-request");
        var pausing = fixture.Executor.Apply(
            activation,
            pause,
            activation.UpdatedAtUtc.AddMinutes(1)).State;

        var cancellation = fixture.Executor.Apply(
            pausing,
            fixture.Cancel(pausing, id: "cancel/conflicting-request"),
            pausing.UpdatedAtUtc.AddMinutes(1));

        Assert.Equal(ProcessControlDecisionDisposition.InvalidState, cancellation.Disposition);
        Assert.Same(pausing, cancellation.State);
        Assert.Equal(ProcessControlMode.PauseRequested, cancellation.State.Mode);
        Assert.Equal(pause.Context.CommandId, cancellation.State.PendingCommandId);
        Assert.Null(cancellation.Receipt);
    }

    [Fact]
    public void AlreadySatisfiedLifecycleCommands_RecordTypedNoOpsWithoutAdvancingRevision()
    {
        var fixture = ProcessControlTestFixture.Create();
        var running = fixture.State();

        var continued = fixture.Executor.Apply(
            running,
            fixture.Continue(running),
            running.UpdatedAtUtc.AddMinutes(1));
        var paused = fixture.Executor.Apply(
            continued.State,
            fixture.Pause(continued.State),
            continued.State.UpdatedAtUtc.AddMinutes(1));
        var pausedAgain = fixture.Executor.Apply(
            paused.State,
            fixture.Pause(paused.State, id: "pause/2"),
            paused.State.UpdatedAtUtc.AddMinutes(1));

        Assert.Equal(ProcessControlDecisionDisposition.AlreadySatisfied, continued.Disposition);
        Assert.Equal(running.Revision, continued.State.Revision);
        Assert.Equal(ProcessControlReceiptDisposition.AlreadySatisfied, continued.Receipt?.Disposition);
        Assert.Equal(ProcessControlDecisionDisposition.AlreadySatisfied, pausedAgain.Disposition);
        Assert.Equal(paused.State.Revision, pausedAgain.State.Revision);
        Assert.Equal(3, pausedAgain.State.Receipts.Length);
    }

    [Fact]
    public void RepeatedPendingPauseAndCancelCommands_AreTypedAlreadyRequestedNoOps()
    {
        var fixture = ProcessControlTestFixture.Create();
        var pauseActivation = fixture.BeginActivation(fixture.State()).State;
        var firstPause = fixture.Pause(pauseActivation, id: "pause/requested/1");
        var pausing = fixture.Executor.Apply(
            pauseActivation,
            firstPause,
            pauseActivation.UpdatedAtUtc.AddMinutes(1)).State;

        var secondPause = fixture.Executor.Apply(
            pausing,
            fixture.Pause(pausing, id: "pause/requested/2"),
            pausing.UpdatedAtUtc.AddMinutes(1));

        Assert.Equal(ProcessControlDecisionDisposition.AlreadyRequested, secondPause.Disposition);
        Assert.Equal(ProcessControlReceiptDisposition.AlreadyRequested, secondPause.Receipt?.Disposition);
        Assert.Equal(pausing.Revision, secondPause.State.Revision);
        Assert.Equal(firstPause.Context.CommandId, secondPause.State.PendingCommandId);
        Assert.Null(secondPause.Intent);

        var cancelActivation = fixture.BeginActivation(fixture.State()).State;
        var firstCancel = fixture.Cancel(cancelActivation, id: "cancel/requested/1");
        var cancelling = fixture.Executor.Apply(
            cancelActivation,
            firstCancel,
            cancelActivation.UpdatedAtUtc.AddMinutes(1)).State;

        var secondCancel = fixture.Executor.Apply(
            cancelling,
            fixture.Cancel(cancelling, id: "cancel/requested/2"),
            cancelling.UpdatedAtUtc.AddMinutes(1));

        Assert.Equal(ProcessControlDecisionDisposition.AlreadyRequested, secondCancel.Disposition);
        Assert.Equal(ProcessControlReceiptDisposition.AlreadyRequested, secondCancel.Receipt?.Disposition);
        Assert.Equal(cancelling.Revision, secondCancel.State.Revision);
        Assert.Equal(firstCancel.Context.CommandId, secondCancel.State.PendingCommandId);
        Assert.Null(secondCancel.Intent);
    }

    [Theory]
    [InlineData("pause")]
    [InlineData("cancel")]
    [InlineData("restart")]
    public void DeferredCompletion_ReturnsTheOriginatingCommandReceipt(string action)
    {
        var fixture = ProcessControlTestFixture.Create();
        var activation = fixture.BeginActivation(fixture.State()).State;
        ProcessControlCommand command = action switch
        {
            "pause" => fixture.Pause(activation, id: "pause/deferred-receipt"),
            "cancel" => fixture.Cancel(activation, id: "cancel/deferred-receipt"),
            "restart" => fixture.Restart(activation, id: "restart/deferred-receipt"),
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown test action.")
        };
        var deferred = fixture.Executor.Apply(
            activation,
            command,
            activation.UpdatedAtUtc.AddMinutes(1));

        var completed = fixture.ReachSafePoint(deferred.State);

        Assert.Equal(ProcessControlDecisionDisposition.DeferredToSafePoint, deferred.Disposition);
        Assert.NotNull(deferred.Receipt);
        Assert.Equal(ProcessControlDecisionDisposition.SafePointReached, completed.Disposition);
        Assert.Same(deferred.Receipt, completed.Receipt);
        Assert.Equal(command.Context.CommandId, completed.Receipt?.Command.Context.CommandId);
    }
}
