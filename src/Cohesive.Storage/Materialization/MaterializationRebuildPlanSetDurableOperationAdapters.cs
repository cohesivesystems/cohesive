using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Compilation;
using Cohesive.Processes.Execution;
using Cohesive.Storage.Processes;
using static Cohesive.Storage.Materialization.MaterializationRebuildPlanSetPortableContracts;

namespace Cohesive.Storage.Materialization;

/// <summary>Stable diagnostics emitted by the durable rebuild plan-set operation adapters.</summary>
public static class MaterializationRebuildPlanSetDurableOperationDiagnosticCodes
{
    /// <summary>The adapter received a Request outside its exact declared capability.</summary>
    public const string RequestUnsupported = "storage.materialization.rebuild.planSet.adapter.request.unsupported";

    /// <summary>The Request payload was absent, malformed, or not the expected exact reference.</summary>
    public const string RequestPayloadInvalid = "storage.materialization.rebuild.planSet.adapter.request.payloadInvalid";

    /// <summary>The Request did not originate from the exact expected Process, node, and attempt.</summary>
    public const string RequestOriginInvalid = "storage.materialization.rebuild.planSet.adapter.request.originInvalid";

    /// <summary>The exact plan-set execution could not currently be resolved.</summary>
    public const string ExecutionUnavailable = "storage.materialization.rebuild.planSet.adapter.execution.unavailable";

    /// <summary>The resolved plan-set execution differs from the durable Request authority.</summary>
    public const string ExecutionInexact = "storage.materialization.rebuild.planSet.adapter.execution.inexact";

    /// <summary>The exact parent or promotion-child checkpoint could not be loaded.</summary>
    public const string CheckpointUnavailable = "storage.materialization.rebuild.planSet.adapter.checkpoint.unavailable";

    /// <summary>The loaded checkpoint is incompatible with the exact compiled Process or Request attempt.</summary>
    public const string CheckpointInexact = "storage.materialization.rebuild.planSet.adapter.checkpoint.inexact";

    /// <summary>The retained bounded child ledger does not cover the exact linked plan-set leaves.</summary>
    public const string ChildLedgerInexact = "storage.materialization.rebuild.planSet.adapter.childLedger.inexact";

    /// <summary>A build child did not produce exact reusable readiness evidence.</summary>
    public const string LeafNotReady = "storage.materialization.rebuild.planSet.adapter.leaf.notReady";

    /// <summary>An independent promotion child returned malformed or inexact routing evidence.</summary>
    public const string PromotionResultInexact = "storage.materialization.rebuild.planSet.adapter.promotion.resultInexact";

    /// <summary>An independent promotion completed without selecting the rebuilt generation for both routes.</summary>
    public const string PromotionNotSelected = "storage.materialization.rebuild.planSet.adapter.promotion.notSelected";
}

/// <summary>Resolves one exact persisted rebuild plan set.</summary>
public interface IMaterializationRebuildPlanSetExecutionResolver
{
    /// <summary>Attempts to resolve one exact content-addressed plan set.</summary>
    /// <param name="planSet">Exact durable linked plan-set reference.</param>
    /// <param name="resolvedPlanSet">Receives the complete constructor-verified plan set when available.</param>
    /// <returns><see langword="true"/> when the exact plan set is currently available.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="planSet"/> is <see langword="null"/>.</exception>
    bool TryResolve(
        MaterializationRebuildPlanSetReference planSet,
        out MaterializationRebuildPlanSet? resolvedPlanSet);
}

/// <summary>Returns exact capacity-aware leaf work for one durable parent Process attempt.</summary>
public sealed class MaterializationRebuildPlanSetInitializationDurableOperationAdapter : IDurableOperationAdapter
{
    readonly IMaterializationRebuildPlanSetExecutionResolver resolver;
    readonly CompiledProcessPlan parentPlan;

    /// <summary>Creates an adapter for the exact parent initialization Request contract.</summary>
    /// <param name="request">Exact initialization Request contract.</param>
    /// <param name="resolver">Resolver for content-addressed plan-set execution bindings.</param>
    /// <param name="parentPlan">Exact compiled parent Process owning the initialization Request.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public MaterializationRebuildPlanSetInitializationDurableOperationAdapter(
        RequestContractReference request,
        IMaterializationRebuildPlanSetExecutionResolver resolver,
        CompiledProcessPlan parentPlan)
    {
        ArgumentNullException.ThrowIfNull(request);
        this.resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        this.parentPlan = parentPlan ?? throw new ArgumentNullException(nameof(parentPlan));
        Capabilities = new(
            idempotencyEvidence: DurableOperationIdempotencyEvidence.NaturallyIdempotent,
            reconciliation: DurableOperationReconciliationCapability.Supported,
            supportedRequests: [request]);
    }

    /// <inheritdoc />
    public DurableOperationAdapterCapabilities Capabilities { get; }

    /// <inheritdoc />
    public ValueTask<DurableOperationAttemptObservation> ExecuteAsync(
        OperationContext context,
        DurableOperationInvocation invocation)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(invocation);
        context.ThrowIfCancellationRequested();
        var outcome = Run(invocation.Request, remainUnresolved: false)
            ?? throw new InvalidOperationException("Execute must project a terminal initialization outcome.");
        return ValueTask.FromResult<DurableOperationAttemptObservation>(new DurableOperationOutcomeObservation(outcome));
    }

    /// <inheritdoc />
    public ValueTask<DurableOperationReconciliationObservation> ReconcileAsync(
        OperationContext context,
        DurableOperationReconciliationRequest request)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        context.ThrowIfCancellationRequested();
        var outcome = Run(request.Request, remainUnresolved: true);
        return ValueTask.FromResult<DurableOperationReconciliationObservation>(outcome is null
            ? new DurableOperationUnresolved()
            : new DurableOperationReconciledOutcome(outcome));
    }

    RequestTerminalOutcome? Run(RequestEnvelope request, bool remainUnresolved)
    {
        if (!Capabilities.Supports(request.Contract))
            return PlanSetProjection.Failure(MaterializationRebuildPlanSetDurableOperationDiagnosticCodes.RequestUnsupported);
        if (!PlanSetProjection.TryReadString(request.Payload, out var payload)
            || !PlanSetProjection.TryReadPlanSetReference(payload, out var reference)
            || reference is null)
        {
            return PlanSetProjection.Failure(MaterializationRebuildPlanSetDurableOperationDiagnosticCodes.RequestPayloadInvalid);
        }
        if (request.Context.Origin is not ProcessInteractionOrigin origin
            || origin.Definition != parentPlan.DefinitionReference
            || origin.Node != MaterializationRebuildPlanSetProcessFactory.InitializationNodeId)
        {
            return PlanSetProjection.Failure(MaterializationRebuildPlanSetDurableOperationDiagnosticCodes.RequestOriginInvalid);
        }
        if (!PlanSetProjection.IsExactParentSpecialization(parentPlan, reference))
        {
            return PlanSetProjection.Failure(
                MaterializationRebuildPlanSetDurableOperationDiagnosticCodes.ExecutionInexact);
        }
        if (!resolver.TryResolve(reference, out var planSet)
            || planSet is null)
        {
            return remainUnresolved
                ? null
                : PlanSetProjection.Failure(MaterializationRebuildPlanSetDurableOperationDiagnosticCodes.ExecutionUnavailable);
        }
        if (MaterializationRebuildPlanSetReference.FromPlanSet(planSet) != reference)
        {
            return remainUnresolved
                ? null
                : PlanSetProjection.Failure(MaterializationRebuildPlanSetDurableOperationDiagnosticCodes.ExecutionInexact);
        }

        return new RequestResultOutcome(
            MaterializationRebuildPlanSetProcessFactory.CompletedOutcome,
            PlanSetProjection.WorkItems(planSet));
    }
}

