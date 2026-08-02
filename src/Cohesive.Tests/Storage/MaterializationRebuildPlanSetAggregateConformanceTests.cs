using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Processes.Execution;
using Cohesive.Storage.Materialization;
using Cohesive.Storage.Processes;

namespace Cohesive.Tests.Storage;

public sealed partial class MaterializationRebuildProcessConformanceTests
{
    [Fact]
    public async Task CanonicalPlanSetParent_AwaitAllPersistsExactPartialPromotionReceipt()
    {
        using var fixture = CreateAggregateFixture(AggregateFailureMode.OnePromotion);
        ProcessContinuationIdentity continuation = new(
            processInstanceId: new("process-instance/materialization-plan-set/partial-promotion"),
            processAttemptId: new("process-attempt/materialization-plan-set/partial-promotion/1"));
        var start = PlanSetStart(
            fixture.PlanSet,
            fixture.Artifacts,
            continuation,
            StartedAtUtc);

        var terminal = await RunPlanSetToTerminalAsync(fixture, continuation, start);
        var receipt = TerminalReceipt(terminal);

        Assert.Equal(ExecutionTerminalOutcomeKind.Completed, terminal.Checkpoint.Continuation.Terminal.Kind);
        Assert.Equal(MaterializationRebuildPlanSetOutcome.PartiallyPromoted, receipt.Outcome);
        Assert.Equal(continuation, receipt.ParentContinuation);
        Assert.NotNull(receipt.ReadyBarrier);
        Assert.Equal(fixture.PlanSet.LeafPlans.Length, receipt.Leaves.Length);
        var promoted = Assert.Single(receipt.Leaves,
            static leaf => leaf.Outcome == MaterializationRebuildPlanSetLeafOutcome.Promoted);
        var failed = Assert.Single(receipt.Leaves,
            static leaf => leaf.Outcome == MaterializationRebuildPlanSetLeafOutcome.Failed);
        Assert.Equal(fixture.RejectedPromotionTarget, failed.Authority.PlacementSlice.Target);
        Assert.Equal(
            MaterializationRebuildPlanSetDurableOperationDiagnosticCodes.PromotionResultInexact,
            failed.Failure?.Code);
        Assert.Equal(MaterializationRebuildPlanSetLeafPhase.Promotion, failed.TerminalEvidence?.Phase);
        Assert.Equal(failed.PromotionChild, failed.TerminalEvidence?.Child);
        Assert.Equal(
            MaterializationRebuildPlanSetProcessFactory.FailedOutcome,
            failed.TerminalEvidence?.TerminalOutcome);
        Assert.Equal(
            terminal.Checkpoint.Continuation.Children.Single(
                child => child.Continuation == failed.PromotionChild).Result,
            failed.TerminalEvidence?.TerminalResult);
        Assert.Equal(
            InjectedPromotionFailure,
            RequireString(failed.TerminalEvidence?.TerminalResult));
        Assert.NotNull(promoted.Promotion);
        Assert.True(promoted.Promotion!.IsCurrentlySelected);
        Assert.NotNull(promoted.PromotionChild);
        Assert.Equal(promoted.Authority, promoted.Promotion.Request.Authority);
        Assert.Equal(promoted.Ready?.Generation, promoted.Promotion.Request.ActiveGeneration.Generation);
        Assert.All(receipt.Leaves, leaf =>
            Assert.Contains(fixture.PlanSet.LeafPlans, binding => binding == leaf.Authority.Binding));
        var finalization = Assert.Single(terminal.Checkpoint.DurableOperations,
            operation => operation.Request.Contract == fixture.Artifacts.FinalizeRequest);
        Assert.Equal(DurableOperationStatus.Dispositioned, finalization.Status);
        Assert.Equal(
            MaterializationRebuildPlanSetProcessFactory.CompletedOutcome,
            finalization.Acknowledgement?.Outcome.Id);
        Assert.Equal(receipt, MaterializationRebuildPlanSetReceiptJsonSerializer.DeserializeStructural(
            RequireString(finalization.Acknowledgement?.Outcome.Value)));

        await AssertDurableAndReferenceEquivalentAsync(fixture, continuation, start, terminal, receipt);
    }

