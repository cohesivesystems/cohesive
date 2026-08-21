using System.Collections.Immutable;
using System.Globalization;
using Cohesive.Model.Serialization;

namespace Cohesive.Storage.Materialization;

/// <summary>Stable diagnostics emitted by the reference generation-activation interpreter.</summary>
public static class MaterializationGenerationActivationDiagnosticCodes
{
    /// <summary>The candidate, convergence proof, progress, or target is not ready for activation.</summary>
    public const string NotReady = "materialization.activation.notReady";

    /// <summary>A target lifecycle effect or its reconciliation failed.</summary>
    public const string TargetFailed = "materialization.activation.target.failed";

    /// <summary>The candidate failed target-native validation.</summary>
    public const string ValidationFailed = "materialization.activation.validation.failed";

    /// <summary>A newer synchronization-work owner superseded this activation.</summary>
    public const string Fenced = "materialization.activation.fenced";

    /// <summary>Durable intent or live target state requires a new Process attempt and generation.</summary>
    public const string RestartRequired = "materialization.activation.restartRequired";
}

/// <summary>Terminal disposition of one bounded catch-up, readiness, or generation-activation invocation.</summary>
public enum MaterializationGenerationActivationDisposition
{
    /// <summary>The exact generation is durably activated and remains the target's current read generation.</summary>
    Active = 0,

    /// <summary>Incremental catch-up reached its finite work budget and another invocation is required.</summary>
    WorkRemaining = 1,

    /// <summary>A required baseline, progress, convergence, or target precondition is not currently established.</summary>
    NotReady = 2,

    /// <summary>Source convergence, target execution, validation, or durable reconciliation failed.</summary>
    Failed = 3,

    /// <summary>A newer durable worker fence superseded this invocation.</summary>
    Fenced = 4,

    /// <summary>The exact durable attempt can no longer activate safely and must resume with a new generation.</summary>
    RestartRequired = 5,

    /// <summary>The exact generation is sealed and validated with its target-pointer promotion intent retained.</summary>
    Ready = 6
}

/// <summary>Typed evidence produced by one bounded catch-up and candidate-activation invocation.</summary>
public sealed record MaterializationGenerationActivationResult
{
    /// <summary>Creates one coherent activation result.</summary>
    /// <param name="disposition">Observable terminal disposition.</param>
    /// <param name="generation">Exact Process-attempt generation.</param>
    /// <param name="synchronization">Bounded catch-up evidence when convergence ran during this invocation.</param>
    /// <param name="activation">Latest durable activation prefix, when activation has begun.</param>
    /// <param name="target">Current target pointer evidence, present exactly for an active result.</param>
    /// <param name="diagnostics">Structured diagnostics, required exactly for non-success failure dispositions.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="disposition"/> is unsupported.</exception>
    /// <exception cref="ArgumentException">Supplied evidence contradicts <paramref name="disposition"/>.</exception>
    public MaterializationGenerationActivationResult(
        MaterializationGenerationActivationDisposition disposition,
        MaterializationGenerationId generation,
        MaterializationSynchronizationRunResult? synchronization = null,
        MaterializationGenerationActivationState? activation = null,
        MaterializationTargetSnapshot? target = null,
        ImmutableArray<DocumentValidationDiagnostic> diagnostics = default)
    {
        if (!Enum.IsDefined(disposition))
            throw new ArgumentOutOfRangeException(nameof(disposition), disposition, "Unsupported activation disposition.");
        MaterializationContract.RequireDefinedIdentity(generation.Value, nameof(generation));
        if (synchronization is not null && synchronization.Generation != generation)
            throw new ArgumentException("Synchronization evidence must belong to the exact result generation.", nameof(synchronization));
        if (activation is not null && activation.Convergence.Generation != generation)
            throw new ArgumentException("Activation evidence must belong to the exact result generation.", nameof(activation));

        var normalizedDiagnostics = MaterializationContract.NormalizeDiagnostics(diagnostics, nameof(diagnostics));
        var failed = disposition is MaterializationGenerationActivationDisposition.NotReady
            or MaterializationGenerationActivationDisposition.Failed
            or MaterializationGenerationActivationDisposition.Fenced
            or MaterializationGenerationActivationDisposition.RestartRequired;
        if (failed == normalizedDiagnostics.IsDefaultOrEmpty)
            throw new ArgumentException("Exactly a failed activation result requires diagnostics.", nameof(diagnostics));

        if (disposition == MaterializationGenerationActivationDisposition.Active
            && (activation is not { IsComplete: true }
                || target?.ActiveGenerationId != generation))
        {
            throw new ArgumentException(
                "An active result requires completed durable activation and a current target pointer to the generation.",
                nameof(disposition));
        }
        if (disposition != MaterializationGenerationActivationDisposition.Active && target is not null)
            throw new ArgumentException("Only an active result exposes current target pointer evidence.", nameof(target));
        if (disposition == MaterializationGenerationActivationDisposition.WorkRemaining
            && synchronization?.Disposition != MaterializationSynchronizationRunDisposition.WorkRemaining)
        {
            throw new ArgumentException(
                "Work-remaining activation evidence requires a work-remaining synchronization result.",
                nameof(synchronization));
        }
        if (disposition == MaterializationGenerationActivationDisposition.Ready
            && (activation is not { IsReady: true } || target is not null))
        {
            throw new ArgumentException(
                "A ready result requires successful validation, an exact retained promotion intent, and no active target pointer.",
                nameof(activation));
        }

        Disposition = disposition;
        Generation = generation;
        Synchronization = synchronization;
        Activation = activation;
        Target = target;
        Diagnostics = normalizedDiagnostics;
    }

    /// <summary>Observable terminal disposition.</summary>
    public MaterializationGenerationActivationDisposition Disposition { get; }

    /// <summary>Exact Process-attempt generation.</summary>
    public MaterializationGenerationId Generation { get; }

    /// <summary>Bounded catch-up evidence when convergence ran during this invocation.</summary>
    public MaterializationSynchronizationRunResult? Synchronization { get; }

    /// <summary>Latest durable activation prefix, when activation has begun.</summary>
    public MaterializationGenerationActivationState? Activation { get; }

    /// <summary>Current target pointer evidence, present exactly when <see cref="Disposition"/> is active.</summary>
    public MaterializationTargetSnapshot? Target { get; }

