using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Processes.Execution;
using Cohesive.Storage.Materialization;
using Cohesive.Storage.Processes;

namespace Cohesive.Tests.Storage;

public sealed partial class MaterializationRebuildProcessConformanceTests
{
    [Fact]
    public async Task CanonicalPlanSetParent_CrashReplayPreservesChildAndConvergesWithReferenceInterpretation()
    {
        var materialization = CreateMaterializationFixture();
        var planSet = MaterializationRebuildPlanJsonSerializerTests.CreateSinglePlanSet(materialization.Plan);
        var artifacts = MaterializationRebuildPlanSetProcessFactory.Create(planSet);
        var executionResolver = new PlanSetLeafExecutionResolver(materialization.Resolved, StartedAtUtc);
        var planSetResolver = new ExactPlanSetResolver(planSet);
        var context = OperationContext.Create(timeProvider: new FixedTimeProvider(StartedAtUtc));

        var workerRuntime = new ProcessDurableRuntime(
            store: new InMemoryProcessDurableStore(),
            host: RejectingHost.Instance,
            options: RuntimeOptions("worker/materialization-plan-set/shards"),
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
            options: RuntimeOptions("worker/materialization-plan-set/leaves"),
            bindingResolver: new ExactBindingResolver(
            [
                artifacts.Leaf.InitializationBinding,
                artifacts.Leaf.WorkerInvocationBinding,
                artifacts.Leaf.SynchronizationPreparationBinding
            ]),
            operationAdapterResolver: new ExactAdapterResolver(
            [
                new MaterializationRebuildInitializationDurableOperationAdapter(
                    request: artifacts.Leaf.InitializationRequest,
                    resolver: executionResolver),
                new ProcessChildDurableOperationAdapter(
                    runtime: workerRuntime,
                    planResolver: new ExactChildPlanResolver(artifacts.Leaf.WorkerPlan),
                    supportedRequests: [artifacts.Leaf.WorkerInvocationRequest]),
                new MaterializationSynchronizationPreparationDurableOperationAdapter(
                    request: artifacts.Leaf.SynchronizationPreparationRequest,
                    resolver: executionResolver)
            ]));

        var targetPool = new InMemoryMaterializationTargetPool(
            definition: planSet.Placement.BackendPool.Definition,
            targets: [materialization.Target]);
        using var router = new InMemoryMaterializationBackendRouter(
            document: planSet.Placement.BackendPool,
            targets: targetPool,
            timeProvider: new FixedTimeProvider(StartedAtUtc.AddMinutes(1)));
        var promotionStore = new InMemoryProcessDurableStore();
        var activationAdapter = new MaterializationReadyGenerationActivationDurableOperationAdapter(
            request: artifacts.ActivateReadyRequest,
            resolver: executionResolver,
            promotionWorkerPlan: artifacts.PromotionWorkerPlan);
        var promotionRuntime = new ProcessDurableRuntime(
            store: promotionStore,
            host: RejectingHost.Instance,
            options: RuntimeOptions("worker/materialization-plan-set/promotions"),
            bindingResolver: new ExactBindingResolver(
            [
                artifacts.ActivateReadyBinding,
                artifacts.PreparePromotionBinding,
                artifacts.ApplyPromotionBinding
            ]),
            operationAdapterResolver: new ExactAdapterResolver(
            [
                activationAdapter,
                new MaterializationIndependentPromotionPreparationDurableOperationAdapter(
                    request: artifacts.PreparePromotionRequest,
                    resolver: planSetResolver,
                    router,
                    store: promotionStore,
                    promotionPlan: artifacts.PromotionWorkerPlan),
                new MaterializationIndependentPromotionDurableOperationAdapter(
                    request: artifacts.ApplyPromotionRequest,
                    resolver: planSetResolver,
                    router,
                    promotionPlan: artifacts.PromotionWorkerPlan)
            ]));

        var crashArmed = false;
        var crashed = false;
        var childCompleted = false;
        var parentStore = new InMemoryProcessDurableStore(crash =>
        {
            if (!crashArmed
                || crashed
                || !childCompleted
                || crash.MutationKind != ProcessStoreMutationKind.AggregateCommit
                || crash.Phase != ProcessStoreCrashPhase.AfterAtomicCommitBeforeReturn)
            {
                return false;
            }

            crashed = true;
            return true;
        });
        var leafChildAdapter = new CompletionObservingAdapter(
            new ProcessChildDurableOperationAdapter(
                runtime: leafRuntime,
                planResolver: new ExactChildPlanResolver(artifacts.Leaf.CoordinatorPlan),
                supportedRequests: [artifacts.LeafInvocationRequest]),
            () => childCompleted = true);
        var parentRuntime = new ProcessDurableRuntime(
            store: parentStore,
            host: RejectingHost.Instance,
            options: RuntimeOptions(
                "worker/materialization-plan-set/parent",
                maxAmbiguousStoreMutationAttempts: 1),
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
                leafChildAdapter,
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

        ProcessContinuationIdentity parentContinuation = new(
            processInstanceId: new("process-instance/materialization-plan-set/conformance"),
            processAttemptId: new("process-attempt/materialization-plan-set/1"));
        var start = PlanSetStart(planSet, artifacts, parentContinuation, StartedAtUtc);
        var initialized = await parentRuntime.InitializeAsync(context, artifacts.ParentPlan, start);
        Assert.Equal(ProcessDurableRuntimeDisposition.Applied, initialized.Disposition);

        var snapshot = await ActivatePlanSetAsync(
            parentRuntime,
            context,
            artifacts,
            parentContinuation,
            Assert.IsType<ProcessDurableStoreSnapshot>(initialized.Snapshot),
            ProcessActivationCause.Start);
        snapshot = await AdvanceOnlyOperationAsync(parentRuntime, context, artifacts, parentContinuation, snapshot);
        snapshot = await ActivatePlanSetAsync(
            parentRuntime,
            context,
            artifacts,
            parentContinuation,
            snapshot,
            ProcessActivationCause.Interaction);

        var buildChild = Assert.Single(snapshot.Checkpoint.Continuation.Children);
        var buildOperation = Assert.Single(snapshot.Checkpoint.DurableOperations, operation =>
            operation.Request.Contract == artifacts.LeafInvocationRequest
            && operation.Status != DurableOperationStatus.Dispositioned);
        crashArmed = true;

        var unknown = await parentRuntime.AdvanceOperationAsync(
            context,
            artifacts.ParentPlan,
            parentContinuation.ProcessInstanceId,
            buildOperation.OperationId);

        Assert.Equal(ProcessDurableRuntimeDisposition.CommitOutcomeUnknown, unknown.Disposition);
        Assert.True(crashed);
        var afterCrash = Assert.IsType<ProcessDurableStoreSnapshot>(await parentStore.LoadAsync(
            context,
            parentContinuation.ProcessInstanceId));
        Assert.Equal(buildChild.Continuation, Assert.Single(afterCrash.Checkpoint.Continuation.Children).Continuation);
        var committedOperation = afterCrash.Checkpoint.DurableOperations.Single(
            operation => operation.OperationId == buildOperation.OperationId);
        Assert.Equal(DurableOperationStatus.Acknowledged, committedOperation.Status);
        Assert.NotNull(committedOperation.Acknowledgement);

        var replay = await parentRuntime.AdvanceOperationAsync(
            context,
            artifacts.ParentPlan,
            parentContinuation.ProcessInstanceId,
            buildOperation.OperationId);
        Assert.True(replay.Disposition is ProcessDurableRuntimeDisposition.Applied
            or ProcessDurableRuntimeDisposition.Replayed);
        snapshot = Assert.IsType<ProcessDurableStoreSnapshot>(replay.Snapshot);
        Assert.Equal(DurableOperationStatus.Dispositioned, snapshot.Checkpoint.DurableOperations.Single(
            operation => operation.OperationId == buildOperation.OperationId).Status);
        var exactReplay = await parentRuntime.AdvanceOperationAsync(
            context,
            artifacts.ParentPlan,
            parentContinuation.ProcessInstanceId,
            buildOperation.OperationId);
        Assert.Equal(ProcessDurableRuntimeDisposition.Replayed, exactReplay.Disposition);
        snapshot = Assert.IsType<ProcessDurableStoreSnapshot>(exactReplay.Snapshot);
        snapshot = await DrivePlanSetToTerminalAsync(
            parentRuntime,
            context,
            artifacts,
            parentContinuation,
            snapshot);

        Assert.Equal(ExecutionTerminalOutcomeKind.Completed, snapshot.Checkpoint.Continuation.Terminal.Kind);
        var receipt = MaterializationRebuildPlanSetReceiptJsonSerializer.DeserializeStructural(
            RequireString(snapshot.Checkpoint.Continuation.Terminal.Detail?.Value));
        var leafReceipt = Assert.Single(receipt.Leaves);
        Assert.Equal(MaterializationRebuildPlanSetOutcome.Completed, receipt.Outcome);
        Assert.Equal(MaterializationRebuildPlanSetLeafOutcome.Promoted, leafReceipt.Outcome);
        Assert.Equal(buildChild.Continuation, leafReceipt.BuildChild);
        Assert.NotNull(receipt.ReadyBarrier);
        Assert.Equal(parentContinuation, receipt.ParentContinuation);
        Assert.Equal(1, executionResolver.Count);

        var execution = executionResolver.Single;
        Assert.Equal(execution.Generation, leafReceipt.Ready?.Generation);
        var read = await router.ResolveReadAsync(context, leafReceipt.Authority.PlacementSlice);
        var write = await router.ResolveWriteAsync(context, leafReceipt.Authority.PlacementSlice);
        Assert.Equal(execution.Generation, read.Generation.GenerationId);
        Assert.Equal(read.Generation, write.Generation);
        Assert.Same(materialization.Target, read.Target);
        Assert.Same(materialization.Target, write.Target);

        var status = MaterializationRebuildPlanSetStatusProjector.CreateRuntimeDetails(
            planSet,
            artifacts,
            snapshot,
            Provenance("plan-set-status"));
        var statusRoot = Assert.IsType<ObservationValue>(Assert.Single(status.Extensions).Value.Value!.Value);
        Assert.Equal(
            MaterializationRebuildPlanSetOutcome.Completed.ToString(),
            statusRoot.GetProperty("aggregateOutcome").GetRequiredString());
        Assert.Equal(
            MaterializationRebuildPlanSetReceiptJsonSerializer.Serialize(receipt),
            statusRoot.GetProperty("aggregateReceipt").GetRequiredString());

        var reference = ProcessReferenceInterpreter.Create(artifacts.ParentPlan, start);
        foreach (var activation in snapshot.Checkpoint.Activations)
        {
            var decision = ProcessReferenceInterpreter.Activate(
                artifacts.ParentPlan,
                reference,
                activation.Activation,
                RejectingHost.Instance);
            Assert.Equal(activation.Disposition, decision.Disposition);
            Assert.Equal(activation.AfterContinuation, ProcessStorageContentFingerprints.Continuation(decision.State));
            reference = decision.State;
        }
        Assert.Equal(
            ProcessStorageContentFingerprints.Continuation(snapshot.Checkpoint.Continuation),
            ProcessStorageContentFingerprints.Continuation(reference));
        Assert.Equal(receipt, MaterializationRebuildPlanSetReceiptJsonSerializer.DeserializeStructural(
            RequireString(reference.Terminal.Detail?.Value)));
    }

    static ProcessDurableRuntimeOptions RuntimeOptions(
        string workerId,
        int maxAmbiguousStoreMutationAttempts = 3) => new(
        workerId,
        workerLease: TimeSpan.FromMinutes(5),
        maxAmbiguousStoreMutationAttempts);

    static ProcessStartReceipt PlanSetStart(
        MaterializationRebuildPlanSet planSet,
        MaterializationRebuildPlanSetProcessArtifacts artifacts,
        ProcessContinuationIdentity continuation,
        DateTimeOffset startedAtUtc)
    {
        var request = new ProcessStartRequest(
            schemaVersion: ProcessStartRequest.CurrentSchemaVersion,
            definition: artifacts.ParentPlan.DefinitionReference,
            context: new(
                commandId: new($"command/materialization-plan-set/{continuation.ProcessAttemptId.Value}"),
                idempotencyKey: new($"idempotency/materialization-plan-set/{continuation.ProcessAttemptId.Value}"),
                processInstanceId: continuation.ProcessInstanceId,
                authorization: new(
                    actor: "operator/materialization-plan-set-conformance",
                    authorityScope: Authority,
                    evidenceReference: "policy/materialization-plan-set/allow"),
                issuedAtUtc: startedAtUtc,
                provenance: artifacts.ParentProcessDocument.Metadata.Provenance),
            initialContinuation: continuation,
            input: PortableValue.Concrete(
                artifacts.ParentPlan.Definition.Input,
                ObservationValue.FromString(MaterializationRebuildPlanSetReferenceJsonSerializer.Serialize(
                    MaterializationRebuildPlanSetReference.FromPlanSet(planSet)))));
        return new(request, acceptedAtUtc: startedAtUtc);
    }

    static async Task<ProcessDurableStoreSnapshot> DrivePlanSetToTerminalAsync(
        ProcessDurableRuntime runtime,
        OperationContext context,
        MaterializationRebuildPlanSetProcessArtifacts artifacts,
        ProcessContinuationIdentity continuation,
        ProcessDurableStoreSnapshot snapshot)
    {
        for (var step = 0; step < 64; step++)
        {
            if (snapshot.Checkpoint.Continuation.Terminal.Kind != ExecutionTerminalOutcomeKind.None)
                return snapshot;

            var operation = snapshot.Checkpoint.DurableOperations
                .Where(static candidate => candidate.Status != DurableOperationStatus.Dispositioned)
                .OrderBy(static candidate => candidate.OperationId.Value, StringComparer.Ordinal)
                .FirstOrDefault();
            if (operation is not null)
            {
                var advanced = await runtime.AdvanceOperationAsync(
                    context,
                    artifacts.ParentPlan,
                    continuation.ProcessInstanceId,
                    operation.OperationId);
                Assert.True(
                    advanced.Disposition is ProcessDurableRuntimeDisposition.Applied
                        or ProcessDurableRuntimeDisposition.Replayed,
                    string.Join(Environment.NewLine, advanced.Diagnostics.Select(static diagnostic => diagnostic.Message)));
                snapshot = Assert.IsType<ProcessDurableStoreSnapshot>(advanced.Snapshot);
                continue;
            }

            var pendingInputs = PendingPlanSetInputs(snapshot.Checkpoint);
            var state = snapshot.Checkpoint.Continuation;
            var cause = !pendingInputs.IsEmpty
                ? ProcessActivationCause.Interaction
                : state.CompletedActivationCount == 0
                    ? ProcessActivationCause.Start
                    : state.Tokens.Any(static token => token.Disposition == ExecutionTokenDisposition.Ready)
                        || state.Waits.Any(static wait => wait.Active && wait.Kind is
                            ProcessWaitKind.DurableCut or ProcessWaitKind.RepeatAcrossActivation)
                        ? ProcessActivationCause.Continue
                        : throw new InvalidOperationException("The plan-set parent reached an unexplained nonterminal wait.");
            snapshot = await ActivatePlanSetAsync(
                runtime,
                context,
                artifacts,
                continuation,
                snapshot,
                cause);
        }

        throw new InvalidOperationException(
            "The plan-set parent exceeded its finite conformance drive budget. "
            + $"Activations={snapshot.Checkpoint.Continuation.CompletedActivationCount}; "
            + $"terminal={snapshot.Checkpoint.Continuation.Terminal.Kind}; "
            + $"tokens={string.Join(',', snapshot.Checkpoint.Continuation.Tokens.Select(static token => $"{token.Node.Value}:{token.Disposition}"))}; "
            + $"waits={string.Join(',', snapshot.Checkpoint.Continuation.Waits.Where(static wait => wait.Active).Select(static wait => $"{wait.Node.Value}:{wait.Kind}"))}; "
            + $"operations={string.Join(',', snapshot.Checkpoint.DurableOperations.Select(static operation => $"{operation.Request.Contract.Definition.DefinitionId.Value}:{operation.Status}:{operation.Attempts.LastOrDefault()?.Failure?.Code}:reconciliations={operation.Reconciliations.Length}"))}; "
            + $"pendingInbox={PendingPlanSetInputs(snapshot.Checkpoint).Length}.");
    }

    static async Task<ProcessDurableStoreSnapshot> AdvanceOnlyOperationAsync(
        ProcessDurableRuntime runtime,
        OperationContext context,
        MaterializationRebuildPlanSetProcessArtifacts artifacts,
        ProcessContinuationIdentity continuation,
        ProcessDurableStoreSnapshot snapshot)
    {
        var operation = Assert.Single(snapshot.Checkpoint.DurableOperations, static operation =>
            operation.Status != DurableOperationStatus.Dispositioned);
        var advanced = await runtime.AdvanceOperationAsync(
            context,
            artifacts.ParentPlan,
            continuation.ProcessInstanceId,
            operation.OperationId);
        Assert.True(advanced.Disposition is ProcessDurableRuntimeDisposition.Applied
            or ProcessDurableRuntimeDisposition.Replayed);
        return Assert.IsType<ProcessDurableStoreSnapshot>(advanced.Snapshot);
    }

    static async Task<ProcessDurableStoreSnapshot> ActivatePlanSetAsync(
        ProcessDurableRuntime runtime,
        OperationContext context,
        MaterializationRebuildPlanSetProcessArtifacts artifacts,
        ProcessContinuationIdentity continuation,
        ProcessDurableStoreSnapshot snapshot,
        ProcessActivationCause cause)
    {
        var ordinal = snapshot.Checkpoint.Continuation.CompletedActivationCount + 1;
        var activated = await runtime.ActivateAsync(
            context,
            artifacts.ParentPlan,
            continuation,
            new(
                id: new($"activation/materialization-plan-set/{ordinal}"),
                cause,
                observedAtUtc: context.UtcNow,
                context: new(
                    authorityScope: Authority,
                    correlationId: new("correlation/materialization-plan-set-conformance"),
                    delivery: new(
                        InteractionDurabilityDemand.Durable,
                        InteractionVisibilityDemand.AfterOriginCommit),
                    provenance: artifacts.ParentProcessDocument.Metadata.Provenance),
                inputs: PendingPlanSetInputs(snapshot.Checkpoint)));
        Assert.True(
            activated.Disposition is ProcessDurableRuntimeDisposition.Applied
                or ProcessDurableRuntimeDisposition.Replayed,
            string.Join(Environment.NewLine, activated.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        return Assert.IsType<ProcessDurableStoreSnapshot>(activated.Snapshot);
    }

    static ImmutableArray<ProcessActivationInput> PendingPlanSetInputs(ProcessDurableCheckpoint checkpoint) =>
        [.. checkpoint.Inbox
            .Where(entry => entry.Receipt is null
                && entry.Input.Target.Continuation == checkpoint.ContinuationIdentity)
            .OrderBy(static entry => entry.EmissionId.Value, StringComparer.Ordinal)
            .Select(static entry => entry.Input)];

    static string RequireString(PortableValue? value)
    {
        Assert.NotNull(value);
        var observation = Assert.IsType<ObservationValue>(value.Value);
        Assert.Equal(ObservationValueKind.String, observation.Kind);
        return Assert.IsType<string>(observation.String);
    }

    sealed class ExactPlanSetResolver(MaterializationRebuildPlanSet planSet)
        : IMaterializationRebuildPlanSetExecutionResolver
    {
        public bool TryResolve(
            MaterializationRebuildPlanSetReference reference,
            out MaterializationRebuildPlanSet? resolvedPlanSet)
        {
            var exact = reference == MaterializationRebuildPlanSetReference.FromPlanSet(planSet);
            resolvedPlanSet = exact ? planSet : null;
            return exact;
        }
    }

    sealed class PlanSetLeafExecutionResolver : IMaterializationRebuildExecutionResolver
    {
        readonly ImmutableDictionary<MaterializationRebuildLeafExecutionAuthority, ResolvedMaterializationRebuildPlan>
            resolvedByAuthority;
        readonly Dictionary<ProcessContinuationIdentity, MaterializationRebuildExecution> executions = [];
        readonly IMaterializationSynchronizationWorkStore synchronizationWork =
            new InMemoryMaterializationSynchronizationWorkStore();
        readonly DateTimeOffset startedAtUtc;

        internal PlanSetLeafExecutionResolver(
            ResolvedMaterializationRebuildPlan resolved,
            DateTimeOffset startedAtUtc)
            : this([resolved], startedAtUtc)
        {
        }

        internal PlanSetLeafExecutionResolver(
            ImmutableArray<ResolvedMaterializationRebuildPlan> resolved,
            DateTimeOffset startedAtUtc)
        {
            if (resolved.IsDefaultOrEmpty)
                throw new ArgumentException("At least one resolved leaf is required.", nameof(resolved));
            resolvedByAuthority = resolved.ToImmutableDictionary(static candidate => candidate.Authority);
            this.startedAtUtc = startedAtUtc;
        }

        internal int Count => executions.Count;

        internal MaterializationRebuildExecution Single => Assert.Single(executions.Values);

        public bool TryResolve(
            MaterializationRebuildLeafExecutionAuthority authority,
            ProcessContinuationIdentity continuation,
            out MaterializationRebuildExecution? execution)
        {
            if (!resolvedByAuthority.TryGetValue(authority, out var resolved))
            {
                execution = null;
                return false;
            }

            if (!executions.TryGetValue(continuation, out execution))
            {
                execution = new(
                    resolved,
                    new(continuation, startedAtUtc),
                    synchronizationWork);
                executions.Add(continuation, execution);
            }
            return true;
        }
    }

    sealed class CompletionObservingAdapter(
        IDurableOperationAdapter inner,
        Action completed) : IDurableOperationAdapter
    {
        public DurableOperationAdapterCapabilities Capabilities => inner.Capabilities;

        public async ValueTask<DurableOperationAttemptObservation> ExecuteAsync(
            OperationContext context,
            DurableOperationInvocation invocation)
        {
            var result = await inner.ExecuteAsync(context, invocation);
            completed();
            return result;
        }

        public async ValueTask<DurableOperationReconciliationObservation> ReconcileAsync(
            OperationContext context,
            DurableOperationReconciliationRequest request)
        {
            var result = await inner.ReconcileAsync(context, request);
            completed();
            return result;
        }
    }

}
