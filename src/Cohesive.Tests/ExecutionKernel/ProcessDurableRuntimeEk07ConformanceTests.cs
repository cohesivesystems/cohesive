using Cohesive.Execution;
using Cohesive.Processes.Compilation;
using Cohesive.Processes.Execution;
using Cohesive.Processes.IR;
using Cohesive.Storage.Processes;

namespace Cohesive.Tests.ExecutionKernel;

public sealed partial class ProcessDurableRuntimeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ek07_SignalTimerWinnersAreOrderIndependentAcrossBufferingRestoreAndClosedWaitPolicies(
        bool timerWins)
    {
        var controls = ProcessControlTestFixture.Create();
        var raceAtUtc = ProcessDurabilityTestFixture.ActivatedAtUtc.AddMinutes(10);
        var (plan, start) = SignalRaceProcess(controls, raceAtUtc, timerWins);
        List<ProcessActivationDecision> arbitrationDecisions = [];
        List<ProcessActivationDecision> closedWaitDecisions = [];
        List<ProcessDurableCheckpoint> finalCheckpoints = [];

        foreach (var reversePresentationOrder in new[] { false, true })
        {
            var scenario = reversePresentationOrder ? "reverse" : "forward";
            var host = new RecordingHost(
                ProcessOperationResult.Completed(ProcessDurabilityTestFixture.StringValue("unused")));
            var store = new InMemoryProcessDurableStore();
            var runtime = Runtime(store, host);
            var initialized = await runtime.InitializeAsync(
                Context(ProcessDurabilityTestFixture.AcceptedAtUtc),
                plan,
                start);
            var checkpoint = Assert.IsType<ProcessDurableStoreSnapshot>(initialized.Snapshot).Checkpoint;
            var activationContext = SignalActivationContext();
            var registered = await ActivateAndCompareEk07Async(
                store,
                runtime,
                plan,
                host,
                checkpoint.ContinuationIdentity.ProcessInstanceId,
                new(
                    new("activation/ek07/register-gate"),
                    ProcessActivationCause.Start,
                    ProcessDurabilityTestFixture.ActivatedAtUtc,
                    activationContext));
            checkpoint = registered.Checkpoint;
            var token = Assert.Single(checkpoint.Continuation.Tokens);
            var unscopedTarget = new ProcessTokenInteractionTarget(
                checkpoint.ContinuationIdentity,
                token.Id);
            var alpha = new ProcessActivationInput(
                unscopedTarget,
                Signal(plan, controls, unscopedTarget, activationContext, "ek07/alpha"));
            var zeta = new ProcessActivationInput(
                unscopedTarget,
                Signal(plan, controls, unscopedTarget, activationContext, "ek07/zeta"));
            ProcessActivationInput[] candidates = reversePresentationOrder
                ? [zeta, alpha]
                : [alpha, zeta];
            var admittedAtUtc = ProcessDurabilityTestFixture.ActivatedAtUtc.AddMinutes(1);
            foreach (var candidate in candidates)
            {
                var admission = await store.AdmitInputAsync(
                    Context(admittedAtUtc),
                    checkpoint.ContinuationIdentity.ProcessInstanceId,
                    candidate,
                    admittedAtUtc);
                Assert.Equal(ProcessStoreMutationDisposition.Applied, admission.Disposition);
                checkpoint = Assert.IsType<ProcessDurableStoreSnapshot>(admission.Snapshot).Checkpoint;
            }

            var buffered = await ActivateAndCompareEk07Async(
                store,
                runtime,
                plan,
                host,
                checkpoint.ContinuationIdentity.ProcessInstanceId,
                new(
                    new("activation/ek07/buffer-inputs"),
                    ProcessActivationCause.Interaction,
                    admittedAtUtc.AddMinutes(1),
                    activationContext,
                    [.. candidates]));
            checkpoint = buffered.Checkpoint;
            Assert.All(
                buffered.Decision.InputAdmissions,
                static receipt =>
                {
                    Assert.Equal(ProcessInputAdmissionDisposition.Buffered, receipt.Disposition);
                    Assert.Equal(ProcessInputAdmissionReason.Early, receipt.Reason);
                    Assert.Null(receipt.WaitRegistrationId);
                });
            Assert.Equal(2, checkpoint.Continuation.BufferedInputs.Length);
            Assert.All(
                checkpoint.Inbox,
                static entry =>
                {
                    var receipt = Assert.IsType<ProcessInputReceipt>(entry.Receipt);
                    Assert.Equal(ProcessInputAdmissionDisposition.Buffered, receipt.Disposition);
                    Assert.Equal(ProcessInputAdmissionReason.Early, receipt.Reason);
                });

            (store, checkpoint) = await RestoreEk07Async(
                plan,
                checkpoint,
                $"commit/ek07/{scenario}/restore-buffered",
                admittedAtUtc.AddMinutes(2));
            runtime = Runtime(store, host);
            var arbitrated = await ActivateAndCompareEk07Async(
                store,
                runtime,
                plan,
                host,
                checkpoint.ContinuationIdentity.ProcessInstanceId,
                new(
                    new("activation/ek07/arbitrate"),
                    ProcessActivationCause.Timer,
                    raceAtUtc,
                    activationContext));
            checkpoint = arbitrated.Checkpoint;
            arbitrationDecisions.Add(arbitrated.Decision);

            var raceWait = Assert.Single(
                checkpoint.Continuation.Waits,
                static wait => wait.Node.Value == "await-race");
            Assert.False(raceWait.Active);
            Assert.Equal(timerWins ? "clause/timer" : "clause/signal", raceWait.WinnerClause?.Value);
            Assert.Equal(timerWins ? null : alpha.Envelope.Context.EmissionId, raceWait.WinnerInput);
            AssertReceipt(
                arbitrated.Decision.InputAdmissions,
                alpha.Envelope.Context.EmissionId,
                timerWins
                    ? ProcessInputAdmissionDisposition.Observed
                    : ProcessInputAdmissionDisposition.Consumed,
                timerWins
                    ? ProcessInputAdmissionReason.Superseded
                    : ProcessInputAdmissionReason.Consumed,
                raceWait.RegistrationId);
            AssertReceipt(
                arbitrated.Decision.InputAdmissions,
                zeta.Envelope.Context.EmissionId,
                ProcessInputAdmissionDisposition.Observed,
                ProcessInputAdmissionReason.Superseded,
                raceWait.RegistrationId);
            Assert.Empty(arbitrated.Decision.Diagnostics);
            Assert.Contains(
                arbitrated.Decision.Evidence.Trace,
                trace => trace.Kind == ProcessTraceEventKind.WaitResolved
                         && trace.Node.Value == "await-race"
                         && trace.BranchOrClause?.Value == (timerWins ? "clause/timer" : "clause/signal")
                         && trace.Emission == (timerWins ? null : alpha.Envelope.Context.EmissionId));

            (store, checkpoint) = await RestoreEk07Async(
                plan,
                checkpoint,
                $"commit/ek07/{scenario}/restore-arbitrated",
                raceAtUtc.AddMinutes(1));
            runtime = Runtime(store, host);
            raceWait = Assert.Single(
                checkpoint.Continuation.Waits,
                static wait => wait.Node.Value == "await-race");
            var lateTarget = new ProcessTokenInteractionTarget(
                checkpoint.ContinuationIdentity,
                raceWait.Token,
                raceWait.RegistrationId);
            var staleContinuation = new ProcessContinuationIdentity(
                checkpoint.ContinuationIdentity.ProcessInstanceId,
                new("process-attempt/stale"));
            var staleTarget = new ProcessTokenInteractionTarget(
                staleContinuation,
                raceWait.Token,
                raceWait.RegistrationId);
            var late = new ProcessActivationInput(
                lateTarget,
                Signal(plan, controls, lateTarget, activationContext, "ek07/late"));
            var stale = new ProcessActivationInput(
                staleTarget,
                Signal(plan, controls, staleTarget, activationContext, "ek07/stale"));
            var missing = new ProcessActivationInput(
                lateTarget,
                Signal(
                    plan,
                    controls,
                    lateTarget,
                    activationContext,
                    "ek07/missing",
                    controls.AlternateSignalContract));
            ProcessActivationInput[] closedWaitInputs = reversePresentationOrder
                ? [missing, stale, late, alpha]
                : [alpha, late, stale, missing];
            var closedWaitAtUtc = raceAtUtc.AddMinutes(2);
            foreach (var candidate in closedWaitInputs.Where(candidate => candidate != alpha))
            {
                var admission = await store.AdmitInputAsync(
                    Context(closedWaitAtUtc),
                    checkpoint.ContinuationIdentity.ProcessInstanceId,
                    candidate,
                    closedWaitAtUtc);
                Assert.Equal(ProcessStoreMutationDisposition.Applied, admission.Disposition);
                checkpoint = Assert.IsType<ProcessDurableStoreSnapshot>(admission.Snapshot).Checkpoint;
            }

            var closedWait = await ActivateAndCompareEk07Async(
                store,
                runtime,
                plan,
                host,
                checkpoint.ContinuationIdentity.ProcessInstanceId,
                new(
                    new("activation/ek07/closed-wait-policies"),
                    ProcessActivationCause.Interaction,
                    closedWaitAtUtc.AddMinutes(1),
                    activationContext,
                    [.. closedWaitInputs]));
            checkpoint = closedWait.Checkpoint;
            closedWaitDecisions.Add(closedWait.Decision);
            finalCheckpoints.Add(checkpoint);

            AssertReceipt(
                closedWait.Decision.InputAdmissions,
                alpha.Envelope.Context.EmissionId,
                timerWins
                    ? ProcessInputAdmissionDisposition.Observed
                    : ProcessInputAdmissionDisposition.Consumed,
                ProcessInputAdmissionReason.Duplicate,
                raceWait.RegistrationId);
            AssertReceipt(
                closedWait.Decision.InputAdmissions,
                late.Envelope.Context.EmissionId,
                ProcessInputAdmissionDisposition.Observed,
                ProcessInputAdmissionReason.Late,
                raceWait.RegistrationId);
            AssertReceipt(
                closedWait.Decision.InputAdmissions,
                stale.Envelope.Context.EmissionId,
                ProcessInputAdmissionDisposition.Rejected,
                ProcessInputAdmissionReason.Stale,
                raceWait.RegistrationId);
            AssertReceipt(
                closedWait.Decision.InputAdmissions,
                missing.Envelope.Context.EmissionId,
                ProcessInputAdmissionDisposition.DeadLettered,
                ProcessInputAdmissionReason.MissingTarget,
                raceWait.RegistrationId);
            Assert.Contains(
                closedWait.Decision.Diagnostics,
                static diagnostic => diagnostic.Code == ProcessExecutionDiagnosticCodes.InputNotAdmitted);
            Assert.All(
                closedWait.Decision.InputAdmissions,
                receipt => Assert.Contains(
                    closedWait.Decision.Evidence.Trace,
                    trace => trace.Kind == ProcessTraceEventKind.InputAdmitted
                             && trace.Emission == receipt.Emission
                             && trace.InputDisposition == receipt.Disposition
                             && trace.InputReason == receipt.Reason
                             && trace.WaitRegistrationId == receipt.WaitRegistrationId));
            var retainedRaceWait = Assert.Single(
                checkpoint.Continuation.Waits,
                static wait => wait.Node.Value == "await-race");
            Assert.Equal(raceWait, retainedRaceWait);
            Assert.Equal(
                timerWins
                    ? ProcessInputAdmissionReason.Superseded
                    : ProcessInputAdmissionReason.Consumed,
                Assert.Single(
                    checkpoint.Continuation.InputReceipts,
                    receipt => receipt.Emission == alpha.Envelope.Context.EmissionId).Reason);
            Assert.Equal(
                Assert.Single(
                    checkpoint.Continuation.InputReceipts,
                    receipt => receipt.Emission == alpha.Envelope.Context.EmissionId),
                Assert.IsType<ProcessInputReceipt>(Assert.Single(
                    checkpoint.Inbox,
                    entry => entry.EmissionId == alpha.Envelope.Context.EmissionId).Receipt));
            Assert.Single(
                checkpoint.Activations.SelectMany(static activation => activation.Evidence.Trace),
                trace => trace.Kind == ProcessTraceEventKind.WaitResolved
                         && trace.Node.Value == "await-race");

            var alphaSignal = Assert.IsType<SignalEnvelope>(alpha.Envelope);
            var conflictingAlpha = new ProcessActivationInput(
                alpha.Target,
                new SignalEnvelope(
                    alphaSignal.SchemaVersion,
                    alphaSignal.Context,
                    alphaSignal.Contract,
                    ProcessDurabilityTestFixture.StringValue("conflicting-reuse"),
                    alphaSignal.Target));
            var beforeConflict = Assert.IsType<ProcessDurableStoreSnapshot>(await store.LoadAsync(
                Context(closedWaitAtUtc.AddMinutes(2)),
                checkpoint.ContinuationIdentity.ProcessInstanceId));
            var conflict = await store.AdmitInputAsync(
                Context(closedWaitAtUtc.AddMinutes(2)),
                checkpoint.ContinuationIdentity.ProcessInstanceId,
                conflictingAlpha,
                closedWaitAtUtc.AddMinutes(2));
            Assert.Equal(ProcessStoreMutationDisposition.IdentityConflict, conflict.Disposition);
            Assert.Equal(beforeConflict.Revision, conflict.Snapshot?.Revision);
            Assert.Equal(
                ProcessStorageContentFingerprints.Continuation(beforeConflict.Checkpoint.Continuation),
                ProcessStorageContentFingerprints.Continuation(
                    Assert.IsType<ProcessDurableStoreSnapshot>(conflict.Snapshot).Checkpoint.Continuation));
            Assert.Equal(0, host.RelationCalls);
        }

        Assert.Equivalent(arbitrationDecisions[0], arbitrationDecisions[1], strict: true);
        Assert.Equivalent(closedWaitDecisions[0], closedWaitDecisions[1], strict: true);
        Assert.Equivalent(
            finalCheckpoints[0].Continuation,
            finalCheckpoints[1].Continuation,
            strict: true);
        Assert.Equivalent(
            finalCheckpoints[0].Inbox.OrderBy(static entry => entry.EmissionId.Value),
            finalCheckpoints[1].Inbox.OrderBy(static entry => entry.EmissionId.Value),
            strict: true);
    }

    static (CompiledProcessPlan Plan, ProcessStartReceipt Start) SignalRaceProcess(
        ProcessControlTestFixture controls,
        DateTimeOffset raceAtUtc,
        bool timerWins)
    {
        var definition = new Cohesive.Processes.IR.ProcessDefinition(
            ProcessDurabilityTestFixture.StringContract,
            ProcessDurabilityTestFixture.StringContract,
            new("gate"),
            [
                new TimerProcessNode(
                    new("gate"),
                    Expr.Const(ObservationValue.FromDateTimeOffset(raceAtUtc)),
                    new(new("edge/gate-await"), new("await-race"))),
                new AwaitMatchProcessNode(
                    new("await-race"),
                    ProcessAwaitArbitration.ExclusivePriorityThenClauseId,
                    [
                        new ProcessAwaitInteractionClause(
                            new("clause/signal"),
                            controls.SignalContract,
                            new(
                                new("await.signal"),
                                ProcessDurabilityTestFixture.StringContract),
                            requestObligation: null,
                            guard: null,
                            priority: timerWins ? 0 : 10,
                            new(new(new("edge/signal-settle"), new("settle")))),
                        new ProcessAwaitTimerClause(
                            new("clause/timer"),
                            Expr.Const(ObservationValue.FromDateTimeOffset(raceAtUtc)),
                            priority: timerWins ? 10 : 0,
                            new(new(new("edge/timer-settle"), new("settle"))))
                    ],
                    ProcessAwaitInputDisposition.Observe,
                    ProcessAwaitInputDisposition.Reject,
                    ProcessAwaitInputDisposition.ReusePriorDisposition,
                    ProcessAwaitMissingTargetDisposition.DeadLetter,
                    TimeSpan.FromDays(7)),
                new TimerProcessNode(
                    new("settle"),
                    Expr.Const(ObservationValue.FromDateTimeOffset(raceAtUtc.AddDays(1))),
                    new(new("edge/settle-return"), new("return"))),
                new ReturnProcessNode(new("return"), Expr.Const("settled"))
            ],
            ProcessRecoveryPolicy.ContinueAttempt);
        return CompileSignalProcess(
            controls,
            timerWins ? "ek07-timer-wins" : "ek07-signal-wins",
            definition);
    }

    static async Task<(ProcessActivationDecision Decision, ProcessDurableCheckpoint Checkpoint)>
        ActivateAndCompareEk07Async(
            InMemoryProcessDurableStore store,
            ProcessDurableRuntime runtime,
            CompiledProcessPlan plan,
            IProcessReferenceHost host,
            ProcessInstanceId instanceId,
            ProcessActivation activation)
    {
        var before = Assert.IsType<ProcessDurableStoreSnapshot>(await store.LoadAsync(
            Context(activation.ObservedAtUtc),
            instanceId));
        var expected = ProcessReferenceInterpreter.Activate(
            plan,
            before.Checkpoint.Continuation,
            activation,
            host);
        var result = await runtime.ActivateAsync(
            Context(activation.ObservedAtUtc),
            plan,
            before.Checkpoint.ContinuationIdentity,
            activation);
        var actual = Assert.IsType<ProcessActivationDecision>(result.Decision);
        var checkpoint = Assert.IsType<ProcessDurableStoreSnapshot>(result.Snapshot).Checkpoint;

        Assert.Equal(ProcessDurableRuntimeDisposition.Applied, result.Disposition);
        Assert.Equivalent(expected, actual, strict: true);
        Assert.Equivalent(expected.State, checkpoint.Continuation, strict: true);
        return (actual, checkpoint);
    }

    static async Task<(InMemoryProcessDurableStore Store, ProcessDurableCheckpoint Checkpoint)> RestoreEk07Async(
        CompiledProcessPlan plan,
        ProcessDurableCheckpoint checkpoint,
        string commitId,
        DateTimeOffset restoredAtUtc)
    {
        var json = ProcessDurableCheckpointJsonSerializer.Serialize(checkpoint);
        var restored = ProcessDurableCheckpointJsonSerializer.Deserialize(json, plan);
        Assert.Equal(json, ProcessDurableCheckpointJsonSerializer.Serialize(restored));
        var store = new InMemoryProcessDurableStore();
        var initialized = await store.InitializeAsync(
            Context(restoredAtUtc),
            new(commitId),
            restored);
        Assert.Equal(ProcessStoreMutationDisposition.Applied, initialized.Disposition);
        return (
            store,
            Assert.IsType<ProcessDurableStoreSnapshot>(initialized.Snapshot).Checkpoint);
    }

    static void AssertReceipt(
        IEnumerable<ProcessInputReceipt> receipts,
        EmissionId emission,
        ProcessInputAdmissionDisposition disposition,
        ProcessInputAdmissionReason reason,
        ProcessWaitRegistrationId waitRegistrationId)
    {
        var receipt = Assert.Single(receipts, candidate => candidate.Emission == emission);
        Assert.Equal(disposition, receipt.Disposition);
        Assert.Equal(reason, receipt.Reason);
        Assert.Equal(waitRegistrationId, receipt.WaitRegistrationId);
    }
}