    [Fact]
    public async Task CanonicalPlanSetParent_AwaitAllCompletesSiblingAfterOneBuildFails()
    {
        using var fixture = CreateAggregateFixture(AggregateFailureMode.OneBuild);
        ProcessContinuationIdentity continuation = new(
            processInstanceId: new("process-instance/materialization-plan-set/build-failed"),
            processAttemptId: new("process-attempt/materialization-plan-set/build-failed/1"));
        var start = PlanSetStart(
            fixture.PlanSet,
            fixture.Artifacts,
            continuation,
            StartedAtUtc);

        var terminal = await RunPlanSetToTerminalAsync(fixture, continuation, start);
        var receipt = TerminalReceipt(terminal);

        Assert.Equal(ExecutionTerminalOutcomeKind.Failed, terminal.Checkpoint.Continuation.Terminal.Kind);
        Assert.Equal(MaterializationRebuildPlanSetOutcome.Failed, receipt.Outcome);
        Assert.Equal(continuation, receipt.ParentContinuation);
        Assert.Null(receipt.ReadyBarrier);
        Assert.Equal(fixture.PlanSet.LeafPlans.Length, receipt.Leaves.Length);
        var failed = Assert.Single(receipt.Leaves,
            static leaf => leaf.Outcome == MaterializationRebuildPlanSetLeafOutcome.Failed);
        var ready = Assert.Single(receipt.Leaves,
            static leaf => leaf.Outcome == MaterializationRebuildPlanSetLeafOutcome.Ready);
        Assert.Equal(
            MaterializationRebuildPlanSetDurableOperationDiagnosticCodes.LeafNotReady,
            failed.Failure?.Code);
        Assert.Equal(MaterializationRebuildPlanSetLeafPhase.Build, failed.TerminalEvidence?.Phase);
        Assert.Equal(failed.BuildChild, failed.TerminalEvidence?.Child);
        Assert.Equal(
            MaterializationRebuildPlanSetProcessFactory.FailedOutcome,
            failed.TerminalEvidence?.TerminalOutcome);
        Assert.Equal(
            terminal.Checkpoint.Continuation.Children.Single(
                child => child.Continuation == failed.BuildChild).Result,
            failed.TerminalEvidence?.TerminalResult);
        Assert.NotNull(ready.Ready);
        Assert.Equal(ready.Authority, ready.Ready!.Authority);
        Assert.Equal(ready.BuildChild, ready.Ready.Attempt.Continuation);
        Assert.Null(ready.PromotionChild);
        Assert.All(receipt.Leaves, leaf =>
            Assert.Contains(fixture.PlanSet.LeafPlans, binding => binding == leaf.Authority.Binding));
        Assert.DoesNotContain(
            terminal.Checkpoint.DurableOperations,
            operation => operation.Request.Contract == fixture.Artifacts.PromotionInvocationRequest);
        Assert.DoesNotContain(
            terminal.Checkpoint.Continuation.Children,
            child => child.Process == fixture.Artifacts.PromotionWorkerPlan.DefinitionReference);
        var readiness = Assert.Single(terminal.Checkpoint.DurableOperations,
            operation => operation.Request.Contract == fixture.Artifacts.ReadinessBarrierRequest);
        Assert.Equal(DurableOperationStatus.Dispositioned, readiness.Status);
        Assert.Equal(
            MaterializationRebuildPlanSetProcessFactory.FailedOutcome,
            readiness.Acknowledgement?.Outcome.Id);
        Assert.Equal(receipt, MaterializationRebuildPlanSetReceiptJsonSerializer.DeserializeStructural(
            RequireString(readiness.Acknowledgement?.Outcome.Value)));

        await AssertDurableAndReferenceEquivalentAsync(fixture, continuation, start, terminal, receipt);
    }

    const string InjectedBuildFailure = "tests.materialization-plan-set.build.injected-failure";
    const string InjectedPromotionFailure = "tests.materialization-plan-set.promotion.injected-failure";