/// <summary>Projects the exact retained build-child ledger into a reusable all-leaf readiness barrier.</summary>
public sealed class MaterializationRebuildReadyBarrierDurableOperationAdapter : IDurableOperationAdapter
{
    readonly IMaterializationRebuildPlanSetExecutionResolver resolver;
    readonly IProcessDurableStore store;
    readonly CompiledProcessPlan parentPlan;

    /// <summary>Creates an adapter for one exact parent readiness-barrier Request.</summary>
    /// <param name="request">Exact readiness-barrier Request contract.</param>
    /// <param name="resolver">Resolver for the exact content-addressed plan-set execution.</param>
    /// <param name="store">Durable Process store containing the parent child ledger.</param>
    /// <param name="parentPlan">Exact compiled parent Process selected for checkpoint validation.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public MaterializationRebuildReadyBarrierDurableOperationAdapter(
        RequestContractReference request,
        IMaterializationRebuildPlanSetExecutionResolver resolver,
        IProcessDurableStore store,
        CompiledProcessPlan parentPlan)
    {
        ArgumentNullException.ThrowIfNull(request);
        this.resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.parentPlan = parentPlan ?? throw new ArgumentNullException(nameof(parentPlan));
        Capabilities = new(
            idempotencyEvidence: DurableOperationIdempotencyEvidence.NaturallyIdempotent,
            reconciliation: DurableOperationReconciliationCapability.Supported,
            supportedRequests: [request]);
    }

    /// <inheritdoc />
    public DurableOperationAdapterCapabilities Capabilities { get; }

    /// <inheritdoc />
    public async ValueTask<DurableOperationAttemptObservation> ExecuteAsync(
        OperationContext context,
        DurableOperationInvocation invocation)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(invocation);
        var outcome = await RunAsync(context, invocation.Request, remainUnresolved: false).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Execute must project a terminal readiness-barrier outcome.");
        return new DurableOperationOutcomeObservation(outcome);
    }

    /// <inheritdoc />
    public async ValueTask<DurableOperationReconciliationObservation> ReconcileAsync(
        OperationContext context,
        DurableOperationReconciliationRequest request)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        var outcome = await RunAsync(context, request.Request, remainUnresolved: true).ConfigureAwait(false);
        return outcome is null
            ? new DurableOperationUnresolved()
            : new DurableOperationReconciledOutcome(outcome);
    }

    async Task<RequestTerminalOutcome?> RunAsync(
        OperationContext context,
        RequestEnvelope request,
        bool remainUnresolved)
    {
        context.ThrowIfCancellationRequested();
        if (!Capabilities.Supports(request.Contract))
            return PlanSetProjection.Failure(MaterializationRebuildPlanSetDurableOperationDiagnosticCodes.RequestUnsupported);
        if (!PlanSetProjection.TryReadString(request.Payload, out var payload)
            || !PlanSetProjection.TryReadPlanSetReference(payload, out var reference)
            || reference is null)
        {
            return PlanSetProjection.Failure(MaterializationRebuildPlanSetDurableOperationDiagnosticCodes.RequestPayloadInvalid);
        }
        if (!PlanSetProjection.IsExactParentSpecialization(parentPlan, reference))
        {
            return PlanSetProjection.Failure(
                MaterializationRebuildPlanSetDurableOperationDiagnosticCodes.ExecutionInexact);
        }
        if (!resolver.TryResolve(reference, out var planSet)
            || planSet is null)
        {
            return remainUnresolved
                ? null
                : PlanSetProjection.Failure(MaterializationRebuildPlanSetDurableOperationDiagnosticCodes.ExecutionUnavailable);
        }
        if (MaterializationRebuildPlanSetReference.FromPlanSet(planSet) != reference)
        {
            return remainUnresolved
                ? null
                : PlanSetProjection.Failure(MaterializationRebuildPlanSetDurableOperationDiagnosticCodes.ExecutionInexact);
        }

        var loaded = await PlanSetProjection.LoadCheckpointAsync(
                context,
                request,
                store,
                parentPlan,
                MaterializationRebuildPlanSetProcessFactory.ReadinessBarrierNodeId)
            .ConfigureAwait(false);
        if (loaded.Checkpoint is null)
        {
            return remainUnresolved
                ? null
                : PlanSetProjection.Failure(loaded.FailureCode!);
        }

        var checkpoint = loaded.Checkpoint;
        ImmutableArray<MaterializationRebuildPlanSetLeafReceipt> leaves;
        bool allReady;
        try
        {
            leaves = PlanSetProjection.ProjectBuildLeaves(planSet, checkpoint, out allReady);
        }
        catch (ArgumentException)
        {
            return PlanSetProjection.Failure(
                MaterializationRebuildPlanSetDurableOperationDiagnosticCodes.ChildLedgerInexact);
        }
        if (!allReady)
        {
            var receipt = MaterializationRebuildPlanSetReceipt.Create(
                planSet: planSet,
                parentContinuation: checkpoint.ContinuationIdentity,
                outcome: MaterializationRebuildPlanSetOutcome.Failed,
                leaves: leaves,
                readyBarrier: null,
                completedAtUtc: PlanSetProjection.CompletionBoundary(
                    loaded.RequestBoundary!.Value,
                    leaves));
            receipt.ValidateAgainst(planSet, parentPlan, checkpoint);
            return new RequestFailureOutcome(
                MaterializationRebuildPlanSetProcessFactory.FailedOutcome,
                PlanSetProjection.StringValue(MaterializationRebuildPlanSetReceiptJsonSerializer.Serialize(receipt)));
        }

        var ready = leaves.Select(static leaf => leaf.Ready!).ToImmutableArray();
        MaterializationRebuildReadyBarrier barrier;
        try
        {
            barrier = MaterializationRebuildReadyBarrier.Create(
                planSet: planSet,
                parentContinuation: checkpoint.ContinuationIdentity,
                readyGenerations: ready);
            barrier.ValidateAgainst(planSet, parentPlan, checkpoint);
        }
        catch (ArgumentException)
        {
            return PlanSetProjection.Failure(
                MaterializationRebuildPlanSetDurableOperationDiagnosticCodes.ChildLedgerInexact);
        }

        return new RequestResultOutcome(
            MaterializationRebuildPlanSetProcessFactory.ReadyOutcome,
            PlanSetProjection.BarrierResult(planSet, barrier));
    }
}

