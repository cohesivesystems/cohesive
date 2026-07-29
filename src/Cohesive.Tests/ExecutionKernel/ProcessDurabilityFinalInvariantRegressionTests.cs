using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Execution;
using Cohesive.Processes.IR;
using Cohesive.Storage.Processes;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class ProcessDurabilityFinalInvariantRegressionTests
{
    const string Worker = "worker/final-invariants";

    static readonly DateTimeOffset CheckpointedAtUtc =
        ProcessDurabilityTestFixture.CheckpointedAtUtc;

    static OperationContext Context { get; } = OperationContext.Create();

    [Theory]
    [InlineData("safe-point")]
    [InlineData("affinity")]
    [InlineData("activation")]
    public async Task LaterControlRevision_CannotRewritePriorAttemptHistory(string history)
    {
        var scenario = await CreateRestartedScenarioAsync(history);
        var executor = ControlExecutor(scenario.Fixture);
        var activation = new ProcessActivation(
            new("activation/replacement"),
            ProcessActivationCause.Recovery,
            CheckpointedAtUtc.AddMinutes(3),
            scenario.Fixture.Activation.Context);
        var begun = executor.BeginActivation(
            scenario.Snapshot.Checkpoint.Control,
            new(
                Expectation(scenario.Snapshot.Checkpoint.Control),
                activation.Id,
                activation.ObservedAtUtc));
        Assert.Equal(ProcessControlDecisionDisposition.ActivationStarted, begun.Disposition);

        var control = history switch
        {
            "safe-point" => RewritePriorAttempt(
                begun.State,
                RewriteSafePoint(begun.State.Attempts[0])),
            "affinity" => RewritePriorAttempt(
                begun.State,
                RewriteAffinity(begun.State.Attempts[0])),
            "activation" => begun.State,
            _ => throw new ArgumentOutOfRangeException(nameof(history), history, "Unknown history kind.")
        };
        var activations = history == "activation"
            ? RewriteActivationHistory(scenario.Snapshot.Checkpoint.Activations)
            : scenario.Snapshot.Checkpoint.Activations;
        var observedAtUtc = activation.ObservedAtUtc;
        var replacement = CopyCheckpoint(
            scenario.Snapshot.Checkpoint,
            observedAtUtc,
            control: control,
            activations: activations);
        var commit = new ProcessDurableCommit(
            new($"commit/history-rewrite/{history}"),
            scenario.Snapshot.Revision,
            Worker,
            scenario.Snapshot.WorkerLease!.Fence,
            replacement,
            [],
            observedAtUtc);

        var result = await scenario.Store.CommitAsync(Context, commit);

        Assert.Equal(ProcessStoreMutationDisposition.IdentityConflict, result.Disposition);
        Assert.Equal(scenario.Snapshot.Revision, result.Snapshot!.Revision);
        Assert.Equal(
            scenario.Snapshot.Checkpoint.Control.Attempts[0],
            result.Snapshot.Checkpoint.Control.Attempts[0]);
        Assert.Equal(
            scenario.Snapshot.Checkpoint.Activations.AsEnumerable(),
            result.Snapshot.Checkpoint.Activations.AsEnumerable());
    }

    [Fact]
    public void Compatibility_RequiresControlSafePointToMatchAttemptScopedActivationReceipt()
    {
        var fixture = ProcessDurabilityTestFixture.Create();
        var control = RewritePriorAttempt(
            fixture.Control,
            RewriteSafePoint(fixture.Control.CurrentAttempt));
        var checkpoint = ProcessDurabilityTestFixture.CopyCheckpoint(
            fixture.Checkpoint,
            control: control);

        var validation = ProcessCheckpointCompatibilityValidator.Validate(
            fixture.Plan,
            checkpoint);

        Assert.Contains(validation.Diagnostics, static diagnostic =>
            diagnostic.Code == ProcessCheckpointDiagnosticCodes.ActivationReceiptIncompatible);
    }

    [Theory]
    [InlineData("origin")]
    [InlineData("kind")]
    [InlineData("contract")]
    [InlineData("target")]
    [InlineData("content")]
    public void Compatibility_RequiresExactOutboxEnvelopeForInteractionTrace(string mutation)
    {
        var fixture = ProcessDurabilityTestFixture.Create(
            definitionId: $"process/durable-checkpoint/outbox-{mutation}",
            semanticVariant: $"outbox-{mutation}");
        var originalRecord = Assert.Single(fixture.Checkpoint.Emissions);
        var original = Assert.IsType<RequestEnvelope>(originalRecord.Envelope);
        var mutated = MutateEnvelope(fixture, original, mutation);
        var durableOperations = mutated is RequestEnvelope request
                                && request.Contract == fixture.DurableOperation.Binding.Request
            ? [CopyOperation(fixture.DurableOperation, request)]
            : ImmutableArray<DurableOperationState>.Empty;
        var checkpoint = ProcessDurabilityTestFixture.CopyCheckpoint(
            fixture.Checkpoint,
            emissions:
            [new(
                mutated,
                originalRecord.EnqueuedAtUtc,
                originalRecord.Attempts,
                originalRecord.Publication)],
            durableOperations: durableOperations);
        var activation = Assert.Single(checkpoint.Activations);
        var traceIndex = Enumerable.Range(0, activation.Evidence.Trace.Length)
            .Single(index => activation.Evidence.Trace[index].Kind == ProcessTraceEventKind.InteractionEmitted);
        var expectedLocation = $"/activations/0/evidence/trace/{traceIndex}";

        var validation = ProcessCheckpointCompatibilityValidator.Validate(
            fixture.Plan,
            checkpoint);

        Assert.Contains(validation.Diagnostics, diagnostic =>
            diagnostic.Code == ProcessCheckpointDiagnosticCodes.EmissionLedgerIncompatible
            && diagnostic.Location!.StartsWith(expectedLocation, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(ExecutionTokenDisposition.Ready)]
    [InlineData(ExecutionTokenDisposition.Active)]
    [InlineData(ExecutionTokenDisposition.Waiting)]
    public void TerminalContinuation_RejectsEveryLiveTokenDisposition(
        ExecutionTokenDisposition disposition)
    {
        var fixture = ProcessDurabilityTestFixture.Create();
        var initial = ProcessReferenceInterpreter.Create(fixture.Plan, fixture.Start);
        var source = Assert.Single(initial.Tokens);
        var token = NewToken(
            source.Id,
            source.Node,
            disposition,
            source.Step,
            source.Bindings,
            source.RequestObligations,
            source.ForkMembership,
            source.Failure);
        var terminal = CopyContinuation(
            initial,
            tokens: [token],
            terminal: CompletedOutcome());

        var validation = ProcessContinuationValidator.Validate(fixture.Plan, terminal);

        Assert.Contains(validation.Diagnostics, static diagnostic =>
            diagnostic.Code == ProcessContinuationDiagnosticCodes.TerminalStateInvalid
            && diagnostic.Location == "/terminal");
    }

    [Fact]
    public void TerminalContinuation_RejectsActiveWait()
    {
        var fixture = ProcessDurabilityTestFixture.Create();
        var terminal = CopyContinuation(
            fixture.Checkpoint.Continuation,
            tokens: TombstoneTokens(fixture.Checkpoint.Continuation.Tokens),
            forks: [],
            outstandingRequests: [],
            terminal: CompletedOutcome());

        var validation = ProcessContinuationValidator.Validate(fixture.Plan, terminal);

        Assert.Contains(validation.Diagnostics, static diagnostic =>
            diagnostic.Code == ProcessContinuationDiagnosticCodes.TerminalStateInvalid
            && diagnostic.Location == "/terminal");
    }

    [Fact]
    public void TerminalContinuation_RejectsOutstandingRequest()
    {
        var fixture = ProcessDurabilityTestFixture.Create();
        var terminal = CopyContinuation(
            fixture.Checkpoint.Continuation,
            tokens: TombstoneTokens(fixture.Checkpoint.Continuation.Tokens),
            forks: [],
            waits: InactiveWaits(fixture.Checkpoint.Continuation.Waits),
            terminal: CompletedOutcome());

        var validation = ProcessContinuationValidator.Validate(fixture.Plan, terminal);

        Assert.Contains(validation.Diagnostics, static diagnostic =>
            diagnostic.Code == ProcessContinuationDiagnosticCodes.TerminalStateInvalid
            && diagnostic.Location == "/terminal");
    }

    [Fact]
    public void TerminalContinuation_AllowsInactiveTokenAndWaitTombstones()
    {
        var fixture = ProcessDurabilityTestFixture.Create();
        var terminal = CopyContinuation(
            fixture.Checkpoint.Continuation,
            tokens: TombstoneTokens(fixture.Checkpoint.Continuation.Tokens),
            forks: [],
            waits: InactiveWaits(fixture.Checkpoint.Continuation.Waits),
            outstandingRequests: [],
            terminal: CompletedOutcome());

        var validation = ProcessContinuationValidator.Validate(fixture.Plan, terminal);

        Assert.DoesNotContain(validation.Diagnostics, static diagnostic =>
            diagnostic.Code == ProcessContinuationDiagnosticCodes.TerminalStateInvalid);
    }

    static async Task<RestartedScenario> CreateRestartedScenarioAsync(string history)
    {
        var fixture = ProcessDurabilityTestFixture.Create(
            definitionId: $"process/durable-store/history-{history}",
            semanticVariant: $"history-{history}",
            recoveryPolicy: ProcessRecoveryPolicy.RestartAttempt);
        var executor = ControlExecutor(fixture);
        var boundAtUtc = CheckpointedAtUtc.AddMinutes(1);
        var bound = executor.BindAttemptAffinity(
            fixture.Control,
            new(
                Expectation(fixture.Control),
                new(
                    new("node/index-generation"),
                    ProcessDurabilityTestFixture.StringValue("generation/1")),
                boundAtUtc));
        Assert.Equal(ProcessControlDecisionDisposition.AffinityBound, bound.Disposition);

        ProcessAttemptId replacementAttempt = new("process-attempt/2");
        var restartedAtUtc = CheckpointedAtUtc.AddMinutes(2);
        var restart = executor.Apply(
            bound.State,
            RestartCommand(fixture, bound.State, replacementAttempt, restartedAtUtc),
            restartedAtUtc);
        Assert.Equal(ProcessControlDecisionDisposition.Applied, restart.Disposition);
        var continuation = ProcessReferenceInterpreter.RestartAttempt(
            fixture.Plan,
            fixture.Checkpoint.Continuation,
            replacementAttempt);
        var restarted = CopyCheckpoint(
            fixture.Checkpoint,
            restartedAtUtc,
            continuation: continuation,
            control: restart.State);
        var store = new InMemoryProcessDurableStore();
        var initialized = await store.InitializeAsync(
            Context,
            new($"commit/initialize/history-{history}"),
            restarted);
        Assert.Equal(ProcessStoreMutationDisposition.Applied, initialized.Disposition);
        var acquired = await store.AcquireWorkerAsync(
            Context,
            restarted.ContinuationIdentity.ProcessInstanceId,
            Worker,
            TimeSpan.FromHours(1),
            restartedAtUtc);
        Assert.Equal(ProcessStoreMutationDisposition.Applied, acquired.Disposition);
        return new(
            fixture,
            store,
            Assert.IsType<ProcessDurableStoreSnapshot>(acquired.Snapshot));
    }

    static RestartProcessAttemptCommand RestartCommand(
        ProcessDurabilityTestFixture fixture,
        ProcessControlState state,
        ProcessAttemptId replacementAttempt,
        DateTimeOffset observedAtUtc) =>
        new(
            ProcessControlCommand.CurrentSchemaVersion,
            new(
                new("control/restart-history"),
                new("idempotency/control/restart-history"),
                state.ProcessInstanceId,
                fixture.Start.Request.Context.Authorization,
                observedAtUtc,
                fixture.Start.Request.Context.Provenance),
            Expectation(state),
            new(
                replacementAttempt,
                ProcessAttemptCleanupRequirement.AbandonAffinitiesAndReleaseResources,
                new("tests.history-restart")));

    static ProcessControlReferenceExecutor ControlExecutor(ProcessDurabilityTestFixture fixture) =>
        new(Assert.IsType<InteractionContractCatalog>(
            fixture.Plan.ValidationContext.InteractionContracts));

    static ProcessControlExpectation Expectation(ProcessControlState state) =>
        new(
            new(state.ProcessInstanceId, state.CurrentAttempt.AttemptId),
            state.Revision);

    static ProcessControlState RewritePriorAttempt(
        ProcessControlState source,
        ProcessControlAttemptState priorAttempt) =>
        new(
            source.SchemaVersion,
            source.Definition,
            source.AuthorityScope,
            source.ProcessInstanceId,
            source.Revision,
            source.Mode,
            source.Attempts.SetItem(0, priorAttempt),
            source.PendingCommandId,
            source.Receipts,
            source.CreatedAtUtc,
            source.UpdatedAtUtc);

    static ProcessControlAttemptState RewriteSafePoint(ProcessControlAttemptState source)
    {
        var safePoint = Assert.Single(source.SafePoints);
        var observation = safePoint.Observation;
        var replacement = new ProcessControlSafePoint(
            safePoint.Activation,
            new(
                observation.SafePointId,
                observation.Expectation,
                observation.ActivationId,
                new("return"),
                observation.ObservedAtUtc));
        return CopyAttempt(source, safePoints: [replacement]);
    }

    static ProcessControlAttemptState RewriteAffinity(ProcessControlAttemptState source)
    {
        var binding = Assert.Single(source.AffinityBindings);
        var replacement = new ProcessAttemptAffinityObservation(
            binding.Expectation,
            new(
                binding.Affinity.Slot,
                ProcessDurabilityTestFixture.StringValue("generation/tampered")),
            binding.ObservedAtUtc);
        return CopyAttempt(source, affinityBindings: [replacement]);
    }

    static ProcessControlAttemptState CopyAttempt(
        ProcessControlAttemptState source,
        ImmutableArray<ProcessControlSafePoint> safePoints = default,
        ImmutableArray<ProcessAttemptAffinityObservation> affinityBindings = default) =>
        new(
            source.AttemptId,
            source.StartedAtUtc,
            source.Disposition,
            source.Phase,
            source.ActiveActivation,
            safePoints.IsDefault ? source.SafePoints : safePoints,
            affinityBindings.IsDefault ? source.AffinityBindings : affinityBindings,
            source.Closure);

    static ImmutableArray<ProcessActivationCommitReceipt> RewriteActivationHistory(
        ImmutableArray<ProcessActivationCommitReceipt> activations)
    {
        var receipt = Assert.Single(activations);
        var traceIndex = Enumerable.Range(0, receipt.Evidence.Trace.Length)
            .Single(index => receipt.Evidence.Trace[index].Kind == ProcessTraceEventKind.InteractionEmitted);
        var trace = receipt.Evidence.Trace[traceIndex];
        var evidence = receipt.Evidence with
        {
            Trace = receipt.Evidence.Trace.SetItem(
                traceIndex,
                trace with { Detail = "request:tampered-history" })
        };
        return
        [new(
            receipt.Sequence,
            receipt.Continuation,
            receipt.BeforeContinuation,
            receipt.AfterContinuation,
            receipt.Activation,
            receipt.Disposition,
            evidence,
            receipt.CommittedAtUtc)];
    }

    static InteractionEnvelope MutateEnvelope(
        ProcessDurabilityTestFixture fixture,
        RequestEnvelope source,
        string mutation)
    {
        var origin = Assert.IsType<ProcessInteractionOrigin>(source.Context.Origin);
        var target = Assert.IsType<ProcessTokenInteractionTarget>(source.ResponseTarget);
        return mutation switch
        {
            "origin" => new RequestEnvelope(
                source.SchemaVersion,
                CopyContext(
                    source.Context,
                    new ProcessInteractionOrigin(
                        origin.Definition,
                        new("relation"),
                        origin.Continuation,
                        origin.Activation,
                        origin.Token,
                        origin.Entity,
                        origin.Transition,
                        origin.Outcome)),
                source.Contract,
                source.Payload,
                source.ResponseTarget),
            "kind" => new DomainEventEnvelope(
                source.SchemaVersion,
                source.Context,
                new(ProcessDurabilityTestFixture.DefinitionReference(
                    "interaction/event/tampered-kind",
                    '8')),
                source.Payload),
            "contract" => new RequestEnvelope(
                source.SchemaVersion,
                source.Context,
                new(ProcessDurabilityTestFixture.DefinitionReference(
                    "interaction/request/tampered-contract",
                    '9')),
                source.Payload,
                source.ResponseTarget),
            "target" => new RequestEnvelope(
                source.SchemaVersion,
                source.Context,
                source.Contract,
                source.Payload,
                new ProcessTokenInteractionTarget(
                    target.Continuation,
                    fixture.Checkpoint.Continuation.Tokens
                        .First(token => token.Id != target.Token).Id,
                    target.WaitRegistrationId)),
            "content" => new RequestEnvelope(
                source.SchemaVersion,
                source.Context,
                source.Contract,
                ProcessDurabilityTestFixture.StringValue("tampered-content"),
                source.ResponseTarget),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation), mutation, "Unknown envelope mutation.")
        };
    }

    static InteractionEnvelopeContext CopyContext(
        InteractionEnvelopeContext source,
        InteractionOrigin origin) =>
        new(
            source.EmissionId,
            origin,
            source.CorrelationId,
            source.CausationId,
            source.AuthorityScope,
            source.IdempotencyKey,
            source.Ordering,
            source.Delivery,
            source.Provenance);

    static DurableOperationState CopyOperation(
        DurableOperationState source,
        RequestEnvelope request) =>
        new(
            source.SchemaVersion,
            request,
            source.Binding,
            source.CreatedAtUtc,
            source.Attempts,
            source.Reconciliations,
            source.RecoveryRequirement,
            source.Acknowledgement,
            source.Admission);

    static ImmutableArray<ProcessTokenState> TombstoneTokens(
        ImmutableArray<ProcessTokenState> tokens) =>
        [.. tokens.Select(token => NewToken(
            token.Id,
            token.Node,
            ExecutionTokenDisposition.Completed,
            token.Step,
            token.Bindings,
            token.RequestObligations,
            token.ForkMembership,
            failure: null))];

    static ImmutableArray<ProcessWaitState> InactiveWaits(
        ImmutableArray<ProcessWaitState> waits) =>
        [.. waits.Select(wait => NewWait(
            wait.RegistrationId,
            wait.Token,
            wait.Node,
            wait.Kind,
            wait.RegisteredAtUtc,
            wait.Timers,
            active: false,
            wait.WinnerClause,
            wait.WinnerInput,
            wait.ObligationEmission))];

    static ExecutionTerminalOutcome CompletedOutcome() =>
        new(ExecutionTerminalOutcomeKind.Completed, CheckpointedAtUtc.AddHours(1));

    static ProcessContinuationState CopyContinuation(
        ProcessContinuationState source,
        ImmutableArray<ProcessTokenState> tokens = default,
        ImmutableArray<ProcessForkState> forks = default,
        ImmutableArray<ProcessWaitState> waits = default,
        ImmutableArray<ProcessOutstandingRequest> outstandingRequests = default,
        ExecutionTerminalOutcome? terminal = null) =>
        NewContinuation(
            source.Definition,
            source.Continuation,
            source.CompletedActivationCount,
            tokens.IsDefault ? source.Tokens : tokens,
            forks.IsDefault ? source.Forks : forks,
            waits.IsDefault ? source.Waits : waits,
            source.BufferedInputs,
            source.InputReceipts,
            outstandingRequests.IsDefault ? source.OutstandingRequests : outstandingRequests,
            terminal ?? source.Terminal);

    static ProcessDurableCheckpoint CopyCheckpoint(
        ProcessDurableCheckpoint source,
        DateTimeOffset updatedAtUtc,
        ProcessContinuationState? continuation = null,
        ProcessControlState? control = null,
        ImmutableArray<ProcessActivationCommitReceipt> activations = default) =>
        new(
            source.SchemaVersion,
            source.Start,
            continuation ?? source.Continuation,
            control ?? source.Control,
            activations.IsDefault ? source.Activations : activations,
            source.Operations,
            source.Inbox,
            source.Emissions,
            source.DurableOperations,
            source.CreatedAtUtc,
            updatedAtUtc);

    [UnsafeAccessor(UnsafeAccessorKind.Constructor)]
    static extern ProcessContinuationState NewContinuation(
        ExecutionDefinitionReference definition,
        ProcessContinuationIdentity continuation,
        long completedActivationCount,
        ImmutableArray<ProcessTokenState> tokens,
        ImmutableArray<ProcessForkState> forks,
        ImmutableArray<ProcessWaitState> waits,
        ImmutableArray<ProcessBufferedInput> bufferedInputs,
        ImmutableArray<ProcessInputReceipt> inputReceipts,
        ImmutableArray<ProcessOutstandingRequest> outstandingRequests,
        ExecutionTerminalOutcome terminal);

    [UnsafeAccessor(UnsafeAccessorKind.Constructor)]
    static extern ProcessTokenState NewToken(
        TokenId id,
        ExecutionNodeId node,
        ExecutionTokenDisposition disposition,
        long step,
        ImmutableArray<ProcessBindingValue> bindings,
        ImmutableArray<ProcessRequestObligation> requestObligations,
        ProcessForkMembership? forkMembership,
        DocumentValidationDiagnostic? failure);

    [UnsafeAccessor(UnsafeAccessorKind.Constructor)]
    static extern ProcessWaitState NewWait(
        ProcessWaitRegistrationId registrationId,
        TokenId token,
        ExecutionNodeId node,
        ProcessWaitKind kind,
        DateTimeOffset registeredAtUtc,
        ImmutableArray<ProcessTimerState> timers,
        bool active,
        ExecutionNodeId? winnerClause,
        EmissionId? winnerInput,
        EmissionId? obligationEmission);

    sealed record RestartedScenario(
        ProcessDurabilityTestFixture Fixture,
        InMemoryProcessDurableStore Store,
        ProcessDurableStoreSnapshot Snapshot);
}
