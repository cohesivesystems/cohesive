using System.Collections.Immutable;
using Cohesive.Adapters.Postgres;
using Cohesive.Control;
using Cohesive.Execution;
using Cohesive.MaterializationHarness.Materialize;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Compilation;
using Cohesive.Processes.Execution;
using Cohesive.Storage.Materialization;
using Cohesive.Storage.Processes;
using Npgsql;

namespace Cohesive.MaterializationHarness.Host;

sealed class MaterializationHarnessProviderProcess
{
    const int MaximumParentStepsPerDrive = 256;
    static readonly TimeSpan WorkerLease = TimeSpan.FromMinutes(1);
    readonly PostgresProcessDurableStore processStore;
    readonly InteractionAuthorityScope authorityScope;
    readonly ProcessDurableRuntime parentRuntime;
    readonly AttemptExecutionResolver executionResolver;

    MaterializationHarnessProviderProcess(
        FreightOrderRebuildProviderRuntime provider,
        ProcessInstanceId processInstanceId,
        PostgresProcessDurableStore processStore,
        InteractionAuthorityScope authorityScope,
        MaterializationRebuildPlanSetProcessArtifacts artifacts,
        ProcessDurableRuntime parentRuntime,
        AttemptExecutionResolver executionResolver,
        PostgresMaterializationBackendRouter router,
        MaterializationRebuildPlanSetProcessLifecycle lifecycle)
    {
        Provider = provider;
        ProcessInstanceId = processInstanceId;
        this.processStore = processStore;
        this.authorityScope = authorityScope;
        Artifacts = artifacts;
        this.parentRuntime = parentRuntime;
        this.executionResolver = executionResolver;
        Router = router;
        Lifecycle = lifecycle;
    }

    internal FreightOrderRebuildProviderRuntime Provider { get; }

    internal ProcessInstanceId ProcessInstanceId { get; }

    internal MaterializationRebuildPlanSetProcessArtifacts Artifacts { get; }

    internal PostgresMaterializationBackendRouter Router { get; }

    internal MaterializationRebuildPlanSetProcessLifecycle Lifecycle { get; }

    internal static async Task<MaterializationHarnessProviderProcess> CreateAsync(
        FreightOrderRebuildProviderRuntime provider,
        ProcessInstanceId processInstanceId,
        NpgsqlDataSource dataSource,
        PostgresProcessDurableStore processStore,
        PostgresMaterializationStateStore materializationStore,
        InteractionAuthorityScope authorityScope,
        string workerIncarnation,
        TimeSpan operationBoundaryDelay,
        OperationContext context)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(processStore);
        ArgumentNullException.ThrowIfNull(materializationStore);
        ArgumentNullException.ThrowIfNull(authorityScope);
        ArgumentNullException.ThrowIfNull(context);
        workerIncarnation = Guard.RequireNotNullOrWhiteSpace(workerIncarnation);
        var artifacts = MaterializationRebuildPlanSetProcessFactory.Create(provider.Compilation.PlanSet);
        var executionResolver = new AttemptExecutionResolver(
            resolvedPlan: provider.ResolvedPlan,
            synchronizationWorkStore: materializationStore,
            crashInjector: operationBoundaryDelay == TimeSpan.Zero
                ? NoOpMaterializationRebuildCrashInjector.Instance
                : new DelayAtMaterializationBoundary(operationBoundaryDelay));
        var planSetResolver = new ExactPlanSetResolver(provider.Compilation.PlanSet);

        var workerRuntime = Runtime(
            processStore: processStore,
            workerId: $"worker/materialization-harness/{provider.Provider}/{workerIncarnation}/shards",
            bindings: [artifacts.Leaf.ShardRebuildBinding],
            adapters:
            [
                new MaterializationRebuildShardDurableOperationAdapter(
                    request: artifacts.Leaf.ShardRebuildRequest,
                    resolver: executionResolver)
            ]);
        var leafRuntime = Runtime(
            processStore: processStore,
            workerId: $"worker/materialization-harness/{provider.Provider}/{workerIncarnation}/leaves",
            bindings:
            [
                artifacts.Leaf.InitializationBinding,
                artifacts.Leaf.WorkerInvocationBinding,
                artifacts.Leaf.SynchronizationPreparationBinding
            ],
            adapters:
            [
                new MaterializationRebuildInitializationDurableOperationAdapter(
                    request: artifacts.Leaf.InitializationRequest,
                    resolver: executionResolver),
                new ProcessChildDurableOperationAdapter(
                    runtime: workerRuntime,
                    planResolver: new ProcessChildPlanCatalog([artifacts.Leaf.WorkerPlan]),
                    supportedRequests: [artifacts.Leaf.WorkerInvocationRequest]),
                new MaterializationSynchronizationPreparationDurableOperationAdapter(
                    request: artifacts.Leaf.SynchronizationPreparationRequest,
                    resolver: executionResolver)
            ]);

