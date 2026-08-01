using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Compilation;
using Cohesive.Processes.Execution;
using Cohesive.Processes.IR;
using Cohesive.Storage.Processes;
using CanonicalProcessDefinition = Cohesive.Processes.IR.ProcessDefinition;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class ProcessChildDurableOperationAdapterTests
{
    static readonly DateTimeOffset ObservedAtUtc =
        new(2026, 7, 31, 18, 0, 0, TimeSpan.Zero);

    static readonly InteractionAuthorityScope Authority =
        new("authority/process-child-adapter-tests", "tenant/cohesive");

    [Fact]
    public async Task Execute_InitializesExactChildDrivesWorkerRequestAndReturnsTruthfulTerminalOrigin()
    {
        var fixture = CreateFixture();
        var adapter = fixture.ChildAdapter();
        var invocation = Invocation(fixture.ParentRequest, fixture.ParentContracts.Binding, ordinal: 1);

        var observation = await adapter.ExecuteAsync(fixture.Context, invocation);

        var completed = Assert.IsType<DurableOperationOutcomeObservation>(observation);
        var outcome = Assert.IsType<RequestResultOutcome>(completed.Outcome);
        Assert.Equal(new RequestTerminalOutcomeId("result"), outcome.Id);
        Assert.Equal(fixture.ParentRequest.Payload, outcome.Value);
        var origin = Assert.IsType<ProcessInteractionOrigin>(completed.ReplyOrigin);
        Assert.Equal(fixture.ChildPlan.DefinitionReference, origin.Definition);
        Assert.Equal(fixture.ChildTarget.Continuation, origin.Continuation);
        Assert.Equal(1, fixture.WorkerAdapter.ExecutionCalls);
        Assert.NotEqual(
            fixture.ParentRequest.Context.Provenance,
            fixture.ChildPlan.Document.Metadata.Provenance);

        var inspected = await fixture.ChildRuntime.InspectAsync(
            fixture.Context,
            fixture.ChildPlan,
            fixture.ChildTarget.Continuation);
        var checkpoint = Assert.IsType<ProcessDurableStoreSnapshot>(inspected.Snapshot).Checkpoint;
        Assert.Equal(ExecutionTerminalOutcomeKind.Completed, checkpoint.Continuation.Terminal.Kind);
        Assert.All(checkpoint.Activations, receipt => Assert.Equal(
            fixture.ChildPlan.Document.Metadata.Provenance,
            receipt.Activation.Context.Provenance));
        var terminalReceipt = checkpoint.Activations[^1];
        var terminalTrace = terminalReceipt.Evidence.Trace[^1];
        Assert.Equal(terminalReceipt.Activation.Id, origin.Activation);
        Assert.Equal(terminalTrace.Token, origin.Token);
        Assert.Equal(terminalTrace.Node, origin.Node);
        Assert.Equal(terminalTrace.Node, origin.Outcome);
    }

    [Fact]
    public async Task Execute_RetryLoadsTerminalChildWithoutAnotherStartActivationOrWorkerDispatch()
    {
        var fixture = CreateFixture();
        var adapter = fixture.ChildAdapter();

        var first = await adapter.ExecuteAsync(
            fixture.Context,
            Invocation(fixture.ParentRequest, fixture.ParentContracts.Binding, ordinal: 1));
        var before = await fixture.ChildRuntime.InspectAsync(
            fixture.Context,
            fixture.ChildPlan,
            fixture.ChildTarget.Continuation);
        var beforeCheckpoint = Assert.IsType<ProcessDurableStoreSnapshot>(before.Snapshot).Checkpoint;

        var replay = await adapter.ExecuteAsync(
            fixture.Context,
            Invocation(fixture.ParentRequest, fixture.ParentContracts.Binding, ordinal: 2));
        var after = await fixture.ChildRuntime.InspectAsync(
            fixture.Context,
            fixture.ChildPlan,
            fixture.ChildTarget.Continuation);
        var afterCheckpoint = Assert.IsType<ProcessDurableStoreSnapshot>(after.Snapshot).Checkpoint;

        Assert.Equal(first, replay);
        Assert.Equal(beforeCheckpoint.Start, afterCheckpoint.Start);
        Assert.Equal(beforeCheckpoint.Continuation.CompletedActivationCount, afterCheckpoint.Continuation.CompletedActivationCount);
        Assert.Equal(beforeCheckpoint.DurableOperations, afterCheckpoint.DurableOperations);
        Assert.Equal(1, fixture.WorkerAdapter.ExecutionCalls);
    }

    [Fact]
    public async Task Reconcile_DrivesSamePartiallyCompletedChildAndNeverRedispatchesWorkerRequest()
    {
        var fixture = CreateFixture();
        var bounded = fixture.ChildAdapter(new(
            maximumActivations: 1,
            maximumOperationAdvances: 1));
        var first = await bounded.ExecuteAsync(
            fixture.Context,
            Invocation(fixture.ParentRequest, fixture.ParentContracts.Binding, ordinal: 1));

        var boundedFailure = Assert.IsType<DurableOperationFailureObservation>(first);
        Assert.Equal(
            ProcessChildDurableOperationDiagnosticCodes.DriveLimitExceeded,
            boundedFailure.Failure.Code);
        var partial = await fixture.ChildRuntime.InspectAsync(
            fixture.Context,
            fixture.ChildPlan,
            fixture.ChildTarget.Continuation);
        var partialCheckpoint = Assert.IsType<ProcessDurableStoreSnapshot>(partial.Snapshot).Checkpoint;
        Assert.Equal(1, partialCheckpoint.Continuation.CompletedActivationCount);
        Assert.Equal(DurableOperationStatus.Dispositioned, Assert.Single(partialCheckpoint.DurableOperations).Status);

        var reconciliation = await DurableOperationReferenceExecutor.ReconcileAsync(
            fixture.Context,
            ReconciliationState(fixture),
            fixture.ChildAdapter());

        var completed = Assert.IsType<DurableOperationReconciledOutcome>(reconciliation);
        Assert.Equal(new RequestTerminalOutcomeId("result"), completed.Outcome.Id);
        Assert.Equal(fixture.ChildTarget.Continuation, Assert.IsType<ProcessInteractionOrigin>(completed.ReplyOrigin).Continuation);
        Assert.Equal(1, fixture.WorkerAdapter.ExecutionCalls);
        var terminal = await fixture.ChildRuntime.InspectAsync(
            fixture.Context,
            fixture.ChildPlan,
            fixture.ChildTarget.Continuation);
        Assert.Equal(
            ExecutionTerminalOutcomeKind.Completed,
            Assert.IsType<ProcessDurableStoreSnapshot>(terminal.Snapshot).Checkpoint.Continuation.Terminal.Kind);
    }

    [Fact]
    public async Task Reconcile_AbsentChildProvesNotExecutedWithoutInitializingIt()
    {
        var fixture = CreateFixture();

        var reconciliation = await DurableOperationReferenceExecutor.ReconcileAsync(
            fixture.Context,
            ReconciliationState(fixture),
            fixture.ChildAdapter());

        Assert.IsType<DurableOperationConfirmedNotExecuted>(reconciliation);
        var inspected = await fixture.ChildRuntime.InspectAsync(
            fixture.Context,
            fixture.ChildPlan,
            fixture.ChildTarget.Continuation);
        Assert.Equal(ProcessDurableRuntimeDisposition.NotFound, inspected.Disposition);
        Assert.Equal(0, fixture.WorkerAdapter.ExecutionCalls);
    }

    [Fact]
    public async Task Execute_UnknownCommittedInitializationIsAmbiguousAndReconciliationDrivesExactChild()
    {
        var crash = new CrashOnce(
            ProcessStoreMutationKind.Initialize,
            ProcessStoreCrashPhase.AfterAtomicCommitBeforeReturn);
        var fixture = CreateFixture(
            store: new InMemoryProcessDurableStore(crash.ShouldCrash),
            maximumAmbiguousStoreMutationAttempts: 1);
        var adapter = fixture.ChildAdapter();

        var interrupted = Assert.IsType<DurableOperationFailureObservation>(
            await adapter.ExecuteAsync(
                fixture.Context,
                Invocation(fixture.ParentRequest, fixture.ParentContracts.Binding, ordinal: 1)));
        var committed = await fixture.ChildRuntime.InspectAsync(
            fixture.Context,
            fixture.ChildPlan,
            fixture.ChildTarget.Continuation);

        Assert.Equal(DurableOperationFailurePhase.PostCommitPreAcknowledgement, interrupted.Failure.Phase);
        Assert.Equal(DurableOperationEffectEvidence.Ambiguous, interrupted.Failure.EffectEvidence);
        Assert.Equal(DurableOperationFailureDisposition.Retryable, interrupted.Failure.Disposition);
        Assert.Equal(ProcessChildDurableOperationDiagnosticCodes.ChildRuntimeRejected, interrupted.Failure.Code);
        Assert.Equal(ProcessDurableRuntimeDisposition.Replayed, committed.Disposition);
        Assert.Empty(Assert.IsType<ProcessDurableStoreSnapshot>(committed.Snapshot).Checkpoint.Activations);
        Assert.Equal(0, fixture.WorkerAdapter.ExecutionCalls);

        var reconciled = Assert.IsType<DurableOperationReconciledOutcome>(
            await DurableOperationReferenceExecutor.ReconcileAsync(
                fixture.Context,
                ReconciliationState(fixture, interrupted),
                adapter));
        var origin = Assert.IsType<ProcessInteractionOrigin>(reconciled.ReplyOrigin);
        var terminal = await fixture.ChildRuntime.InspectAsync(
            fixture.Context,
            fixture.ChildPlan,
            fixture.ChildTarget.Continuation);

        Assert.Equal(new RequestTerminalOutcomeId("result"), reconciled.Outcome.Id);
        Assert.Equal(fixture.ChildTarget.Continuation, origin.Continuation);
        Assert.Equal(ExecutionTerminalOutcomeKind.Completed, Assert.IsType<ProcessDurableStoreSnapshot>(
            terminal.Snapshot).Checkpoint.Continuation.Terminal.Kind);
        Assert.Equal(1, fixture.WorkerAdapter.ExecutionCalls);
        Assert.Equal(1, crash.CrashCount);
    }

    [Fact]
    public async Task Execute_PreExistingIncompatibleChildReturnsTerminalNotExecutedEvidence()
    {
        var fixture = CreateFixture();
        var incompatibleStart = new ProcessStartReceipt(
            new ProcessStartRequest(
                ProcessStartRequest.CurrentSchemaVersion,
                fixture.ChildPlan.DefinitionReference,
                new(
                    new("process-child-start/pre-existing"),
                    new("process-child-start/pre-existing"),
                    fixture.ChildTarget.Continuation.ProcessInstanceId,
                    new(
                        "tests.process-child-adapter",
                        Authority,
                        "tests/process-child-adapter/pre-existing"),
                    ObservedAtUtc,
                    Provenance("pre-existing")),
                fixture.ChildTarget.Continuation,
                DurableOperationTestFixture.StringValue("different-input")),
            ObservedAtUtc);
        var initialized = await fixture.ChildRuntime.InitializeAsync(
            fixture.Context,
            fixture.ChildPlan,
            incompatibleStart);
        Assert.Equal(ProcessDurableRuntimeDisposition.Applied, initialized.Disposition);
        var before = Assert.IsType<ProcessDurableStoreSnapshot>(initialized.Snapshot);

        var observation = await fixture.ChildAdapter().ExecuteAsync(
            fixture.Context,
            Invocation(fixture.ParentRequest, fixture.ParentContracts.Binding, ordinal: 1));

        var failure = Assert.IsType<DurableOperationFailureObservation>(observation).Failure;
        Assert.Equal(ProcessChildDurableOperationDiagnosticCodes.ChildIncompatible, failure.Code);
        Assert.Equal(DurableOperationFailurePhase.PreCall, failure.Phase);
        Assert.Equal(DurableOperationEffectEvidence.NotExecuted, failure.EffectEvidence);
        Assert.Equal(DurableOperationFailureDisposition.Terminal, failure.Disposition);
        var after = await fixture.ChildRuntime.InspectAsync(
            fixture.Context,
            fixture.ChildPlan,
            fixture.ChildTarget.Continuation);
        Assert.Equal(before, Assert.IsType<ProcessDurableStoreSnapshot>(after.Snapshot));
        Assert.Equal(0, fixture.WorkerAdapter.ExecutionCalls);
    }

    [Fact]
    public async Task Execute_UnresolvedChildOperationIsReconciledOnceAndDoesNotStarveLaterOperation()
    {
        var workerContracts = DurableOperationTestFixture.Create(
            retry: RequestRetrySemantics.ReconcileBeforeRetry,
            unresolvedOutcome: RequestResolutionSemantics.Reconcile);
        var workerAdapter = new FirstUnresolvedThenEchoWorkerAdapter(workerContracts.RequestContract);
        var fixture = CreateFixture(
            workerContracts: workerContracts,
            workerAdapter: workerAdapter,
            parallelWorkerRequests: true);
        var adapter = fixture.ChildAdapter(new(
            maximumActivations: 8,
            maximumOperationAdvances: 8));

        var observation = await adapter.ExecuteAsync(
            fixture.Context,
            Invocation(fixture.ParentRequest, fixture.ParentContracts.Binding, ordinal: 1));

        var failure = Assert.IsType<DurableOperationFailureObservation>(observation).Failure;
        Assert.Equal(ProcessChildDurableOperationDiagnosticCodes.ChildBlocked, failure.Code);
        Assert.Equal(2, workerAdapter.ExecutionCalls);
        Assert.Equal(1, workerAdapter.ReconciliationCalls);
        var inspected = await fixture.ChildRuntime.InspectAsync(
            fixture.Context,
            fixture.ChildPlan,
            fixture.ChildTarget.Continuation);
        var operations = Assert.IsType<ProcessDurableStoreSnapshot>(inspected.Snapshot)
            .Checkpoint.DurableOperations;
        Assert.Equal(2, operations.Length);
        Assert.Contains(operations, static operation =>
            operation.Status == DurableOperationStatus.ReconciliationRequired);
        Assert.Contains(operations, static operation =>
            operation.Status == DurableOperationStatus.Dispositioned);
    }

    [Fact]
    public async Task Execute_InexactResolvedPlanFailsClosedBeforeCreatingChild()
    {
        var fixture = CreateFixture();
        var wrongPlan = CompileWorkerPlan(
            fixture.WorkerContracts,
            definitionId: "process/worker/wrong-plan");
        var adapter = new ProcessChildDurableOperationAdapter(
            fixture.ChildRuntime,
            new FixedPlanResolver(wrongPlan),
            [fixture.ParentContracts.RequestContract]);

        var observation = await adapter.ExecuteAsync(
            fixture.Context,
            Invocation(fixture.ParentRequest, fixture.ParentContracts.Binding, ordinal: 1));

        var failure = Assert.IsType<DurableOperationFailureObservation>(observation);
        Assert.Equal(ProcessChildDurableOperationDiagnosticCodes.PlanInexact, failure.Failure.Code);
        Assert.Equal(DurableOperationFailurePhase.PreCall, failure.Failure.Phase);
        Assert.Equal(DurableOperationEffectEvidence.NotExecuted, failure.Failure.EffectEvidence);
        var inspected = await fixture.ChildRuntime.InspectAsync(
            fixture.Context,
            fixture.ChildPlan,
            fixture.ChildTarget.Continuation);
        Assert.Equal(ProcessDurableRuntimeDisposition.NotFound, inspected.Disposition);
    }

    [Fact]
    public async Task Execute_CausalCancellationPropagatesWithoutCreatingChild()
    {
        var fixture = CreateFixture();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var cancelledContext = fixture.Context.WithCancellationToken(cancellation.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await fixture.ChildAdapter().ExecuteAsync(
                cancelledContext,
                Invocation(fixture.ParentRequest, fixture.ParentContracts.Binding, ordinal: 1)));

        var inspected = await fixture.ChildRuntime.InspectAsync(
            fixture.Context,
            fixture.ChildPlan,
            fixture.ChildTarget.Continuation);
        Assert.Equal(ProcessDurableRuntimeDisposition.NotFound, inspected.Disposition);
        Assert.Equal(0, fixture.WorkerAdapter.ExecutionCalls);
    }

    static Fixture CreateFixture(
        InMemoryProcessDurableStore? store = null,
        int maximumAmbiguousStoreMutationAttempts = 3,
        DurableOperationTestFixture? workerContracts = null,
        ICountingWorkerAdapter? workerAdapter = null,
        bool parallelWorkerRequests = false)
    {
        workerContracts ??= DurableOperationTestFixture.Create();
        var parentContracts = DurableOperationTestFixture.Create(
            retry: RequestRetrySemantics.ReconcileBeforeRetry,
            supportsChildOutcomes: true);
        var childPlan = parallelWorkerRequests
            ? CompileParallelWorkerPlan(workerContracts, "process/worker/index-shards")
            : CompileWorkerPlan(workerContracts, "process/worker/index-shard");
        ProcessContinuationIdentity childContinuation = new(
            new("process-instance/worker/index-shard/a"),
            new("process-attempt/worker/1"));
        var childTarget = new ProcessChildRequestTarget(
            childPlan.DefinitionReference,
            childContinuation,
            new(
                new("result"),
                new("failure"),
                new("child-cancelled"),
                new("child-terminated")));
        var parentRequest = ParentRequest(parentContracts, childTarget);
        store ??= new InMemoryProcessDurableStore();
        workerAdapter ??= new EchoWorkerAdapter(workerContracts.RequestContract);
        var runtime = new ProcessDurableRuntime(
            store,
            RejectingHost.Instance,
            new(
                workerId: "worker/process-child-adapter-tests",
                workerLease: TimeSpan.FromMinutes(5),
                maxAmbiguousStoreMutationAttempts: maximumAmbiguousStoreMutationAttempts),
            bindingResolver: new ExactBindingResolver(workerContracts.Binding),
            operationAdapterResolver: new ExactAdapterResolver(workerAdapter));
        return new(
            workerContracts,
            parentContracts,
            childPlan,
            childTarget,
            parentRequest,
            runtime,
            workerAdapter,
            Context());
    }

    static CompiledProcessPlan CompileWorkerPlan(
        DurableOperationTestFixture workerContracts,
        string definitionId)
    {
        ValueBindingId result = new("worker.result");
        ValueBindingId failure = new("worker.failure");
        CanonicalProcessDefinition definition = new(
            DurableOperationTestFixture.StringContract,
            DurableOperationTestFixture.StringContract,
            new("work"),
            [
                new RequestProcessNode(
                    new("work"),
                    workerContracts.RequestContract,
                    Expr.BoundValue(ProcessBindingIds.Input),
                    [
                        new(
                            new("worker/result"),
                            new("result"),
                            new(
                                new(new("edge/worker-result"), new("return")),
                                new(result, DurableOperationTestFixture.StringContract))),
                        new(
                            new("worker/failure"),
                            new("failure"),
                            new(
                                new(new("edge/worker-failure"), new("fail")),
                                new(failure, DurableOperationTestFixture.StringContract)))
                    ]),
                new ReturnProcessNode(new("return"), Expr.BoundValue(result)),
                new FailProcessNode(new("fail"), Expr.BoundValue(failure))
            ],
            ProcessRecoveryPolicy.ContinueAttempt);
        var document = ProcessDefinitionDocuments.Create(
            new(definitionId),
            new("revision/1"),
            definition,
            Provenance("child"));
        var compilation = ProcessStaticCompiler.Compile(
            document,
            new ProcessDefinitionValidationContext(interactionContracts: workerContracts.Catalog));
        Assert.True(
            compilation.IsSuccessful,
            DurableOperationTestFixture.FormatDiagnostics(compilation.Validation));
        return Assert.IsType<CompiledProcessPlan>(compilation.Plan);
    }

    static CompiledProcessPlan CompileParallelWorkerPlan(
        DurableOperationTestFixture workerContracts,
        string definitionId)
    {
        CanonicalProcessDefinition definition = new(
            DurableOperationTestFixture.StringContract,
            DurableOperationTestFixture.StringContract,
            new("fork"),
            [
                new ForkProcessNode(
                    new("fork"),
                    [
                        new(new("branch/a"), new(new("edge/fork-a"), new("work/a"))),
                        new(new("branch/b"), new(new("edge/fork-b"), new("work/b")))
                    ],
                    new("join")),
                WorkerRequest(nodeId: "work/a", payload: "a"),
                WorkerRequest(nodeId: "work/b", payload: "b"),
                new JoinProcessNode(
                    new("join"),
                    new("fork"),
                    new(
                        ProcessJoinMode.All,
                        requiredCount: 0,
                        ProcessJoinFailurePolicy.FailFast,
                        ProcessJoinCancellationPolicy.AwaitRemaining,
                        ProcessJoinCompletionOrder.Unobservable,
                        ProcessJoinTieBreak.BranchIdentity),
                    new(new("edge/join-return"), new("return"))),
                new ReturnProcessNode(new("return"), Expr.Const("done"))
            ],
            ProcessRecoveryPolicy.ContinueAttempt);
        var document = ProcessDefinitionDocuments.Create(
            new(definitionId),
            new("revision/1"),
            definition,
            Provenance("child"));
        var compilation = ProcessStaticCompiler.Compile(
            document,
            new ProcessDefinitionValidationContext(interactionContracts: workerContracts.Catalog));
        Assert.True(
            compilation.IsSuccessful,
            DurableOperationTestFixture.FormatDiagnostics(compilation.Validation));
        return Assert.IsType<CompiledProcessPlan>(compilation.Plan);

        RequestProcessNode WorkerRequest(string nodeId, string payload) => new(
            new(nodeId),
            workerContracts.RequestContract,
            Expr.Const(payload),
            [
                new(
                    new($"{nodeId}/result"),
                    new("result"),
                    new(
                        new(new($"edge/{nodeId}-result"), new("join")),
                        new(new($"{nodeId}.result"), DurableOperationTestFixture.StringContract))),
                new(
                    new($"{nodeId}/failure"),
                    new("failure"),
                    new(
                        new(new($"edge/{nodeId}-failure"), new("join")),
                        new(new($"{nodeId}.failure"), DurableOperationTestFixture.StringContract)))
            ]);
    }

    static RequestEnvelope ParentRequest(
        DurableOperationTestFixture contracts,
        ProcessChildRequestTarget childTarget)
    {
        ProcessContinuationIdentity parentContinuation = new(
            new("process-instance/coordinator"),
            new("process-attempt/coordinator/1"));
        var origin = new ProcessInteractionOrigin(
            ProcessDurabilityTestFixture.DefinitionReference("process/coordinator", 'c'),
            new("partition"),
            parentContinuation,
            new("activation/coordinator/1"),
            new("token/coordinator/partition-a"));
        return new(
            InteractionEnvelope.CurrentSchemaVersion,
            new(
                new("emission/coordinator/child-a"),
                origin,
                new("correlation/process-child-adapter-tests"),
                causationId: null,
                Authority,
                new("idempotency/coordinator/child-a"),
                ordering: null,
                new(
                    InteractionDurabilityDemand.Durable,
                    InteractionVisibilityDemand.AfterOriginCommit),
                Provenance("parent")),
            contracts.RequestContract,
            DurableOperationTestFixture.StringValue("partition/a"),
            new ProcessTokenInteractionTarget(
                parentContinuation,
                new("token/coordinator/partition-a"),
                new("wait/coordinator/partition-a")),
            childTarget);
    }

    static DurableOperationInvocation Invocation(
        RequestEnvelope request,
        DurableRequestBinding binding,
        int ordinal)
    {
        var fixture = DurableOperationTestFixture.Create(
            retry: RequestRetrySemantics.ReconcileBeforeRetry,
            supportsChildOutcomes: true);
        var validation = fixture.Executor.TryCreate(
            request,
            binding,
            ObservedAtUtc,
            out var created);
        Assert.True(validation.IsValid, DurableOperationTestFixture.FormatDiagnostics(validation));
        var claimed = fixture.Executor.Claim(
            Assert.IsType<DurableOperationState>(created),
            new($"operation-attempt/parent/{ordinal}"),
            "worker/parent",
            ObservedAtUtc);
        var claim = Assert.IsType<DurableOperationClaim>(claimed.Claim);
        var dispatched = fixture.Executor.BeginDispatch(
            claimed.State,
            claim.AttemptId,
            claim.Fence,
            ObservedAtUtc);
        return Assert.IsType<DurableOperationInvocation>(dispatched.Invocation);
    }

    static DurableOperationState ReconciliationState(
        Fixture fixture,
        DurableOperationFailureObservation? observation = null)
    {
        var validation = fixture.ParentContracts.Executor.TryCreate(
            fixture.ParentRequest,
            fixture.ParentContracts.Binding,
            ObservedAtUtc,
            out var created);
        Assert.True(validation.IsValid, DurableOperationTestFixture.FormatDiagnostics(validation));
        var claimed = fixture.ParentContracts.Executor.Claim(
            Assert.IsType<DurableOperationState>(created),
            new("operation-attempt/parent/failed"),
            "worker/parent",
            ObservedAtUtc);
        var claim = Assert.IsType<DurableOperationClaim>(claimed.Claim);
        var dispatched = fixture.ParentContracts.Executor.BeginDispatch(
            claimed.State,
            claim.AttemptId,
            claim.Fence,
            ObservedAtUtc);
        var failed = fixture.ParentContracts.Executor.RecordObservation(
            dispatched.State,
            claim.AttemptId,
            claim.Fence,
            observation ?? new DurableOperationFailureObservation(new(
                DurableOperationFailurePhase.InCall,
                DurableOperationEffectEvidence.Ambiguous,
                DurableOperationFailureDisposition.Retryable,
                "tests.parent-outcome-ambiguous")),
            ObservedAtUtc);
        Assert.Equal(DurableOperationRecoveryRequirement.Reconcile, failed.State.RecoveryRequirement);
        return failed.State;
    }

    static OperationContext Context() =>
        OperationContext.Create(timeProvider: new FixedTimeProvider(ObservedAtUtc));

    static ExecutionProvenance Provenance(string role) =>
        new(
            new("process-child-adapter-tests", "1"),
            new($"tests/execution-kernel/process-child-adapter/{role}"),
            DocumentOrigin.Generated);

    sealed record Fixture(
        DurableOperationTestFixture WorkerContracts,
        DurableOperationTestFixture ParentContracts,
        CompiledProcessPlan ChildPlan,
        ProcessChildRequestTarget ChildTarget,
        RequestEnvelope ParentRequest,
        ProcessDurableRuntime ChildRuntime,
        ICountingWorkerAdapter WorkerAdapter,
        OperationContext Context)
    {
        internal ProcessChildDurableOperationAdapter ChildAdapter(
            ProcessChildDurableOperationAdapterOptions? options = null) =>
            new(
                ChildRuntime,
                new FixedPlanResolver(ChildPlan),
                [ParentContracts.RequestContract],
                options);
    }

    sealed class FixedPlanResolver(CompiledProcessPlan plan) : IProcessChildPlanResolver
    {
        public bool TryResolve(
            ExecutionDefinitionReference definition,
            out CompiledProcessPlan? resolved)
        {
            resolved = plan;
            return true;
        }
    }

    sealed class ExactBindingResolver(DurableRequestBinding binding) : IProcessDurableRequestBindingResolver
    {
        public bool TryResolve(RequestEnvelope request, out DurableRequestBinding? resolved)
        {
            resolved = request.Contract == binding.Request ? binding : null;
            return resolved is not null;
        }
    }

    sealed class ExactAdapterResolver(IDurableOperationAdapter adapter) : IProcessDurableOperationAdapterResolver
    {
        public bool TryResolve(RequestEnvelope request, out IDurableOperationAdapter? resolved)
        {
            resolved = adapter.Capabilities.Supports(request.Contract) ? adapter : null;
            return resolved is not null;
        }
    }

    interface ICountingWorkerAdapter : IDurableOperationAdapter
    {
        int ExecutionCalls { get; }

        int ReconciliationCalls { get; }
    }

    sealed class EchoWorkerAdapter(RequestContractReference request) : ICountingWorkerAdapter
    {
        public DurableOperationAdapterCapabilities Capabilities { get; } = new(
            DurableOperationIdempotencyEvidence.TargetDeduplication,
            DurableOperationReconciliationCapability.Supported,
            [request]);

        public int ExecutionCalls { get; private set; }

        public int ReconciliationCalls { get; private set; }

        public ValueTask<DurableOperationAttemptObservation> ExecuteAsync(
            OperationContext context,
            DurableOperationInvocation invocation)
        {
            context.ThrowIfCancellationRequested();
            ExecutionCalls++;
            return ValueTask.FromResult<DurableOperationAttemptObservation>(
                new DurableOperationOutcomeObservation(
                    new RequestResultOutcome(new("result"), invocation.Request.Payload)));
        }

        public ValueTask<DurableOperationReconciliationObservation> ReconcileAsync(
            OperationContext context,
            DurableOperationReconciliationRequest request)
        {
            context.ThrowIfCancellationRequested();
            ReconciliationCalls++;
            return ValueTask.FromResult<DurableOperationReconciliationObservation>(new DurableOperationUnresolved());
        }
    }

    sealed class FirstUnresolvedThenEchoWorkerAdapter(RequestContractReference request)
        : ICountingWorkerAdapter
    {
        public DurableOperationAdapterCapabilities Capabilities { get; } = new(
            DurableOperationIdempotencyEvidence.TargetDeduplication,
            DurableOperationReconciliationCapability.Supported,
            [request]);

        public int ExecutionCalls { get; private set; }

        public int ReconciliationCalls { get; private set; }

        public ValueTask<DurableOperationAttemptObservation> ExecuteAsync(
            OperationContext context,
            DurableOperationInvocation invocation)
        {
            context.ThrowIfCancellationRequested();
            ExecutionCalls++;
            return ValueTask.FromResult<DurableOperationAttemptObservation>(ExecutionCalls == 1
                ? new DurableOperationFailureObservation(new(
                    DurableOperationFailurePhase.PostCommitPreAcknowledgement,
                    DurableOperationEffectEvidence.Ambiguous,
                    DurableOperationFailureDisposition.Retryable,
                    "tests.worker.first-outcome-ambiguous"))
                : new DurableOperationOutcomeObservation(
                    new RequestResultOutcome(new("result"), invocation.Request.Payload)));
        }

        public ValueTask<DurableOperationReconciliationObservation> ReconcileAsync(
            OperationContext context,
            DurableOperationReconciliationRequest request)
        {
            context.ThrowIfCancellationRequested();
            ReconciliationCalls++;
            return ValueTask.FromResult<DurableOperationReconciliationObservation>(
                new DurableOperationUnresolved());
        }
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

    sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    sealed class CrashOnce(
        ProcessStoreMutationKind mutation,
        ProcessStoreCrashPhase phase)
    {
        bool crashed;

        internal int CrashCount { get; private set; }

        internal bool ShouldCrash(ProcessStoreCrashContext context)
        {
            if (crashed || context.MutationKind != mutation || context.Phase != phase)
            {
                return false;
            }

            crashed = true;
            CrashCount++;
            return true;
        }
    }
}