    static AggregatePlanSetFixture CreateAggregateFixture(AggregateFailureMode failureMode)
    {
        var materialization = CreateMaterializationFixture();
        var scenario = MaterializationRebuildPlanSetTests.CreateIndependentTwoLeafScenario(
            materialization.Plan);
        var planSet = scenario.PlanSet;
        var artifacts = MaterializationRebuildPlanSetProcessFactory.Create(planSet);
        var targets = scenario.Leaves.Select(leaf => leaf.Target.Id == materialization.Target.Descriptor.Id
                ? materialization.Target
                : new InMemoryMaterializationTarget(leaf.Target))
            .ToImmutableArray();
        var resolved = scenario.Leaves.Select((leaf, index) => new ResolvedMaterializationRebuildPlan(
                planSet,
                leaf,
                targets[index],
                new InMemoryMaterializationProgressStore(),
                leaf.Shards.Select(shard => materialization.Resolved.GetShard(shard.Id)),
                leaf.ChangeFeeds.Select(feed => materialization.Resolved.GetChangeFeed(feed.Id))))
            .ToImmutableArray();
        var executionResolver = new PlanSetLeafExecutionResolver(resolved, StartedAtUtc);
        var planSetResolver = new ExactPlanSetResolver(planSet);

        var initialization = new MaterializationRebuildInitializationDurableOperationAdapter(
            request: artifacts.Leaf.InitializationRequest,
            resolver: executionResolver);
        var rejectedBuildTarget = scenario.Leaves[0].Target.Id;
        IDurableOperationAdapter initializationAdapter = failureMode == AggregateFailureMode.OneBuild
            ? new InjectedOutcomeAdapter(
                initialization,
                request => BuildTarget(request) == rejectedBuildTarget,
                MaterializationRebuildProcessFactory.FailedOutcome,
                InjectedBuildFailure)
            : initialization;
        var shardRuntime = new ProcessDurableRuntime(
            store: new InMemoryProcessDurableStore(),
            host: RejectingHost.Instance,
            options: RuntimeOptions("worker/materialization-plan-set/aggregate/shards"),
            bindingResolver: new ExactBindingResolver([artifacts.Leaf.ShardRebuildBinding]),
            operationAdapterResolver: new ExactAdapterResolver(
            [
                new MaterializationRebuildShardDurableOperationAdapter(
                    request: artifacts.Leaf.ShardRebuildRequest,
                    resolver: executionResolver)
            ]));
        var leafRuntime = new ProcessDurableRuntime(
            store: new InMemoryProcessDurableStore(),
            host: RejectingHost.Instance,
            options: RuntimeOptions("worker/materialization-plan-set/aggregate/leaves"),
            bindingResolver: new ExactBindingResolver(
            [
                artifacts.Leaf.InitializationBinding,
                artifacts.Leaf.WorkerInvocationBinding,
                artifacts.Leaf.SynchronizationPreparationBinding
            ]),
            operationAdapterResolver: new ExactAdapterResolver(
            [
                initializationAdapter,
                new ProcessChildDurableOperationAdapter(
                    runtime: shardRuntime,
                    planResolver: new ExactChildPlanResolver(artifacts.Leaf.WorkerPlan),
                    supportedRequests: [artifacts.Leaf.WorkerInvocationRequest]),
                new MaterializationSynchronizationPreparationDurableOperationAdapter(
                    request: artifacts.Leaf.SynchronizationPreparationRequest,
                    resolver: executionResolver)
            ]));

        var targetPool = new InMemoryMaterializationTargetPool(
            definition: planSet.Placement.BackendPool.Definition,
            targets: targets);
        var router = new InMemoryMaterializationBackendRouter(
            document: planSet.Placement.BackendPool,
            targets: targetPool,
            timeProvider: new FixedTimeProvider(StartedAtUtc.AddMinutes(1)));
        var promotionStore = new InMemoryProcessDurableStore();
        var applyPromotion = new MaterializationIndependentPromotionDurableOperationAdapter(
            request: artifacts.ApplyPromotionRequest,
            resolver: planSetResolver,
            router,
            promotionPlan: artifacts.PromotionWorkerPlan);
        var rejectedPromotionTarget = scenario.Leaves[^1].Target.Id;
        IDurableOperationAdapter applyPromotionAdapter = failureMode == AggregateFailureMode.OnePromotion
            ? new InjectedOutcomeAdapter(
                applyPromotion,
                request => PromotionTarget(request) == rejectedPromotionTarget,
                MaterializationRebuildPlanSetProcessFactory.FailedOutcome,
                InjectedPromotionFailure)
            : applyPromotion;
        var promotionRuntime = new ProcessDurableRuntime(
            store: promotionStore,
            host: RejectingHost.Instance,
            options: RuntimeOptions("worker/materialization-plan-set/aggregate/promotions"),
            bindingResolver: new ExactBindingResolver(
            [
                artifacts.ActivateReadyBinding,
                artifacts.PreparePromotionBinding,
                artifacts.ApplyPromotionBinding
            ]),
            operationAdapterResolver: new ExactAdapterResolver(
            [
                new MaterializationReadyGenerationActivationDurableOperationAdapter(
                    request: artifacts.ActivateReadyRequest,
                    resolver: executionResolver,
                    promotionWorkerPlan: artifacts.PromotionWorkerPlan),
                new MaterializationIndependentPromotionPreparationDurableOperationAdapter(
                    request: artifacts.PreparePromotionRequest,
                    resolver: planSetResolver,
                    router,
                    store: promotionStore,
                    promotionPlan: artifacts.PromotionWorkerPlan),
                applyPromotionAdapter
            ]));

        var parentStore = new InMemoryProcessDurableStore();
        var parentRuntime = new ProcessDurableRuntime(
            store: parentStore,
            host: RejectingHost.Instance,
            options: RuntimeOptions("worker/materialization-plan-set/aggregate/parent"),
            bindingResolver: new ExactBindingResolver(
            [
                artifacts.InitializationBinding,
                artifacts.LeafInvocationBinding,
                artifacts.ReadinessBarrierBinding,
                artifacts.PromotionInvocationBinding,
                artifacts.FinalizeBinding
            ]),
            operationAdapterResolver: new ExactAdapterResolver(
            [
                new MaterializationRebuildPlanSetInitializationDurableOperationAdapter(
                    request: artifacts.InitializationRequest,
                    resolver: planSetResolver,
                    parentPlan: artifacts.ParentPlan),
                new ProcessChildDurableOperationAdapter(
                    runtime: leafRuntime,
                    planResolver: new ExactChildPlanResolver(artifacts.Leaf.CoordinatorPlan),
                    supportedRequests: [artifacts.LeafInvocationRequest]),
                new MaterializationRebuildReadyBarrierDurableOperationAdapter(
                    request: artifacts.ReadinessBarrierRequest,
                    resolver: planSetResolver,
                    store: parentStore,
                    parentPlan: artifacts.ParentPlan),
                new ProcessChildDurableOperationAdapter(
                    runtime: promotionRuntime,
                    planResolver: new ExactChildPlanResolver(artifacts.PromotionWorkerPlan),
                    supportedRequests: [artifacts.PromotionInvocationRequest]),
                new MaterializationRebuildPlanSetFinalizationDurableOperationAdapter(
                    request: artifacts.FinalizeRequest,
                    resolver: planSetResolver,
                    store: parentStore,
                    parentPlan: artifacts.ParentPlan)
            ]));
        return new(
            planSet,
            artifacts,
            parentRuntime,
            parentStore,
            router,
            rejectedPromotionTarget);
    }