/// <summary>Captures one replay-stable independent routing intent after exact target activation.</summary>
public sealed class MaterializationIndependentPromotionPreparationDurableOperationAdapter : IDurableOperationAdapter
{
    readonly IMaterializationRebuildPlanSetExecutionResolver resolver;
    readonly IMaterializationBackendRouter router;
    readonly IProcessDurableStore store;
    readonly CompiledProcessPlan promotionPlan;

    /// <summary>Creates an adapter for the read-only independent-promotion preparation Request.</summary>
    /// <param name="request">Exact routing-intent preparation Request contract.</param>
    /// <param name="resolver">Resolver for exact plan-set promotion execution.</param>
    /// <param name="router">Exact backend-pool routing authority.</param>
    /// <param name="store">Durable Process store supplying a stable Request issuance boundary.</param>
    /// <param name="promotionPlan">Exact compiled promotion-child Process.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public MaterializationIndependentPromotionPreparationDurableOperationAdapter(
        RequestContractReference request,
        IMaterializationRebuildPlanSetExecutionResolver resolver,
        IMaterializationBackendRouter router,
        IProcessDurableStore store,
        CompiledProcessPlan promotionPlan)
    {
        ArgumentNullException.ThrowIfNull(request);
        this.resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        this.router = router ?? throw new ArgumentNullException(nameof(router));
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.promotionPlan = promotionPlan ?? throw new ArgumentNullException(nameof(promotionPlan));
        Capabilities = new(
            idempotencyEvidence: DurableOperationIdempotencyEvidence.NaturallyIdempotent,
            reconciliation: DurableOperationReconciliationCapability.Supported,
            supportedRequests: [request]);
    }

    /// <inheritdoc />
    public DurableOperationAdapterCapabilities Capabilities { get; }

    /// <inheritdoc />
    public async ValueTask<DurableOperationAttemptObservation> ExecuteAsync(
        OperationContext context,
        DurableOperationInvocation invocation)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(invocation);
        var outcome = await RunAsync(context, invocation.Request).ConfigureAwait(false);
        return new DurableOperationOutcomeObservation(outcome);
    }

    /// <inheritdoc />
    public ValueTask<DurableOperationReconciliationObservation> ReconcileAsync(
        OperationContext context,
        DurableOperationReconciliationRequest request)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        context.ThrowIfCancellationRequested();
        DurableOperationReconciliationObservation observation = Capabilities.Supports(request.Request.Contract)
            // Preparation performs only a read. A lost acknowledgement cannot leave an external consequence, so
            // recovery may safely claim another physical attempt and persist the exact current intent it reads.
            ? new DurableOperationConfirmedNotExecuted()
            : new DurableOperationUnresolved();
        return ValueTask.FromResult(observation);
    }

    async Task<RequestTerminalOutcome> RunAsync(
        OperationContext context,
        RequestEnvelope request)
    {
        context.ThrowIfCancellationRequested();
        if (!Capabilities.Supports(request.Contract))
            return PlanSetProjection.Failure(MaterializationRebuildPlanSetDurableOperationDiagnosticCodes.RequestUnsupported);
        if (!PlanSetProjection.TryReadString(request.Payload, out var payload)
            || !PlanSetProjection.TryDeserialize(payload, MaterializationActiveGenerationReferenceJsonSerializer.Deserialize, out MaterializationActiveGenerationReference? active)
            || active is null)
        {
            return PlanSetProjection.Failure(MaterializationRebuildPlanSetDurableOperationDiagnosticCodes.RequestPayloadInvalid);
        }
        if (!resolver.TryResolve(active.Authority.PlanSet, out var planSet)
            || planSet is null)
        {
            return PlanSetProjection.Failure(
                MaterializationRebuildPlanSetDurableOperationDiagnosticCodes.ExecutionUnavailable);
        }
        if (MaterializationRebuildPlanSetReference.FromPlanSet(planSet) != active.Authority.PlanSet
            || !PlanSetProjection.Contains(planSet, active.Authority))
        {
            return PlanSetProjection.Failure(
                MaterializationRebuildPlanSetDurableOperationDiagnosticCodes.ExecutionInexact);
        }

        var loaded = await PlanSetProjection.LoadCheckpointAsync(
                context,
                request,
                store,
                promotionPlan,
                MaterializationRebuildPlanSetProcessFactory.PromotionPrepareNodeId)
            .ConfigureAwait(false);
        if (loaded.Checkpoint is null)
        {
            return PlanSetProjection.Failure(loaded.FailureCode!);
        }

        var executor = new MaterializationIndependentPromotionExecutor(planSet, active.Authority);
        var snapshot = await router.InspectAsync(context, active.PlacementSlice).ConfigureAwait(false);
        if (snapshot.PlacementSlice != active.PlacementSlice)
        {
            return PlanSetProjection.Failure(
                MaterializationRebuildPlanSetDurableOperationDiagnosticCodes.ExecutionInexact);
        }

        MaterializationBackendRoutingFence fence;
        try
        {
            fence = snapshot.LatestFence is null
                ? MaterializationBackendRoutingFence.Initial
                : new(checked(snapshot.LatestFence.Value.Ordinal + 1).ToString(CultureInfo.InvariantCulture));
        }
        catch (OverflowException)
        {
            return PlanSetProjection.Failure(
                MaterializationRebuildPlanSetDurableOperationDiagnosticCodes.ExecutionUnavailable);
        }

        var promotionRequest = executor.CreateRequest(
            activeGeneration: active,
            snapshot: snapshot,
            fence: fence,
            issuedAtUtc: loaded.RequestBoundary!.Value);
        return new RequestResultOutcome(
            MaterializationRebuildPlanSetProcessFactory.CompletedOutcome,
            PlanSetProjection.StringValue(
                MaterializationIndependentPromotionRequestJsonSerializer.Serialize(promotionRequest)));
    }
}