        var router = new PostgresMaterializationBackendRouter(
            dataSource: dataSource,
            options: new(
                authorityId: $"materialization-harness/freight-rebuild/{provider.Provider}/routing/"
                    + provider.Compilation.Placement.BackendPool.DefinitionFingerprint.Value),
            document: provider.Compilation.Placement.BackendPool,
            targets: provider.TargetPool);
        await router.EnsureCreatedAsync(context).ConfigureAwait(false);
        var promotionRuntime = Runtime(
            processStore: processStore,
            workerId: $"worker/materialization-harness/{provider.Provider}/{workerIncarnation}/promotions",
            bindings:
            [
                artifacts.ActivateReadyBinding,
                artifacts.PreparePromotionBinding,
                artifacts.ApplyPromotionBinding
            ],
            adapters:
            [
                new MaterializationReadyGenerationActivationDurableOperationAdapter(
                    request: artifacts.ActivateReadyRequest,
                    resolver: executionResolver,
                    promotionWorkerPlan: artifacts.PromotionWorkerPlan),
                new MaterializationIndependentPromotionPreparationDurableOperationAdapter(
                    request: artifacts.PreparePromotionRequest,
                    resolver: planSetResolver,
                    router: router,
                    store: processStore,
                    promotionPlan: artifacts.PromotionWorkerPlan),
                new MaterializationIndependentPromotionDurableOperationAdapter(
                    request: artifacts.ApplyPromotionRequest,
                    resolver: planSetResolver,
                    router: router,
                    promotionPlan: artifacts.PromotionWorkerPlan)
            ]);
        var parentRuntime = Runtime(
            processStore: processStore,
            workerId: $"worker/materialization-harness/{provider.Provider}/{workerIncarnation}/parent",
            bindings:
            [
                artifacts.InitializationBinding,
                artifacts.LeafInvocationBinding,
                artifacts.ReadinessBarrierBinding,
                artifacts.PromotionInvocationBinding,
                artifacts.FinalizeBinding
            ],
            adapters:
            [
                new MaterializationRebuildPlanSetInitializationDurableOperationAdapter(
                    request: artifacts.InitializationRequest,
                    resolver: planSetResolver,
                    parentPlan: artifacts.ParentPlan),
                new ProcessChildDurableOperationAdapter(
                    runtime: leafRuntime,
                    planResolver: new ProcessChildPlanCatalog([artifacts.Leaf.CoordinatorPlan]),
                    supportedRequests: [artifacts.LeafInvocationRequest]),
                new MaterializationRebuildReadyBarrierDurableOperationAdapter(
                    request: artifacts.ReadinessBarrierRequest,
                    resolver: planSetResolver,
                    store: processStore,
                    parentPlan: artifacts.ParentPlan),
                new ProcessChildDurableOperationAdapter(
                    runtime: promotionRuntime,
                    planResolver: new ProcessChildPlanCatalog([artifacts.PromotionWorkerPlan]),
                    supportedRequests: [artifacts.PromotionInvocationRequest]),
                new MaterializationRebuildPlanSetFinalizationDurableOperationAdapter(
                    request: artifacts.FinalizeRequest,
                    resolver: planSetResolver,
                    store: processStore,
                    parentPlan: artifacts.ParentPlan)
            ]);
        var lifecycle = new MaterializationRebuildPlanSetProcessLifecycle(
            parentRuntime: parentRuntime,
            leafRuntime: leafRuntime,
            promotionRuntime: promotionRuntime,
            artifacts: artifacts,
            planSet: provider.Compilation.PlanSet,
            executionResolver: executionResolver,
            router: router);
        var result = new MaterializationHarnessProviderProcess(
            provider: provider,
            processInstanceId: processInstanceId,
            processStore: processStore,
            authorityScope: authorityScope,
            artifacts: artifacts,
            parentRuntime: parentRuntime,
            executionResolver: executionResolver,
            router: router,
            lifecycle: lifecycle);
        return result;
    }

    internal ProcessStartRequest CreateStartRequest(ProcessAttemptId attemptId, DateTimeOffset issuedAtUtc) => new(
        schemaVersion: ProcessStartRequest.CurrentSchemaVersion,
        definition: Artifacts.ParentPlan.DefinitionReference,
        context: new(
            commandId: new($"command/materialization-harness/{Provider.Provider}/start/{attemptId.Value}"),
            idempotencyKey: new($"idempotency/materialization-harness/{Provider.Provider}/start/{attemptId.Value}"),
            processInstanceId: ProcessInstanceId,
            authorization: Authorization(),
            issuedAtUtc: issuedAtUtc,
            provenance: Provenance("sdk-start")),
        initialContinuation: new(
            processInstanceId: ProcessInstanceId,
            processAttemptId: attemptId),
        input: PortableValue.Concrete(
            contract: Artifacts.ParentPlan.Definition.Input,
            value: ObservationValue.FromString(MaterializationRebuildPlanSetReferenceJsonSerializer.Serialize(
                MaterializationRebuildPlanSetReference.FromPlanSet(Provider.Compilation.PlanSet)))));

    internal Task<ProcessDurableStoreSnapshot?> LoadAsync(OperationContext context) =>
        processStore.LoadAsync(context, ProcessInstanceId);

    internal async Task<MaterializationRebuildPlanSetProcessLifecycleResult> InitializeAsync(
        OperationContext context,
        ProcessStartReceipt receipt)
    {
        var result = await Lifecycle.InitializeAsync(context, receipt).ConfigureAwait(false);
        if (result.Snapshot is not null)
            RegisterRetainedAttempts(result.Snapshot);
        return result;
    }

    internal async Task<MaterializationRebuildPlanSetProcessLifecycleResult> ApplyControlAsync(
        OperationContext context,
        ProcessControlCommand command)
    {
        await RegisterRetainedAttemptsAsync(context).ConfigureAwait(false);
        return await Lifecycle.ApplyControlAsync(context, command).ConfigureAwait(false);
    }

    internal async Task<MaterializationRebuildPlanSetProcessLifecycleResult> CancelAsync(
        OperationContext context,
        CancelProcessCommand command,
        ProcessActivationContext activationContext)
    {
        await RegisterRetainedAttemptsAsync(context).ConfigureAwait(false);
        return await Lifecycle.CancelAsync(context, command, activationContext).ConfigureAwait(false);
    }

    internal async Task<ProcessDurableStoreSnapshot?> DriveAsync(CancellationToken cancellationToken)
    {
        for (var step = 0; step < MaximumParentStepsPerDrive; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var context = OperationContext.Create(cancellationToken: cancellationToken);
            var snapshot = await LoadAsync(context).ConfigureAwait(false);
            if (snapshot is null
                || snapshot.Checkpoint.Continuation.Terminal.Kind != ExecutionTerminalOutcomeKind.None
                || snapshot.Checkpoint.Control.IsTerminal
                || snapshot.Checkpoint.Control.Mode != ProcessControlMode.Running)
            {
                return snapshot;
            }

            RegisterRetainedAttempts(snapshot);
            var operation = snapshot.Checkpoint.DurableOperations
                .Where(candidate => candidate.Status != DurableOperationStatus.Dispositioned
                    && candidate.Request.Context.Origin is ProcessInteractionOrigin origin
                    && origin.Continuation == snapshot.Checkpoint.ContinuationIdentity)
                .OrderBy(static candidate => candidate.OperationId.Value, StringComparer.Ordinal)
                .FirstOrDefault();
            if (operation is not null)
            {
                RegisterPendingLeafAttempt(operation);
                var advanced = await parentRuntime.AdvanceOperationAsync(
                        context: context,
                        plan: Artifacts.ParentPlan,
                        instanceId: ProcessInstanceId,
                        operationId: operation.OperationId)
                    .ConfigureAwait(false);
                if (ShouldYieldAndReload(advanced.Disposition))
                {
                    // Lease and revision outcomes are physical scheduling boundaries. An ambiguous commit retains
                    // its exact immutable identity. In either case, reload canonical evidence on the next pass.
                    return advanced.Snapshot ?? snapshot;
                }
                RequireCommitted(advanced.Disposition, advanced.Diagnostics);
                continue;
            }

            var pendingInputs = PendingInputs(snapshot.Checkpoint);
            var continuation = snapshot.Checkpoint.ContinuationIdentity;
            var cause = ActivationCause(snapshot.Checkpoint, pendingInputs);
            var ordinal = snapshot.Checkpoint.Continuation.CompletedActivationCount + 1;
            var activation = new ProcessActivation(
                id: new($"activation/materialization-harness/{Provider.Provider}/{continuation.ProcessAttemptId.Value}/{ordinal}"),
                cause: cause,
                observedAtUtc: context.UtcNow,
                context: new(
                    authorityScope: authorityScope,
                    correlationId: new($"correlation/materialization-harness/{Provider.Provider}/{continuation.ProcessAttemptId.Value}"),
                    delivery: new(
                        durability: InteractionDurabilityDemand.Durable,
                        visibility: InteractionVisibilityDemand.AfterOriginCommit),
                    provenance: Artifacts.ParentProcessDocument.Metadata.Provenance),
                inputs: pendingInputs);
            var activated = await Lifecycle.ActivateAsync(
                    context: context,
                    expectedContinuation: continuation,
                    activation: activation)
                .ConfigureAwait(false);
            if (activated.ProcessDisposition is { } activationDisposition
                && ShouldYieldAndReload(activationDisposition))
            {
                // Re-enter through lifecycle inspection on the next bounded drive. It owns exact activation replay
                // and any generation realization or physical ownership change across the boundary.
                return activated.Snapshot ?? snapshot;
            }
            if (activated.ProcessDisposition is { } disposition)
                RequireCommitted(disposition, activated.Diagnostics);
            if (activated.Realization == MaterializationRebuildPlanSetProcessRealization.Unresolved)
            {
                // Cleanup can be temporarily unresolved while an abandoned child's previously dispatched work
                // reaches a conclusive durable cut. Yield without fabricating failure or starting replacement work;
                // the next worker pass reloads the same canonical evidence and retries lifecycle reconciliation.
                return activated.Snapshot ?? snapshot;
            }
        }

        throw new InvalidOperationException(
            $"Provider '{Provider.Provider}' exceeded its finite {MaximumParentStepsPerDrive}-step parent drive budget.");
    }

    internal async ValueTask<ControlLimitUpdateDecision> SubmitLimitUpdateAsync(
        OperationContext context,
        ControlLimitUpdateCommand command,
        DateTimeOffset decidedAtUtc)
    {
        var execution = await ResolveCurrentExecutionAsync(context).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("The current rebuild attempt has not allocated a leaf generation.");
        return await Provider.ControlRuntimeProvider.ForGeneration(execution.Generation).SubmitLimitUpdateAsync(
                context: context,
                workload: MaterializationIndexSyncWorkloadKind.Rebuild,
                command: command,
                decidedAtUtc: decidedAtUtc)
            .ConfigureAwait(false);
    }

    internal ExecutionRuntimeStatusDetails RuntimeStatus(
        ProcessDurableStoreSnapshot snapshot,
        string source) => MaterializationRebuildPlanSetStatusProjector.CreateRuntimeDetails(
        planSet: Provider.Compilation.PlanSet,
        artifacts: Artifacts,
        snapshot: snapshot,
        provenance: Provenance(source));

    internal async Task<MaterializationRebuildExecution?> ResolveCurrentExecutionAsync(OperationContext context)
    {
        var snapshot = await LoadAsync(context).ConfigureAwait(false);
        if (snapshot is null)
            return null;
        RegisterRetainedAttempts(snapshot);
        var operation = snapshot.Checkpoint.DurableOperations.FirstOrDefault(candidate =>
            candidate.Request.Contract == Artifacts.LeafInvocationRequest
            && candidate.Request.Context.Origin is ProcessInteractionOrigin origin
            && origin.Continuation == snapshot.Checkpoint.ContinuationIdentity);
        var continuation = operation?.Request.ChildTarget?.Continuation;
        return continuation is not null
            && executionResolver.TryResolve(
                Provider.ResolvedPlan.Authority,
                continuation,
                out var execution)
            ? execution
            : null;
    }

    internal ProcessControlAuthorizationContext Authorization() => new(
        actor: "operator/materialization-harness",
        authorityScope: authorityScope,
        evidenceReference: "policy/materialization-harness/local-allow");

    internal ExecutionProvenance Provenance(string source) => new(
        producer: new("cohesive-materialization-harness", "1"),
        source: new($"eng/materialization-harness/host/{Provider.Provider}/{source}"),
        origin: DocumentOrigin.Generated);

    async Task RegisterRetainedAttemptsAsync(OperationContext context)
    {
        var snapshot = await LoadAsync(context).ConfigureAwait(false);
        if (snapshot is not null)
            RegisterRetainedAttempts(snapshot);
    }

    void RegisterRetainedAttempts(ProcessDurableStoreSnapshot parentSnapshot)
    {
        foreach (var operation in parentSnapshot.Checkpoint.DurableOperations)
        {
            if (operation.Request.Contract != Artifacts.LeafInvocationRequest
                || operation.Request.ChildTarget is not { } target)
            {
                continue;
            }

            // The parent allocates the logical leaf attempt when it durably emits this child operation. Child
            // acceptance is a later physical scheduling event and can change across host recovery, so it cannot
            // participate in the deterministic generation identity reconstructed by a replacement host.
            executionResolver.Register(
                continuation: target.Continuation,
                startedAtUtc: operation.CreatedAtUtc);
        }
    }

    void RegisterPendingLeafAttempt(DurableOperationState operation)
    {
        if (operation.Request.Contract != Artifacts.LeafInvocationRequest
            || operation.Request.ChildTarget is not { } target)
        {
            return;
        }

        executionResolver.Register(
            continuation: target.Continuation,
            startedAtUtc: operation.CreatedAtUtc);
    }

    static ProcessDurableRuntime Runtime(
        PostgresProcessDurableStore processStore,
        string workerId,
        ImmutableArray<DurableRequestBinding> bindings,
        ImmutableArray<IDurableOperationAdapter> adapters) => new(
        store: processStore,
        host: RejectingProcessHost.Instance,
        options: new(
            workerId: workerId,
            workerLease: WorkerLease,
            maxAmbiguousStoreMutationAttempts: 3),
        bindingResolver: new DurableRequestBindingCatalog(bindings),
        storeMutationExceptionClassifier: PostgresProcessStoreMutationExceptionClassifier.Instance,
        operationAdapterResolver: new DurableOperationAdapterCatalog(adapters));

    static ImmutableArray<ProcessActivationInput> PendingInputs(ProcessDurableCheckpoint checkpoint) =>
        [.. checkpoint.Inbox
            .Where(entry => entry.Receipt is null
                && entry.Input.Target.Continuation == checkpoint.ContinuationIdentity)
            .OrderBy(static entry => entry.EmissionId.Value, StringComparer.Ordinal)
            .Select(static entry => entry.Input)];

    static ProcessActivationCause ActivationCause(
        ProcessDurableCheckpoint checkpoint,
        ImmutableArray<ProcessActivationInput> pendingInputs)
    {
        if (!pendingInputs.IsEmpty)
            return ProcessActivationCause.Interaction;
        var state = checkpoint.Continuation;
        if (state.CompletedActivationCount == 0)
            return ProcessActivationCause.Start;
        if (state.Tokens.Any(static token => token.Disposition == ExecutionTokenDisposition.Ready)
            || state.Waits.Any(static wait => wait.Active && wait.Kind is
                ProcessWaitKind.DurableCut or ProcessWaitKind.RepeatAcrossActivation))
        {
            return ProcessActivationCause.Continue;
        }
        throw new InvalidOperationException("The rebuild parent reached an unexplained nonterminal wait.");
    }

    static void RequireCommitted(
        ProcessDurableRuntimeDisposition disposition,
        ImmutableArray<DocumentValidationDiagnostic> diagnostics)
    {
        if (disposition is ProcessDurableRuntimeDisposition.Applied or ProcessDurableRuntimeDisposition.Replayed)
            return;
        throw new InvalidOperationException(
            $"The durable Process operation returned '{disposition}': "
            + string.Join(" ", diagnostics.Select(static diagnostic => diagnostic.Message)));
    }

    static bool ShouldYieldAndReload(ProcessDurableRuntimeDisposition disposition) =>
        disposition is ProcessDurableRuntimeDisposition.LeaseHeld
            or ProcessDurableRuntimeDisposition.RevisionConflict
            or ProcessDurableRuntimeDisposition.StaleFence
            or ProcessDurableRuntimeDisposition.LeaseExpired
            or ProcessDurableRuntimeDisposition.CommitOutcomeUnknown;

    sealed class ExactPlanSetResolver(MaterializationRebuildPlanSet planSet)
        : IMaterializationRebuildPlanSetExecutionResolver
    {
        readonly MaterializationRebuildPlanSetReference reference =
            MaterializationRebuildPlanSetReference.FromPlanSet(planSet);

        public bool TryResolve(
            MaterializationRebuildPlanSetReference planSetReference,
            out MaterializationRebuildPlanSet? resolvedPlanSet)
        {
            ArgumentNullException.ThrowIfNull(planSetReference);
            var exact = planSetReference == reference;
            resolvedPlanSet = exact ? planSet : null;
            return exact;
        }
    }

    sealed class AttemptExecutionResolver(
        ResolvedMaterializationRebuildPlan resolvedPlan,
        IMaterializationSynchronizationWorkStore synchronizationWorkStore,
        IMaterializationRebuildCrashInjector crashInjector)
        : IMaterializationRebuildExecutionResolver
    {
        readonly object gate = new();
        readonly Dictionary<ProcessContinuationIdentity, DateTimeOffset> starts = [];
        readonly Dictionary<ProcessContinuationIdentity, MaterializationRebuildExecution> executions = [];

        internal void Register(ProcessContinuationIdentity continuation, DateTimeOffset startedAtUtc)
        {
            ArgumentNullException.ThrowIfNull(continuation);
            if (startedAtUtc.Offset != TimeSpan.Zero)
                throw new ArgumentException("A rebuild attempt start must use the UTC offset.", nameof(startedAtUtc));
            lock (gate)
            {
                if (starts.TryGetValue(continuation, out var retained) && retained != startedAtUtc)
                {
                    throw new InvalidOperationException(
                        $"Process continuation '{continuation.ProcessInstanceId.Value}' changed its durable start time.");
                }
                starts[continuation] = startedAtUtc;
            }
        }

        public bool TryResolve(
            MaterializationRebuildLeafExecutionAuthority authority,
            ProcessContinuationIdentity continuation,
            out MaterializationRebuildExecution? execution)
        {
            ArgumentNullException.ThrowIfNull(authority);
            ArgumentNullException.ThrowIfNull(continuation);
            lock (gate)
            {
                if (authority != resolvedPlan.Authority || !starts.TryGetValue(continuation, out var startedAtUtc))
                {
                    execution = null;
                    return false;
                }
                if (!executions.TryGetValue(continuation, out execution))
                {
                    execution = new(
                        resolved: resolvedPlan,
                        attempt: new(
                            continuation: continuation,
                            startedAtUtc: startedAtUtc),
                        synchronizationWorkStore: synchronizationWorkStore,
                        crashInjector: crashInjector);
                    executions.Add(continuation, execution);
                }
                return true;
            }
        }
    }

    sealed class DelayAtMaterializationBoundary(TimeSpan delay) : IMaterializationRebuildCrashInjector
    {
        public async ValueTask ObserveAsync(
            OperationContext context,
            MaterializationRebuildCrashObservation observation)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(observation);
            await Task.Delay(delay, context.CancellationToken).ConfigureAwait(false);
        }
    }

    sealed class RejectingProcessHost : IProcessReferenceHost
    {
        internal static RejectingProcessHost Instance { get; } = new();

        public ProcessOperationResult InvokeTransition(ProcessTransitionInvocation invocation) =>
            throw new InvalidOperationException($"Unexpected Transition invocation at '{invocation.Node.Value}'.");

        public ProcessOperationResult EvaluateRelation(ProcessRelationEvaluation evaluation) =>
            throw new InvalidOperationException($"Unexpected Relation evaluation at '{evaluation.Node.Value}'.");

        public ProcessSignalTargetResult ResolveSignalTarget(ProcessSignalTargetResolution resolution) =>
            throw new InvalidOperationException($"Unexpected Signal resolution at '{resolution.Node.Value}'.");
    }
}