    static async Task<ProcessDurableStoreSnapshot> RunPlanSetToTerminalAsync(
        AggregatePlanSetFixture fixture,
        ProcessContinuationIdentity continuation,
        ProcessStartReceipt start)
    {
        var context = OperationContext.Create(timeProvider: new FixedTimeProvider(StartedAtUtc));
        var initialized = await fixture.ParentRuntime.InitializeAsync(
            context,
            fixture.Artifacts.ParentPlan,
            start);
        Assert.Equal(ProcessDurableRuntimeDisposition.Applied, initialized.Disposition);
        return await DrivePlanSetToTerminalAsync(
            fixture.ParentRuntime,
            context,
            fixture.Artifacts,
            continuation,
            Assert.IsType<ProcessDurableStoreSnapshot>(initialized.Snapshot));
    }

    static MaterializationRebuildPlanSetReceipt TerminalReceipt(ProcessDurableStoreSnapshot snapshot) =>
        MaterializationRebuildPlanSetReceiptJsonSerializer.DeserializeStructural(
            RequireString(snapshot.Checkpoint.Continuation.Terminal.Detail?.Value));

    static async Task AssertDurableAndReferenceEquivalentAsync(
        AggregatePlanSetFixture fixture,
        ProcessContinuationIdentity continuation,
        ProcessStartReceipt start,
        ProcessDurableStoreSnapshot terminal,
        MaterializationRebuildPlanSetReceipt receipt)
    {
        var durable = Assert.IsType<ProcessDurableStoreSnapshot>(await fixture.ParentStore.LoadAsync(
            OperationContext.Create(timeProvider: new FixedTimeProvider(StartedAtUtc)),
            continuation.ProcessInstanceId));
        Assert.Equal(
            ProcessStorageContentFingerprints.Continuation(terminal.Checkpoint.Continuation),
            ProcessStorageContentFingerprints.Continuation(durable.Checkpoint.Continuation));
        Assert.Equal(receipt, TerminalReceipt(durable));

        var reference = ProcessReferenceInterpreter.Create(fixture.Artifacts.ParentPlan, start);
        foreach (var activation in durable.Checkpoint.Activations)
        {
            var decision = ProcessReferenceInterpreter.Activate(
                fixture.Artifacts.ParentPlan,
                reference,
                activation.Activation,
                RejectingHost.Instance);
            Assert.Equal(activation.Disposition, decision.Disposition);
            Assert.Equal(
                activation.AfterContinuation,
                ProcessStorageContentFingerprints.Continuation(decision.State));
            reference = decision.State;
        }
        Assert.Equal(
            ProcessStorageContentFingerprints.Continuation(durable.Checkpoint.Continuation),
            ProcessStorageContentFingerprints.Continuation(reference));
        Assert.Equal(receipt, MaterializationRebuildPlanSetReceiptJsonSerializer.DeserializeStructural(
            RequireString(reference.Terminal.Detail?.Value)));
    }