/// <summary>Applies or reconciles one already-persisted exact independent routing intent.</summary>
public sealed class MaterializationIndependentPromotionDurableOperationAdapter : IDurableOperationAdapter
{
    readonly IMaterializationRebuildPlanSetExecutionResolver resolver;
    readonly IMaterializationBackendRouter router;
    readonly CompiledProcessPlan promotionPlan;

    /// <summary>Creates an adapter for the exact independent routing application Request.</summary>
    /// <param name="request">Exact routing-application Request contract.</param>
    /// <param name="resolver">Resolver for exact plan-set promotion execution.</param>
    /// <param name="router">Exact backend-pool routing authority.</param>
    /// <param name="promotionPlan">Exact compiled promotion-child Process.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public MaterializationIndependentPromotionDurableOperationAdapter(
        RequestContractReference request,
        IMaterializationRebuildPlanSetExecutionResolver resolver,
        IMaterializationBackendRouter router,
        CompiledProcessPlan promotionPlan)
    {
        ArgumentNullException.ThrowIfNull(request);
        this.resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        this.router = router ?? throw new ArgumentNullException(nameof(router));
        this.promotionPlan = promotionPlan ?? throw new ArgumentNullException(nameof(promotionPlan));
        Capabilities = new(
            idempotencyEvidence: DurableOperationIdempotencyEvidence.TargetDeduplication,
            reconciliation: DurableOperationReconciliationCapability.Supported,
            supportedRequests: [request]);
    }

    /// <inheritdoc />
    public DurableOperationAdapterCapabilities Capabilities { get; }

    /// <inheritdoc />
    public async ValueTask<DurableOperationAttemptObservation> ExecuteAsync(
        OperationContext context,
        DurableOperationInvocation invocation)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(invocation);
        var outcome = await RunAsync(context, invocation.Request, remainUnresolved: false).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Execute must project a terminal independent-promotion outcome.");
        return new DurableOperationOutcomeObservation(outcome);
    }

    /// <inheritdoc />
    public async ValueTask<DurableOperationReconciliationObservation> ReconcileAsync(
        OperationContext context,
        DurableOperationReconciliationRequest request)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        var outcome = await RunAsync(context, request.Request, remainUnresolved: true).ConfigureAwait(false);
        return outcome is null
            ? new DurableOperationUnresolved()
            : new DurableOperationReconciledOutcome(outcome);
    }

    async Task<RequestTerminalOutcome?> RunAsync(
        OperationContext context,
        RequestEnvelope request,
        bool remainUnresolved)
    {
        context.ThrowIfCancellationRequested();
        if (!Capabilities.Supports(request.Contract))
            return PlanSetProjection.Failure(MaterializationRebuildPlanSetDurableOperationDiagnosticCodes.RequestUnsupported);
        if (!PlanSetProjection.TryReadString(request.Payload, out var payload)
            || !PlanSetProjection.TryDeserialize(payload, MaterializationIndependentPromotionRequestJsonSerializer.Deserialize, out MaterializationIndependentPromotionRequest? promotionRequest)
            || promotionRequest is null)
        {
            return PlanSetProjection.Failure(MaterializationRebuildPlanSetDurableOperationDiagnosticCodes.RequestPayloadInvalid);
        }
        if (request.Context.Origin is not ProcessInteractionOrigin origin
            || origin.Definition != promotionPlan.DefinitionReference
            || origin.Node != MaterializationRebuildPlanSetProcessFactory.PromotionApplyNodeId)
        {
            return PlanSetProjection.Failure(MaterializationRebuildPlanSetDurableOperationDiagnosticCodes.RequestOriginInvalid);
        }
        if (!resolver.TryResolve(promotionRequest.Authority.PlanSet, out var planSet)
            || planSet is null)
        {
            return remainUnresolved
                ? null
                : PlanSetProjection.Failure(MaterializationRebuildPlanSetDurableOperationDiagnosticCodes.ExecutionUnavailable);
        }
        if (MaterializationRebuildPlanSetReference.FromPlanSet(planSet) != promotionRequest.Authority.PlanSet
            || !PlanSetProjection.Contains(planSet, promotionRequest.Authority))
        {
            return remainUnresolved
                ? null
                : PlanSetProjection.Failure(MaterializationRebuildPlanSetDurableOperationDiagnosticCodes.ExecutionInexact);
        }

        var executor = new MaterializationIndependentPromotionExecutor(planSet, promotionRequest.Authority);
        var result = await executor.ExecuteAsync(context, promotionRequest, router).ConfigureAwait(false);
        return new RequestResultOutcome(
            MaterializationRebuildPlanSetProcessFactory.CompletedOutcome,
            PlanSetProjection.StringValue(MaterializationIndependentPromotionResultJsonSerializer.Serialize(result)));
    }
}

/// <summary>Projects exact settled promotion children into one honest aggregate plan-set receipt.</summary>
public sealed class MaterializationRebuildPlanSetFinalizationDurableOperationAdapter : IDurableOperationAdapter
{
    readonly IMaterializationRebuildPlanSetExecutionResolver resolver;
    readonly IProcessDurableStore store;
    readonly CompiledProcessPlan parentPlan;

