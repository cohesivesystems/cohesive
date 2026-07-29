using Cohesive.Execution;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class ProcessControlReplayAndFencingTests
{
    [Fact]
    public void ExactCommandReplay_ReusesTheReceiptBeforeEvaluatingStaleExpectations()
    {
        var fixture = ProcessControlTestFixture.Create();
        var initial = fixture.State();
        var pause = fixture.Pause(initial);
        var first = fixture.Executor.Apply(
            initial,
            pause,
            initial.UpdatedAtUtc.AddMinutes(1));
        var continued = fixture.Executor.Apply(
            first.State,
            fixture.Continue(first.State),
            first.State.UpdatedAtUtc.AddMinutes(1));

        var replay = fixture.Executor.Apply(
            continued.State,
            pause,
            continued.State.UpdatedAtUtc.AddMinutes(1));

        Assert.Equal(ProcessControlDecisionDisposition.Replayed, replay.Disposition);
        Assert.Same(continued.State, replay.State);
        Assert.Same(first.Receipt, replay.Receipt);
        Assert.Null(replay.Intent);
        Assert.Empty(replay.Diagnostics);
        Assert.Equal(2, replay.State.Receipts.Length);
    }

    [Fact]
    public void EquivalentIdempotentCommand_ReusesThePriorLogicalDecision()
    {
        var fixture = ProcessControlTestFixture.Create();
        var initial = fixture.State();
        var original = fixture.Pause(
            initial,
            id: "pause/original",
            idempotencyKey: "idempotency/pause-logical");
        var retry = fixture.Pause(
            initial,
            id: "pause/retry",
            idempotencyKey: "idempotency/pause-logical");
        var first = fixture.Executor.Apply(
            initial,
            original,
            initial.UpdatedAtUtc.AddMinutes(1));

        var replay = fixture.Executor.Apply(
            first.State,
            retry,
            first.State.UpdatedAtUtc.AddMinutes(1));

        Assert.Equal(ProcessControlDecisionDisposition.Replayed, replay.Disposition);
        Assert.Same(first.State, replay.State);
        Assert.Equal(original.Context.CommandId, replay.Receipt?.Command.Context.CommandId);
        Assert.Null(first.State.FindReceipt(retry.Context.CommandId));
    }

    [Fact]
    public void ReusedCommandIdentityAndIdempotencyKey_WithDifferentIntentAreDiagnosed()
    {
        var fixture = ProcessControlTestFixture.Create();
        var initial = fixture.State();
        var pause = fixture.Pause(initial, id: "command/shared");
        var paused = fixture.Executor.Apply(
            initial,
            pause,
            initial.UpdatedAtUtc.AddMinutes(1)).State;

        var identityConflict = fixture.Executor.Apply(
            paused,
            fixture.Cancel(initial, id: "command/shared"),
            paused.UpdatedAtUtc.AddMinutes(1));
        var idempotencyConflict = fixture.Executor.Apply(
            paused,
            new ContinueProcessCommand(
                ProcessControlCommand.CurrentSchemaVersion,
                new(
                    new("continue/different-id"),
                    pause.Context.IdempotencyKey,
                    paused.ProcessInstanceId,
                    pause.Context.Authorization,
                    paused.UpdatedAtUtc,
                    pause.Context.Provenance),
                fixture.Expectation(paused)),
            paused.UpdatedAtUtc.AddMinutes(1));

        Assert.Equal(ProcessControlDecisionDisposition.IdentityConflict, identityConflict.Disposition);
        Assert.Same(paused, identityConflict.State);
        Assert.Equal(
            ProcessControlDiagnosticCodes.CommandIdentityConflict,
            Assert.Single(identityConflict.Diagnostics).Code);
        Assert.Equal(ProcessControlDecisionDisposition.IdempotencyConflict, idempotencyConflict.Disposition);
        Assert.Same(paused, idempotencyConflict.State);
        Assert.Equal(
            ProcessControlDiagnosticCodes.CommandIdempotencyConflict,
            Assert.Single(idempotencyConflict.Diagnostics).Code);
    }

    [Fact]
    public void StaleAttemptAndRevision_AreDistinctStructuredFences()
    {
        var fixture = ProcessControlTestFixture.Create();
        var initial = fixture.State();
        var oldAttemptPause = fixture.Pause(initial, id: "pause/stale-attempt");
        var restarted = fixture.Executor.Apply(
            initial,
            fixture.Restart(initial),
            initial.UpdatedAtUtc.AddMinutes(1)).State;

        var staleAttempt = fixture.Executor.Apply(
            restarted,
            oldAttemptPause,
            restarted.UpdatedAtUtc.AddMinutes(1));

        var current = fixture.State();
        var oldRevisionPause = fixture.Pause(current, id: "pause/stale-revision");
        var rebound = fixture.BindAffinity(current).State;
        var staleRevision = fixture.Executor.Apply(
            rebound,
            oldRevisionPause,
            rebound.UpdatedAtUtc.AddMinutes(1));

        Assert.Equal(ProcessControlDecisionDisposition.StaleAttempt, staleAttempt.Disposition);
        Assert.Equal(ProcessControlDiagnosticCodes.StaleAttempt, Assert.Single(staleAttempt.Diagnostics).Code);
        Assert.Equal(ProcessControlDecisionDisposition.StaleRevision, staleRevision.Disposition);
        Assert.Equal(ProcessControlDiagnosticCodes.StaleRevision, Assert.Single(staleRevision.Diagnostics).Code);
        Assert.Same(restarted, staleAttempt.State);
        Assert.Same(rebound, staleRevision.State);
    }

    [Fact]
    public void AffinityBinding_IsWriteOnceAndItsReplayWinsOverAStaleFence()
    {
        var fixture = ProcessControlTestFixture.Create();
        var initial = fixture.State();
        var expectation = fixture.Expectation(initial);
        var observedAt = initial.UpdatedAtUtc.AddMinutes(1);
        var affinity = ProcessControlTestFixture.Affinity();
        var observation = new ProcessAttemptAffinityObservation(expectation, affinity, observedAt);
        var first = fixture.Executor.BindAttemptAffinity(initial, observation);

        var replay = fixture.Executor.BindAttemptAffinity(first.State, observation);
        var conflict = fixture.Executor.BindAttemptAffinity(
            first.State,
            new(
                expectation,
                ProcessControlTestFixture.Affinity(value: "generation/other"),
                observedAt));

        Assert.Equal(ProcessControlDecisionDisposition.AffinityBound, first.Disposition);
        Assert.Equal(ProcessControlDecisionDisposition.Replayed, replay.Disposition);
        Assert.Same(first.State, replay.State);
        Assert.Equal(ProcessControlDecisionDisposition.AffinityConflict, conflict.Disposition);
        Assert.Equal(ProcessControlDiagnosticCodes.AffinityConflict, Assert.Single(conflict.Diagnostics).Code);
        Assert.Same(first.State, conflict.State);
    }

    [Fact]
    public void OldAttemptAffinityEvidence_CannotBindANewSlotAfterRestart()
    {
        var fixture = ProcessControlTestFixture.Create();
        var initial = fixture.State();
        var oldExpectation = fixture.Expectation(initial);
        var restarted = fixture.Executor.Apply(
            initial,
            fixture.Restart(initial),
            initial.UpdatedAtUtc.AddMinutes(1)).State;

        var stale = fixture.Executor.BindAttemptAffinity(
            restarted,
            new(
                oldExpectation,
                ProcessControlTestFixture.Affinity(slot: "node/new-slot"),
                restarted.UpdatedAtUtc.AddMinutes(1)));

        Assert.Equal(ProcessControlDecisionDisposition.StaleAttempt, stale.Disposition);
        Assert.Empty(restarted.CurrentAttempt.AffinityBindings);
        Assert.Same(restarted, stale.State);
    }

    [Fact]
    public void SafePointReplay_IsStableAndConflictingReuseIsRejected()
    {
        var fixture = ProcessControlTestFixture.Create();
        var activation = fixture.BeginActivation(fixture.State()).State;
        var observation = new ProcessSafePointObservation(
            new("safe-point/stable"),
            fixture.Expectation(activation),
            Assert.IsType<ActivationId>(activation.CurrentAttempt.ActiveActivationId),
            new("node/checkpoint"),
            activation.UpdatedAtUtc.AddMinutes(1));
        var reached = fixture.Executor.ReachSafePoint(activation, observation);

        var replay = fixture.Executor.ReachSafePoint(reached.State, observation);
        var conflict = fixture.Executor.ReachSafePoint(
            reached.State,
            new(
                observation.SafePointId,
                observation.Expectation,
                observation.ActivationId,
                new("node/different"),
                observation.ObservedAtUtc));

        Assert.Equal(ProcessControlDecisionDisposition.SafePointReached, reached.Disposition);
        Assert.Equal(ProcessControlDecisionDisposition.Replayed, replay.Disposition);
        Assert.Same(reached.State, replay.State);
        Assert.Equal(ProcessControlDecisionDisposition.InvalidCommand, conflict.Disposition);
        Assert.Equal(ProcessControlDiagnosticCodes.SafePointConflict, Assert.Single(conflict.Diagnostics).Code);
    }

    [Fact]
    public void ConcurrentActivationStart_IsFencedByTheSemanticRevision()
    {
        var fixture = ProcessControlTestFixture.Create();
        var initial = fixture.State();
        var staleExpectation = fixture.Expectation(initial);
        var bound = fixture.BindAffinity(initial).State;

        var stale = fixture.Executor.BeginActivation(
            bound,
            new(
                staleExpectation,
                new("activation/stale"),
                bound.UpdatedAtUtc.AddMinutes(1)));

        Assert.Equal(ProcessControlDecisionDisposition.StaleRevision, stale.Disposition);
        Assert.Equal(ProcessControlExecutionPhase.Ready, stale.State.CurrentAttempt.Phase);
        Assert.Same(bound, stale.State);
    }

    [Fact]
    public void OlderActivationAndSafePointEvidence_RemainsReplayableAndConflictDetectable()
    {
        var fixture = ProcessControlTestFixture.Create();
        var initial = fixture.State();
        var firstActivation = new ProcessActivationStartObservation(
            fixture.Expectation(initial),
            new("activation/older"),
            initial.UpdatedAtUtc.AddMinutes(1));
        var firstActive = fixture.Executor.BeginActivation(initial, firstActivation).State;
        var firstSafePoint = new ProcessSafePointObservation(
            new("safe-point/older"),
            fixture.Expectation(firstActive),
            firstActivation.ActivationId,
            new("node/older-checkpoint"),
            firstActive.UpdatedAtUtc.AddMinutes(1));
        var afterFirst = fixture.Executor.ReachSafePoint(firstActive, firstSafePoint).State;
        var secondActivation = new ProcessActivationStartObservation(
            fixture.Expectation(afterFirst),
            new("activation/later"),
            afterFirst.UpdatedAtUtc.AddMinutes(1));
        var secondActive = fixture.Executor.BeginActivation(afterFirst, secondActivation).State;
        var secondSafePoint = new ProcessSafePointObservation(
            new("safe-point/later"),
            fixture.Expectation(secondActive),
            secondActivation.ActivationId,
            new("node/later-checkpoint"),
            secondActive.UpdatedAtUtc.AddMinutes(1));
        var afterSecond = fixture.Executor.ReachSafePoint(secondActive, secondSafePoint).State;

        var activationReplay = fixture.Executor.BeginActivation(afterSecond, firstActivation);
        var activationConflict = fixture.Executor.BeginActivation(
            afterSecond,
            new(
                firstActivation.Expectation,
                firstActivation.ActivationId,
                firstActivation.ObservedAtUtc.AddSeconds(1)));
        var safePointReplay = fixture.Executor.ReachSafePoint(afterSecond, firstSafePoint);
        var safePointConflict = fixture.Executor.ReachSafePoint(
            afterSecond,
            new(
                firstSafePoint.SafePointId,
                firstSafePoint.Expectation,
                firstSafePoint.ActivationId,
                new("node/conflicting-checkpoint"),
                firstSafePoint.ObservedAtUtc));

        Assert.Equal(ProcessControlDecisionDisposition.Replayed, activationReplay.Disposition);
        Assert.Same(afterSecond, activationReplay.State);
        Assert.Equal(ProcessControlDecisionDisposition.InvalidCommand, activationConflict.Disposition);
        Assert.Equal(
            ProcessControlDiagnosticCodes.ActivationConflict,
            Assert.Single(activationConflict.Diagnostics).Code);
        Assert.Equal(ProcessControlDecisionDisposition.Replayed, safePointReplay.Disposition);
        Assert.Same(afterSecond, safePointReplay.State);
        Assert.Equal(ProcessControlDecisionDisposition.InvalidCommand, safePointConflict.Disposition);
        Assert.Equal(
            ProcessControlDiagnosticCodes.SafePointConflict,
            Assert.Single(safePointConflict.Diagnostics).Code);
        Assert.Equal(2, afterSecond.CurrentAttempt.SafePoints.Length);
    }
}
