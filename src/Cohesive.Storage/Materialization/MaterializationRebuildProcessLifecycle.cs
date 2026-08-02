using System.Collections.Concurrent;
using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Execution;
using Cohesive.Storage.Processes;

namespace Cohesive.Storage.Materialization;

/// <summary>Stable diagnostics emitted by the materialization-specific durable Process lifecycle.</summary>
public static class MaterializationRebuildProcessLifecycleDiagnosticCodes
{
    /// <summary>The canonical Process operation returned no coherent durable snapshot.</summary>
    public const string ProcessSnapshotUnavailable =
        "storage.materialization.rebuild.lifecycle.process.snapshotUnavailable";

    /// <summary>The coordinator start input is not the exact canonical reference configured for this facade.</summary>
    public const string StartPlanInexact =
        "storage.materialization.rebuild.lifecycle.start.planInexact";

    /// <summary>A restart did not require abandonment of affinities and release of attempt resources.</summary>
    public const string CleanupUnsupported =
        "storage.materialization.rebuild.lifecycle.restart.cleanupUnsupported";

    /// <summary>A committed restart has not yet reached the safe point that creates its replacement attempt.</summary>
    public const string RestartPending =
        "storage.materialization.rebuild.lifecycle.restart.pending";

    /// <summary>The retained old-attempt lineage does not prove the exact committed restart closure.</summary>
    public const string RestartClosureInexact =
        "storage.materialization.rebuild.lifecycle.restart.closureInexact";

    /// <summary>No exact persisted rebuild execution is available for the retained Process attempt.</summary>
    public const string ExecutionUnavailable =
        "storage.materialization.rebuild.lifecycle.execution.unavailable";

    /// <summary>The rebuild execution resolver returned evidence for another plan, attempt, or start cut.</summary>
    public const string ExecutionInexact =
        "storage.materialization.rebuild.lifecycle.execution.inexact";

    /// <summary>The retained generation affinity is absent when it is required or differs from the exact execution.</summary>
    public const string GenerationAffinityInexact =
        "storage.materialization.rebuild.lifecycle.affinity.inexact";

    /// <summary>The canonical Process runtime could not conclusively persist the exact generation affinity.</summary>
    public const string GenerationAffinityUnresolved =
        "storage.materialization.rebuild.lifecycle.affinity.unresolved";

    /// <summary>The old attempt's candidate generation could not be conclusively abandoned.</summary>
    public const string AbandonmentUnresolved =
        "storage.materialization.rebuild.lifecycle.abandonment.unresolved";

    /// <summary>The candidate initialization result was rejected or did not match the exact attempt generation.</summary>
    public const string InitializationRejected =
        "storage.materialization.rebuild.lifecycle.initialization.rejected";
}

/// <summary>Materialization lifecycle realization observed alongside one canonical Process operation.</summary>
public enum MaterializationRebuildProcessRealization
{
    /// <summary>No lifecycle realization was supplied; invalid in a completed result.</summary>
    Unspecified = 0,

    /// <summary>No materialization lifecycle work ran because the canonical Process operation did not commit.</summary>
    NotAttempted = 1,

    /// <summary>A committed restart remains deferred until its current activation reaches a safe point.</summary>
    Pending = 2,

    /// <summary>The exact generation affinity and unreadable candidate are durably reconciled.</summary>
    Ready = 3,

    /// <summary>Pause or continue preserved the exact generation affinity without candidate lifecycle I/O.</summary>
    Preserved = 4,

    /// <summary>Exact retained evidence deterministically rejected the requested lifecycle realization.</summary>
    Rejected = 5,

    /// <summary>A physical lifecycle boundary did not return a conclusive outcome.</summary>
    Unresolved = 6
}

/// <summary>Combined canonical Process disposition and materialization lifecycle realization.</summary>
/// <remarks>
/// Process durability and candidate-generation lifecycle are distinct authorities. This result keeps their
/// outcomes separate: <see cref="ProcessDisposition"/> reports only the requested Process operation, while
/// <see cref="Realization"/> reports the required Storage-owned generation work performed around that operation.
/// </remarks>
public sealed record MaterializationRebuildProcessLifecycleResult
{
    internal MaterializationRebuildProcessLifecycleResult(
        ProcessDurableRuntimeDisposition? processDisposition,
        MaterializationRebuildProcessRealization realization,
        ProcessDurableStoreSnapshot? snapshot = null,
        MaterializationGenerationId? generation = null,
        ImmutableArray<DocumentValidationDiagnostic> diagnostics = default)
    {
        if (!Enum.IsDefined(realization) || realization == MaterializationRebuildProcessRealization.Unspecified)
        {
            throw new ArgumentOutOfRangeException(
                nameof(realization),
                realization,
                "A completed rebuild lifecycle result requires an explicit realization.");
        }
        if (processDisposition == ProcessDurableRuntimeDisposition.Unspecified)
        {
            throw new ArgumentOutOfRangeException(
                nameof(processDisposition),
                processDisposition,
                "A supplied Process disposition must be explicit.");
        }
        ProcessDisposition = processDisposition;
        Realization = realization;
        Snapshot = snapshot;
        Generation = generation;
        Diagnostics = diagnostics.IsDefault ? [] : diagnostics;
    }