    /// <summary>Creates an adapter for one exact parent finalization Request.</summary>
    /// <param name="request">Exact aggregate-finalization Request contract.</param>
    /// <param name="resolver">Resolver for the exact content-addressed plan-set execution.</param>
    /// <param name="store">Durable Process store containing both bounded child ledgers.</param>
    /// <param name="parentPlan">Exact compiled parent Process selected for checkpoint validation.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public MaterializationRebuildPlanSetFinalizationDurableOperationAdapter(
        RequestContractReference request,
        IMaterializationRebuildPlanSetExecutionResolver resolver,
        IProcessDurableStore store,
        CompiledProcessPlan parentPlan)
    {
        ArgumentNullException.ThrowIfNull(request);
        this.resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.parentPlan = parentPlan ?? throw new ArgumentNullException(nameof(parentPlan));
        Capabilities = new(
            idempotencyEvidence: DurableOperationIdempotencyEvidence.NaturallyIdempotent,
            reconciliation: DurableOperationReconciliationCapability.Supported,
            supportedRequests: [request]);
    }

    /// <inheritdoc />
    public DurableOperationAdapterCapabilities Capabilities { get; }

    /// <inheritdoc />
    public async ValueTask<DurableOperationAttemptObservation> ExecuteAsync(
        OperationContext context,
        DurableOperationInvocation invocation)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(invocation);
        var outcome = await RunAsync(context, invocation.Request, remainUnresolved: false).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Execute must project a terminal aggregate-finalization outcome.");
        return new DurableOperationOutcomeObservation(outcome);
    }

    /// <inheritdoc />
    public async ValueTask<DurableOperationReconciliationObservation> ReconcileAsync(
        OperationContext context,
        DurableOperationReconciliationRequest request)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        var outcome = await RunAsync(context, request.Request, remainUnresolved: true).ConfigureAwait(false);
        return outcome is null
            ? new DurableOperationUnresolved()
            : new DurableOperationReconciledOutcome(outcome);
    }

    async Task<RequestTerminalOutcome?> RunAsync(
        OperationContext context,
        RequestEnvelope request,
        bool remainUnresolved)
    {
        context.ThrowIfCancellationRequested();
        if (!Capabilities.Supports(request.Contract))
            return PlanSetProjection.Failure(MaterializationRebuildPlanSetDurableOperationDiagnosticCodes.RequestUnsupported);
        if (!PlanSetProjection.TryReadString(request.Payload, out var payload)
            || !PlanSetProjection.TryDeserialize(payload, MaterializationRebuildReadyBarrierJsonSerializer.DeserializeStructural, out MaterializationRebuildReadyBarrier? barrier)
            || barrier is null)
        {
            return PlanSetProjection.Failure(MaterializationRebuildPlanSetDurableOperationDiagnosticCodes.RequestPayloadInvalid);
        }
        if (!PlanSetProjection.IsExactParentSpecialization(parentPlan, barrier.PlanSet))
        {
            return PlanSetProjection.Failure(
                MaterializationRebuildPlanSetDurableOperationDiagnosticCodes.ExecutionInexact);
        }
        if (!resolver.TryResolve(barrier.PlanSet, out var planSet)
            || planSet is null)
        {
            return remainUnresolved
                ? null
                : PlanSetProjection.Failure(MaterializationRebuildPlanSetDurableOperationDiagnosticCodes.ExecutionUnavailable);
        }
        if (MaterializationRebuildPlanSetReference.FromPlanSet(planSet) != barrier.PlanSet)
        {
            return remainUnresolved
                ? null
                : PlanSetProjection.Failure(MaterializationRebuildPlanSetDurableOperationDiagnosticCodes.ExecutionInexact);
        }

        var loaded = await PlanSetProjection.LoadCheckpointAsync(
                context,
                request,
                store,
                parentPlan,
                MaterializationRebuildPlanSetProcessFactory.FinalizeNodeId)
            .ConfigureAwait(false);
        if (loaded.Checkpoint is null)
        {
            return remainUnresolved
                ? null
                : PlanSetProjection.Failure(loaded.FailureCode!);
        }
        if (barrier.ParentContinuation != loaded.Checkpoint.ContinuationIdentity)
        {
            return PlanSetProjection.Failure(
                MaterializationRebuildPlanSetDurableOperationDiagnosticCodes.RequestPayloadInvalid);
        }

        ImmutableArray<MaterializationRebuildPlanSetLeafReceipt> leaves;
        try
        {
            barrier.ValidateAgainst(planSet, parentPlan, loaded.Checkpoint);
            leaves = PlanSetProjection.ProjectPromotionLeaves(planSet, barrier, loaded.Checkpoint);
        }
        catch (ArgumentException)
        {
            return PlanSetProjection.Failure(
                MaterializationRebuildPlanSetDurableOperationDiagnosticCodes.ChildLedgerInexact);
        }

        var promoted = leaves.Count(static leaf => leaf.Outcome == MaterializationRebuildPlanSetLeafOutcome.Promoted);
        var outcome = promoted == leaves.Length
            ? MaterializationRebuildPlanSetOutcome.Completed
            : promoted > 0
                ? MaterializationRebuildPlanSetOutcome.PartiallyPromoted
                : MaterializationRebuildPlanSetOutcome.Failed;
        var receipt = MaterializationRebuildPlanSetReceipt.Create(
            planSet: planSet,
            parentContinuation: loaded.Checkpoint.ContinuationIdentity,
            outcome: outcome,
            leaves: leaves,
            readyBarrier: barrier,
            completedAtUtc: PlanSetProjection.CompletionBoundary(
                loaded.RequestBoundary!.Value,
                leaves));
        receipt.ValidateAgainst(planSet, parentPlan, loaded.Checkpoint);
        var value = PlanSetProjection.StringValue(MaterializationRebuildPlanSetReceiptJsonSerializer.Serialize(receipt));
        return outcome == MaterializationRebuildPlanSetOutcome.Failed
            ? new RequestFailureOutcome(MaterializationRebuildPlanSetProcessFactory.FailedOutcome, value)
            : new RequestResultOutcome(MaterializationRebuildPlanSetProcessFactory.CompletedOutcome, value);
    }
}

static class PlanSetProjection
{
    internal static PortableValue StringValue(string value) => PortableValue.Concrete(
        StringContract,
        ObservationValue.FromString(value));

    internal static bool TryReadString(PortableValue value, out string payload)
    {
        if (value.State == PortableValueState.Concrete
            && value.Value is { Kind: ObservationValueKind.String, String: { } text }
            && !string.IsNullOrWhiteSpace(text))
        {
            payload = text;
            return true;
        }
        payload = string.Empty;
        return false;
    }

    internal static bool TryReadPlanSetReference(
        string json,
        out MaterializationRebuildPlanSetReference? reference) =>
        TryDeserialize(json, MaterializationRebuildPlanSetReferenceJsonSerializer.Deserialize, out reference);