    /// <summary>Structured deterministic failure diagnostics.</summary>
    public ImmutableArray<DocumentValidationDiagnostic> Diagnostics { get; }
}

/// <summary>
/// Storage-owned durable interpreter that prepares one generation and can separately reconcile its retained promotion.
/// </summary>
/// <remarks>
/// Every target lifecycle request is persisted before its effect. Preparation stops after persisting successful
/// validation and the exact target-pointer compare-and-swap request. Activation later reloads that prefix, revalidates
/// it when the target effect has not happened, and never reconstructs an expectation from later target state. A
/// completed activation is successful only while the target still points at its generation.
/// </remarks>
public sealed class MaterializationGenerationActivationExecutor
{
    readonly ResolvedMaterializationRebuildPlan resolved;
    readonly IMaterializationSynchronizationWorkStore workStore;
    readonly MaterializationSynchronizationExecutor synchronization;
    readonly IMaterializationExecutionBoundaryObserver boundaryObserver;

    /// <summary>Creates one activation executor over exact plan bindings and durable work authority.</summary>
    /// <param name="resolved">Exact persisted rebuild plan resolved to source, progress, and target ports.</param>
    /// <param name="workStore">Generation-wide synchronization and activation state authority.</param>
    /// <param name="boundaryObserver">Optional provider-neutral lifecycle boundary observer.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public MaterializationGenerationActivationExecutor(
        ResolvedMaterializationRebuildPlan resolved,
        IMaterializationSynchronizationWorkStore workStore,
        IMaterializationExecutionBoundaryObserver? boundaryObserver = null)
    {
        this.resolved = resolved ?? throw new ArgumentNullException(nameof(resolved));
        this.workStore = workStore ?? throw new ArgumentNullException(nameof(workStore));
        this.boundaryObserver = boundaryObserver ?? NoOpMaterializationExecutionBoundaryObserver.Instance;
        synchronization = new(
            resolved: resolved,
            workStore: workStore,
            workload: MaterializationIndexSyncWorkloadKind.Rebuild,
            boundaryObserver: this.boundaryObserver);
    }

    /// <summary>Exact persisted plan interpreted by this executor.</summary>
    public MaterializationRebuildPlan Plan => resolved.Plan;

    /// <summary>Creates the exact durable synchronization and activation key for one Process attempt.</summary>
    /// <param name="attempt">Exact Process attempt owning the candidate generation.</param>
    /// <returns>The definition-, plan-, impact-, and generation-fenced work key.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="attempt"/> is <see langword="null"/>.</exception>
    public MaterializationSynchronizationWorkKey GetWorkKey(MaterializationRebuildAttempt attempt) =>
        synchronization.GetWorkKey(attempt);

    /// <summary>
    /// Runs or resumes bounded convergence, sealing, and validation without applying the retained promotion intent.
    /// </summary>
    /// <param name="context">Explicit cancellation, time, identity, and tracing context.</param>
    /// <param name="attempt">Exact Process attempt retaining the candidate generation.</param>
    /// <param name="invocation">Stable bounded invocation identity retained across an exact durable retry.</param>
    /// <param name="worker">Explicit physical worker identity used to fence overlapping activations.</param>
    /// <returns>Ready, already-active, work-remaining, not-ready, failed, fenced, or restart-required evidence.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> or <paramref name="attempt"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="invocation"/> or <paramref name="worker"/> is default.</exception>
    /// <exception cref="OperationCanceledException">The operation is cancelled.</exception>
    /// <exception cref="Exception">
    /// A delegated source, durable-store, or target operation fails without a conclusive typed disposition. The
    /// exception is propagated so the owning durable Request attempt remains ambiguous and can reconcile the exact
    /// persisted intent.
    /// </exception>
    public async Task<MaterializationGenerationActivationResult> PrepareAsync(
        OperationContext context,
        MaterializationRebuildAttempt attempt,
        MaterializationSynchronizationInvocationId invocation,
        MaterializationSynchronizationWorkerId worker)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(attempt);
        MaterializationContract.RequireDefinedIdentity(invocation.Value, nameof(invocation));
        MaterializationContract.RequireDefinedIdentity(worker.Value, nameof(worker));
        context.ThrowIfCancellationRequested();

        var generation = MaterializationRebuildIdentities.Generation(resolved.Plan, attempt);
        var key = synchronization.GetWorkKey(attempt);
        MaterializationSynchronizationRunResult? synchronizationResult = null;
        MaterializationSynchronizationWorkSnapshot? work = null;

        work = await workStore.LoadAsync(context, key).ConfigureAwait(false);
        if (work?.Activation is { IsComplete: true } completed)
            return await CompletedAsync(context, generation, completed, synchronizationResult).ConfigureAwait(false);
        if (work?.Activation is { IsReady: true } ready)
            return Ready(generation, synchronizationResult, ready);
        if (work?.Activation is { ValidationReceipt.Validation.IsValid: false } invalid)
            return InvalidValidation(generation, synchronizationResult, invalid);

        var convergence = work?.Activation?.Convergence;
        if (convergence is null)
        {
            synchronizationResult = await synchronization.ConvergeAsync(
                    context,
                    attempt,
                    invocation,
                    worker)
                .ConfigureAwait(false);
            if (synchronizationResult.Disposition != MaterializationSynchronizationRunDisposition.Converged)
                return FromSynchronization(synchronizationResult);
            convergence = synchronizationResult.Receipt!;
            work = await workStore.LoadAsync(context, key).ConfigureAwait(false);
            if (work?.Activation is { IsComplete: true } concurrentlyCompleted)
            {
                return await CompletedAsync(
                        context,
                        generation,
                        concurrentlyCompleted,
                        synchronizationResult)
                    .ConfigureAwait(false);
            }
            if (work?.Activation is { } concurrentActivation)
                convergence = concurrentActivation.Convergence;
        }

        if (work is null)
        {
            return Failure(
                MaterializationGenerationActivationDisposition.NotReady,
                generation,
                synchronizationResult,
                activation: null,
                MaterializationGenerationActivationDiagnosticCodes.NotReady,
                "Convergence did not establish its generation-wide durable work aggregate.");
        }

        var owner = MaterializationGenerationActivationIdentities.Owner(key, convergence, invocation, worker);
        if (!string.Equals(work.FenceOwner, owner, StringComparison.Ordinal))
        {
            var acquired = await workStore.AcquireFenceAsync(
                    context,
                    key,
                    MaterializationGenerationActivationIdentities.AcquireFence(
                        key,
                        convergence,
                        invocation,
                        worker),
                    work.Revision,
                    owner)
                .ConfigureAwait(false);
            if (acquired.Disposition is not (
                MaterializationSynchronizationWorkMutationDisposition.Applied
                or MaterializationSynchronizationWorkMutationDisposition.Replayed))
            {
                return WorkStoreFailure(
                    generation,
                    synchronizationResult,
                    work.Activation,
                    acquired,
                    acquisition: true);
            }
            work = acquired.Snapshot!;
        }

        if (work.Activation is { } persisted)
        {
            if (persisted.Convergence.Fingerprint != convergence.Fingerprint)
            {
                return Failure(
                    MaterializationGenerationActivationDisposition.Fenced,
                    generation,
                    synchronizationResult,
                    persisted,
                    MaterializationGenerationActivationDiagnosticCodes.Fenced,
                    "Another activation became durable while the promotion-specific fence was acquired.");
            }
            convergence = persisted.Convergence;
            if (persisted.IsComplete)
                return await CompletedAsync(context, generation, persisted, synchronizationResult).ConfigureAwait(false);
            if (persisted.ValidationReceipt is { Validation.IsValid: false })
                return InvalidValidation(generation, synchronizationResult, persisted);
        }
        else
        {
            var readiness = await ValidateLiveConvergenceAsync(context, work, convergence).ConfigureAwait(false);
            if (!readiness.IsValid)
            {
                return new(
                    MaterializationGenerationActivationDisposition.NotReady,
                    generation,
                    synchronizationResult,
                    activation: null,
                    target: null,
                    readiness.Diagnostics);
            }

            var candidate = await resolved.Target.InspectGenerationAsync(context, generation).ConfigureAwait(false);
            if (!IsWritableCandidate(candidate, generation))
            {
                return Failure(
                    MaterializationGenerationActivationDisposition.NotReady,
                    generation,
                    synchronizationResult,
                    activation: null,
                    MaterializationGenerationActivationDiagnosticCodes.NotReady,
                    "The exact converged generation is absent, changed, or no longer a writable loading candidate.");
            }

            var sealRequest = new MaterializationSealGenerationRequest(
                sealId: MaterializationGenerationActivationIdentities.Seal(key, convergence),
                generationId: generation,
                expectedRevision: candidate!.Revision,
                workerFence: ToTargetWorkerFence(work.Fence),
                sealedAtUtc: context.UtcNow);
            var initial = new MaterializationGenerationActivationState(
                convergence: convergence,
                sealRequest: sealRequest);
            var saved = await SaveActivationAsync(
                    context,
                    work,
                    initial,
                    stage: "seal-intent")
                .ConfigureAwait(false);
            if (saved.Disposition is not (
                MaterializationSynchronizationWorkMutationDisposition.Applied
                or MaterializationSynchronizationWorkMutationDisposition.Replayed))
            {
                return WorkStoreFailure(
                    generation,
                    synchronizationResult,
                    activation: null,
                    saved,
                    acquisition: false);
            }
            work = saved.Snapshot!;
        }

        while (true)
        {
            var activation = work.Activation
                ?? throw new InvalidOperationException("Successful activation persistence omitted durable state.");
            if (activation.SealReceipt is null)
            {
                var outcome = await ReconcileSealAsync(context, generation, synchronizationResult, work, activation)
                    .ConfigureAwait(false);
                if (outcome.Result is not null)
                    return outcome.Result;
                work = outcome.Work!;
                continue;
            }
            if (activation.ValidationReceipt is null)
            {
                var outcome = await ReconcileValidationAsync(context, generation, synchronizationResult, work, activation)
                    .ConfigureAwait(false);
                if (outcome.Result is not null)
                    return outcome.Result;
                work = outcome.Work!;
                continue;
            }
            if (!activation.ValidationReceipt.Validation.IsValid)
                return InvalidValidation(generation, synchronizationResult, activation);
            if (activation.PromotionReceipt is null)
            {
                return Ready(generation, synchronizationResult, activation);
            }
            return await CompletedAsync(context, generation, activation, synchronizationResult).ConfigureAwait(false);
        }
    }

    /// <summary>Reconciles only the exact target-pointer promotion retained by a ready-generation reference.</summary>
    /// <param name="context">Explicit cancellation, time, identity, and tracing context.</param>
    /// <param name="attempt">Exact Process attempt retaining the candidate generation.</param>
    /// <param name="ready">Exact durable successful-validation and promotion-intent evidence.</param>
    /// <returns>Active, not-ready, failed, fenced, or restart-required evidence.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="ready"/> belongs to another leaf, attempt, or generation.</exception>
    /// <exception cref="OperationCanceledException">The operation is cancelled.</exception>
    /// <exception cref="Exception">
    /// A delegated durable-store or target operation fails without a conclusive typed disposition. The exception is
    /// propagated so the owning durable Request can reconcile the exact retained intent.
    /// </exception>
    public async Task<MaterializationGenerationActivationResult> ActivateReadyAsync(
        OperationContext context,
        MaterializationRebuildAttempt attempt,
        MaterializationReadyGenerationReference ready)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentNullException.ThrowIfNull(ready);
        context.ThrowIfCancellationRequested();

        var generation = MaterializationRebuildIdentities.Generation(resolved.Plan, attempt);
        if (ready.Authority != resolved.Authority
            || ready.Attempt != attempt
            || ready.Generation != generation)
        {
            throw new ArgumentException(
                "Ready-generation evidence belongs to another linked leaf, Process attempt, or generation.",
                nameof(ready));
        }

        var key = synchronization.GetWorkKey(attempt);
        if (ready.Convergence.Synchronization != key)
        {
            throw new ArgumentException(
                "Ready-generation convergence belongs to another exact synchronization-work aggregate.",
                nameof(ready));
        }

        var work = await workStore.LoadAsync(context, key).ConfigureAwait(false);
        if (work?.Activation is not { } activation)
        {
            return Failure(
                MaterializationGenerationActivationDisposition.NotReady,
                generation,
                synchronizationResult: null,
                activation: null,
                MaterializationGenerationActivationDiagnosticCodes.NotReady,
                "The exact durable readiness prefix is unavailable.");
        }
        if (!HasExactPreparation(activation, ready.Preparation))
        {
            return Failure(
                MaterializationGenerationActivationDisposition.RestartRequired,
                generation,
                synchronizationResult: null,
                activation,
                MaterializationGenerationActivationDiagnosticCodes.RestartRequired,
                "Durable activation state no longer retains the exact supplied readiness prefix.");
        }
        if (activation.IsComplete)
            return await CompletedAsync(context, generation, activation, synchronizationResult: null).ConfigureAwait(false);
        if (!activation.IsReady)
        {
            return Failure(
                MaterializationGenerationActivationDisposition.NotReady,
                generation,
                synchronizationResult: null,
                activation,
                MaterializationGenerationActivationDiagnosticCodes.NotReady,
                "The exact durable generation is not ready for target-pointer promotion.");
        }

        var outcome = await ReconcilePromotionAsync(
                context: context,
                attempt: attempt,
                generation: generation,
                synchronizationResult: null,
                work: work,
                activation: activation)
            .ConfigureAwait(false);
        if (outcome.Result is not null)
            return outcome.Result;
        return await CompletedAsync(
                context,
                generation,
                outcome.Work!.Activation!,
                synchronizationResult: null)
            .ConfigureAwait(false);
    }

    /// <summary>Runs preparation and immediately consumes its exact ready-generation evidence for activation.</summary>
    /// <param name="context">Explicit cancellation, time, identity, and tracing context.</param>
    /// <param name="attempt">Exact Process attempt retaining the candidate generation.</param>
    /// <param name="invocation">Stable bounded invocation identity retained across an exact durable retry.</param>
    /// <param name="worker">Explicit physical worker identity used to fence overlapping activations.</param>
    /// <returns>Active, work-remaining, not-ready, failed, fenced, or restart-required evidence.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> or <paramref name="attempt"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="invocation"/> or <paramref name="worker"/> is default.</exception>
    /// <exception cref="OperationCanceledException">The operation is cancelled.</exception>
    /// <exception cref="Exception">A delegated source, durable-store, or target operation fails inconclusively.</exception>
    public async Task<MaterializationGenerationActivationResult> ActivateAsync(
        OperationContext context,
        MaterializationRebuildAttempt attempt,
        MaterializationSynchronizationInvocationId invocation,
        MaterializationSynchronizationWorkerId worker)
    {
        var prepared = await PrepareAsync(context, attempt, invocation, worker).ConfigureAwait(false);
        if (prepared.Disposition != MaterializationGenerationActivationDisposition.Ready)
            return prepared;
        var ready = new MaterializationReadyGenerationReference(
            MaterializationReadyGenerationReference.CurrentSchemaVersion,
            resolved.Authority,
            attempt,
            prepared.Generation,
            prepared.Activation!);
        var activated = await ActivateReadyAsync(context, attempt, ready).ConfigureAwait(false);
        return prepared.Synchronization is null
            ? activated
            : new(
                activated.Disposition,
                activated.Generation,
                prepared.Synchronization,
                activated.Activation,
                activated.Target,
                activated.Diagnostics);
    }

    static bool HasExactPreparation(
        MaterializationGenerationActivationState observed,
        MaterializationGenerationActivationState expected) =>
        observed.Convergence == expected.Convergence
        && observed.SealRequest == expected.SealRequest
        && observed.SealReceipt == expected.SealReceipt
        && observed.ValidationRequest == expected.ValidationRequest
        && observed.ValidationReceipt == expected.ValidationReceipt
        && observed.PromotionRequest == expected.PromotionRequest;

    async Task<ActivationStep> ReconcileSealAsync(
        OperationContext context,
        MaterializationGenerationId generation,
        MaterializationSynchronizationRunResult? synchronizationResult,
        MaterializationSynchronizationWorkSnapshot work,
        MaterializationGenerationActivationState activation)
    {
        var current = await RequireCurrentWorkAsync(context, work).ConfigureAwait(false);
        if (current is null)
            return new(null, Fenced(generation, synchronizationResult, activation));
        work = current;

        var candidate = await resolved.Target.InspectGenerationAsync(context, generation).ConfigureAwait(false);
        if (WouldApplySeal(candidate, activation.SealRequest))
        {
            var authorization = await ValidateLiveConvergenceAsync(context, work, activation.Convergence).ConfigureAwait(false);
            if (!authorization.IsValid)
                return new(null, RestartForStaleProof(generation, synchronizationResult, activation, authorization));
        }

        var result = await resolved.Target.SealGenerationAsync(context, activation.SealRequest).ConfigureAwait(false);
        if (result.Disposition is not (
            MaterializationTargetOperationDisposition.Applied
            or MaterializationTargetOperationDisposition.Replayed))
        {
            return new(null, TargetFailure(generation, synchronizationResult, activation, result.Disposition, "seal"));
        }

        var receipt = result.Receipt!;
        var validatedAtUtc = Later(context.UtcNow, receipt.SealedAtUtc);
        var validationRequest = new MaterializationValidateGenerationRequest(
            validationId: MaterializationGenerationActivationIdentities.Validation(work.Key, activation.Convergence),
            generationId: generation,
            expectedRevision: receipt.GenerationRevision,
            expectedSealFingerprint: receipt.Fingerprint,
            expectedVisibleItemCount: receipt.VisibleItemCount,
            validator: resolved.Target.Descriptor.Capabilities.Id.Value,
            workerFence: ToTargetWorkerFence(work.Fence),
            validatedAtUtc: validatedAtUtc);
        var next = new MaterializationGenerationActivationState(
            convergence: activation.Convergence,
            sealRequest: activation.SealRequest,
            sealReceipt: receipt,
            validationRequest: validationRequest);
        var saved = await SaveActivationAsync(context, work, next, stage: "seal-receipt").ConfigureAwait(false);
        return saved.Disposition is MaterializationSynchronizationWorkMutationDisposition.Applied
            or MaterializationSynchronizationWorkMutationDisposition.Replayed
            ? new(saved.Snapshot, null)
            : new(null, WorkStoreFailure(generation, synchronizationResult, activation, saved, acquisition: false));
    }

    async Task<ActivationStep> ReconcileValidationAsync(
        OperationContext context,
        MaterializationGenerationId generation,
        MaterializationSynchronizationRunResult? synchronizationResult,
        MaterializationSynchronizationWorkSnapshot work,
        MaterializationGenerationActivationState activation)
    {
        var current = await RequireCurrentWorkAsync(context, work).ConfigureAwait(false);
        if (current is null)
            return new(null, Fenced(generation, synchronizationResult, activation));
        work = current;

        var request = activation.ValidationRequest!;
        var candidate = await resolved.Target.InspectGenerationAsync(context, generation).ConfigureAwait(false);
        if (WouldApplyValidation(candidate, request))
        {
            var authorization = await ValidateLiveConvergenceAsync(context, work, activation.Convergence).ConfigureAwait(false);
            if (!authorization.IsValid)
                return new(null, RestartForStaleProof(generation, synchronizationResult, activation, authorization));
        }

        var result = await resolved.Target.ValidateGenerationAsync(context, request).ConfigureAwait(false);
        if (result.Disposition is not (
            MaterializationTargetOperationDisposition.Applied
            or MaterializationTargetOperationDisposition.Replayed
            or MaterializationTargetOperationDisposition.ValidationFailed))
        {
            return new(null, TargetFailure(generation, synchronizationResult, activation, result.Disposition, "validation"));
        }

        var receipt = result.Receipt!;
        MaterializationPromoteGenerationRequest? promotionRequest = null;
        if (receipt.Validation.IsValid)
        {
            var target = await resolved.Target.InspectAsync(context).ConfigureAwait(false);
            promotionRequest = new MaterializationPromoteGenerationRequest(
                promotionId: MaterializationGenerationActivationIdentities.Promotion(work.Key, activation.Convergence),
                generationId: generation,
                expectedGenerationRevision: receipt.GenerationRevision,
                validationFingerprint: receipt.Fingerprint,
                expectedActiveGenerationId: target.ActiveGenerationId,
                expectedTargetRevision: target.Revision,
                generationWorkerFence: ToTargetWorkerFence(work.Fence),
                promotionFence: NextPromotionFence(target.LatestPromotionFence),
                promotedAtUtc: Later(context.UtcNow, receipt.ValidatedAtUtc));
        }

        var next = new MaterializationGenerationActivationState(
            convergence: activation.Convergence,
            sealRequest: activation.SealRequest,
            sealReceipt: activation.SealReceipt,
            validationRequest: request,
            validationReceipt: receipt,
            promotionRequest: promotionRequest);
        var saved = await SaveActivationAsync(context, work, next, stage: "validation-receipt").ConfigureAwait(false);
        if (saved.Disposition is not (
            MaterializationSynchronizationWorkMutationDisposition.Applied
            or MaterializationSynchronizationWorkMutationDisposition.Replayed))
        {
            return new(null, WorkStoreFailure(generation, synchronizationResult, activation, saved, acquisition: false));
        }
        if (!receipt.Validation.IsValid)
            return new(null, InvalidValidation(generation, synchronizationResult, saved.Snapshot!.Activation!));
        return new(saved.Snapshot, null);
    }

    async Task<ActivationStep> ReconcilePromotionAsync(
        OperationContext context,
        MaterializationRebuildAttempt attempt,
        MaterializationGenerationId generation,
        MaterializationSynchronizationRunResult? synchronizationResult,
        MaterializationSynchronizationWorkSnapshot work,
        MaterializationGenerationActivationState activation)
    {
        var current = await RequireCurrentWorkAsync(context, work).ConfigureAwait(false);
        if (current is null)
            return new(null, Fenced(generation, synchronizationResult, activation));
        work = current;

        var request = activation.PromotionRequest!;
        var target = await resolved.Target.InspectAsync(context).ConfigureAwait(false);
        var candidate = await resolved.Target.InspectGenerationAsync(context, generation).ConfigureAwait(false);
        if (WouldApplyPromotion(target, candidate, request))
        {
            var authorization = await ValidateLiveConvergenceAsync(context, work, activation.Convergence).ConfigureAwait(false);
            if (!authorization.IsValid)
                return new(null, RestartForStaleProof(generation, synchronizationResult, activation, authorization));
        }

        await ObserveBoundaryAsync(
                context: context,
                attempt: attempt,
                generation: generation,
                point: MaterializationExecutionBoundaryPoint.BeforeGenerationPromotion,
                operationIdentity: request.PromotionId.Value)
            .ConfigureAwait(false);
        var result = await resolved.Target.PromoteGenerationAsync(context, request).ConfigureAwait(false);
        if (result.Disposition is not (
            MaterializationTargetOperationDisposition.Applied
            or MaterializationTargetOperationDisposition.Replayed))
        {
            return new(null, TargetFailure(generation, synchronizationResult, activation, result.Disposition, "promotion"));
        }
        await ObserveBoundaryAsync(
                context: context,
                attempt: attempt,
                generation: generation,
                point: MaterializationExecutionBoundaryPoint.AfterGenerationPromotion,
                operationIdentity: request.PromotionId.Value)
            .ConfigureAwait(false);

        var next = new MaterializationGenerationActivationState(
            convergence: activation.Convergence,
            sealRequest: activation.SealRequest,
            sealReceipt: activation.SealReceipt,
            validationRequest: activation.ValidationRequest,
            validationReceipt: activation.ValidationReceipt,
            promotionRequest: request,
            promotionReceipt: result.Receipt);
        var saved = await SaveActivationAsync(context, work, next, stage: "promotion-receipt").ConfigureAwait(false);
        if (saved.Disposition is not (MaterializationSynchronizationWorkMutationDisposition.Applied
            or MaterializationSynchronizationWorkMutationDisposition.Replayed))
        {
            return new(
                null,
                WorkStoreFailure(generation, synchronizationResult, activation, saved, acquisition: false));
        }

        if (resolved.ControlRuntimeProvider is { } controlProvider)
        {
            controlProvider.RetireAdmissionContributions(
                generation,
                MaterializationIndexSyncWorkloadKind.Rebuild);
            if (result.Receipt!.PreviousGenerationId is { } previousGeneration)
            {
                controlProvider.RetireAdmissionContributions(
                    previousGeneration,
                    MaterializationIndexSyncWorkloadKind.Realtime);
            }
        }
        return new(saved.Snapshot, null);
    }

    async Task<MaterializationSynchronizationWorkMutationResult> SaveActivationAsync(
        OperationContext context,
        MaterializationSynchronizationWorkSnapshot work,
        MaterializationGenerationActivationState activation,
        string stage) =>
        await workStore.SaveActivationAsync(
                context,
                work.Key,
                MaterializationGenerationActivationIdentities.Save(work.Key, activation.Convergence, work.Fence, stage),
                work.Revision,
                work.FenceOwner,
                work.Fence,
                activation)
            .ConfigureAwait(false);

    async ValueTask ObserveBoundaryAsync(
        OperationContext context,
        MaterializationRebuildAttempt attempt,
        MaterializationGenerationId generation,
        MaterializationExecutionBoundaryPoint point,
        string operationIdentity) =>
        await boundaryObserver.ObserveAsync(
                context: context,
                observation: new(
                    attempt: attempt,
                    generation: generation,
                    point: point,
                    scopeIdentity: resolved.Target.Descriptor.Id.Value,
                    operationIdentity: operationIdentity,
                    occurrence: 0))
            .ConfigureAwait(false);

    async Task<MaterializationSynchronizationWorkSnapshot?> RequireCurrentWorkAsync(
        OperationContext context,
        MaterializationSynchronizationWorkSnapshot expected)
    {
        var current = await workStore.LoadAsync(context, expected.Key).ConfigureAwait(false);
        return current is not null
            && current.Revision == expected.Revision
            && current.Fence == expected.Fence
            && string.Equals(current.FenceOwner, expected.FenceOwner, StringComparison.Ordinal)
            && current.PendingWork is null
            ? current
            : null;
    }

    async Task<DocumentValidationResult> ValidateLiveConvergenceAsync(
        OperationContext context,
        MaterializationSynchronizationWorkSnapshot work,
        MaterializationConvergenceReceipt convergence)
    {
        var diagnostics = ImmutableArray.CreateBuilder<DocumentValidationDiagnostic>(convergence.Feeds.Length + 2);
        if (work.PendingWork is not null)
        {
            diagnostics.Add(Diagnostic(
                MaterializationGenerationActivationDiagnosticCodes.NotReady,
                "Generation activation cannot overlap prepared incremental target work.",
                convergence,
                location: "/synchronization/pendingWork",
                expected: "no prepared target work",
                observed: work.PendingWork.PreparationId.Value));
        }

        foreach (var evidence in convergence.Feeds)
        {
            var progressKey = MaterializationRebuildExecutor.ProgressKey(
                resolved.Plan,
                convergence.Generation,
                evidence.Scope);
            var progress = await resolved.ProgressStore.LoadAsync(
                    context,
                    progressKey)
                .ConfigureAwait(false);
            if (!IsExactLiveProgress(progress, progressKey, evidence))
            {
                diagnostics.Add(Diagnostic(
                    MaterializationGenerationActivationDiagnosticCodes.NotReady,
                    "Live application progress or settlement no longer matches the exact convergence receipt.",
                    convergence,
                    location: $"/feeds/{evidence.Feed.Value}",
                    expected: $"checkpoint={evidence.LatestChangeCheckpoint.Value}; position={evidence.ThroughPosition.Value}",
                    observed: progress?.LatestChangeCheckpoint is { } checkpoint
                        ? $"checkpoint={checkpoint.Id.Value}; position={checkpoint.Position?.Value ?? "absent"}"
                        : "progress absent"));
            }
        }

        // Evaluate dynamic proof age after every live progress read so the decision is adjacent to the target effect.
        diagnostics.AddRange(convergence.ValidateAgainst(resolved.Plan, context.UtcNow).Diagnostics);

        var normalized = MaterializationContract.NormalizeDiagnostics(diagnostics.ToImmutable(), nameof(convergence));
        return normalized.IsDefaultOrEmpty
            ? DocumentValidationResult.Valid
            : new DocumentValidationResult(normalized);
    }

    bool IsWritableCandidate(
        MaterializationGenerationSnapshot? candidate,
        MaterializationGenerationId generation) =>
        candidate is not null
        && candidate.GenerationId == generation
        && candidate.MaterializationId == resolved.Plan.Materialization.Definition.Id
        && candidate.DefinitionFingerprint == resolved.Plan.Materialization.DefinitionFingerprint
        && candidate.State == MaterializationGenerationState.Loading
        && !candidate.HasPermanentFailures
        && candidate.PendingRetryableMutationCount == 0;

    static bool IsExactLiveProgress(
        MaterializationProgressSnapshot? progress,
        MaterializationProgressKey expectedKey,
        MaterializationCatchUpFeedEvidence evidence)
    {
        var checkpoint = progress?.LatestChangeCheckpoint;
        if (progress is null
            || progress.Key != expectedKey
            || checkpoint is null
            || checkpoint.Id != evidence.LatestChangeCheckpoint
            || checkpoint.Kind != MaterializationCheckpointKind.ChangeProgress
            || checkpoint.Position != evidence.ThroughPosition
            || checkpoint.CommittedAtUtc != evidence.CheckpointCommittedAtUtc)
        {
            return false;
        }

        if (evidence.SettlementRequirement == MaterializationConvergenceSettlementRequirement.NotRequired)
            return evidence.Settlement is null;
        return evidence.Settlement is { } expected
            && SameSettlement(progress.LatestSettlement, expected);
    }

    static bool SameSettlement(
        MaterializationSourceSettlement? current,
        MaterializationSourceSettlement expected) =>
        current is not null
        && current.Id == expected.Id
        && current.Checkpoint == expected.Checkpoint
        && current.Scope == expected.Scope
        && current.Kind == expected.Kind
        && current.Position == expected.Position
        && current.Deliveries.SequenceEqual(expected.Deliveries)
        && current.SettledAtUtc == expected.SettledAtUtc
        && string.Equals(current.EvidenceReference, expected.EvidenceReference, StringComparison.Ordinal);

    static bool WouldApplySeal(
        MaterializationGenerationSnapshot? candidate,
        MaterializationSealGenerationRequest request) =>
        candidate is not null
        && candidate.GenerationId == request.GenerationId
        && candidate.Revision == request.ExpectedRevision
        && candidate.State == MaterializationGenerationState.Loading;

    static bool WouldApplyValidation(
        MaterializationGenerationSnapshot? candidate,
        MaterializationValidateGenerationRequest request) =>
        candidate is not null
        && candidate.GenerationId == request.GenerationId
        && candidate.Revision == request.ExpectedRevision
        && candidate.State == MaterializationGenerationState.Sealed;

    static bool WouldApplyPromotion(
        MaterializationTargetSnapshot target,
        MaterializationGenerationSnapshot? candidate,
        MaterializationPromoteGenerationRequest request) =>
        target.Revision == request.ExpectedTargetRevision
        && target.ActiveGenerationId == request.ExpectedActiveGenerationId
        && candidate is not null
        && candidate.GenerationId == request.GenerationId
        && candidate.Revision == request.ExpectedGenerationRevision
        && candidate.State == MaterializationGenerationState.Validated;

    static MaterializationGenerationActivationResult Ready(
        MaterializationGenerationId generation,
        MaterializationSynchronizationRunResult? synchronizationResult,
        MaterializationGenerationActivationState preparation) =>
        new(
            MaterializationGenerationActivationDisposition.Ready,
            generation,
            synchronizationResult,
            preparation,
            target: null);

    async Task<MaterializationGenerationActivationResult> CompletedAsync(
        OperationContext context,
        MaterializationGenerationId generation,
        MaterializationGenerationActivationState activation,
        MaterializationSynchronizationRunResult? synchronizationResult)
    {
        var target = await resolved.Target.InspectAsync(context).ConfigureAwait(false);
        return target.ActiveGenerationId == generation
            ? new(
                MaterializationGenerationActivationDisposition.Active,
                generation,
                synchronizationResult,
                activation,
                target)
            : Failure(
                MaterializationGenerationActivationDisposition.RestartRequired,
                generation,
                synchronizationResult,
                activation,
                MaterializationGenerationActivationDiagnosticCodes.RestartRequired,
                "The completed activation's generation is no longer the target's current read generation.");
    }

    static MaterializationGenerationActivationResult FromSynchronization(
        MaterializationSynchronizationRunResult result) =>
        result.Disposition == MaterializationSynchronizationRunDisposition.WorkRemaining
            ? new(
                MaterializationGenerationActivationDisposition.WorkRemaining,
                result.Generation,
                result)
            : new(
                result.Disposition switch
                {
                    MaterializationSynchronizationRunDisposition.NotReady =>
                        MaterializationGenerationActivationDisposition.NotReady,
                    MaterializationSynchronizationRunDisposition.Fenced =>
                        MaterializationGenerationActivationDisposition.Fenced,
                    MaterializationSynchronizationRunDisposition.RestartRequired =>
                        MaterializationGenerationActivationDisposition.RestartRequired,
                    _ => MaterializationGenerationActivationDisposition.Failed
                },
                result.Generation,
                result,
                diagnostics: result.Diagnostics);

    static MaterializationGenerationActivationResult WorkStoreFailure(
        MaterializationGenerationId generation,
        MaterializationSynchronizationRunResult? synchronizationResult,
        MaterializationGenerationActivationState? activation,
        MaterializationSynchronizationWorkMutationResult result,
        bool acquisition)
    {
        var disposition = result.Disposition switch
        {
            MaterializationSynchronizationWorkMutationDisposition.StaleFence
                or MaterializationSynchronizationWorkMutationDisposition.RevisionConflict =>
                MaterializationGenerationActivationDisposition.Fenced,
            MaterializationSynchronizationWorkMutationDisposition.IdentityConflict
                or MaterializationSynchronizationWorkMutationDisposition.ActivationConflict =>
                acquisition
                    ? MaterializationGenerationActivationDisposition.Fenced
                    : MaterializationGenerationActivationDisposition.RestartRequired,
            MaterializationSynchronizationWorkMutationDisposition.NotFound
                or MaterializationSynchronizationWorkMutationDisposition.PendingWorkConflict =>
                MaterializationGenerationActivationDisposition.NotReady,
            _ => MaterializationGenerationActivationDisposition.Failed
        };
        var diagnostics = result.Diagnostics.IsDefaultOrEmpty
            ? [Diagnostic(
                disposition == MaterializationGenerationActivationDisposition.Fenced
                    ? MaterializationGenerationActivationDiagnosticCodes.Fenced
                    : MaterializationGenerationActivationDiagnosticCodes.RestartRequired,
                $"Durable activation state was rejected with '{result.Disposition}'.",
                activation?.Convergence,
                location: "/activation",
                expected: "exact prefix-ordered durable activation",
                observed: result.Disposition.ToString())]
            : result.Diagnostics;
        return new(disposition, generation, synchronizationResult, activation, target: null, diagnostics);
    }

    static MaterializationGenerationActivationResult TargetFailure(
        MaterializationGenerationId generation,
        MaterializationSynchronizationRunResult? synchronizationResult,
        MaterializationGenerationActivationState activation,
        MaterializationTargetOperationDisposition targetDisposition,
        string operation)
    {
        var disposition = targetDisposition switch
        {
            MaterializationTargetOperationDisposition.StaleFence =>
                MaterializationGenerationActivationDisposition.Fenced,
            MaterializationTargetOperationDisposition.NotFound
                or MaterializationTargetOperationDisposition.IdentityConflict
                or MaterializationTargetOperationDisposition.RevisionConflict
                or MaterializationTargetOperationDisposition.StateConflict
                or MaterializationTargetOperationDisposition.ActiveGenerationConflict
                or MaterializationTargetOperationDisposition.MaterializationConflict =>
                MaterializationGenerationActivationDisposition.RestartRequired,
            _ => MaterializationGenerationActivationDisposition.Failed
        };
        return Failure(
            disposition,
            generation,
            synchronizationResult,
            activation,
            disposition switch
            {
                MaterializationGenerationActivationDisposition.Fenced =>
                    MaterializationGenerationActivationDiagnosticCodes.Fenced,
                MaterializationGenerationActivationDisposition.RestartRequired =>
                    MaterializationGenerationActivationDiagnosticCodes.RestartRequired,
                _ => MaterializationGenerationActivationDiagnosticCodes.TargetFailed
            },
            $"The exact target {operation} request was rejected with '{targetDisposition}'.");
    }

    static MaterializationGenerationActivationResult InvalidValidation(
        MaterializationGenerationId generation,
        MaterializationSynchronizationRunResult? synchronizationResult,
        MaterializationGenerationActivationState activation)
    {
        var diagnostics = activation.ValidationReceipt!.Validation.Diagnostics;
        if (diagnostics.IsDefaultOrEmpty)
        {
            diagnostics =
            [
                Diagnostic(
                    MaterializationGenerationActivationDiagnosticCodes.ValidationFailed,
                    "Target-native validation did not establish a promotable generation.",
                    activation.Convergence,
                    location: "/activation/validation",
                    expected: "valid immutable candidate generation",
                    observed: "validation failed")
            ];
        }
        return new(
            MaterializationGenerationActivationDisposition.Failed,
            generation,
            synchronizationResult,
            activation,
            target: null,
            diagnostics);
    }

    static MaterializationGenerationActivationResult RestartForStaleProof(
        MaterializationGenerationId generation,
        MaterializationSynchronizationRunResult? synchronizationResult,
        MaterializationGenerationActivationState activation,
        DocumentValidationResult authorization) =>
        new(
            MaterializationGenerationActivationDisposition.RestartRequired,
            generation,
            synchronizationResult,
            activation,
            target: null,
            authorization.Diagnostics);

    static MaterializationGenerationActivationResult Fenced(
        MaterializationGenerationId generation,
        MaterializationSynchronizationRunResult? synchronizationResult,
        MaterializationGenerationActivationState activation) =>
        Failure(
            MaterializationGenerationActivationDisposition.Fenced,
            generation,
            synchronizationResult,
            activation,
            MaterializationGenerationActivationDiagnosticCodes.Fenced,
            "A newer durable synchronization-work fence superseded this activation.");

    static MaterializationGenerationActivationResult Failure(
        MaterializationGenerationActivationDisposition disposition,
        MaterializationGenerationId generation,
        MaterializationSynchronizationRunResult? synchronizationResult,
        MaterializationGenerationActivationState? activation,
        string code,
        string message) =>
        new(
            disposition,
            generation,
            synchronizationResult,
            activation,
            target: null,
            diagnostics:
            [
                Diagnostic(
                    code,
                    message,
                    activation?.Convergence,
                    location: "/activation",
                    expected: "fresh, exact, prefix-ordered activation",
                    observed: disposition.ToString())
            ]);

    static DocumentValidationDiagnostic Diagnostic(
        string code,
        string message,
        MaterializationConvergenceReceipt? convergence,
        string location,
        string expected,
        string observed) =>
        MaterializationContract.CreateDiagnostic(
            code: code,
            severity: DiagnosticSeverity.Error,
            message: message,
            location: location,
            stage: "materialization-generation-activation-executor",
            subject: convergence?.Generation.Value ?? "materialization-generation",
            sourceReferences: convergence is null
                ? ["materialization-generation-activation/v1"]
                : [convergence.RebuildPlan.Value, convergence.Fingerprint.Value],
            expected: expected,
            observed: observed);

    static MaterializationWorkerFence ToTargetWorkerFence(MaterializationProgressFence fence) =>
        new(fence.Value);

    static MaterializationPromotionFence NextPromotionFence(MaterializationPromotionFence? current) =>
        current is null
            ? MaterializationPromotionFence.Initial
            : new MaterializationPromotionFence(
                checked(current.Value.Ordinal + 1).ToString(CultureInfo.InvariantCulture));

    static DateTimeOffset Later(DateTimeOffset first, DateTimeOffset second) =>
        first >= second ? first : second;

    sealed record ActivationStep(
        MaterializationSynchronizationWorkSnapshot? Work,
        MaterializationGenerationActivationResult? Result);
}