    /// <summary>
    /// Canonical Process operation outcome, or <see langword="null"/> when lifecycle validation prevented that
    /// operation from being attempted.
    /// </summary>
    public ProcessDurableRuntimeDisposition? ProcessDisposition { get; }

    /// <summary>Independent Storage-owned materialization lifecycle realization.</summary>
    public MaterializationRebuildProcessRealization Realization { get; }

    /// <summary>Latest coherent Process aggregate snapshot when one was available.</summary>
    public ProcessDurableStoreSnapshot? Snapshot { get; }

    /// <summary>Exact current candidate generation when its affinity and initialization are conclusive.</summary>
    public MaterializationGenerationId? Generation { get; }

    /// <summary>Structured Process and materialization lifecycle diagnostics.</summary>
    public ImmutableArray<DocumentValidationDiagnostic> Diagnostics { get; }
}

/// <summary>
/// Coordinates the canonical rebuild Process with its Storage-owned attempt-scoped candidate lifecycle.
/// </summary>
/// <remarks>
/// The facade serializes lifecycle sequences per Process instance. It binds generation affinity before candidate
/// initialization, gates every activation on reconciliation of that binding and candidate, and completes a
/// RestartAttempt in the strict order Process commit, old-generation abandonment, replacement affinity, then
/// replacement initialization. Replaying the same calls resumes those idempotent post-commit steps.
/// </remarks>
public sealed class MaterializationRebuildProcessLifecycle
{
    readonly ProcessDurableRuntime runtime;
    readonly MaterializationRebuildProcessArtifacts artifacts;
    readonly MaterializationRebuildLeafExecutionAuthority authority;
    readonly IMaterializationRebuildExecutionResolver executionResolver;
    readonly ConcurrentDictionary<ProcessInstanceId, SemaphoreSlim> instanceGates = [];