    static MaterializationTargetId PromotionTarget(RequestEnvelope request)
    {
        var payload = Assert.IsType<ObservationValue>(request.Payload.Value).GetRequiredString();
        return MaterializationIndependentPromotionRequestJsonSerializer.Deserialize(payload)
            .Authority.PlacementSlice.Target;
    }

    static MaterializationTargetId BuildTarget(RequestEnvelope request)
    {
        var payload = RequireString(request.Payload);
        Assert.True(MaterializationRebuildWorkReferenceJsonSerializer.TryDeserializeAuthority(
            payload,
            out var authority,
            out _));
        return Assert.IsType<MaterializationRebuildLeafExecutionAuthority>(authority)
            .PlacementSlice.Target;
    }

    sealed class InjectedOutcomeAdapter(
        IDurableOperationAdapter inner,
        Func<RequestEnvelope, bool> reject,
        RequestTerminalOutcomeId outcome,
        string detail) : IDurableOperationAdapter
    {
        public DurableOperationAdapterCapabilities Capabilities => inner.Capabilities;

        public ValueTask<DurableOperationAttemptObservation> ExecuteAsync(
            OperationContext context,
            DurableOperationInvocation invocation) =>
            reject(invocation.Request)
                ? ValueTask.FromResult<DurableOperationAttemptObservation>(new DurableOperationOutcomeObservation(
                    Failure()))
                : inner.ExecuteAsync(context, invocation);

        public ValueTask<DurableOperationReconciliationObservation> ReconcileAsync(
            OperationContext context,
            DurableOperationReconciliationRequest request) =>
            reject(request.Request)
                ? ValueTask.FromResult<DurableOperationReconciliationObservation>(
                    new DurableOperationReconciledOutcome(Failure()))
                : inner.ReconcileAsync(context, request);

        RequestFailureOutcome Failure() => new(
            outcome,
            PortableValue.Concrete(
                new(new ScalarTypeRef(ScalarTypeKind.String)),
                ObservationValue.FromString(detail)));
    }

    sealed record AggregatePlanSetFixture(
        MaterializationRebuildPlanSet PlanSet,
        MaterializationRebuildPlanSetProcessArtifacts Artifacts,
        ProcessDurableRuntime ParentRuntime,
        InMemoryProcessDurableStore ParentStore,
        InMemoryMaterializationBackendRouter Router,
        MaterializationTargetId RejectedPromotionTarget) : IDisposable
    {
        public void Dispose() => Router.Dispose();
    }

    enum AggregateFailureMode
    {
        OneBuild,
        OnePromotion
    }
}