static class MaterializationGenerationActivationIdentities
{
    const string Prefix = "materialization-generation-activation/v1";

    internal static string Owner(
        MaterializationSynchronizationWorkKey key,
        MaterializationConvergenceReceipt convergence,
        MaterializationSynchronizationInvocationId invocation,
        MaterializationSynchronizationWorkerId worker) =>
        $"{Prefix}/worker/{Digest(key, convergence, [invocation.Value, worker.Value])}";

    internal static MaterializationProgressMutationId AcquireFence(
        MaterializationSynchronizationWorkKey key,
        MaterializationConvergenceReceipt convergence,
        MaterializationSynchronizationInvocationId invocation,
        MaterializationSynchronizationWorkerId worker) =>
        new($"{Prefix}/fence/{Digest(key, convergence, [invocation.Value, worker.Value])}");

    internal static MaterializationProgressMutationId Save(
        MaterializationSynchronizationWorkKey key,
        MaterializationConvergenceReceipt convergence,
        MaterializationProgressFence fence,
        string stage) =>
        new($"{Prefix}/state/{Digest(key, convergence, [fence.Value, stage])}");

    internal static MaterializationSealId Seal(
        MaterializationSynchronizationWorkKey key,
        MaterializationConvergenceReceipt convergence) =>
        new($"{Prefix}/seal/{Digest(key, convergence, [])}");

    internal static MaterializationValidationId Validation(
        MaterializationSynchronizationWorkKey key,
        MaterializationConvergenceReceipt convergence) =>
        new($"{Prefix}/validation/{Digest(key, convergence, [])}");

    internal static MaterializationPromotionId Promotion(
        MaterializationSynchronizationWorkKey key,
        MaterializationConvergenceReceipt convergence) =>
        new($"{Prefix}/promotion/{Digest(key, convergence, [])}");

    static string Digest(
        MaterializationSynchronizationWorkKey key,
        MaterializationConvergenceReceipt convergence,
        ReadOnlySpan<string> suffix)
    {
        var components = new string[8 + suffix.Length];
        components[0] = key.Materialization.Value;
        components[1] = key.DefinitionFingerprint.Value;
        components[2] = key.RebuildPlanFingerprint.Value;
        components[3] = key.ImpactPlanFingerprint.Value;
        components[4] = key.Generation.Value;
        components[5] = convergence.Fingerprint.Algorithm;
        components[6] = convergence.Fingerprint.Canonicalization;
        components[7] = convergence.Fingerprint.Value;
        suffix.CopyTo(components.AsSpan(8));
        return MaterializationStableIdentity.Digest(components);
    }
}