    internal static bool Contains(
        MaterializationRebuildPlanSet planSet,
        MaterializationRebuildLeafExecutionAuthority authority) =>
        authority.PlanSet == MaterializationRebuildPlanSetReference.FromPlanSet(planSet)
        && planSet.LeafPlans.Any(binding => binding == authority.Binding);

    internal static bool IsExactParentSpecialization(
        CompiledProcessPlan parentPlan,
        MaterializationRebuildPlanSetReference planSet) =>
        parentPlan.DefinitionReference.DefinitionId
            == MaterializationRebuildPlanSetProcessFactory.GetParentDefinitionId(planSet)
        && parentPlan.DefinitionReference.RevisionId == MaterializationRebuildPlanSetProcessFactory.RevisionId;

    internal static bool HasExactParentStart(
        ProcessStartReceipt start,
        CompiledProcessPlan parentPlan,
        MaterializationRebuildPlanSetReference planSet)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(parentPlan);
        ArgumentNullException.ThrowIfNull(planSet);
        var input = start.Request.Input;
        if (start.Request.Definition != parentPlan.DefinitionReference
            || input is not
            {
                State: PortableValueState.Concrete,
                Value: { Kind: ObservationValueKind.String, String: { } json }
            }
            || input.Contract != parentPlan.Definition.Input)
        {
            return false;
        }

