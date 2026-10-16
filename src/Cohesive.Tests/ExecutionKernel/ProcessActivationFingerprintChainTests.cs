using Cohesive.Execution;
using Cohesive.Processes.Execution;
using Cohesive.Processes.IR;
using Cohesive.Storage.Processes;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class ProcessActivationFingerprintChainTests
{
    const string Worker = "worker/activation-fingerprint-chain";

    static OperationContext Context { get; } = OperationContext.Create();

    [Fact]
    public void Checkpoint_RejectsBrokenActivationContinuationFingerprintChain()
    {
        var fixture = ProcessDurabilityTestFixture.Create(
            definitionId: "process/durable-checkpoint/broken-activation-chain",
            semanticVariant: "broken-activation-chain");
        var current = fixture.Checkpoint.Continuation;
        var activation = new ProcessActivation(
            new("activation/second"),
            ProcessActivationCause.Interaction,
            fixture.Checkpoint.UpdatedAtUtc.AddMinutes(1),
            fixture.Activation.Context,
            [fixture.PendingReply]);
        var decision = ProcessReferenceInterpreter.Activate(
            fixture.Plan,
            current,
            activation,
            RejectingHost.Instance);
        Assert.Equal(ProcessActivationDisposition.Completed, decision.Disposition);

        var first = Assert.Single(fixture.Checkpoint.Activations);
        var wrongBefore = first.BeforeContinuation;
        Assert.NotEqual(first.AfterContinuation, wrongBefore);
        var second = new ProcessActivationCommitReceipt(
            sequence: 2,
            decision.State.Continuation,
            wrongBefore,
            ProcessStorageContentFingerprints.Continuation(decision.State),
            activation,
            decision.Disposition,
            decision.Evidence,
            fixture.Checkpoint.UpdatedAtUtc.AddMinutes(2), ProcessCheckpointVerification.Diagnostic);

        var exception = Assert.Throws<ArgumentException>(() => new ProcessDurableCheckpoint(
            fixture.Checkpoint.SchemaVersion,
            fixture.Checkpoint.Start,
            decision.State,
            fixture.Checkpoint.Control,
            [first, second],
            fixture.Checkpoint.Operations,
            fixture.Checkpoint.Inbox,
            fixture.Checkpoint.Emissions,
            fixture.Checkpoint.DurableOperations,
            fixture.Checkpoint.CreatedAtUtc,
            fixture.Checkpoint.UpdatedAtUtc.AddMinutes(2)));

        Assert.Contains("contiguous before/after continuation fingerprint chain", exception.Message);
    }

    [Fact]
    public void Checkpoint_RejectsFinalActivationFingerprintThatDoesNotPublishContinuation()
    {
        var fixture = ProcessDurabilityTestFixture.Create(
            definitionId: "process/durable-checkpoint/final-activation-fingerprint",
            semanticVariant: "final-activation-fingerprint");
        var original = Assert.Single(fixture.Checkpoint.Activations);
        var mismatched = new ProcessActivationCommitReceipt(
            original.Sequence,
            original.Continuation,
            original.BeforeContinuation,
            original.BeforeContinuation,
            original.Activation,
            original.Disposition,
            original.Evidence,
            original.CommittedAtUtc, ProcessCheckpointVerification.Diagnostic);
        Assert.NotEqual(
            mismatched.AfterContinuation,
            ProcessStorageContentFingerprints.Continuation(fixture.Checkpoint.Continuation));

        var exception = Assert.Throws<ArgumentException>(() =>
            ProcessDurabilityTestFixture.CopyCheckpoint(
                fixture.Checkpoint,
                activations: [mismatched]));

        Assert.Contains("final activation receipt must publish the exact checkpoint continuation", exception.Message);
    }

    [Fact]
    public void Checkpoint_RejectsFinalActivationDispositionThatContradictsContinuation()
    {
        var fixture = ProcessDurabilityTestFixture.Create(
            definitionId: "process/durable-checkpoint/final-activation-disposition",
            semanticVariant: "final-activation-disposition");
        var original = Assert.Single(fixture.Checkpoint.Activations);
        Assert.Equal(ExecutionTerminalOutcomeKind.None, fixture.Checkpoint.Continuation.Terminal.Kind);
        var mismatched = new ProcessActivationCommitReceipt(
            original.Sequence,
            original.Continuation,
            original.BeforeContinuation,
            original.AfterContinuation,
            original.Activation,
            ProcessActivationDisposition.Completed,
            original.Evidence,
            original.CommittedAtUtc, ProcessCheckpointVerification.Diagnostic);

        var exception = Assert.Throws<ArgumentException>(() =>
            ProcessDurabilityTestFixture.CopyCheckpoint(
                fixture.Checkpoint,
                activations: [mismatched]));

        Assert.Contains("final activation disposition must match", exception.Message);
    }

    [Theory]
    [InlineData(ProcessCheckpointVerification.Standard)]
    [InlineData(ProcessCheckpointVerification.Diagnostic)]
    public void Compatibility_RejectsProgressedReplacementAttemptThatDidNotBeginFromCleanContinuation(ProcessCheckpointVerification verification)
    {
        var fixture = ProcessDurabilityTestFixture.Create(
            definitionId: "process/durable-checkpoint/restart-fingerprint",
            semanticVariant: "restart-fingerprint",
            recoveryPolicy: ProcessRecoveryPolicy.RestartAttempt);
        var checkpoint = CreateReplacementAttemptCheckpoint(fixture, verification);

        var validation = ProcessCheckpointCompatibilityValidator.Validate(fixture.Plan, checkpoint);

        var diagnostic = Assert.Single(validation.Diagnostics, static candidate =>
            candidate.Code == ProcessCheckpointDiagnosticCodes.RestartAttemptIncompatible);
        Assert.Equal("/activations/1/beforeContinuation", diagnostic.Location);
    }

    [Fact]
    public void Compatibility_RejectsHistoricalAttemptWithMissingFirstReceipt()
    {
        var fixture = ProcessDurabilityTestFixture.Create(recoveryPolicy: ProcessRecoveryPolicy.RestartAttempt);
        var checkpoint = CreateReplacementAttemptCheckpoint(fixture, ProcessCheckpointVerification.Standard, progress: false);
        var originalValidation = ProcessCheckpointCompatibilityValidator.Validate(fixture.Plan, checkpoint);
        Assert.True(originalValidation.IsValid, string.Join("; ", originalValidation.Diagnostics));
        var missing = ProcessDurabilityTestFixture.CopyCheckpoint(checkpoint,
            activations: checkpoint.Activations.RemoveAt(0));

        var validation = ProcessCheckpointCompatibilityValidator.Validate(fixture.Plan, missing);

        Assert.Contains(validation.Diagnostics, diagnostic =>
            diagnostic.Code == ProcessCheckpointDiagnosticCodes.RestartAttemptIncompatible
            && diagnostic.Location == "/control/attempts/0");
    }

    [Fact]
    public void Compatibility_AdmitsRestartBeforeAnyCompletedActivation()
    {
        var fixture = ProcessDurabilityTestFixture.Create(recoveryPolicy: ProcessRecoveryPolicy.RestartAttempt);
        var initial = fixture.Start.CreateInitialState();
        var restartedAtUtc = fixture.Checkpoint.UpdatedAtUtc.AddMinutes(1);
        ProcessAttemptId replacementAttempt = new("process-attempt/unstarted-replacement");
        var executor = new ProcessControlReferenceExecutor(
            Assert.IsType<InteractionContractCatalog>(fixture.Plan.ValidationContext.InteractionContracts));
        var restarted = executor.Apply(initial,
            new RestartProcessAttemptCommand(ProcessControlCommand.CurrentSchemaVersion,
                new(new("control/restart-unstarted"), new("idempotency/restart-unstarted"),
                    initial.ProcessInstanceId, fixture.Start.Request.Context.Authorization,
                    restartedAtUtc, fixture.Start.Request.Context.Provenance),
                Expectation(initial),
                new(replacementAttempt, ProcessAttemptCleanupRequirement.RetainEvidence, new("tests.unstarted"))),
            restartedAtUtc);
        Assert.Equal(ProcessControlDecisionDisposition.Applied, restarted.Disposition);
        var clean = ProcessReferenceInterpreter.Create(fixture.Plan,
            new(initial.ProcessInstanceId, replacementAttempt), fixture.Start.Request.Input!);
        var checkpoint = new ProcessDurableCheckpoint(fixture.Checkpoint.SchemaVersion, fixture.Start,
            clean, restarted.State, createdAtUtc: fixture.Start.AcceptedAtUtc, updatedAtUtc: restartedAtUtc);

        Assert.Empty(checkpoint.Activations);
        Assert.True(ProcessCheckpointCompatibilityValidator.Validate(fixture.Plan, checkpoint).IsValid);
    }

    [Fact]
    public async Task Commit_RejectsFirstAppendedActivationThatDidNotConsumeStoredContinuation()
    {
        var fixture = ProcessDurabilityTestFixture.Create(
            definitionId: "process/durable-store/activation-fingerprint-successor",
            semanticVariant: "activation-fingerprint-successor");
        var initialContinuation = ProcessReferenceInterpreter.Create(fixture.Plan, fixture.Start);
        var initial = new ProcessDurableCheckpoint(
            fixture.Checkpoint.SchemaVersion,
            fixture.Start,
            initialContinuation,
            fixture.Start.CreateInitialState(),
            createdAtUtc: fixture.Start.AcceptedAtUtc,
            updatedAtUtc: fixture.Start.AcceptedAtUtc);
        var store = new InMemoryProcessDurableStore();
        var initialized = await store.InitializeAsync(
            Context,
            new("commit/initialize/activation-fingerprint-successor"),
            initial);
        Assert.Equal(ProcessStoreMutationDisposition.Applied, initialized.Disposition);
        var acquired = await store.AcquireWorkerAsync(
            Context,
            initial.ContinuationIdentity.ProcessInstanceId,
            initialized.Snapshot!.Revision,
            Worker,
            TimeSpan.FromHours(1),
            fixture.Start.AcceptedAtUtc.AddSeconds(1));
        Assert.Equal(ProcessStoreMutationDisposition.Applied, acquired.Disposition);
        var snapshot = Assert.IsType<ProcessDurableStoreSnapshot>(acquired.Snapshot);

        var original = Assert.Single(fixture.Checkpoint.Activations);
        var wrongBefore = ProcessStorageContentFingerprints.Continuation(fixture.Checkpoint.Continuation);
        Assert.NotEqual(
            ProcessStorageContentFingerprints.Continuation(initialContinuation),
            wrongBefore);
        var incompatibleReceipt = new ProcessActivationCommitReceipt(
            original.Sequence,
            original.Continuation,
            wrongBefore,
            original.AfterContinuation,
            original.Activation,
            original.Disposition,
            original.Evidence,
            original.CommittedAtUtc, ProcessCheckpointVerification.Diagnostic);
        var incompatible = ProcessDurabilityTestFixture.CopyCheckpoint(
            fixture.Checkpoint,
            activations: [incompatibleReceipt]);
        var commit = new ProcessDurableCommit(
            new("commit/activation-fingerprint-successor"),
            snapshot.Revision,
            Worker,
            snapshot.WorkerLease!.Fence,
            incompatible,
            [],
            fixture.Checkpoint.UpdatedAtUtc);

        var result = await ProcessDurabilityTestFixture.CommitAtEvidenceTimeAsync(store, commit);

        Assert.Equal(ProcessStoreMutationDisposition.IdentityConflict, result.Disposition);
        Assert.Equal(snapshot.Revision, result.Snapshot!.Revision);
        Assert.Equal(
            ProcessStorageContentFingerprints.Continuation(initialContinuation),
            ProcessStorageContentFingerprints.Continuation(result.Snapshot.Checkpoint.Continuation));
    }

    static ProcessDurableCheckpoint CreateReplacementAttemptCheckpoint(
        ProcessDurabilityTestFixture fixture, ProcessCheckpointVerification verification, bool progress = true)
    {
        var controlExecutor = new ProcessControlReferenceExecutor(
            Assert.IsType<InteractionContractCatalog>(
                fixture.Plan.ValidationContext.InteractionContracts));
        ProcessAttemptId replacementAttempt = new("process-attempt/2");
        var restartedAtUtc = fixture.Checkpoint.UpdatedAtUtc.AddMinutes(1);
        var restart = controlExecutor.Apply(
            fixture.Control,
            new RestartProcessAttemptCommand(
                ProcessControlCommand.CurrentSchemaVersion,
                new(
                    new("control/restart-fingerprint"),
                    new("idempotency/control/restart-fingerprint"),
                    fixture.Checkpoint.ContinuationIdentity.ProcessInstanceId,
                    fixture.Start.Request.Context.Authorization,
                    restartedAtUtc,
                    fixture.Start.Request.Context.Provenance),
                Expectation(fixture.Control),
                new(
                    replacementAttempt,
                    ProcessAttemptCleanupRequirement.RetainEvidence,
                    new("tests.activation-fingerprint-chain"))),
            restartedAtUtc);
        Assert.Equal(ProcessControlDecisionDisposition.Applied, restart.Disposition);
        var clean = ProcessReferenceInterpreter.RestartAttempt(
            fixture.Plan,
            fixture.Checkpoint.Continuation,
            replacementAttempt);
        if (!progress)
        {
            return new(fixture.Checkpoint.SchemaVersion, fixture.Start, clean, restart.State,
                fixture.Checkpoint.Activations, fixture.Checkpoint.Operations, fixture.Checkpoint.Inbox,
                fixture.Checkpoint.Emissions, fixture.Checkpoint.DurableOperations,
                fixture.Checkpoint.CreatedAtUtc, restartedAtUtc);
        }
        var activatedAtUtc = restartedAtUtc.AddMinutes(1);
        var activation = new ProcessActivation(
            new("activation/replacement"),
            ProcessActivationCause.Start,
            activatedAtUtc,
            fixture.Activation.Context);
        var decision = ProcessReferenceInterpreter.Activate(
            fixture.Plan,
            clean,
            activation,
            PassthroughRelationHost.Instance);
        Assert.Equal(ProcessActivationDisposition.DurableCut, decision.Disposition);

        var begun = controlExecutor.BeginActivation(
            restart.State,
            new(
                Expectation(restart.State),
                activation.Id,
                activatedAtUtc));
        Assert.Equal(ProcessControlDecisionDisposition.ActivationStarted, begun.Disposition);
        var committedAtUtc = activatedAtUtc.AddMinutes(1);
        var safePoint = controlExecutor.ReachSafePoint(
            begun.State,
            new(
                new("safe-point/replacement-fingerprint"),
                Expectation(begun.State),
                activation.Id,
                Assert.IsType<ExecutionNodeId>(decision.Evidence.SafePointNode),
                committedAtUtc));
        Assert.Equal(ProcessControlDecisionDisposition.SafePointReached, safePoint.Disposition);

        Assert.NotEqual(
            ProcessStorageContentFingerprints.Continuation(clean),
            ProcessStorageContentFingerprints.Continuation(fixture.Checkpoint.Continuation));
        var receipt = new ProcessActivationCommitReceipt(
            sequence: 1,
            decision.State.Continuation,
            ProcessStorageContentFingerprints.Continuation(fixture.Checkpoint.Continuation),
            verification == ProcessCheckpointVerification.Diagnostic ? ProcessStorageContentFingerprints.Continuation(decision.State) : null,
            activation,
            decision.Disposition,
            decision.Evidence,
            committedAtUtc,
            verification);
        return new(
            fixture.Checkpoint.SchemaVersion,
            fixture.Checkpoint.Start,
            decision.State,
            safePoint.State,
            [.. fixture.Checkpoint.Activations, receipt],
            fixture.Checkpoint.Operations,
            fixture.Checkpoint.Inbox,
            fixture.Checkpoint.Emissions,
            fixture.Checkpoint.DurableOperations,
            fixture.Checkpoint.CreatedAtUtc,
            committedAtUtc);
    }

    static ProcessControlExpectation Expectation(ProcessControlState state) =>
        new(
            new(state.ProcessInstanceId, state.CurrentAttempt.AttemptId),
            state.Revision);

    sealed class PassthroughRelationHost : IProcessReferenceHost
    {
        internal static PassthroughRelationHost Instance { get; } = new();

        public ProcessOperationResult InvokeTransition(ProcessTransitionInvocation invocation) =>
            throw new InvalidOperationException($"Unexpected Transition invocation at '{invocation.Node.Value}'.");

        public ProcessOperationResult EvaluateRelation(ProcessRelationEvaluation evaluation) =>
            ProcessOperationResult.Completed(evaluation.Input);

        public ProcessSignalTargetResult ResolveSignalTarget(ProcessSignalTargetResolution resolution) =>
            throw new InvalidOperationException($"Unexpected Signal resolution at '{resolution.Node.Value}'.");
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
}