    /// <summary>Creates the lifecycle facade for one exact persisted rebuild plan and canonical Process protocol.</summary>
    /// <param name="runtime">Storage-owned durable Process runtime.</param>
    /// <param name="artifacts">Exact canonical rebuild Process and interaction artifacts.</param>
    /// <param name="authority">Exact linked plan-set, rebuild leaf, and placement authority supplied as coordinator input.</param>
    /// <param name="executionResolver">Resolver for exact attempt-scoped rebuild executions.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public MaterializationRebuildProcessLifecycle(
        ProcessDurableRuntime runtime,
        MaterializationRebuildProcessArtifacts artifacts,
        MaterializationRebuildLeafExecutionAuthority authority,
        IMaterializationRebuildExecutionResolver executionResolver)
    {
        this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        this.artifacts = artifacts ?? throw new ArgumentNullException(nameof(artifacts));
        this.authority = authority ?? throw new ArgumentNullException(nameof(authority));
        this.executionResolver = executionResolver ?? throw new ArgumentNullException(nameof(executionResolver));
    }

    /// <summary>
    /// Creates or replays the coordinator aggregate, binds its initial generation, then initializes the candidate.
    /// </summary>
    /// <param name="context">Explicit cancellation, time, identity, and tracing context.</param>
    /// <param name="start">Previously accepted canonical coordinator start evidence.</param>
    /// <returns>Separate Process durability and candidate-lifecycle outcomes.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException">The operation is cancelled.</exception>
    public async Task<MaterializationRebuildProcessLifecycleResult> InitializeAsync(
        OperationContext context,
        ProcessStartReceipt start)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(start);
        if (!HasExactPlanInput(start))
        {
            return new(
                processDisposition: null,
                MaterializationRebuildProcessRealization.Rejected,
                diagnostics: [Error(
                    MaterializationRebuildProcessLifecycleDiagnosticCodes.StartPlanInexact,
                    "The coordinator start input must be the exact canonical linked leaf execution authority configured for this lifecycle.",
                    "/start/request/input",
                    authority.LeafPlan.Plan.Value)]);
        }
        var instanceId = start.Request.InitialContinuation.ProcessInstanceId;
        return await WithInstanceGateAsync(
                context,
                instanceId,
                async () =>
                {
                    var initialized = await runtime.InitializeAsync(
                            context,
                            artifacts.CoordinatorPlan,
                            start)
                        .ConfigureAwait(false);
                    if (!IsCommitted(initialized.Disposition))
                    {
                        return new(
                            initialized.Disposition,
                            MaterializationRebuildProcessRealization.NotAttempted,
                            initialized.Snapshot,
                            diagnostics: initialized.Diagnostics);
                    }

                    if (initialized.Snapshot is not { } snapshot)
                    {
                        return MissingSnapshot(
                            initialized.Disposition,
                            initialized.Diagnostics,
                            subject: instanceId.Value);
                    }

                    var realization = await EnsureCurrentAttemptReadyAsync(context, snapshot)
                        .ConfigureAwait(false);
                    return Result(
                        initialized.Disposition,
                        realization,
                        initialized.Diagnostics);
                })
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Applies Pause, Continue, or RestartAttempt with materialization-specific affinity and generation semantics.
    /// </summary>
    /// <param name="context">Explicit cancellation, time, identity, and tracing context.</param>
    /// <param name="command">Canonical Pause, Continue, or RestartAttempt command.</param>
    /// <returns>Separate Process-control and candidate-lifecycle outcomes.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="command"/> is not Pause, Continue, or RestartAttempt.</exception>
    /// <exception cref="OperationCanceledException">The operation is cancelled.</exception>
    public async Task<MaterializationRebuildProcessLifecycleResult> ApplyControlAsync(
        OperationContext context,
        ProcessControlCommand command)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(command);
        if (command is not (PauseProcessCommand or ContinueProcessCommand or RestartProcessAttemptCommand))
        {
            throw new ArgumentException(
                "The materialization rebuild lifecycle supports only Pause, Continue, and RestartAttempt.",
                nameof(command));
        }

        if (command is RestartProcessAttemptCommand restart
            && restart.Plan.Cleanup != ProcessAttemptCleanupRequirement.AbandonAffinitiesAndReleaseResources)
        {
            return new(
                processDisposition: null,
                MaterializationRebuildProcessRealization.Rejected,
                diagnostics: [Error(
                    MaterializationRebuildProcessLifecycleDiagnosticCodes.CleanupUnsupported,
                    "A materialization rebuild restart must abandon affinities and release attempt resources.",
                    "/command/plan/cleanup",
                    restart.Plan.Cleanup.ToString())]);
        }

        var instanceId = command.Context.ProcessInstanceId;
        return await WithInstanceGateAsync(
                context,
                instanceId,
                async () =>
                {
                    var controlled = await runtime.ApplyControlAsync(
                            context,
                            artifacts.CoordinatorPlan,
                            command)
                        .ConfigureAwait(false);
                    if (!IsCommitted(controlled.Disposition))
                    {
                        return new(
                            controlled.Disposition,
                            MaterializationRebuildProcessRealization.NotAttempted,
                            controlled.Snapshot,
                            diagnostics: controlled.Diagnostics);
                    }

                    if (controlled.Snapshot is not { } snapshot)
                    {
                        return MissingSnapshot(
                            controlled.Disposition,
                            controlled.Diagnostics,
                            subject: instanceId.Value);
                    }

                    if (command is RestartProcessAttemptCommand restartCommand)
                    {
                        var realization = await CompleteRestartAsync(context, snapshot, restartCommand)
                            .ConfigureAwait(false);
                        return Result(
                            controlled.Disposition,
                            realization,
                            controlled.Diagnostics);
                    }

                    var preserved = ValidateBoundCurrentAttempt(snapshot);
                    return preserved.Execution is null
                        ? Result(
                            controlled.Disposition,
                            preserved,
                            controlled.Diagnostics)
                        : new(
                            controlled.Disposition,
                            MaterializationRebuildProcessRealization.Preserved,
                            snapshot,
                            preserved.Execution.Generation,
                            controlled.Diagnostics);
                })
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Reconciles the exact current generation affinity and candidate before attempting one finite activation.
    /// </summary>
    /// <param name="context">Explicit cancellation, time, identity, and tracing context.</param>
    /// <param name="expectedContinuation">Exact coordinator instance and attempt intended for activation.</param>
    /// <param name="activation">Exact caller-owned finite activation.</param>
    /// <returns>Separate Process-activation and candidate-lifecycle outcomes.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException">The operation is cancelled.</exception>
    public async Task<MaterializationRebuildProcessLifecycleResult> ActivateAsync(
        OperationContext context,
        ProcessContinuationIdentity expectedContinuation,
        ProcessActivation activation)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(expectedContinuation);
        ArgumentNullException.ThrowIfNull(activation);
        return await WithInstanceGateAsync(
                context,
                expectedContinuation.ProcessInstanceId,
                async () =>
                {
                    var inspected = await runtime.InspectAsync(
                            context,
                            artifacts.CoordinatorPlan,
                            expectedContinuation)
                        .ConfigureAwait(false);
                    if (inspected.Disposition == ProcessDurableRuntimeDisposition.StaleFence
                        && inspected.Snapshot is { } staleSnapshot
                        && TryFindRetainedRestart(
                            staleSnapshot,
                            expectedContinuation,
                            out var retainedRestart))
                    {
                        var replayed = await runtime.ActivateAsync(
                                context,
                                artifacts.CoordinatorPlan,
                                expectedContinuation,
                                activation)
                            .ConfigureAwait(false);
                        if (replayed.Disposition != ProcessDurableRuntimeDisposition.Replayed
                            || replayed.Snapshot is not { } replayedSnapshot)
                        {
                            return new(
                                replayed.Disposition,
                                MaterializationRebuildProcessRealization.NotAttempted,
                                replayed.Snapshot ?? staleSnapshot,
                                diagnostics: replayed.Diagnostics);
                        }

                        var restarted = await CompleteRestartAsync(
                                context,
                                replayedSnapshot,
                                retainedRestart!)
                            .ConfigureAwait(false);
                        return Result(replayed.Disposition, restarted, replayed.Diagnostics);
                    }

                    if (inspected.Disposition != ProcessDurableRuntimeDisposition.Replayed
                        || inspected.Snapshot is not { } snapshot)
                    {
                        return new(
                            processDisposition: null,
                            MaterializationRebuildProcessRealization.NotAttempted,
                            inspected.Snapshot,
                            diagnostics: inspected.Diagnostics);
                    }

                    var realization = await EnsureCurrentAttemptReadyAsync(context, snapshot)
                        .ConfigureAwait(false);
                    if (realization.Realization != MaterializationRebuildProcessRealization.Ready)
                    {
                        return new(
                            processDisposition: null,
                            realization.Realization,
                            realization.Snapshot,
                            realization.Generation,
                            realization.Diagnostics);
                    }

                    var activated = await runtime.ActivateAsync(
                            context,
                            artifacts.CoordinatorPlan,
                            expectedContinuation,
                            activation)
                        .ConfigureAwait(false);
                    if (activated.Disposition == ProcessDurableRuntimeDisposition.CommitOutcomeUnknown)
                    {
                        return new(
                            activated.Disposition,
                            MaterializationRebuildProcessRealization.Unresolved,
                            activated.Snapshot ?? realization.Snapshot,
                            realization.Generation,
                            activated.Diagnostics);
                    }

                    if (activated.Disposition == ProcessDurableRuntimeDisposition.StaleFence)
                    {
                        if (activated.Snapshot is { } postActivationSnapshot
                            && TryFindRetainedRestart(
                                postActivationSnapshot,
                                expectedContinuation,
                                out var concurrentRestart))
                        {
                            var restarted = await CompleteRestartAsync(
                                    context,
                                    postActivationSnapshot,
                                    concurrentRestart!)
                                .ConfigureAwait(false);
                            return Result(activated.Disposition, restarted, activated.Diagnostics);
                        }

                        return new(
                            activated.Disposition,
                            MaterializationRebuildProcessRealization.Unresolved,
                            activated.Snapshot ?? realization.Snapshot,
                            generation: null,
                            diagnostics: Add(
                                activated.Diagnostics,
                                Error(
                                    MaterializationRebuildProcessLifecycleDiagnosticCodes.RestartClosureInexact,
                                    "Activation fencing did not retain exact causal RestartAttempt evidence for lifecycle reconciliation.",
                                    "/checkpoint/control/attempts",
                                    expectedContinuation.ProcessAttemptId.Value)));
                    }

                    if (IsCommitted(activated.Disposition)
                        && activated.Snapshot is { } activatedSnapshot
                        && activatedSnapshot.Checkpoint.ContinuationIdentity != expectedContinuation)
                    {
                        var predecessor = FindAttempt(
                            activatedSnapshot.Checkpoint.Control,
                            expectedContinuation.ProcessAttemptId);
                        var restart = predecessor?.Closure is { } closure
                            ? FindRestartCommand(activatedSnapshot.Checkpoint.Control, closure.CommandId)
                            : null;
                        if (restart is null)
                        {
                            var rejected = Rejected(
                                activatedSnapshot,
                                MaterializationRebuildProcessLifecycleDiagnosticCodes.RestartClosureInexact,
                                "An activation replaced its Process attempt without exact retained RestartAttempt evidence.",
                                "/checkpoint/control/attempts",
                                expectedContinuation.ProcessAttemptId.Value);
                            return Result(activated.Disposition, rejected, activated.Diagnostics);
                        }

                        var restarted = await CompleteRestartAsync(context, activatedSnapshot, restart)
                            .ConfigureAwait(false);
                        return Result(activated.Disposition, restarted, activated.Diagnostics);
                    }

                    return new(
                        activated.Disposition,
                        MaterializationRebuildProcessRealization.Ready,
                        activated.Snapshot ?? realization.Snapshot,
                        realization.Generation,
                        activated.Diagnostics);
                })
            .ConfigureAwait(false);
    }

    async Task<AttemptRealization> CompleteRestartAsync(
        OperationContext context,
        ProcessDurableStoreSnapshot snapshot,
        RestartProcessAttemptCommand command)
    {
        if (command.Plan.Cleanup != ProcessAttemptCleanupRequirement.AbandonAffinitiesAndReleaseResources)
        {
            return Rejected(
                snapshot,
                MaterializationRebuildProcessLifecycleDiagnosticCodes.CleanupUnsupported,
                "A materialization rebuild restart must abandon affinities and release attempt resources.",
                "/checkpoint/control/receipts/command/plan/cleanup",
                command.Plan.Cleanup.ToString());
        }

        var checkpoint = snapshot.Checkpoint;
        var oldAttemptId = command.Expectation!.Continuation.ProcessAttemptId;
        var oldAttempt = FindAttempt(checkpoint.Control, oldAttemptId);
        var replacement = checkpoint.Control.CurrentAttempt;
        if (replacement.AttemptId != command.Plan.NewAttemptId)
        {
            if (oldAttempt?.Disposition == ProcessControlAttemptDisposition.Current
                && checkpoint.Control.Mode == ProcessControlMode.RestartRequested)
            {
                return new(
                    MaterializationRebuildProcessRealization.Pending,
                    snapshot,
                    Generation: null,
                    Execution: null,
                    Diagnostics: [Error(
                        MaterializationRebuildProcessLifecycleDiagnosticCodes.RestartPending,
                        "RestartAttempt is durably requested and awaits the current activation's next safe point.",
                        "/checkpoint/control/mode",
                        checkpoint.Control.Mode.ToString())]);
            }

            return Rejected(
                snapshot,
                MaterializationRebuildProcessLifecycleDiagnosticCodes.RestartClosureInexact,
                "The retained Process lineage does not contain the command's exact replacement attempt.",
                "/checkpoint/control/currentAttempt/attemptId",
                command.Plan.NewAttemptId.Value);
        }

        if (oldAttempt is not
            {
                Disposition: ProcessControlAttemptDisposition.Abandoned,
                Closure: { } closure
            }
            || closure.CommandId != command.Context.CommandId
            || replacement.StartedAtUtc != closure.OccurredAtUtc)
        {
            return Rejected(
                snapshot,
                MaterializationRebuildProcessLifecycleDiagnosticCodes.RestartClosureInexact,
                "The retained predecessor does not prove the exact command-linked abandonment cut.",
                "/checkpoint/control/attempts",
                oldAttemptId.Value);
        }

        var abandoned = ValidateAbandonableAttempt(snapshot, oldAttempt);
        if (abandoned.Execution is null)
            return abandoned;
        if (!await abandoned.Execution.AbandonAttemptAsync(context, closure.OccurredAtUtc)
                .ConfigureAwait(false))
        {
            return new(
                MaterializationRebuildProcessRealization.Unresolved,
                snapshot,
                abandoned.Execution.Generation,
                Execution: null,
                Diagnostics: [Error(
                    MaterializationRebuildProcessLifecycleDiagnosticCodes.AbandonmentUnresolved,
                    "The abandoned attempt's candidate absence or tombstone could not be proven.",
                    "/checkpoint/control/attempts",
                    oldAttemptId.Value)]);
        }

        return await EnsureCurrentAttemptReadyAsync(context, snapshot).ConfigureAwait(false);
    }

    async Task<AttemptRealization> EnsureCurrentAttemptReadyAsync(
        OperationContext context,
        ProcessDurableStoreSnapshot snapshot)
    {
        var current = snapshot.Checkpoint.Control.CurrentAttempt;
        var resolved = ResolveExactExecution(snapshot, current);
        if (resolved.Execution is null)
            return resolved;
        var execution = resolved.Execution;
        var expectedAffinity = MaterializationRebuildIdentities.GenerationAffinity(
            MaterializationRebuildProcessFactory.CoordinatorInitializationNodeId,
            execution.Generation);
        var retainedAffinity = current.FindAffinity(
            MaterializationRebuildProcessFactory.CoordinatorInitializationNodeId);
        if (retainedAffinity is not null && retainedAffinity != expectedAffinity)
        {
            return Rejected(
                snapshot,
                MaterializationRebuildProcessLifecycleDiagnosticCodes.GenerationAffinityInexact,
                "The current attempt retains another generation in the rebuild affinity slot.",
                "/checkpoint/control/currentAttempt/affinityBindings",
                current.AttemptId.Value);
        }

        if (retainedAffinity is null)
        {
            var bound = await runtime.BindAttemptAffinityAsync(
                    context,
                    artifacts.CoordinatorPlan,
                    new(
                        new(
                            snapshot.Checkpoint.ContinuationIdentity,
                            snapshot.Checkpoint.Control.Revision),
                        expectedAffinity,
                        context.UtcNow))
                .ConfigureAwait(false);
            if (!IsCommitted(bound.Disposition) || bound.Snapshot is not { } boundSnapshot)
            {
                var realization = bound.Disposition == ProcessDurableRuntimeDisposition.CommitOutcomeUnknown
                    ? MaterializationRebuildProcessRealization.Unresolved
                    : MaterializationRebuildProcessRealization.Rejected;
                return new(
                    realization,
                    bound.Snapshot ?? snapshot,
                    Generation: null,
                    Execution: null,
                    Diagnostics: Add(
                        bound.Diagnostics,
                        Error(
                            MaterializationRebuildProcessLifecycleDiagnosticCodes.GenerationAffinityUnresolved,
                            $"The exact generation affinity returned Process disposition '{bound.Disposition}'.",
                            "/checkpoint/control/currentAttempt/affinityBindings",
                            execution.Generation.Value)));
            }

            snapshot = boundSnapshot;
            current = snapshot.Checkpoint.Control.CurrentAttempt;
            retainedAffinity = current.FindAffinity(
                MaterializationRebuildProcessFactory.CoordinatorInitializationNodeId);
            if (current.AttemptId != execution.Attempt.Continuation.ProcessAttemptId
                || retainedAffinity != expectedAffinity)
            {
                return Rejected(
                    snapshot,
                    MaterializationRebuildProcessLifecycleDiagnosticCodes.GenerationAffinityInexact,
                    "The persisted affinity does not exactly bind the resolved current Process attempt.",
                    "/checkpoint/control/currentAttempt/affinityBindings",
                    execution.Generation.Value);
            }
        }

        var initialized = await execution.BeginAttemptAsync(context).ConfigureAwait(false);
        if (initialized.Disposition != MaterializationRebuildInitializationDisposition.Ready
            || initialized.Generation != execution.Generation
            || initialized.GenerationSnapshot is not { } generation
            || generation.GenerationId != execution.Generation
            || generation.State != MaterializationGenerationState.Loading)
        {
            return new(
                MaterializationRebuildProcessRealization.Rejected,
                snapshot,
                Generation: null,
                Execution: null,
                Diagnostics: Add(
                    initialized.Diagnostics,
                    Error(
                        MaterializationRebuildProcessLifecycleDiagnosticCodes.InitializationRejected,
                        "Candidate initialization did not return exact Loading-generation evidence.",
                        "/materialization/generation",
                        execution.Generation.Value)));
        }

        return new(
            MaterializationRebuildProcessRealization.Ready,
            snapshot,
            execution.Generation,
            execution,
            Diagnostics: []);
    }

    AttemptRealization ValidateBoundCurrentAttempt(ProcessDurableStoreSnapshot snapshot) =>
        ValidateBoundAttempt(snapshot, snapshot.Checkpoint.Control.CurrentAttempt);

    AttemptRealization ValidateAbandonableAttempt(
        ProcessDurableStoreSnapshot snapshot,
        ProcessControlAttemptState attempt)
    {
        var resolved = ResolveExactExecution(snapshot, attempt);
        if (resolved.Execution is null)
            return resolved;
        var expectedAffinity = MaterializationRebuildIdentities.GenerationAffinity(
            MaterializationRebuildProcessFactory.CoordinatorInitializationNodeId,
            resolved.Execution.Generation);
        var retained = attempt.FindAffinity(MaterializationRebuildProcessFactory.CoordinatorInitializationNodeId);
        if (retained is not null && retained != expectedAffinity)
        {
            return Rejected(
                snapshot,
                MaterializationRebuildProcessLifecycleDiagnosticCodes.GenerationAffinityInexact,
                "The abandoned Process attempt retains another generation in the rebuild affinity slot.",
                "/checkpoint/control/attempts/affinityBindings",
                attempt.AttemptId.Value);
        }

        return new(
            MaterializationRebuildProcessRealization.Preserved,
            snapshot,
            resolved.Execution.Generation,
            resolved.Execution,
            Diagnostics: []);
    }

    AttemptRealization ValidateBoundAttempt(
        ProcessDurableStoreSnapshot snapshot,
        ProcessControlAttemptState attempt)
    {
        var resolved = ResolveExactExecution(snapshot, attempt);
        if (resolved.Execution is null)
            return resolved;
        var expectedAffinity = MaterializationRebuildIdentities.GenerationAffinity(
            MaterializationRebuildProcessFactory.CoordinatorInitializationNodeId,
            resolved.Execution.Generation);
        var retained = attempt.FindAffinity(MaterializationRebuildProcessFactory.CoordinatorInitializationNodeId);
        if (retained != expectedAffinity)
        {
            return Rejected(
                snapshot,
                MaterializationRebuildProcessLifecycleDiagnosticCodes.GenerationAffinityInexact,
                "The retained Process attempt does not carry its exact candidate generation affinity.",
                "/checkpoint/control/attempts/affinityBindings",
                attempt.AttemptId.Value);
        }

        return new(
            MaterializationRebuildProcessRealization.Preserved,
            snapshot,
            resolved.Execution.Generation,
            resolved.Execution,
            Diagnostics: []);
    }

    AttemptRealization ResolveExactExecution(
        ProcessDurableStoreSnapshot snapshot,
        ProcessControlAttemptState attempt)
    {
        var continuation = new ProcessContinuationIdentity(
            snapshot.Checkpoint.ContinuationIdentity.ProcessInstanceId,
            attempt.AttemptId);
        if (!executionResolver.TryResolve(authority, continuation, out var execution) || execution is null)
        {
            return Unresolved(
                snapshot,
                MaterializationRebuildProcessLifecycleDiagnosticCodes.ExecutionUnavailable,
                "No rebuild execution is available for the exact retained Process attempt.",
                "/checkpoint/control/attempts",
                attempt.AttemptId.Value);
        }

        if (execution.Authority != authority
            || execution.Attempt.Continuation != continuation
            || execution.Attempt.StartedAtUtc != attempt.StartedAtUtc)
        {
            return Rejected(
                snapshot,
                MaterializationRebuildProcessLifecycleDiagnosticCodes.ExecutionInexact,
                "The rebuild resolver returned another plan, continuation, or attempt start cut.",
                "/execution",
                attempt.AttemptId.Value);
        }

        return new(
            MaterializationRebuildProcessRealization.Preserved,
            snapshot,
            execution.Generation,
            execution,
            Diagnostics: []);
    }

    async Task<MaterializationRebuildProcessLifecycleResult> WithInstanceGateAsync(
        OperationContext context,
        ProcessInstanceId instanceId,
        Func<Task<MaterializationRebuildProcessLifecycleResult>> action)
    {
        var gate = instanceGates.GetOrAdd(instanceId, static _ => new(1, 1));
        await gate.WaitAsync(context.CancellationToken).ConfigureAwait(false);
        try
        {
            return await action().ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    static ProcessControlAttemptState? FindAttempt(ProcessControlState state, ProcessAttemptId attemptId)
    {
        foreach (var attempt in state.Attempts)
        {
            if (attempt.AttemptId == attemptId)
                return attempt;
        }
        return null;
    }

    static RestartProcessAttemptCommand? FindRestartCommand(
        ProcessControlState state,
        ProcessControlCommandId commandId)
    {
        foreach (var receipt in state.Receipts)
        {
            if (receipt.Command.Context.CommandId == commandId)
                return receipt.Command as RestartProcessAttemptCommand;
        }
        return null;
    }

    static bool TryFindRetainedRestart(
        ProcessDurableStoreSnapshot snapshot,
        ProcessContinuationIdentity predecessor,
        out RestartProcessAttemptCommand? restart)
    {
        restart = null;
        if (snapshot.Checkpoint.ContinuationIdentity == predecessor)
            return false;
        var oldAttempt = FindAttempt(snapshot.Checkpoint.Control, predecessor.ProcessAttemptId);
        if (oldAttempt is not
            {
                Disposition: ProcessControlAttemptDisposition.Abandoned,
                Closure: { } closure
            })
        {
            return false;
        }

        restart = FindRestartCommand(snapshot.Checkpoint.Control, closure.CommandId);
        return restart is not null
            && restart.Expectation?.Continuation == predecessor
            && restart.Plan.NewAttemptId
                == snapshot.Checkpoint.Control.CurrentAttempt.AttemptId;
    }

    static bool IsCommitted(ProcessDurableRuntimeDisposition disposition) =>
        disposition is ProcessDurableRuntimeDisposition.Applied or ProcessDurableRuntimeDisposition.Replayed;

    bool HasExactPlanInput(ProcessStartReceipt start)
    {
        var input = start.Request.Input;
        if (start.Request.Definition != artifacts.CoordinatorPlan.DefinitionReference
            || input is not
            {
                State: PortableValueState.Concrete,
                Value: { } value
            }
            || input.Contract != artifacts.CoordinatorPlan.Definition.Input
            || value.Kind != Cohesive.Model.ObservationValueKind.String)
        {
            return false;
        }

        return MaterializationRebuildWorkReferenceJsonSerializer.TryDeserializeAuthority(
                value.GetRequiredString(),
                out var reference,
                out _)
            && reference == authority;
    }

    static MaterializationRebuildProcessLifecycleResult Result(
        ProcessDurableRuntimeDisposition processDisposition,
        AttemptRealization realization,
        ImmutableArray<DocumentValidationDiagnostic> processDiagnostics) =>
        new(
            processDisposition,
            realization.Realization,
            realization.Snapshot,
            realization.Generation,
            Add(processDiagnostics, realization.Diagnostics));

    static MaterializationRebuildProcessLifecycleResult MissingSnapshot(
        ProcessDurableRuntimeDisposition processDisposition,
        ImmutableArray<DocumentValidationDiagnostic> diagnostics,
        string subject) =>
        new(
            processDisposition,
            MaterializationRebuildProcessRealization.Unresolved,
            diagnostics: Add(
                diagnostics,
                Error(
                    MaterializationRebuildProcessLifecycleDiagnosticCodes.ProcessSnapshotUnavailable,
                    "A committed Process operation returned no coherent aggregate snapshot.",
                    "/process/snapshot",
                    subject)));

    static AttemptRealization Rejected(
        ProcessDurableStoreSnapshot snapshot,
        string code,
        string message,
        string location,
        string subject) =>
        new(
            MaterializationRebuildProcessRealization.Rejected,
            snapshot,
            Generation: null,
            Execution: null,
            Diagnostics: [Error(code, message, location, subject)]);

    static AttemptRealization Unresolved(
        ProcessDurableStoreSnapshot snapshot,
        string code,
        string message,
        string location,
        string subject) =>
        new(
            MaterializationRebuildProcessRealization.Unresolved,
            snapshot,
            Generation: null,
            Execution: null,
            Diagnostics: [Error(code, message, location, subject)]);

    static ImmutableArray<DocumentValidationDiagnostic> Add(
        ImmutableArray<DocumentValidationDiagnostic> first,
        ImmutableArray<DocumentValidationDiagnostic> second)
    {
        if (first.IsDefaultOrEmpty)
            return second.IsDefault ? [] : second;
        if (second.IsDefaultOrEmpty)
            return first;
        var builder = ImmutableArray.CreateBuilder<DocumentValidationDiagnostic>(first.Length + second.Length);
        builder.AddRange(first);
        builder.AddRange(second);
        return builder.MoveToImmutable();
    }

    static ImmutableArray<DocumentValidationDiagnostic> Add(
        ImmutableArray<DocumentValidationDiagnostic> diagnostics,
        DocumentValidationDiagnostic diagnostic) =>
        diagnostics.IsDefaultOrEmpty ? [diagnostic] : [.. diagnostics, diagnostic];

    static DocumentValidationDiagnostic Error(
        string code,
        string message,
        string location,
        string subject) =>
        new(
            Code: code,
            Severity: DiagnosticSeverity.Error,
            Message: message,
            Location: location,
            Evidence: new(
                stage: "materialization-rebuild-process-lifecycle",
                subject: subject));

    readonly record struct AttemptRealization(
        MaterializationRebuildProcessRealization Realization,
        ProcessDurableStoreSnapshot Snapshot,
        MaterializationGenerationId? Generation,
        MaterializationRebuildExecution? Execution,
        ImmutableArray<DocumentValidationDiagnostic> Diagnostics);
}
