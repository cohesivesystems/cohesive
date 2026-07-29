using System.Collections.Immutable;
using Cohesive.Execution;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class ProcessControlMalformedStateTests
{
    [Theory]
    [InlineData("restart")]
    [InlineData("cancel")]
    [InlineData("terminate")]
    public void AppliedClosingReceipt_RequiresItsExactLineageEffect(string commandKind)
    {
        var fixture = ProcessControlTestFixture.Create();
        var initial = fixture.State();
        var advanced = fixture.BindAffinity(initial).State;
        var recordedAtUtc = advanced.UpdatedAtUtc.AddMinutes(1);
        ProcessControlCommand original;
        ProcessControlCommand orphan;
        switch (commandKind)
        {
            case "restart":
                original = fixture.Restart(advanced, newAttemptId: "process-attempt/2", id: "restart/original");
                orphan = fixture.Restart(initial, newAttemptId: "process-attempt/2", id: "restart/orphan");
                break;
            case "cancel":
                original = fixture.Cancel(advanced, id: "cancel/original");
                orphan = fixture.Cancel(initial, id: "cancel/orphan");
                break;
            case "terminate":
                original = fixture.Terminate(advanced, id: "terminate/original");
                orphan = fixture.Terminate(initial, id: "terminate/orphan");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(commandKind), commandKind, "Unknown test command kind.");
        }

        var closed = fixture.Executor.Apply(advanced, original, recordedAtUtc).State;
        var orphanReceipt = new ProcessControlCommandReceipt(
            orphan,
            ProcessControlReceiptDisposition.Applied,
            recordedAtUtc);

        var exception = Assert.Throws<ArgumentException>(() => CopyState(
            closed,
            receipts: [orphanReceipt, .. closed.Receipts]));

        Assert.Equal("Receipts", exception.ParamName);
    }

    [Fact]
    public void DeferredReceipt_MustRemainPendingResolveOrBeExplicitlyPreempted()
    {
        var fixture = ProcessControlTestFixture.Create();
        var initial = fixture.State();
        var active = fixture.BeginActivation(initial).State;
        var deferred = fixture.Executor.Apply(
            active,
            fixture.Pause(active, id: "pause/pending"),
            active.UpdatedAtUtc.AddMinutes(1)).State;
        var orphan = new ProcessControlCommandReceipt(
            fixture.Pause(
                initial,
                id: "pause/orphan",
                expectation: fixture.Expectation(initial)),
            ProcessControlReceiptDisposition.DeferredToSafePoint,
            deferred.UpdatedAtUtc);

        var exception = Assert.Throws<ArgumentException>(() => CopyState(
            deferred,
            receipts: [orphan, .. deferred.Receipts]));

        Assert.Contains("neither pending, resolved, nor explicitly preempted", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PendingReceipt_CannotBelongToAnAbandonedAttempt()
    {
        var fixture = ProcessControlTestFixture.Create();
        var initial = fixture.State();
        var advanced = fixture.BindAffinity(initial).State;
        var restarted = fixture.Executor.Apply(
            advanced,
            fixture.Restart(advanced, newAttemptId: "process-attempt/2"),
            advanced.UpdatedAtUtc.AddMinutes(1)).State;
        var active = fixture.BeginActivation(restarted).State;
        var oldPause = fixture.Pause(
            initial,
            id: "pause/old-attempt",
            issuedAtUtc: initial.CreatedAtUtc,
            expectation: fixture.Expectation(initial));
        var oldReceipt = new ProcessControlCommandReceipt(
            oldPause,
            ProcessControlReceiptDisposition.DeferredToSafePoint,
            restarted.UpdatedAtUtc);

        var exception = Assert.Throws<ArgumentException>(() => CopyState(
            active,
            mode: ProcessControlMode.PauseRequested,
            pendingCommandId: oldPause.Context.CommandId,
            receipts: [oldReceipt, .. restarted.Receipts]));

        Assert.Contains("does not belong to the current active attempt", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PendingReceipt_CannotPrecedeTheCurrentActivation()
    {
        var fixture = ProcessControlTestFixture.Create();
        var initial = fixture.State();
        var active = fixture.BeginActivation(initial).State;
        var deferred = fixture.Executor.Apply(
            active,
            fixture.Pause(active, id: "pause/early"),
            active.UpdatedAtUtc.AddMinutes(1)).State;
        var earlyCommand = fixture.Pause(
            active,
            id: "pause/early",
            issuedAtUtc: initial.CreatedAtUtc,
            expectation: fixture.Expectation(active));
        var earlyReceipt = new ProcessControlCommandReceipt(
            earlyCommand,
            ProcessControlReceiptDisposition.DeferredToSafePoint,
            Assert.IsType<ProcessActivationStartObservation>(active.CurrentAttempt.ActiveActivation)
                .ObservedAtUtc.AddTicks(-1));

        var exception = Assert.Throws<ArgumentException>(() => CopyState(
            deferred,
            receipts: [earlyReceipt]));

        Assert.Contains("does not belong to the current active attempt", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void StateRevision_CannotSkipAControlEvidenceStep()
    {
        var fixture = ProcessControlTestFixture.Create();
        var atSafePoint = fixture.ReachSafePoint(
            fixture.BeginActivation(fixture.State()).State).State;

        var exception = Assert.Throws<ArgumentException>(() => CopyState(
            atSafePoint,
            revision: new("4")));

        Assert.Contains("not reachable from retained incrementing evidence", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SafePointObservation_CannotCarryTheCurrentStateRevision()
    {
        var fixture = ProcessControlTestFixture.Create();
        var atSafePoint = fixture.ReachSafePoint(
            fixture.BeginActivation(fixture.State()).State).State;
        var current = atSafePoint.CurrentAttempt;
        var retained = Assert.Single(current.SafePoints);
        var ahead = new ProcessControlSafePoint(
            retained.Activation,
            new(
                retained.SafePointId,
                new(
                    retained.Observation.Expectation.Continuation,
                    atSafePoint.Revision),
                retained.ActivationId,
                retained.Node,
                retained.ObservedAtUtc));
        var malformedAttempt = CopyAttempt(current, safePoints: [ahead]);

        var exception = Assert.Throws<ArgumentException>(() => CopyState(
            atSafePoint,
            attempts: [malformedAttempt]));

        Assert.Contains("duplicate or out-of-range revision step", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ActiveActivationFence_MustFollowThePrecedingSafePointFence()
    {
        var fixture = ProcessControlTestFixture.Create();
        var atSafePoint = fixture.ReachSafePoint(
            fixture.BeginActivation(fixture.State()).State).State;
        var active = fixture.BeginActivation(atSafePoint, activationId: "activation/2").State;
        var current = active.CurrentAttempt;
        var retained = Assert.Single(current.SafePoints);
        var observed = Assert.IsType<ProcessActivationStartObservation>(current.ActiveActivation);
        var regressed = new ProcessActivationStartObservation(
            retained.Observation.Expectation,
            observed.ActivationId,
            observed.ObservedAtUtc);

        var exception = Assert.Throws<ArgumentException>(() => new ProcessControlAttemptState(
            current.AttemptId,
            current.StartedAtUtc,
            current.Disposition,
            current.Phase,
            regressed,
            current.SafePoints,
            current.AffinityBindings));

        Assert.Contains("must follow the preceding safe-point fence", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SignalReceipt_TargetMustMatchItsExactReceiptedAttempt()
    {
        var fixture = ProcessControlTestFixture.Create();
        var initial = fixture.State();
        var restarted = fixture.Executor.Apply(
            initial,
            fixture.Restart(initial, newAttemptId: "process-attempt/2"),
            initial.UpdatedAtUtc.AddMinutes(1)).State;
        var accepted = fixture.Executor.Apply(
            restarted,
            fixture.SignalCommand(restarted, id: "signal/accepted"),
            restarted.UpdatedAtUtc.AddMinutes(1)).State;
        var acceptedReceipt = accepted.Receipts[^1];
        var wrongTargetReceipt = new ProcessControlCommandReceipt(
            fixture.SignalCommand(
                restarted,
                id: "signal/accepted",
                targetAttemptId: initial.CurrentAttempt.AttemptId),
            acceptedReceipt.Disposition,
            acceptedReceipt.RecordedAtUtc);

        var exception = Assert.Throws<ArgumentException>(() => CopyState(
            accepted,
            receipts: [.. accepted.Receipts[..^1], wrongTargetReceipt]));

        Assert.Contains("targets another Process attempt", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SignalReceipt_CannotPredateItsReceiptedAttempt()
    {
        var fixture = ProcessControlTestFixture.Create();
        var initial = fixture.State();
        var restarted = fixture.Executor.Apply(
            initial,
            fixture.Restart(initial, newAttemptId: "process-attempt/2"),
            initial.UpdatedAtUtc.AddMinutes(1)).State;
        var accepted = fixture.Executor.Apply(
            restarted,
            fixture.SignalCommand(restarted, id: "signal/accepted"),
            restarted.UpdatedAtUtc.AddMinutes(1)).State;
        var acceptedReceipt = accepted.Receipts[^1];
        var predatingReceipt = new ProcessControlCommandReceipt(
            fixture.SignalCommand(
                restarted,
                id: "signal/accepted",
                issuedAtUtc: initial.CreatedAtUtc),
            acceptedReceipt.Disposition,
            restarted.CurrentAttempt.StartedAtUtc.AddTicks(-1));

        var exception = Assert.Throws<ArgumentException>(() => CopyState(
            accepted,
            receipts: [.. accepted.Receipts[..^1], predatingReceipt]));

        Assert.Contains("predates its current attempt", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("restart")]
    public void SignalAcceptedReceipt_IsIllegalWhileAClosingActionIsPending(string pendingAction)
    {
        var fixture = ProcessControlTestFixture.Create();
        var active = fixture.BeginActivation(fixture.State()).State;
        ProcessControlCommand command = pendingAction switch
        {
            "cancel" => fixture.Cancel(active, id: "cancel/pending"),
            "restart" => fixture.Restart(
                active,
                newAttemptId: "process-attempt/2",
                id: "restart/pending"),
            _ => throw new ArgumentOutOfRangeException(nameof(pendingAction), pendingAction, "Unknown pending action.")
        };
        var pending = fixture.Executor.Apply(
            active,
            command,
            active.UpdatedAtUtc.AddMinutes(1)).State;
        var recordedAtUtc = pending.UpdatedAtUtc.AddMinutes(1);
        var malformedSignal = new ProcessControlCommandReceipt(
            fixture.SignalCommand(
                pending,
                id: $"signal/during-{pendingAction}",
                issuedAtUtc: recordedAtUtc),
            ProcessControlReceiptDisposition.SignalAccepted,
            recordedAtUtc);

        var exception = Assert.Throws<ArgumentException>(() => CopyState(
            pending,
            receipts: [.. pending.Receipts, malformedSignal],
            updatedAtUtc: recordedAtUtc));

        Assert.Contains("not legal in its retained lifecycle mode", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AppliedClosure_MustOccurAtItsReceiptCut()
    {
        var fixture = ProcessControlTestFixture.Create();
        var initial = fixture.State();
        var cancelled = fixture.Executor.Apply(
            initial,
            fixture.Cancel(initial),
            initial.UpdatedAtUtc.AddMinutes(1)).State;
        var current = cancelled.CurrentAttempt;
        var closure = Assert.IsType<ProcessAttemptClosure>(current.Closure);
        var malformedAttempt = CopyAttempt(
            current,
            closure: new(closure.CommandId, closure.OccurredAtUtc.AddTicks(1)));

        var exception = Assert.Throws<ArgumentException>(() => CopyState(
            cancelled,
            attempts: [malformedAttempt]));

        Assert.Contains("must close its attempt at the receipt cut", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DeferredClosure_MustOccurAtItsResolvingSafePoint()
    {
        var fixture = ProcessControlTestFixture.Create();
        var active = fixture.BeginActivation(fixture.State()).State;
        var deferred = fixture.Executor.Apply(
            active,
            fixture.Cancel(active),
            active.UpdatedAtUtc.AddMinutes(1)).State;
        var cancelled = fixture.ReachSafePoint(deferred).State;
        var current = cancelled.CurrentAttempt;
        var closure = Assert.IsType<ProcessAttemptClosure>(current.Closure);
        var malformedAttempt = CopyAttempt(
            current,
            closure: new(closure.CommandId, closure.OccurredAtUtc.AddTicks(1)));

        var exception = Assert.Throws<ArgumentException>(() => CopyState(
            cancelled,
            attempts: [malformedAttempt]));

        Assert.Contains("must close its attempt at the resolving safe point", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TerminationDuringActivation_RequiresItsInterruptedActivationEvidence()
    {
        var fixture = ProcessControlTestFixture.Create();
        var active = fixture.BeginActivation(fixture.State()).State;
        var terminated = fixture.Executor.Apply(
            active,
            fixture.Terminate(active),
            active.UpdatedAtUtc.AddMinutes(1)).State;
        var current = terminated.CurrentAttempt;
        var closure = Assert.IsType<ProcessAttemptClosure>(current.Closure);
        var malformedAttempt = CopyAttempt(
            current,
            closure: new(closure.CommandId, closure.OccurredAtUtc));

        var exception = Assert.Throws<ArgumentException>(() => CopyState(
            terminated,
            attempts: [malformedAttempt]));

        Assert.Contains("not reachable from retained incrementing evidence", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PostTerminalAffinityEvidence_IsRejectedByLifecycleHistory()
    {
        var fixture = ProcessControlTestFixture.Create();
        var initial = fixture.State();
        var cancelled = fixture.Executor.Apply(
            initial,
            fixture.Cancel(initial),
            initial.UpdatedAtUtc.AddMinutes(1)).State;
        var current = cancelled.CurrentAttempt;
        var extraEvidence = new ProcessAttemptAffinityObservation(
            new(
                new(cancelled.ProcessInstanceId, current.AttemptId),
                cancelled.Revision),
            ProcessControlTestFixture.Affinity(),
            cancelled.UpdatedAtUtc);
        var malformedAttempt = CopyAttempt(current, affinityBindings: [extraEvidence]);

        var exception = Assert.Throws<ArgumentException>(() => CopyState(
            cancelled,
            revision: new("3"),
            attempts: [malformedAttempt]));

        Assert.Contains("Affinity evidence is not legal at its retained lifecycle cut", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NonTerminalCommandReceipt_CannotFollowATerminalCut()
    {
        var fixture = ProcessControlTestFixture.Create();
        var initial = fixture.State();
        var cancelled = fixture.Executor.Apply(
            initial,
            fixture.Cancel(initial),
            initial.UpdatedAtUtc.AddMinutes(1)).State;
        var recordedAtUtc = cancelled.UpdatedAtUtc.AddMinutes(1);
        var postTerminal = new ProcessControlCommandReceipt(
            fixture.Pause(
                cancelled,
                id: "pause/after-terminal",
                issuedAtUtc: recordedAtUtc),
            ProcessControlReceiptDisposition.AlreadySatisfied,
            recordedAtUtc);

        var exception = Assert.Throws<ArgumentException>(() => CopyState(
            cancelled,
            receipts: [.. cancelled.Receipts, postTerminal],
            updatedAtUtc: recordedAtUtc));

        Assert.Contains("follows the lifetime of its current attempt", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AffinityBearingRestart_RequiresExplicitAffinityCleanup()
    {
        var fixture = ProcessControlTestFixture.Create();
        var bound = fixture.BindAffinity(fixture.State()).State;
        var restarted = fixture.Executor.Apply(
            bound,
            fixture.Restart(
                bound,
                newAttemptId: "process-attempt/2",
                id: "restart/with-affinity"),
            bound.UpdatedAtUtc.AddMinutes(1)).State;
        var receipt = Assert.Single(restarted.Receipts);
        var malformedReceipt = new ProcessControlCommandReceipt(
            fixture.Restart(
                bound,
                newAttemptId: "process-attempt/2",
                id: "restart/with-affinity",
                cleanup: ProcessAttemptCleanupRequirement.RetainEvidence),
            receipt.Disposition,
            receipt.RecordedAtUtc);

        var exception = Assert.Throws<ArgumentException>(() => CopyState(
            restarted,
            receipts: [malformedReceipt]));

        Assert.Contains("requires explicit affinity cleanup", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeferredRestart_CannotReuseEarlierLineageAttemptIdentity(bool preempted)
    {
        var fixture = ProcessControlTestFixture.Create();
        var initial = fixture.State();
        var earlierAttemptId = initial.CurrentAttempt.AttemptId;
        var restarted = fixture.Executor.Apply(
            initial,
            fixture.Restart(initial, newAttemptId: "process-attempt/current", id: "restart/seed"),
            initial.UpdatedAtUtc.AddMinutes(1)).State;
        var active = fixture.BeginActivation(restarted).State;
        var validRestart = fixture.Restart(
            active,
            newAttemptId: "process-attempt/future",
            id: "restart/deferred-lineage");
        var deferred = fixture.Executor.Apply(
            active,
            validRestart,
            active.UpdatedAtUtc.AddMinutes(1)).State;
        var retained = preempted
            ? fixture.Executor.Apply(
                deferred,
                fixture.Terminate(deferred, id: "terminate/deferred-lineage"),
                deferred.UpdatedAtUtc.AddMinutes(1)).State
            : deferred;
        var malformedCommand = fixture.Restart(
            active,
            newAttemptId: earlierAttemptId.Value,
            id: "restart/deferred-lineage");
        var malformedReceipt = new ProcessControlCommandReceipt(
            malformedCommand,
            ProcessControlReceiptDisposition.DeferredToSafePoint,
            deferred.Receipts[^1].RecordedAtUtc);
        var receiptIndex = preempted ? retained.Receipts.Length - 2 : retained.Receipts.Length - 1;

        var exception = Assert.Throws<ArgumentException>(() => CopyState(
            retained,
            receipts: retained.Receipts.SetItem(receiptIndex, malformedReceipt)));

        Assert.Contains("unrealized restart receipt", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeferredAffinityBearingRestart_RequiresCleanupEvenWhenPreempted(bool preempted)
    {
        var fixture = ProcessControlTestFixture.Create();
        var bound = fixture.BindAffinity(fixture.State()).State;
        var active = fixture.BeginActivation(bound).State;
        var validRestart = fixture.Restart(
            active,
            newAttemptId: "process-attempt/future",
            id: "restart/deferred-affinity");
        var deferred = fixture.Executor.Apply(
            active,
            validRestart,
            active.UpdatedAtUtc.AddMinutes(1)).State;
        var retained = preempted
            ? fixture.Executor.Apply(
                deferred,
                fixture.Terminate(deferred, id: "terminate/deferred-affinity"),
                deferred.UpdatedAtUtc.AddMinutes(1)).State
            : deferred;
        var malformedCommand = fixture.Restart(
            active,
            newAttemptId: validRestart.Plan.NewAttemptId.Value,
            id: "restart/deferred-affinity",
            cleanup: ProcessAttemptCleanupRequirement.RetainEvidence);
        var malformedReceipt = new ProcessControlCommandReceipt(
            malformedCommand,
            ProcessControlReceiptDisposition.DeferredToSafePoint,
            deferred.Receipts[^1].RecordedAtUtc);
        var receiptIndex = preempted ? retained.Receipts.Length - 2 : retained.Receipts.Length - 1;

        var exception = Assert.Throws<ArgumentException>(() => CopyState(
            retained,
            receipts: retained.Receipts.SetItem(receiptIndex, malformedReceipt)));

        Assert.Contains("requires explicit affinity cleanup", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadyAttempt_CannotRetainSafePointHistory()
    {
        var fixture = ProcessControlTestFixture.Create();
        var atSafePoint = fixture.ReachSafePoint(
            fixture.BeginActivation(fixture.State()).State).State;
        var current = atSafePoint.CurrentAttempt;

        var exception = Assert.Throws<ArgumentException>(() => new ProcessControlAttemptState(
            current.AttemptId,
            current.StartedAtUtc,
            current.Disposition,
            ProcessControlExecutionPhase.Ready,
            activeActivation: null,
            current.SafePoints,
            current.AffinityBindings));

        Assert.Contains("ready attempt cannot retain completed safe points", exception.Message, StringComparison.Ordinal);
    }

    static ProcessControlState CopyState(
        ProcessControlState source,
        ProcessControlRevision? revision = null,
        ProcessControlMode? mode = null,
        ImmutableArray<ProcessControlAttemptState> attempts = default,
        ProcessControlCommandId? pendingCommandId = null,
        ImmutableArray<ProcessControlCommandReceipt> receipts = default,
        DateTimeOffset? updatedAtUtc = null) =>
        new(
            source.SchemaVersion,
            source.Definition,
            source.AuthorityScope,
            source.ProcessInstanceId,
            revision ?? source.Revision,
            mode ?? source.Mode,
            attempts.IsDefault ? source.Attempts : attempts,
            pendingCommandId ?? source.PendingCommandId,
            receipts.IsDefault ? source.Receipts : receipts,
            source.CreatedAtUtc,
            updatedAtUtc ?? source.UpdatedAtUtc);

    static ProcessControlAttemptState CopyAttempt(
        ProcessControlAttemptState source,
        ImmutableArray<ProcessControlSafePoint> safePoints = default,
        ImmutableArray<ProcessAttemptAffinityObservation> affinityBindings = default,
        ProcessAttemptClosure? closure = null) =>
        new(
            source.AttemptId,
            source.StartedAtUtc,
            source.Disposition,
            source.Phase,
            source.ActiveActivation,
            safePoints.IsDefault ? source.SafePoints : safePoints,
            affinityBindings.IsDefault ? source.AffinityBindings : affinityBindings,
            closure ?? source.Closure);
}
