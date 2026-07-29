using Cohesive.Execution;
using Cohesive.Processes.Execution;
using Cohesive.Storage.Processes;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class InMemoryProcessDurableStoreCrashTests
{
    static readonly DateTimeOffset CheckpointedAtUtc = ProcessDurabilityTestFixture.CheckpointedAtUtc;

    static OperationContext Context { get; } = OperationContext.Create();

    [Fact]
    public async Task TerminalCheckpoint_StillDurablyAdmitsLateInputForPolicyClassification()
    {
        var fixture = ProcessDurabilityTestFixture.Create(
            definitionId: "process/durable-store/terminal-input",
            semanticVariant: "terminal-input");
        var terminal = CompositeCheckpoint(fixture, CheckpointedAtUtc.AddMinutes(2));
        Assert.NotEqual(ExecutionTerminalOutcomeKind.None, terminal.Continuation.Terminal.Kind);
        var store = new InMemoryProcessDurableStore();
        await InitializeAsync(store, terminal);
        var input = NewInput(fixture);

        var admitted = await store.AdmitInputAsync(
            Context,
            terminal.ContinuationIdentity.ProcessInstanceId,
            input,
            CheckpointedAtUtc.AddMinutes(3));

        Assert.Equal(ProcessStoreMutationDisposition.Applied, admitted.Disposition);
        var retained = Assert.Single(
            admitted.Snapshot!.Checkpoint.Inbox,
            entry => entry.EmissionId == input.Envelope.Context.EmissionId);
        Assert.Null(retained.Receipt);
    }

    [Theory]
    [InlineData(nameof(ProcessStoreMutationKind.Initialize))]
    [InlineData(nameof(ProcessStoreMutationKind.InboxAdmission))]
    [InlineData(nameof(ProcessStoreMutationKind.WorkerAcquisition))]
    [InlineData(nameof(ProcessStoreMutationKind.WorkerRenewal))]
    [InlineData(nameof(ProcessStoreMutationKind.AggregateCommit))]
    public async Task BeforeAtomicCommit_ExposesNoneAndExactRetryApplies(
        string mutationKindName)
    {
        var mutationKind = ParseMutationKind(mutationKindName);
        var scenario = await CreateScenarioAsync(
            mutationKind,
            ProcessStoreCrashPhase.BeforeAtomicCommit);

        var exception = await Assert.ThrowsAsync<ProcessStoreInjectedCrashException>(async () =>
        {
            _ = await scenario.Mutate();
        });
        var afterCrash = await scenario.Store.LoadAsync(Context, scenario.InstanceId);

        Assert.Equal(mutationKind, exception.Context.MutationKind);
        Assert.Equal(ProcessStoreCrashPhase.BeforeAtomicCommit, exception.Context.Phase);
        scenario.AssertBefore(afterCrash);

        var retry = await scenario.Mutate();

        Assert.Equal(ProcessStoreMutationDisposition.Applied, retry.Disposition);
        scenario.AssertAfter(retry.Snapshot);

        var replay = await scenario.Mutate();

        Assert.Equal(ProcessStoreMutationDisposition.Replayed, replay.Disposition);
        Assert.Equal(retry.Snapshot!.Revision, replay.Snapshot!.Revision);
        scenario.AssertAfter(replay.Snapshot);
    }

    [Theory]
    [InlineData(nameof(ProcessStoreMutationKind.Initialize))]
    [InlineData(nameof(ProcessStoreMutationKind.InboxAdmission))]
    [InlineData(nameof(ProcessStoreMutationKind.WorkerAcquisition))]
    [InlineData(nameof(ProcessStoreMutationKind.WorkerRenewal))]
    [InlineData(nameof(ProcessStoreMutationKind.AggregateCommit))]
    public async Task AfterAtomicCommitBeforeReturn_ExposesAllAndExactRetryReplays(
        string mutationKindName)
    {
        var mutationKind = ParseMutationKind(mutationKindName);
        var scenario = await CreateScenarioAsync(
            mutationKind,
            ProcessStoreCrashPhase.AfterAtomicCommitBeforeReturn);

        var exception = await Assert.ThrowsAsync<ProcessStoreInjectedCrashException>(async () =>
        {
            _ = await scenario.Mutate();
        });
        var committed = await scenario.Store.LoadAsync(Context, scenario.InstanceId);

        Assert.Equal(mutationKind, exception.Context.MutationKind);
        Assert.Equal(ProcessStoreCrashPhase.AfterAtomicCommitBeforeReturn, exception.Context.Phase);
        scenario.AssertAfter(committed);

        var retry = await scenario.Mutate();

        Assert.Equal(ProcessStoreMutationDisposition.Replayed, retry.Disposition);
        Assert.Equal(committed!.Revision, retry.Snapshot!.Revision);
        scenario.AssertAfter(retry.Snapshot);
    }

    static async Task<CrashScenario> CreateScenarioAsync(
        ProcessStoreMutationKind mutationKind,
        ProcessStoreCrashPhase crashPhase)
    {
        var crash = new CrashOnce(mutationKind, crashPhase);
        var store = new InMemoryProcessDurableStore(crash.ShouldCrash);
        var fixture = ProcessDurabilityTestFixture.Create(
            definitionId: $"process/durable-store-crash/{mutationKind}",
            semanticVariant: $"crash-{mutationKind}");
        var checkpoint = fixture.Checkpoint;
        var instanceId = checkpoint.ContinuationIdentity.ProcessInstanceId;

        switch (mutationKind)
        {
            case ProcessStoreMutationKind.Initialize:
                return new(
                    store,
                    instanceId,
                    () => store.InitializeAsync(Context, new("commit/crash/initialize"), checkpoint),
                    Assert.Null,
                    snapshot =>
                    {
                        var stored = Assert.IsType<ProcessDurableStoreSnapshot>(snapshot);
                        Assert.Equal(ProcessStorageRevision.Initial, stored.Revision);
                        Assert.Equal(checkpoint.UpdatedAtUtc, stored.Checkpoint.UpdatedAtUtc);
                        Assert.Null(stored.WorkerLease);
                    });

            case ProcessStoreMutationKind.InboxAdmission:
                {
                    await InitializeAsync(store, checkpoint);
                    var input = NewInput(fixture);
                    var admittedAtUtc = CheckpointedAtUtc.AddMinutes(1);
                    return new(
                        store,
                        instanceId,
                        () => store.AdmitInputAsync(Context, instanceId, input, admittedAtUtc),
                        snapshot =>
                        {
                            var stored = Assert.IsType<ProcessDurableStoreSnapshot>(snapshot);
                            Assert.Equal(ProcessStorageRevision.Initial, stored.Revision);
                            Assert.DoesNotContain(
                                stored.Checkpoint.Inbox,
                                entry => entry.EmissionId == input.Envelope.Context.EmissionId);
                        },
                        snapshot =>
                        {
                            var stored = Assert.IsType<ProcessDurableStoreSnapshot>(snapshot);
                            Assert.Equal(2, stored.Revision.Ordinal);
                            Assert.Equal(checkpoint.Inbox.Length + 1, stored.Checkpoint.Inbox.Length);
                            var entry = Assert.Single(
                                stored.Checkpoint.Inbox,
                                candidate => candidate.EmissionId == input.Envelope.Context.EmissionId);
                            Assert.Equal(input, entry.Input);
                            Assert.Equal(admittedAtUtc, entry.AdmittedAtUtc);
                        });
                }

            case ProcessStoreMutationKind.WorkerAcquisition:
                {
                    await InitializeAsync(store, checkpoint);
                    var acquiredAtUtc = CheckpointedAtUtc.AddMinutes(1);
                    return new(
                        store,
                        instanceId,
                        () => store.AcquireWorkerAsync(
                            Context,
                            instanceId,
                            "worker/crash",
                            TimeSpan.FromMinutes(5),
                            acquiredAtUtc),
                        snapshot =>
                        {
                            var stored = Assert.IsType<ProcessDurableStoreSnapshot>(snapshot);
                            Assert.Equal(ProcessStorageRevision.Initial, stored.Revision);
                            Assert.Null(stored.WorkerLease);
                        },
                        snapshot =>
                        {
                            var stored = Assert.IsType<ProcessDurableStoreSnapshot>(snapshot);
                            Assert.Equal(2, stored.Revision.Ordinal);
                            var lease = Assert.IsType<ProcessWorkerLease>(stored.WorkerLease);
                            Assert.Equal("worker/crash", lease.Owner);
                            Assert.Equal(acquiredAtUtc, lease.ClaimedAtUtc);
                            Assert.Equal(acquiredAtUtc.AddMinutes(5), lease.ExpiresAtUtc);
                        });
                }

            case ProcessStoreMutationKind.WorkerRenewal:
                {
                    await InitializeAsync(store, checkpoint);
                    var acquiredAtUtc = CheckpointedAtUtc.AddMinutes(1);
                    var acquired = await store.AcquireWorkerAsync(
                        Context,
                        instanceId,
                        "worker/crash",
                        TimeSpan.FromMinutes(5),
                        acquiredAtUtc);
                    var acquiredSnapshot = Assert.IsType<ProcessDurableStoreSnapshot>(acquired.Snapshot);
                    var originalLease = Assert.IsType<ProcessWorkerLease>(acquiredSnapshot.WorkerLease);
                    var renewedAtUtc = acquiredAtUtc.AddMinutes(2);
                    return new(
                        store,
                        instanceId,
                        () => store.RenewWorkerAsync(
                            Context,
                            instanceId,
                            "worker/crash",
                            originalLease.Fence,
                            TimeSpan.FromMinutes(10),
                            renewedAtUtc),
                        snapshot =>
                        {
                            var stored = Assert.IsType<ProcessDurableStoreSnapshot>(snapshot);
                            Assert.Equal(acquiredSnapshot.Revision, stored.Revision);
                            Assert.Equal(originalLease, stored.WorkerLease);
                        },
                        snapshot =>
                        {
                            var stored = Assert.IsType<ProcessDurableStoreSnapshot>(snapshot);
                            Assert.Equal(acquiredSnapshot.Revision.Ordinal + 1, stored.Revision.Ordinal);
                            var lease = Assert.IsType<ProcessWorkerLease>(stored.WorkerLease);
                            Assert.Equal(originalLease.Fence, lease.Fence);
                            Assert.Equal(originalLease.ClaimedAtUtc, lease.ClaimedAtUtc);
                            Assert.Equal(renewedAtUtc, lease.RenewedAtUtc);
                            Assert.Equal(renewedAtUtc.AddMinutes(10), lease.ExpiresAtUtc);
                        });
                }

            case ProcessStoreMutationKind.AggregateCommit:
                {
                    await InitializeAsync(store, checkpoint);
                    var acquired = await store.AcquireWorkerAsync(
                        Context,
                        instanceId,
                        "worker/crash",
                        TimeSpan.FromHours(1),
                        CheckpointedAtUtc.AddMinutes(1));
                    var acquiredSnapshot = Assert.IsType<ProcessDurableStoreSnapshot>(acquired.Snapshot);
                    var committedAtUtc = CheckpointedAtUtc.AddMinutes(2);
                    var replacement = CompositeCheckpoint(fixture, committedAtUtc);
                    var mutation = new ProcessLocalMutation(
                        "mutation/crash/composite",
                        "local/crash/composite",
                        ProcessDurabilityTestFixture.StringValue("committed"),
                        expectedVersion: 0);
                    var commit = new ProcessDurableCommit(
                        new("commit/crash/composite"),
                        acquiredSnapshot.Revision,
                        "worker/crash",
                        acquiredSnapshot.WorkerLease!.Fence,
                        replacement,
                        [mutation],
                        committedAtUtc);
                    var priorActivationCount = checkpoint.Continuation.CompletedActivationCount;
                    var priorWait = Assert.Single(checkpoint.Continuation.Waits);
                    var priorInbox = Assert.Single(checkpoint.Inbox);
                    var priorEmission = Assert.Single(checkpoint.Emissions);
                    var priorOperation = Assert.Single(checkpoint.DurableOperations);
                    return new(
                        store,
                        instanceId,
                        () => store.CommitAsync(Context, commit),
                        snapshot =>
                        {
                            var stored = Assert.IsType<ProcessDurableStoreSnapshot>(snapshot);
                            Assert.Equal(acquiredSnapshot.Revision, stored.Revision);
                            Assert.Equal(priorActivationCount, stored.Checkpoint.Continuation.CompletedActivationCount);
                            Assert.Equal(checkpoint.Activations.Length, stored.Checkpoint.Activations.Length);
                            Assert.Equal(checkpoint.UpdatedAtUtc, stored.Checkpoint.UpdatedAtUtc);
                            Assert.Equal(checkpoint.Control.Revision, stored.Checkpoint.Control.Revision);
                            Assert.True(Assert.Single(stored.Checkpoint.Continuation.Waits).Active);
                            Assert.Null(Assert.Single(stored.Checkpoint.Inbox).Receipt);
                            Assert.Empty(Assert.Single(stored.Checkpoint.Emissions).Attempts);
                            Assert.Empty(Assert.Single(stored.Checkpoint.DurableOperations).Attempts);
                            Assert.Single(stored.Checkpoint.Continuation.OutstandingRequests);
                            Assert.Empty(stored.LocalState);
                        },
                        snapshot =>
                        {
                            var stored = Assert.IsType<ProcessDurableStoreSnapshot>(snapshot);
                            Assert.Equal(acquiredSnapshot.Revision.Ordinal + 1, stored.Revision.Ordinal);
                            Assert.Equal(priorActivationCount + 1, stored.Checkpoint.Continuation.CompletedActivationCount);
                            Assert.Equal(checkpoint.Activations.Length + 1, stored.Checkpoint.Activations.Length);
                            Assert.Equal(committedAtUtc, stored.Checkpoint.UpdatedAtUtc);
                            Assert.True(
                                stored.Checkpoint.Control.Revision.CompareTo(checkpoint.Control.Revision) > 0);
                            var wait = Assert.Single(stored.Checkpoint.Continuation.Waits);
                            Assert.Equal(priorWait.RegistrationId, wait.RegistrationId);
                            Assert.False(wait.Active);
                            var inbox = Assert.Single(stored.Checkpoint.Inbox);
                            Assert.Equal(priorInbox.EmissionId, inbox.EmissionId);
                            Assert.Equal(ProcessInputAdmissionDisposition.Consumed, inbox.Receipt?.Disposition);
                            Assert.Equal(stored.Checkpoint.ContinuationIdentity, inbox.DispositionContinuation);
                            var emission = Assert.Single(stored.Checkpoint.Emissions);
                            Assert.Equal(priorEmission.EmissionId, emission.EmissionId);
                            Assert.Single(emission.Attempts);
                            var operation = Assert.Single(stored.Checkpoint.DurableOperations);
                            Assert.Equal(priorOperation.OperationId, operation.OperationId);
                            Assert.Single(operation.Attempts);
                            Assert.Empty(stored.Checkpoint.Continuation.OutstandingRequests);
                            Assert.Equal(checkpoint.Operations.Length, stored.Checkpoint.Operations.Length);
                            Assert.Equal(
                                ProcessStorageContentFingerprints.Value(checkpoint.Operations[0]),
                                ProcessStorageContentFingerprints.Value(stored.Checkpoint.Operations[0]));
                            var local = Assert.Single(stored.LocalState);
                            Assert.Equal(mutation.Identity, local.MutationIdentity);
                            Assert.Equal(mutation.Value, local.Value);
                            Assert.Equal(1, local.Version);
                        });
                }

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(mutationKind),
                    mutationKind,
                    "Unsupported Process-store crash scenario.");
        }
    }

    static ProcessStoreMutationKind ParseMutationKind(string value) =>
        Enum.Parse<ProcessStoreMutationKind>(value, ignoreCase: false);

    static async Task InitializeAsync(
        InMemoryProcessDurableStore store,
        ProcessDurableCheckpoint checkpoint)
    {
        var initialized = await store.InitializeAsync(
            Context,
            new("commit/crash/setup"),
            checkpoint);
        Assert.Equal(ProcessStoreMutationDisposition.Applied, initialized.Disposition);
    }

    static ProcessActivationInput NewInput(ProcessDurabilityTestFixture fixture)
    {
        var template = Assert.IsType<ReplyEnvelope>(fixture.PendingReply.Envelope);
        var envelope = new ReplyEnvelope(
            template.SchemaVersion,
            new(
                new("emission/reply/crash-input"),
                template.Context.Origin,
                template.Context.CorrelationId,
                template.InReplyTo,
                template.Context.AuthorityScope,
                new("idempotency/reply/crash-input"),
                template.Context.Ordering,
                template.Context.Delivery,
                template.Context.Provenance),
            template.Contract,
            template.InReplyTo,
            template.Outcome);
        return new(fixture.PendingReply.Target, envelope);
    }

    static ProcessDurableCheckpoint CompositeCheckpoint(
        ProcessDurabilityTestFixture fixture,
        DateTimeOffset committedAtUtc)
    {
        var current = fixture.Checkpoint.Continuation;
        var activation = new ProcessActivation(
            new("activation/crash/composite"),
            ProcessActivationCause.Interaction,
            committedAtUtc.AddMinutes(-1),
            fixture.Activation.Context,
            [fixture.PendingReply]);
        var decision = ProcessReferenceInterpreter.Activate(
            fixture.Plan,
            current,
            activation,
            RejectingHost.Instance);

        Assert.Equal(ProcessActivationDisposition.Completed, decision.Disposition);
        Assert.Equal(current.CompletedActivationCount + 1, decision.State.CompletedActivationCount);
        var activationReceipt = new ProcessActivationCommitReceipt(
            sequence: decision.State.CompletedActivationCount,
            decision.State.Continuation,
            ProcessStorageContentFingerprints.Continuation(current),
            ProcessStorageContentFingerprints.Continuation(decision.State),
            activation,
            decision.Disposition,
            decision.Evidence,
            committedAtUtc);

        var inputReceipt = Assert.Single(decision.State.InputReceipts, receipt =>
            receipt.Emission == fixture.PendingReply.Envelope.Context.EmissionId);
        Assert.Equal(ProcessInputAdmissionDisposition.Consumed, inputReceipt.Disposition);
        var inboxEntry = Assert.Single(fixture.Checkpoint.Inbox);
        var inbox = new ProcessDurableInboxEntry(
            inboxEntry.Input,
            inboxEntry.AdmittedAtUtc,
            inputReceipt,
            decision.State.Continuation);

        var contracts = Assert.IsType<InteractionContractCatalog>(
            fixture.Plan.ValidationContext.InteractionContracts);
        var operationExecutor = new DurableOperationReferenceExecutor(contracts);
        var claimed = operationExecutor.Claim(
            fixture.DurableOperation,
            new("operation-attempt/crash/composite"),
            "operation-worker/crash/composite",
            committedAtUtc.AddMinutes(-1));
        Assert.Equal(DurableOperationClaimDisposition.Claimed, claimed.Disposition);
        var claim = Assert.IsType<DurableOperationClaim>(claimed.Claim);
        var publicationAttempt = new DurableOperationAttempt(
            ordinal: 1,
            claim,
            DurableOperationAttemptStage.Claimed);
        var emissionEntry = Assert.Single(fixture.Checkpoint.Emissions);
        var emission = new ProcessEmissionRecord(
            emissionEntry.Envelope,
            emissionEntry.EnqueuedAtUtc,
            [publicationAttempt]);

        var controlExecutor = new ProcessControlReferenceExecutor(contracts);
        var begun = controlExecutor.BeginActivation(
            fixture.Control,
            new(
                new(
                    new(fixture.Control.ProcessInstanceId, fixture.Control.CurrentAttempt.AttemptId),
                    fixture.Control.Revision),
                activation.Id,
                activation.ObservedAtUtc));
        Assert.Equal(ProcessControlDecisionDisposition.ActivationStarted, begun.Disposition);
        var safePointNode = decision.Evidence.Trace[^1].Node;
        var safePoint = controlExecutor.ReachSafePoint(
            begun.State,
            new(
                new("safe-point/crash/composite"),
                new(
                    new(begun.State.ProcessInstanceId, begun.State.CurrentAttempt.AttemptId),
                    begun.State.Revision),
                activation.Id,
                safePointNode,
                committedAtUtc));
        Assert.Equal(ProcessControlDecisionDisposition.SafePointReached, safePoint.Disposition);

        return new(
            fixture.Checkpoint.SchemaVersion,
            fixture.Checkpoint.Start,
            decision.State,
            safePoint.State,
            [.. fixture.Checkpoint.Activations, activationReceipt],
            fixture.Checkpoint.Operations,
            [inbox],
            [emission],
            [claimed.State],
            fixture.Checkpoint.CreatedAtUtc,
            committedAtUtc);
    }

    sealed class RejectingHost : IProcessReferenceHost
    {
        internal static RejectingHost Instance { get; } = new();

        public ProcessOperationResult InvokeTransition(ProcessTransitionInvocation invocation) =>
            throw new InvalidOperationException($"Unexpected Transition invocation at '{invocation.Node.Value}'.");

        public ProcessOperationResult EvaluateRelation(ProcessRelationEvaluation evaluation) =>
            throw new InvalidOperationException($"Unexpected Relation evaluation at '{evaluation.Node.Value}'.");

        public ProcessSignalTargetResult ResolveSignalTarget(ProcessSignalTargetResolution resolution) =>
            throw new InvalidOperationException($"Unexpected Signal resolution at '{resolution.Node.Value}'.");
    }

    sealed record CrashScenario(
        InMemoryProcessDurableStore Store,
        ProcessInstanceId InstanceId,
        Func<Task<ProcessStoreMutationResult>> Mutate,
        Action<ProcessDurableStoreSnapshot?> AssertBefore,
        Action<ProcessDurableStoreSnapshot?> AssertAfter);

    sealed class CrashOnce(
        ProcessStoreMutationKind mutationKind,
        ProcessStoreCrashPhase crashPhase)
    {
        bool triggered;

        internal bool ShouldCrash(ProcessStoreCrashContext context)
        {
            if (triggered
                || context.MutationKind != mutationKind
                || context.Phase != crashPhase)
            {
                return false;
            }

            triggered = true;
            return true;
        }
    }
}
