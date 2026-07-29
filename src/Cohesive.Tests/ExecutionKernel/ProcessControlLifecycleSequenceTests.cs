using Cohesive.Execution;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class ProcessControlLifecycleSequenceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ImmediateRestartFromPausedSafeBoundary_PreservesPauseWithFreshReadyAttempt(
        bool beginAtSafePoint)
    {
        var fixture = ProcessControlTestFixture.Create();
        var state = RoundTrip(fixture, fixture.State());
        if (beginAtSafePoint)
        {
            state = RoundTrip(
                fixture,
                fixture.BeginActivation(
                    state,
                    activationId: "activation/paused-restart",
                    observedAtUtc: state.UpdatedAtUtc.AddMinutes(1)).State);
            state = RoundTrip(
                fixture,
                fixture.ReachSafePoint(
                    state,
                    safePointId: "safe-point/paused-restart",
                    observedAtUtc: state.UpdatedAtUtc.AddMinutes(1)).State);
        }

        var phaseBeforePause = beginAtSafePoint
            ? ProcessControlExecutionPhase.AtSafePoint
            : ProcessControlExecutionPhase.Ready;
        Assert.Equal(phaseBeforePause, state.CurrentAttempt.Phase);
        var paused = fixture.Executor.Apply(
            state,
            fixture.Pause(state, id: $"pause/restart/{beginAtSafePoint}"),
            state.UpdatedAtUtc.AddMinutes(1));
        state = RoundTrip(fixture, paused.State);
        var priorAttemptId = state.CurrentAttempt.AttemptId;

        var restarted = fixture.Executor.Apply(
            state,
            fixture.Restart(
                state,
                newAttemptId: $"process-attempt/paused-replacement/{beginAtSafePoint}",
                id: $"restart/paused/{beginAtSafePoint}"),
            state.UpdatedAtUtc.AddMinutes(1));
        var restored = RoundTrip(fixture, restarted.State);

        Assert.Equal(ProcessControlDecisionDisposition.Applied, restarted.Disposition);
        Assert.Equal(ProcessControlMode.Paused, restored.Mode);
        Assert.Equal(ProcessControlExecutionPhase.Ready, restored.CurrentAttempt.Phase);
        Assert.NotEqual(priorAttemptId, restored.CurrentAttempt.AttemptId);
        Assert.Empty(restored.CurrentAttempt.AffinityBindings);
        Assert.Equal(ProcessControlAttemptDisposition.Abandoned, restored.Attempts[^2].Disposition);
        Assert.Equal(priorAttemptId, restored.Attempts[^2].AttemptId);
    }

    [Fact]
    public void PendingPause_AllowsBufferedSignalTypedNoOpAndAffinityBeforeItsSafePoint()
    {
        var fixture = ProcessControlTestFixture.Create();
        var state = RoundTrip(fixture, fixture.State());
        state = RoundTrip(
            fixture,
            fixture.BeginActivation(
                state,
                activationId: "activation/pause-history",
                observedAtUtc: state.UpdatedAtUtc.AddMinutes(1)).State);

        var pause = fixture.Pause(state, id: "pause/history/original");
        var deferred = fixture.Executor.Apply(state, pause, state.UpdatedAtUtc.AddMinutes(1));
        state = RoundTrip(fixture, deferred.State);
        var signal = fixture.Executor.Apply(
            state,
            fixture.SignalCommand(
                state,
                id: "signal/pause-history",
                emissionId: "emission/pause-history"),
            state.UpdatedAtUtc.AddMinutes(1));
        state = RoundTrip(fixture, signal.State);
        var repeatedPause = fixture.Executor.Apply(
            state,
            fixture.Pause(state, id: "pause/history/repeated"),
            state.UpdatedAtUtc.AddMinutes(1));
        state = RoundTrip(fixture, repeatedPause.State);
        var affinity = fixture.BindAffinity(
            state,
            slot: "node/pause-history-affinity",
            value: "generation/pause-history",
            observedAtUtc: state.UpdatedAtUtc.AddMinutes(1));
        state = RoundTrip(fixture, affinity.State);

        var reached = fixture.ReachSafePoint(
            state,
            safePointId: "safe-point/pause-history",
            observedAtUtc: state.UpdatedAtUtc.AddMinutes(1));
        var restored = RoundTrip(fixture, reached.State);

        Assert.Equal(ProcessControlDecisionDisposition.DeferredToSafePoint, deferred.Disposition);
        Assert.Equal(ProcessControlDecisionDisposition.SignalBuffered, signal.Disposition);
        Assert.Equal(ProcessControlDecisionDisposition.AlreadyRequested, repeatedPause.Disposition);
        Assert.Equal(ProcessControlDecisionDisposition.AffinityBound, affinity.Disposition);
        Assert.Equal(ProcessControlDecisionDisposition.SafePointReached, reached.Disposition);
        Assert.Equal(ProcessControlMode.Paused, restored.Mode);
        Assert.Equal(ProcessControlExecutionPhase.AtSafePoint, restored.CurrentAttempt.Phase);
        Assert.Null(restored.PendingCommandId);
        Assert.Single(restored.SignalAdmissions);
        Assert.NotNull(restored.CurrentAttempt.FindAffinity(new("node/pause-history-affinity")));
        Assert.Equal(pause.Context.CommandId, reached.Receipt?.Command.Context.CommandId);
    }

    [Fact]
    public void PendingCancellation_AllowsTypedNoOpBeforeCooperativeCompletion()
    {
        var fixture = ProcessControlTestFixture.Create();
        var state = RoundTrip(fixture, fixture.State());
        state = RoundTrip(
            fixture,
            fixture.BeginActivation(
                state,
                activationId: "activation/cancel-history",
                observedAtUtc: state.UpdatedAtUtc.AddMinutes(1)).State);

        var cancel = fixture.Cancel(state, id: "cancel/history/original");
        var deferred = fixture.Executor.Apply(state, cancel, state.UpdatedAtUtc.AddMinutes(1));
        state = RoundTrip(fixture, deferred.State);
        var repeated = fixture.Executor.Apply(
            state,
            fixture.Cancel(state, id: "cancel/history/repeated"),
            state.UpdatedAtUtc.AddMinutes(1));
        state = RoundTrip(fixture, repeated.State);
        var completed = fixture.ReachSafePoint(
            state,
            safePointId: "safe-point/cancel-history",
            observedAtUtc: state.UpdatedAtUtc.AddMinutes(1));
        var restored = RoundTrip(fixture, completed.State);

        Assert.Equal(ProcessControlDecisionDisposition.AlreadyRequested, repeated.Disposition);
        Assert.Equal(ProcessControlMode.Cancelled, restored.Mode);
        Assert.True(restored.IsTerminal);
        Assert.Null(restored.PendingCommandId);
        Assert.Equal(ProcessControlAttemptDisposition.Cancelled, restored.CurrentAttempt.Disposition);
        Assert.Equal(cancel.Context.CommandId, completed.Receipt?.Command.Context.CommandId);
    }

    [Fact]
    public void PendingRestart_CanBePreemptedByImmediateTerminationWithoutCreatingItsReplacement()
    {
        var fixture = ProcessControlTestFixture.Create();
        var state = RoundTrip(fixture, fixture.State());
        state = RoundTrip(
            fixture,
            fixture.BeginActivation(
                state,
                activationId: "activation/restart-preemption",
                observedAtUtc: state.UpdatedAtUtc.AddMinutes(1)).State);
        var originalAttemptId = state.CurrentAttempt.AttemptId;
        var restart = fixture.Restart(
            state,
            newAttemptId: "process-attempt/preempted-replacement",
            id: "restart/preempted");
        var deferred = fixture.Executor.Apply(state, restart, state.UpdatedAtUtc.AddMinutes(1));
        state = RoundTrip(fixture, deferred.State);

        var terminated = fixture.Executor.Apply(
            state,
            fixture.Terminate(state, id: "terminate/restart-preemption"),
            state.UpdatedAtUtc.AddMinutes(1));
        var restored = RoundTrip(fixture, terminated.State);

        Assert.Equal(ProcessControlMode.Terminated, restored.Mode);
        Assert.Null(restored.PendingCommandId);
        Assert.Single(restored.Attempts);
        Assert.Equal(originalAttemptId, restored.CurrentAttempt.AttemptId);
        Assert.DoesNotContain(
            restored.Attempts,
            attempt => attempt.AttemptId == restart.Plan.NewAttemptId);
        Assert.Contains(
            restored.Receipts,
            receipt => receipt.Command.Context.CommandId == restart.Context.CommandId
                       && receipt.Disposition == ProcessControlReceiptDisposition.DeferredToSafePoint);
    }

    [Fact]
    public void RepeatedActivationsAndSafePoints_RetainAReplayableValidHistory()
    {
        var fixture = ProcessControlTestFixture.Create();
        var state = RoundTrip(fixture, fixture.State());
        const int cycleCount = 4;

        for (var index = 0; index < cycleCount; index++)
        {
            state = RoundTrip(
                fixture,
                fixture.BeginActivation(
                    state,
                    activationId: $"activation/cycle/{index}",
                    observedAtUtc: state.UpdatedAtUtc.AddMinutes(1)).State);
            state = RoundTrip(
                fixture,
                fixture.ReachSafePoint(
                    state,
                    safePointId: $"safe-point/cycle/{index}",
                    node: $"node/checkpoint/{index}",
                    observedAtUtc: state.UpdatedAtUtc.AddMinutes(1)).State);
        }

        Assert.Equal(ProcessControlMode.Running, state.Mode);
        Assert.Equal(ProcessControlExecutionPhase.AtSafePoint, state.CurrentAttempt.Phase);
        Assert.Equal(cycleCount, state.CurrentAttempt.SafePoints.Length);
        Assert.Equal(new ProcessControlRevision("9"), state.Revision);
        Assert.Empty(state.Receipts);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("terminate")]
    public void TerminalLifecycleCommand_RecordsAStableTypedNoOp(string action)
    {
        var fixture = ProcessControlTestFixture.Create();
        var state = RoundTrip(fixture, fixture.State());
        ProcessControlCommand firstCommand = action switch
        {
            "cancel" => fixture.Cancel(state, id: "cancel/terminal/original"),
            "terminate" => fixture.Terminate(state, id: "terminate/terminal/original"),
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown terminal action.")
        };
        var terminal = fixture.Executor.Apply(
            state,
            firstCommand,
            state.UpdatedAtUtc.AddMinutes(1));
        state = RoundTrip(fixture, terminal.State);
        var revisionAtTerminalCut = state.Revision;
        ProcessControlCommand repeatedCommand = action switch
        {
            "cancel" => fixture.Cancel(state, id: "cancel/terminal/repeated"),
            "terminate" => fixture.Terminate(state, id: "terminate/terminal/repeated"),
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown terminal action.")
        };

        var repeated = fixture.Executor.Apply(
            state,
            repeatedCommand,
            state.UpdatedAtUtc.AddMinutes(1));
        var restored = RoundTrip(fixture, repeated.State);

        Assert.Equal(ProcessControlDecisionDisposition.AlreadySatisfied, repeated.Disposition);
        Assert.Equal(ProcessControlReceiptDisposition.AlreadySatisfied, repeated.Receipt?.Disposition);
        Assert.Equal(revisionAtTerminalCut, restored.Revision);
        Assert.True(restored.IsTerminal);
        Assert.Equal(2, restored.Receipts.Length);
    }

    static ProcessControlState RoundTrip(
        ProcessControlTestFixture fixture,
        ProcessControlState state)
    {
        var restored = ProcessControlJsonSerializer.DeserializeState(
            ProcessControlJsonSerializer.Serialize(state),
            fixture.Catalog);
        Assert.Equal(state, restored);
        return restored;
    }
}