        return TryReadPlanSetReference(json, out var observed) && observed == planSet;
    }

    internal static void ValidateParentContext(
        MaterializationRebuildPlanSet planSet,
        CompiledProcessPlan parentPlan,
        ProcessDurableCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(planSet);
        ArgumentNullException.ThrowIfNull(parentPlan);
        ArgumentNullException.ThrowIfNull(checkpoint);
        var reference = MaterializationRebuildPlanSetReference.FromPlanSet(planSet);
        if (!IsExactParentSpecialization(parentPlan, reference)
            || !HasExactParentStart(checkpoint.Start, parentPlan, reference))
        {
            throw new ArgumentException(
                "The parent Process context does not belong to the exact supplied rebuild plan set.",
                nameof(checkpoint));
        }

        var compatibility = ProcessCheckpointCompatibilityValidator.Validate(parentPlan, checkpoint);
        if (!compatibility.IsValid)
        {
            throw new ArgumentException(
                "The parent checkpoint is incompatible with its exact compiled Process: "
                + string.Join(" ", compatibility.Diagnostics.Select(static diagnostic =>
                    $"{diagnostic.Code} at {diagnostic.Location}: {diagnostic.Message}")),
                nameof(checkpoint));
        }
    }

    internal static bool TryDeserialize<T>(string json, Func<string, T> deserialize, out T? result)
        where T : class
    {
        try
        {
            result = deserialize(json);
            return true;
        }
        catch (JsonException)
        {
            result = null;
            return false;
        }
        catch (ArgumentException)
        {
            result = null;
            return false;
        }
    }

    internal static RequestFailureOutcome Failure(string code) =>
        MaterializationRebuildDurableOperationProjection.Failure(code);

    internal static PortableValue WorkItems(MaterializationRebuildPlanSet planSet)
    {
        var capacityBySlice = planSet.Placement.CapacityBindings.ToDictionary(
            static binding => binding.Slice,
            static binding => binding.CapacityDomain);
        var authorities = Authorities(planSet);
        var work = ImmutableArray.CreateBuilder<ObservationValue>(authorities.Length);
        foreach (var authority in authorities)
        {
            work.Add(WorkItem(
                authority.PlacementSlice.Id.Value,
                capacityBySlice[authority.PlacementSlice.Id].Value,
                MaterializationRebuildWorkReferenceJsonSerializer.SerializeAuthority(authority)));
        }
        return PortableValue.Concrete(WorkItemsContract, ObservationValue.FromImmutableArray(work.MoveToImmutable()));
    }

    internal static PortableValue BarrierResult(
        MaterializationRebuildPlanSet planSet,
        MaterializationRebuildReadyBarrier barrier)
    {
        var capacityBySlice = planSet.Placement.CapacityBindings.ToDictionary(
            static binding => binding.Slice,
            static binding => binding.CapacityDomain);
        var work = ImmutableArray.CreateBuilder<ObservationValue>(barrier.ReadyGenerations.Length);
        foreach (var ready in barrier.ReadyGenerations)
        {
            work.Add(WorkItem(
                ready.PlacementSlice.Id.Value,
                capacityBySlice[ready.PlacementSlice.Id].Value,
                MaterializationReadyGenerationReferenceJsonSerializer.Serialize(ready)));
        }

        return PortableValue.Concrete(
            BarrierResultContract,
            ObservationValue.FromObject(new Dictionary<string, ObservationValue>(StringComparer.Ordinal)
            {
                ["barrier"] = ObservationValue.FromString(MaterializationRebuildReadyBarrierJsonSerializer.Serialize(barrier)),
                ["work"] = ObservationValue.FromImmutableArray(work.MoveToImmutable())
            }));
    }

    internal static async Task<CheckpointLoad> LoadCheckpointAsync(
        OperationContext context,
        RequestEnvelope request,
        IProcessDurableStore store,
        CompiledProcessPlan plan,
        ExecutionNodeId node)
    {
        if (request.Context.Origin is not ProcessInteractionOrigin origin
            || origin.Definition != plan.DefinitionReference
            || origin.Node != node)
        {
            return new(
                Checkpoint: null,
                RequestBoundary: null,
                FailureCode: MaterializationRebuildPlanSetDurableOperationDiagnosticCodes.RequestOriginInvalid);
        }

        var snapshot = await store.LoadAsync(context, origin.Continuation.ProcessInstanceId).ConfigureAwait(false);
        if (snapshot is null)
        {
            return new(
                Checkpoint: null,
                RequestBoundary: null,
                FailureCode: MaterializationRebuildPlanSetDurableOperationDiagnosticCodes.CheckpointUnavailable);
        }
        if (snapshot.Checkpoint.ContinuationIdentity != origin.Continuation
            || !ProcessCheckpointCompatibilityValidator.Validate(plan, snapshot.Checkpoint).IsValid)
        {
            return new(
                Checkpoint: null,
                RequestBoundary: null,
                FailureCode: MaterializationRebuildPlanSetDurableOperationDiagnosticCodes.CheckpointInexact);
        }
        var operations = snapshot.Checkpoint.DurableOperations.Where(candidate =>
            candidate.OperationId == request.Context.EmissionId
            && candidate.Request == request).ToArray();
        if (operations is not [var operation])
        {
            return new(
                Checkpoint: null,
                RequestBoundary: null,
                FailureCode: MaterializationRebuildPlanSetDurableOperationDiagnosticCodes.CheckpointInexact);
        }
        return new(snapshot.Checkpoint, operation.CreatedAtUtc, FailureCode: null);
    }

    internal static ImmutableArray<MaterializationRebuildPlanSetLeafReceipt> ProjectBuildLeaves(
        MaterializationRebuildPlanSet planSet,
        ProcessDurableCheckpoint checkpoint,
        out bool allReady)
    {
        var partition = ExactPartition(
            checkpoint,
            MaterializationRebuildPlanSetProcessFactory.BuildLeavesNodeId,
            planSet.LeafPlans.Length)
            ?? throw new ArgumentException("The exact resolved build partition ledger is unavailable.", nameof(checkpoint));
        var children = checkpoint.Continuation.Children.ToDictionary(static child => child.RegistrationId, StringComparer.Ordinal);
        var workByProgress = partition.Work.ToDictionary(static work => work.ProgressIdentity, StringComparer.Ordinal);
        var authorities = Authorities(planSet);
        var receipts = ImmutableArray.CreateBuilder<MaterializationRebuildPlanSetLeafReceipt>(authorities.Length);
        allReady = true;
        foreach (var authority in authorities)
        {
            var sliceId = authority.PlacementSlice.Id.Value;
            if (!workByProgress.TryGetValue(sliceId, out var work)
                || !children.TryGetValue(work.ChildRegistrationId, out var child)
                || !ValidWorkItem(planSet, authority, work,
                    MaterializationRebuildWorkReferenceJsonSerializer.SerializeAuthority(authority)))
            {
                throw new ArgumentException(
                    "The parent build ledger is missing or substitutes an exact linked leaf.",
                    nameof(checkpoint));
            }

            if (child.Disposition == ProcessChildDisposition.Completed
                && child.TerminalOutcome == MaterializationRebuildPlanSetProcessFactory.CompletedOutcome
                && child.Result is not null
                && TryReadString(child.Result, out var readyJson)
                && TryDeserialize(readyJson, MaterializationReadyGenerationReferenceJsonSerializer.Deserialize, out MaterializationReadyGenerationReference? ready)
                && ready is not null
                && ready.Authority == authority
                && ready.Attempt.Continuation == child.Continuation)
            {
                receipts.Add(new(
                    authority: authority,
                    buildChild: child.Continuation,
                    outcome: MaterializationRebuildPlanSetLeafOutcome.Ready,
                    ready: ready));
                continue;
            }

            allReady = false;
            var outcome = LeafOutcome(child.TerminalOutcome);
            receipts.Add(new(
                authority: authority,
                buildChild: child.Continuation,
                outcome: outcome,
                terminalEvidence: ChildTerminalEvidence(
                    child,
                    MaterializationRebuildPlanSetLeafPhase.Build,
                    nameof(checkpoint)),
                failure: outcome == MaterializationRebuildPlanSetLeafOutcome.Failed
                    ? Error(
                        MaterializationRebuildPlanSetDurableOperationDiagnosticCodes.LeafNotReady,
                        "The build child did not return exact reusable readiness evidence.",
                        sliceId)
                    : null));
        }
        return receipts.MoveToImmutable();
    }

    internal static ImmutableArray<MaterializationRebuildPlanSetLeafReceipt> ProjectPromotionLeaves(
        MaterializationRebuildPlanSet planSet,
        MaterializationRebuildReadyBarrier barrier,
        ProcessDurableCheckpoint checkpoint)
    {
        var partition = ExactPartition(
            checkpoint,
            MaterializationRebuildPlanSetProcessFactory.PromoteLeavesNodeId,
            planSet.LeafPlans.Length)
            ?? throw new ArgumentException("The exact resolved promotion partition ledger is unavailable.", nameof(checkpoint));
        var children = checkpoint.Continuation.Children.ToDictionary(static child => child.RegistrationId, StringComparer.Ordinal);
        var workByProgress = partition.Work.ToDictionary(static work => work.ProgressIdentity, StringComparer.Ordinal);
        var readyBySlice = barrier.ReadyGenerations.ToDictionary(static ready => ready.PlacementSlice.Id);
        var authorities = Authorities(planSet);
        var receipts = ImmutableArray.CreateBuilder<MaterializationRebuildPlanSetLeafReceipt>(authorities.Length);
        foreach (var authority in authorities)
        {
            var sliceId = authority.PlacementSlice.Id.Value;
            var ready = readyBySlice[authority.PlacementSlice.Id];
            if (!workByProgress.TryGetValue(sliceId, out var work)
                || !children.TryGetValue(work.ChildRegistrationId, out var child)
                || !ValidWorkItem(planSet, authority, work,
                    MaterializationReadyGenerationReferenceJsonSerializer.Serialize(ready)))
            {
                throw new ArgumentException(
                    "The parent promotion ledger is missing or substitutes an exact linked leaf.",
                    nameof(checkpoint));
            }

            if (child.Disposition == ProcessChildDisposition.Completed
                && child.TerminalOutcome == MaterializationRebuildPlanSetProcessFactory.CompletedOutcome
                && child.Result is not null
                && TryReadString(child.Result, out var resultJson)
                && TryDeserialize(resultJson, MaterializationIndependentPromotionResultJsonSerializer.Deserialize, out MaterializationIndependentPromotionResult? promotion)
                && promotion is not null
                && promotion.Request.Authority == authority
                && ready.MatchesActiveGeneration(promotion.Request.ActiveGeneration))
            {
                receipts.Add(promotion.IsCurrentlySelected
                    ? new(
                        authority: authority,
                        buildChild: ready.Attempt.Continuation,
                        outcome: MaterializationRebuildPlanSetLeafOutcome.Promoted,
                        ready: ready,
                        promotionChild: child.Continuation,
                        promotion: promotion)
                    : new(
                        authority: authority,
                        buildChild: ready.Attempt.Continuation,
                        outcome: MaterializationRebuildPlanSetLeafOutcome.Failed,
                        ready: ready,
                        promotionChild: child.Continuation,
                        promotion: promotion,
                        terminalEvidence: ChildTerminalEvidence(
                            child,
                            MaterializationRebuildPlanSetLeafPhase.Promotion,
                            nameof(checkpoint)),
                        failure: Error(
                            MaterializationRebuildPlanSetDurableOperationDiagnosticCodes.PromotionNotSelected,
                            "Independent promotion completed without selecting the rebuilt generation for both routes.",
                            sliceId)));
                continue;
            }

            var outcome = LeafOutcome(child.TerminalOutcome);
            receipts.Add(new(
                authority: authority,
                buildChild: ready.Attempt.Continuation,
                outcome: outcome,
                ready: ready,
                promotionChild: child.Continuation,
                terminalEvidence: ChildTerminalEvidence(
                    child,
                    MaterializationRebuildPlanSetLeafPhase.Promotion,
                    nameof(checkpoint)),
                failure: outcome == MaterializationRebuildPlanSetLeafOutcome.Failed
                    ? Error(
                        MaterializationRebuildPlanSetDurableOperationDiagnosticCodes.PromotionResultInexact,
                        "The promotion child did not return exact independent routing evidence.",
                        sliceId)
                    : null));
        }
        return receipts.MoveToImmutable();
    }

    internal static DateTimeOffset CompletionBoundary(
        DateTimeOffset requestBoundary,
        ImmutableArray<MaterializationRebuildPlanSetLeafReceipt> leaves)
    {
        var boundary = requestBoundary;
        foreach (var leaf in leaves)
        {
            AdvanceBoundary(ref boundary, leaf.Ready?.ReadyAtUtc);
            AdvanceBoundary(ref boundary, leaf.Promotion?.Admission.Receipt?.CommittedAtUtc);
            AdvanceBoundary(ref boundary, leaf.Promotion?.Routing?.Receipt?.CommittedAtUtc);
        }
        return boundary;
    }

    static void AdvanceBoundary(ref DateTimeOffset boundary, DateTimeOffset? evidenceAtUtc)
    {
        if (evidenceAtUtc is { } evidence && evidence > boundary)
            boundary = evidence;
    }

    static ProcessPartitionState? ExactPartition(
        ProcessDurableCheckpoint checkpoint,
        ExecutionNodeId node,
        int expectedCount)
    {
        var partitions = checkpoint.Continuation.Partitions.Where(candidate => candidate.Node == node).ToArray();
        if (partitions.Length != 1
            || !partitions[0].Resolved
            || partitions[0].Work.Length != expectedCount
            || partitions[0].Work.Select(static work => work.ProgressIdentity).Distinct(StringComparer.Ordinal).Count() != expectedCount)
        {
            return null;
        }
        return partitions[0];
    }

    static bool ValidWorkItem(
        MaterializationRebuildPlanSet planSet,
        MaterializationRebuildLeafExecutionAuthority authority,
        ProcessPartitionWorkState work,
        string expectedPayload)
    {
        var expectedCapacity = planSet.Placement.CapacityBindings.Single(
            binding => binding.Slice == authority.PlacementSlice.Id).CapacityDomain.Value;
        if (!string.Equals(work.CapacityIdentity, expectedCapacity, StringComparison.Ordinal)
            || work.Partition.State != PortableValueState.Concrete
            || work.Partition.Value is not { Kind: ObservationValueKind.Object } value
            || !value.TryGetProperty("sliceId", out var slice)
            || slice.Kind != ObservationValueKind.String
            || !string.Equals(slice.String, authority.PlacementSlice.Id.Value, StringComparison.Ordinal)
            || !value.TryGetProperty("capacityDomain", out var capacity)
            || capacity.Kind != ObservationValueKind.String
            || !string.Equals(capacity.String, expectedCapacity, StringComparison.Ordinal)
            || !value.TryGetProperty("payload", out var payload)
            || payload.Kind != ObservationValueKind.String
            || !string.Equals(payload.String, expectedPayload, StringComparison.Ordinal))
        {
            return false;
        }
        return true;
    }

    static ObservationValue WorkItem(string sliceId, string capacityDomain, string payload) =>
        ObservationValue.FromObject(new Dictionary<string, ObservationValue>(StringComparer.Ordinal)
        {
            ["sliceId"] = ObservationValue.FromString(sliceId),
            ["capacityDomain"] = ObservationValue.FromString(capacityDomain),
            ["payload"] = ObservationValue.FromString(payload)
        });

    static ImmutableArray<MaterializationRebuildLeafExecutionAuthority> Authorities(
        MaterializationRebuildPlanSet planSet)
    {
        var authorities = ImmutableArray.CreateBuilder<MaterializationRebuildLeafExecutionAuthority>(
            planSet.LeafPlans.Length);
        foreach (var binding in planSet.LeafPlans)
            authorities.Add(MaterializationRebuildLeafExecutionAuthority.FromPlanSet(planSet, binding));
        return authorities.MoveToImmutable();
    }

    static MaterializationRebuildPlanSetLeafOutcome LeafOutcome(RequestTerminalOutcomeId? outcome) =>
        outcome == MaterializationRebuildPlanSetProcessFactory.CancelledOutcome
            ? MaterializationRebuildPlanSetLeafOutcome.Cancelled
            : outcome == MaterializationRebuildPlanSetProcessFactory.TerminatedOutcome
                ? MaterializationRebuildPlanSetLeafOutcome.Terminated
                : MaterializationRebuildPlanSetLeafOutcome.Failed;

    static MaterializationRebuildPlanSetChildTerminalEvidence ChildTerminalEvidence(
        ProcessChildState child,
        MaterializationRebuildPlanSetLeafPhase phase,
        string parameterName)
    {
        if (child.TerminalOutcome is not { } terminalOutcome || child.Result is not { } terminalResult)
        {
            throw new ArgumentException(
                "A terminal plan-set child must retain its exact outcome identity and typed portable result.",
                parameterName);
        }

        return new(
            phase: phase,
            child: child.Continuation,
            terminalOutcome: terminalOutcome,
            terminalResult: terminalResult);
    }

    static DocumentValidationDiagnostic Error(string code, string message, string sliceId) => new(
        code,
        DiagnosticSeverity.Error,
        message,
        $"/leaves/{sliceId}");

    internal sealed record CheckpointLoad(
        ProcessDurableCheckpoint? Checkpoint,
        DateTimeOffset? RequestBoundary,
        string? FailureCode);
}
