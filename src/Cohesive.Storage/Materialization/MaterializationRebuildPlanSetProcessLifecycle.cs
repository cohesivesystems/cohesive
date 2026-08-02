using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Execution;
using Cohesive.Storage.Processes;

namespace Cohesive.Storage.Materialization;

/// <summary>Stable diagnostics emitted by the exact rebuild-plan-set Process lifecycle.</summary>
public static class MaterializationRebuildPlanSetProcessLifecycleDiagnosticCodes
{
    /// <summary>The parent Process start or retained checkpoint names another plan set.</summary>
    public const string PlanSetInexact =
        "storage.materialization.rebuild.planSet.lifecycle.planSet.inexact";

    /// <summary>A committed parent operation returned no coherent durable snapshot.</summary>
    public const string ProcessSnapshotUnavailable =
        "storage.materialization.rebuild.planSet.lifecycle.process.snapshotUnavailable";

    /// <summary>A restart omitted the candidate-abandonment cleanup required by rebuild semantics.</summary>
    public const string CleanupUnsupported =
        "storage.materialization.rebuild.planSet.lifecycle.cleanup.unsupported";

    /// <summary>The retained parent attempt closure does not prove the requested restart or cancellation cut.</summary>
    public const string AttemptClosureInexact =
        "storage.materialization.rebuild.planSet.lifecycle.attemptClosure.inexact";

    /// <summary>Retained leaf-child operation evidence is malformed, duplicated, or belongs to another authority.</summary>
    public const string LeafEvidenceInexact =
        "storage.materialization.rebuild.planSet.lifecycle.leafEvidence.inexact";

    /// <summary>An admitted leaf child could not be conclusively closed.</summary>
    public const string ChildClosureUnresolved =
        "storage.materialization.rebuild.planSet.lifecycle.childClosure.unresolved";

    /// <summary>A parent child-invocation operation may still create or advance its external child aggregate.</summary>
    public const string ParentChildInvocationUnresolved =
        "storage.materialization.rebuild.planSet.lifecycle.parentChildInvocation.unresolved";

    /// <summary>No exact execution is available to tombstone an old leaf attempt's candidate generation.</summary>
    public const string LeafExecutionUnavailable =
        "storage.materialization.rebuild.planSet.lifecycle.leafExecution.unavailable";

    /// <summary>The execution resolver returned another leaf authority or child continuation.</summary>
    public const string LeafExecutionInexact =
        "storage.materialization.rebuild.planSet.lifecycle.leafExecution.inexact";

    /// <summary>An old candidate generation could not be conclusively abandoned.</summary>
    public const string CandidateAbandonmentUnresolved =
        "storage.materialization.rebuild.planSet.lifecycle.candidateAbandonment.unresolved";

    /// <summary>Replacement-attempt activation remains gated by unresolved predecessor cleanup.</summary>
    public const string ReplacementCleanupPending =
        "storage.materialization.rebuild.planSet.lifecycle.replacement.cleanupPending";

    /// <summary>Pause or Continue changed attempt, child, or durable leaf-operation evidence.</summary>
    public const string PreservationInexact =
        "storage.materialization.rebuild.planSet.lifecycle.preservation.inexact";
}

/// <summary>Storage-owned realization surrounding one exact plan-set parent Process operation.</summary>
public enum MaterializationRebuildPlanSetProcessRealization
{
    /// <summary>No realization was supplied; invalid in a completed result.</summary>
    Unspecified = 0,

    /// <summary>No Storage lifecycle work ran because the canonical Process operation did not commit.</summary>
    NotAttempted = 1,

    /// <summary>The exact parent plan set is admitted and may activate.</summary>
    Ready = 2,

    /// <summary>Pause or Continue retained the exact attempt, children, and generation-bearing operations.</summary>
    Preserved = 3,

    /// <summary>Every old leaf is conclusively unallocated, abandoned, or preserved as active route/target state.</summary>
    Closed = 4,

    /// <summary>The requested operation was deterministically rejected before any lifecycle mutation or effect.</summary>
    Rejected = 5,

    /// <summary>Required child or candidate cleanup is not yet conclusive.</summary>
    Unresolved = 6
}

/// <summary>Conclusive Storage disposition of one leaf when its owning parent attempt closes.</summary>
public enum MaterializationRebuildPlanSetLeafClosureDisposition
{
    /// <summary>No disposition was supplied; invalid in closure evidence.</summary>
    Unspecified = 0,

    /// <summary>The parent conclusively caused no child execution or generation allocation for this leaf.</summary>
    NotStarted = 1,

    /// <summary>The exact candidate is permanently abandoned, including a pre-initialization tombstone.</summary>
    CandidateAbandoned = 2,

    /// <summary>The generation is already an active read or write route and is intentionally preserved.</summary>
    ActiveRoutePreserved = 3,

    /// <summary>
    /// The generation is active on its physical target but is not selected by the placement router; it is preserved
    /// until a later generation supersedes it.
    /// </summary>
    ActiveTargetPreserved = 4,

    /// <summary>Child or candidate closure remains inconclusive and replacement activation must stay gated.</summary>
    Unresolved = 5
}

/// <summary>Per-leaf closure evidence retained by a plan-set lifecycle result.</summary>
public sealed record MaterializationRebuildPlanSetLeafClosure
{
    /// <summary>Creates exact per-leaf closure evidence.</summary>
    /// <param name="authority">Exact linked plan-set, leaf-plan, and placement authority.</param>
    /// <param name="disposition">Conclusive or unresolved Storage disposition.</param>
    /// <param name="closedAtUtc">Stable parent-attempt closure cut.</param>
    /// <param name="childContinuation">Admitted exact child continuation, when one existed.</param>
    /// <param name="childTerminal">Child terminal outcome observed after closure, when a child existed.</param>
    /// <param name="promotionContinuation">Admitted exact promotion-child continuation, when one existed.</param>
    /// <param name="promotionTerminal">Promotion-child terminal outcome observed after closure, when available.</param>
    /// <param name="generation">Exact attempt-owned generation, when an execution was resolved.</param>
    /// <exception cref="ArgumentNullException"><paramref name="authority"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="disposition"/> is unspecified.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="closedAtUtc"/> is not UTC, or optional child/generation evidence contradicts the disposition.
    /// </exception>
    public MaterializationRebuildPlanSetLeafClosure(
        MaterializationRebuildLeafExecutionAuthority authority,
        MaterializationRebuildPlanSetLeafClosureDisposition disposition,
        DateTimeOffset closedAtUtc,
        ProcessContinuationIdentity? childContinuation = null,
        ExecutionTerminalOutcomeKind? childTerminal = null,
        ProcessContinuationIdentity? promotionContinuation = null,
        ExecutionTerminalOutcomeKind? promotionTerminal = null,
        MaterializationGenerationId? generation = null)
    {
        Authority = authority ?? throw new ArgumentNullException(nameof(authority));
        if (!Enum.IsDefined(disposition) || disposition == MaterializationRebuildPlanSetLeafClosureDisposition.Unspecified)
        {
            throw new ArgumentOutOfRangeException(nameof(disposition), disposition, "Leaf closure disposition must be explicit.");
        }
        MaterializationContract.RequireUtc(closedAtUtc, nameof(closedAtUtc));
        if (childTerminal == ExecutionTerminalOutcomeKind.None)
            throw new ArgumentException("Present child terminal evidence must be terminal.", nameof(childTerminal));
        if (childTerminal is not null && childContinuation is null)
            throw new ArgumentException("Child terminal evidence requires its exact continuation.", nameof(childTerminal));
        if (promotionTerminal == ExecutionTerminalOutcomeKind.None)
            throw new ArgumentException("Present promotion terminal evidence must be terminal.", nameof(promotionTerminal));
        if (promotionTerminal is not null && promotionContinuation is null)
            throw new ArgumentException("Promotion terminal evidence requires its exact continuation.", nameof(promotionTerminal));
        if (disposition == MaterializationRebuildPlanSetLeafClosureDisposition.NotStarted
            && (childContinuation is not null
                || childTerminal is not null
                || promotionContinuation is not null
                || promotionTerminal is not null
                || generation is not null))
        {
            throw new ArgumentException("A leaf with conclusive child non-execution cannot carry child or generation evidence.", nameof(disposition));
        }
        if (disposition is MaterializationRebuildPlanSetLeafClosureDisposition.CandidateAbandoned
                or MaterializationRebuildPlanSetLeafClosureDisposition.ActiveRoutePreserved
                or MaterializationRebuildPlanSetLeafClosureDisposition.ActiveTargetPreserved
            && (childContinuation is null || generation is null))
        {
            throw new ArgumentException("A realized leaf closure requires its exact child and generation.", nameof(disposition));
        }

        Disposition = disposition;
        ClosedAtUtc = closedAtUtc;
        ChildContinuation = childContinuation;
        ChildTerminal = childTerminal;
        PromotionContinuation = promotionContinuation;
        PromotionTerminal = promotionTerminal;
        Generation = generation;
    }

    /// <summary>Exact linked plan-set, leaf-plan, and placement authority.</summary>
    public MaterializationRebuildLeafExecutionAuthority Authority { get; }

    /// <summary>Conclusive or unresolved Storage disposition.</summary>
    public MaterializationRebuildPlanSetLeafClosureDisposition Disposition { get; }

    /// <summary>Stable parent-attempt closure cut used for idempotent child and candidate cleanup.</summary>
    public DateTimeOffset ClosedAtUtc { get; }

    /// <summary>Admitted exact child continuation, when one existed.</summary>
    public ProcessContinuationIdentity? ChildContinuation { get; }

    /// <summary>Child terminal outcome observed after closure, when available.</summary>
    public ExecutionTerminalOutcomeKind? ChildTerminal { get; }

    /// <summary>Admitted exact independent-promotion child continuation, when one existed.</summary>
    public ProcessContinuationIdentity? PromotionContinuation { get; }

    /// <summary>Promotion-child terminal outcome observed after closure, when available.</summary>
    public ExecutionTerminalOutcomeKind? PromotionTerminal { get; }

    /// <summary>Exact attempt-owned generation, when an execution was resolved.</summary>
    public MaterializationGenerationId? Generation { get; }
}

/// <summary>Combined parent Process disposition and Storage-owned plan-set lifecycle realization.</summary>
public sealed record MaterializationRebuildPlanSetProcessLifecycleResult
{
    internal MaterializationRebuildPlanSetProcessLifecycleResult(
        ProcessDurableRuntimeDisposition? processDisposition,
        MaterializationRebuildPlanSetProcessRealization realization,
        ProcessDurableStoreSnapshot? snapshot = null,
        ImmutableArray<MaterializationRebuildPlanSetLeafClosure> leaves = default,
        ImmutableArray<DocumentValidationDiagnostic> diagnostics = default)
    {
        if (!Enum.IsDefined(realization) || realization == MaterializationRebuildPlanSetProcessRealization.Unspecified)
        {
            throw new ArgumentOutOfRangeException(nameof(realization), realization, "Lifecycle realization must be explicit.");
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
        Leaves = leaves.IsDefault ? [] : leaves;
        Diagnostics = diagnostics.IsDefault ? [] : diagnostics;
    }

    /// <summary>Canonical parent Process outcome, or null when validation or cleanup prevented an operation.</summary>
    public ProcessDurableRuntimeDisposition? ProcessDisposition { get; }

    /// <summary>Independent Storage-owned lifecycle realization.</summary>
    public MaterializationRebuildPlanSetProcessRealization Realization { get; }

    /// <summary>Latest coherent parent aggregate snapshot when available.</summary>
    public ProcessDurableStoreSnapshot? Snapshot { get; }

    /// <summary>Canonical per-leaf closure evidence for RestartAttempt or Cancel.</summary>
    public ImmutableArray<MaterializationRebuildPlanSetLeafClosure> Leaves { get; }

    /// <summary>Structured Process and Storage lifecycle diagnostics.</summary>
    public ImmutableArray<DocumentValidationDiagnostic> Diagnostics { get; }
}

/// <summary>
/// Coordinates one exact rebuild plan-set parent Process with the lifecycle of its attempt-bound leaf candidates.
/// </summary>
/// <remarks>
/// The parent checkpoint retains child references and durable Request evidence, never copies child checkpoints.
/// Restart and cancellation first commit through the generic Process runtime, then reconstruct admitted children from
/// the old attempt's retained operations. Replacement activation is withheld until every old candidate is either
/// permanently abandoned or proven to be active on its target. Replaying the same command repeats only idempotent
/// child cancellation, route and target inspection, and candidate abandonment under the original closure cut.
/// </remarks>
public sealed class MaterializationRebuildPlanSetProcessLifecycle
{
    const string ChildCloseReason = "materialization-rebuild-plan-set-parent-attempt-closed";

    readonly ProcessDurableRuntime parentRuntime;
    readonly ProcessDurableRuntime leafRuntime;
    readonly ProcessDurableRuntime promotionRuntime;
    readonly MaterializationRebuildPlanSetProcessArtifacts artifacts;
    readonly MaterializationRebuildPlanSetReference planSetReference;
    readonly IMaterializationRebuildExecutionResolver executionResolver;
    readonly IMaterializationBackendRouter router;
    readonly ImmutableArray<MaterializationRebuildLeafExecutionAuthority> authorities;
    readonly ConcurrentDictionary<ProcessInstanceId, SemaphoreSlim> instanceGates = [];

    /// <summary>Creates a lifecycle facade for one exact persisted plan set and its parent/leaf Process graph.</summary>
    /// <param name="parentRuntime">Durable runtime owning the exact parent Process aggregate.</param>
    /// <param name="leafRuntime">Durable runtime owning leaf coordinator aggregates.</param>
    /// <param name="promotionRuntime">Durable runtime owning independent-promotion child aggregates.</param>
    /// <param name="artifacts">Canonical parent and descendant Process artifacts specialized to the plan set.</param>
    /// <param name="planSet">Exact resolved persisted plan set.</param>
    /// <param name="executionResolver">Resolver for exact attempt-scoped leaf executions.</param>
    /// <param name="router">Placement routing authority used only to preserve already-promoted generations.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="artifacts"/> belongs to another plan set.</exception>
    public MaterializationRebuildPlanSetProcessLifecycle(
        ProcessDurableRuntime parentRuntime,
        ProcessDurableRuntime leafRuntime,
        ProcessDurableRuntime promotionRuntime,
        MaterializationRebuildPlanSetProcessArtifacts artifacts,
        MaterializationRebuildPlanSet planSet,
        IMaterializationRebuildExecutionResolver executionResolver,
        IMaterializationBackendRouter router)
    {
        this.parentRuntime = parentRuntime ?? throw new ArgumentNullException(nameof(parentRuntime));
        this.leafRuntime = leafRuntime ?? throw new ArgumentNullException(nameof(leafRuntime));
        this.promotionRuntime = promotionRuntime ?? throw new ArgumentNullException(nameof(promotionRuntime));
        this.artifacts = artifacts ?? throw new ArgumentNullException(nameof(artifacts));
        ArgumentNullException.ThrowIfNull(planSet);
        this.executionResolver = executionResolver ?? throw new ArgumentNullException(nameof(executionResolver));
        this.router = router ?? throw new ArgumentNullException(nameof(router));
        planSetReference = MaterializationRebuildPlanSetReference.FromPlanSet(planSet);
        if (artifacts.PlanSet != planSetReference
            || artifacts.ParentPlan.DefinitionReference.DefinitionId
                != MaterializationRebuildPlanSetProcessFactory.GetParentDefinitionId(planSetReference))
        {
            throw new ArgumentException("Process artifacts must be specialized to the exact supplied plan set.", nameof(artifacts));
        }

        var builder = ImmutableArray.CreateBuilder<MaterializationRebuildLeafExecutionAuthority>(planSet.LeafPlans.Length);
        foreach (var binding in planSet.LeafPlans)
            builder.Add(MaterializationRebuildLeafExecutionAuthority.FromPlanSet(planSet, binding));
        authorities = builder.MoveToImmutable();
    }

    /// <summary>Creates or replays the exact parent aggregate without allocating a leaf generation.</summary>
    /// <param name="context">Explicit cancellation, time, identity, and tracing context.</param>
    /// <param name="start">Canonical parent start containing the exact plan-set reference.</param>
    /// <returns>Separate Process durability and plan-set lifecycle outcomes.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException">The operation is cancelled.</exception>
    public async Task<MaterializationRebuildPlanSetProcessLifecycleResult> InitializeAsync(
        OperationContext context,
        ProcessStartReceipt start)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(start);
        if (!HasExactPlanSet(start))
            return RejectedPlanSet(subject: start.Request.InitialContinuation.ProcessInstanceId.Value);

        return await WithGateAsync(context, start.Request.InitialContinuation.ProcessInstanceId, async () =>
        {
            var initialized = await parentRuntime.InitializeAsync(context, artifacts.ParentPlan, start).ConfigureAwait(false);
            if (!IsCommitted(initialized.Disposition))
            {
                return new(
                    initialized.Disposition,
                    MaterializationRebuildPlanSetProcessRealization.NotAttempted,
                    initialized.Snapshot,
                    diagnostics: initialized.Diagnostics);
            }
            if (initialized.Snapshot is not { } snapshot)
                return MissingSnapshot(initialized.Disposition, initialized.Diagnostics, start.Request.InitialContinuation.ProcessInstanceId.Value);
            if (!HasExactPlanSet(snapshot.Checkpoint.Start))
                return RejectedPlanSet(snapshot, initialized.Disposition);
            return new(
                initialized.Disposition,
                MaterializationRebuildPlanSetProcessRealization.Ready,
                snapshot,
                diagnostics: initialized.Diagnostics);
        }).ConfigureAwait(false);
    }

    /// <summary>Applies Pause, Continue, or RestartAttempt with exact plan-set candidate lifecycle semantics.</summary>
    /// <param name="context">Explicit cancellation, time, identity, and tracing context.</param>
    /// <param name="command">Canonical Pause, Continue, or RestartAttempt command.</param>
    /// <returns>Separate Process-control and Storage lifecycle outcomes.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="command"/> is not a supported command variant.</exception>
    /// <exception cref="OperationCanceledException">The operation is cancelled.</exception>
    public async Task<MaterializationRebuildPlanSetProcessLifecycleResult> ApplyControlAsync(
        OperationContext context,
        ProcessControlCommand command)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(command);
        if (command is not (PauseProcessCommand or ContinueProcessCommand or RestartProcessAttemptCommand))
        {
            throw new ArgumentException(
                "The plan-set lifecycle supports only Pause, Continue, and RestartAttempt through ApplyControlAsync.",
                nameof(command));
        }
        if (command is RestartProcessAttemptCommand restart
            && restart.Plan.Cleanup != ProcessAttemptCleanupRequirement.AbandonAffinitiesAndReleaseResources)
        {
            return new(
                processDisposition: null,
                MaterializationRebuildPlanSetProcessRealization.Rejected,
                diagnostics: [Error(
                    MaterializationRebuildPlanSetProcessLifecycleDiagnosticCodes.CleanupUnsupported,
                    "A rebuild plan-set restart must abandon affinities and release attempt resources.",
                    "/command/plan/cleanup",
                    restart.Plan.Cleanup.ToString())]);
        }

        return await WithGateAsync(context, command.Context.ProcessInstanceId, async () =>
        {
            ProcessDurableStoreSnapshot? before = null;
            var inspected = await parentRuntime.InspectAsync(
                context,
                artifacts.ParentPlan,
                command.Expectation!.Continuation).ConfigureAwait(false);
            if (inspected.Snapshot is { } inspectedSnapshot)
            {
                if (!HasExactPlanSet(inspectedSnapshot.Checkpoint.Start))
                    return RejectedPlanSet(inspectedSnapshot, processDisposition: null);
                if (command is PauseProcessCommand or ContinueProcessCommand)
                    before = inspectedSnapshot;
            }

            var controlled = await parentRuntime.ApplyControlAsync(context, artifacts.ParentPlan, command).ConfigureAwait(false);
            if (!IsCommitted(controlled.Disposition))
            {
                return new(
                    controlled.Disposition,
                    MaterializationRebuildPlanSetProcessRealization.NotAttempted,
                    controlled.Snapshot,
                    diagnostics: controlled.Diagnostics);
            }
            if (controlled.Snapshot is not { } snapshot)
                return MissingSnapshot(controlled.Disposition, controlled.Diagnostics, command.Context.ProcessInstanceId.Value);
            if (!HasExactPlanSet(snapshot.Checkpoint.Start))
                return RejectedPlanSet(snapshot, controlled.Disposition);

            if (command is not RestartProcessAttemptCommand restartCommand)
                return PreserveResult(before, snapshot, controlled);

            var oldAttempt = FindAttempt(snapshot.Checkpoint.Control, restartCommand.Expectation!.Continuation.ProcessAttemptId);
            if (oldAttempt is not
                {
                    Disposition: ProcessControlAttemptDisposition.Abandoned,
                    Closure: { } closure
                }
                || closure.CommandId != restartCommand.Context.CommandId)
            {
                return new(
                    controlled.Disposition,
                    MaterializationRebuildPlanSetProcessRealization.Unresolved,
                    snapshot,
                    diagnostics: Add(controlled.Diagnostics, Error(
                        MaterializationRebuildPlanSetProcessLifecycleDiagnosticCodes.AttemptClosureInexact,
                        "The retained parent lineage does not prove the exact requested RestartAttempt closure.",
                        "/checkpoint/control/attempts",
                        restartCommand.Expectation.Continuation.ProcessAttemptId.Value)));
            }

            var cleanup = await CloseAttemptLeavesAsync(context, snapshot, oldAttempt).ConfigureAwait(false);
            return CloseResult(controlled.Disposition, cleanup.Snapshot, cleanup, controlled.Diagnostics);
        }).ConfigureAwait(false);
    }

    /// <summary>Cooperatively cancels the parent and conclusively closes each allocated leaf candidate.</summary>
    /// <param name="context">Explicit cancellation, time, identity, and tracing context.</param>
    /// <param name="command">Canonical parent cancellation command.</param>
    /// <param name="activationContext">Explicit authority, delivery, and provenance for the terminal parent cut.</param>
    /// <returns>Parent cancellation plus canonical per-leaf closure evidence.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException">The operation is cancelled.</exception>
    public async Task<MaterializationRebuildPlanSetProcessLifecycleResult> CancelAsync(
        OperationContext context,
        CancelProcessCommand command,
        ProcessActivationContext activationContext)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(activationContext);
        return await WithGateAsync(context, command.Context.ProcessInstanceId, async () =>
        {
            var inspected = await parentRuntime.InspectAsync(
                context,
                artifacts.ParentPlan,
                command.Expectation!.Continuation).ConfigureAwait(false);
            if (inspected.Snapshot is { } inspectedSnapshot
                && !HasExactPlanSet(inspectedSnapshot.Checkpoint.Start))
            {
                return RejectedPlanSet(inspectedSnapshot, processDisposition: null);
            }

            var cancelled = await parentRuntime.CancelAsync(
                context,
                artifacts.ParentPlan,
                command,
                activationContext).ConfigureAwait(false);
            if (!IsCommitted(cancelled.Disposition))
            {
                return new(
                    cancelled.Disposition,
                    MaterializationRebuildPlanSetProcessRealization.NotAttempted,
                    cancelled.Snapshot,
                    diagnostics: cancelled.Diagnostics);
            }
            if (cancelled.Snapshot is not { } snapshot)
                return MissingSnapshot(cancelled.Disposition, cancelled.Diagnostics, command.Context.ProcessInstanceId.Value);
            if (!HasExactPlanSet(snapshot.Checkpoint.Start))
                return RejectedPlanSet(snapshot, cancelled.Disposition);

            var attempt = FindAttempt(snapshot.Checkpoint.Control, command.Expectation!.Continuation.ProcessAttemptId);
            if (attempt is not
                {
                    Disposition: ProcessControlAttemptDisposition.Cancelled,
                    Closure: { } closure
                }
                || closure.CommandId != command.Context.CommandId)
            {
                return new(
                    cancelled.Disposition,
                    MaterializationRebuildPlanSetProcessRealization.Unresolved,
                    snapshot,
                    diagnostics: Add(cancelled.Diagnostics, Error(
                        MaterializationRebuildPlanSetProcessLifecycleDiagnosticCodes.AttemptClosureInexact,
                        "The retained parent lineage does not prove the exact requested cancellation closure.",
                        "/checkpoint/control/attempts",
                        command.Expectation.Continuation.ProcessAttemptId.Value)));
            }

            var cleanup = await CloseAttemptLeavesAsync(context, snapshot, attempt).ConfigureAwait(false);
            return CloseResult(cancelled.Disposition, cleanup.Snapshot, cleanup, cancelled.Diagnostics);
        }).ConfigureAwait(false);
    }

    /// <summary>Activates the parent only after every abandoned predecessor attempt has conclusive leaf cleanup.</summary>
    /// <param name="context">Explicit cancellation, time, identity, and tracing context.</param>
    /// <param name="expectedContinuation">Exact current parent continuation.</param>
    /// <param name="activation">Caller-owned finite parent activation.</param>
    /// <returns>Activation outcome, or unresolved cleanup evidence without attempting activation.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException">The operation is cancelled.</exception>
    public async Task<MaterializationRebuildPlanSetProcessLifecycleResult> ActivateAsync(
        OperationContext context,
        ProcessContinuationIdentity expectedContinuation,
        ProcessActivation activation)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(expectedContinuation);
        ArgumentNullException.ThrowIfNull(activation);
        return await WithGateAsync(context, expectedContinuation.ProcessInstanceId, async () =>
        {
            var inspected = await parentRuntime.InspectAsync(
                context,
                artifacts.ParentPlan,
                expectedContinuation).ConfigureAwait(false);
            if (inspected.Disposition != ProcessDurableRuntimeDisposition.Replayed
                || inspected.Snapshot is not { } snapshot)
            {
                return new(
                    inspected.Disposition,
                    MaterializationRebuildPlanSetProcessRealization.NotAttempted,
                    inspected.Snapshot,
                    diagnostics: inspected.Diagnostics);
            }
            if (!HasExactPlanSet(snapshot.Checkpoint.Start))
                return RejectedPlanSet(snapshot, processDisposition: null);

            var allClosures = ImmutableArray.CreateBuilder<MaterializationRebuildPlanSetLeafClosure>();
            var cleanupDiagnostics = ImmutableArray.CreateBuilder<DocumentValidationDiagnostic>();
            foreach (var attempt in snapshot.Checkpoint.Control.Attempts)
            {
                if (attempt.Disposition != ProcessControlAttemptDisposition.Abandoned)
                    continue;
                var cleanup = await CloseAttemptLeavesAsync(context, snapshot, attempt).ConfigureAwait(false);
                snapshot = cleanup.Snapshot;
                allClosures.AddRange(cleanup.Leaves);
                cleanupDiagnostics.AddRange(cleanup.Diagnostics);
                if (!cleanup.Conclusive)
                {
                    cleanupDiagnostics.Add(Error(
                        MaterializationRebuildPlanSetProcessLifecycleDiagnosticCodes.ReplacementCleanupPending,
                        "Replacement activation is gated until every predecessor leaf candidate is conclusively closed.",
                        "/checkpoint/control/attempts",
                        attempt.AttemptId.Value));
                }
            }
            if (cleanupDiagnostics.Count > 0)
            {
                return new(
                    processDisposition: null,
                    MaterializationRebuildPlanSetProcessRealization.Unresolved,
                    snapshot,
                    allClosures.ToImmutable(),
                    cleanupDiagnostics.ToImmutable());
            }

            var activated = await parentRuntime.ActivateAsync(
                context,
                artifacts.ParentPlan,
                expectedContinuation,
                activation).ConfigureAwait(false);
            return new(
                activated.Disposition,
                IsCommitted(activated.Disposition)
                    ? MaterializationRebuildPlanSetProcessRealization.Ready
                    : MaterializationRebuildPlanSetProcessRealization.NotAttempted,
                activated.Snapshot,
                allClosures.ToImmutable(),
                activated.Diagnostics);
        }).ConfigureAwait(false);
    }

    MaterializationRebuildPlanSetProcessLifecycleResult PreserveResult(
        ProcessDurableStoreSnapshot? before,
        ProcessDurableStoreSnapshot after,
        ProcessDurableControlResult controlled)
    {
        if (before is null
            || before.Checkpoint.ContinuationIdentity != after.Checkpoint.ContinuationIdentity
            || !HasExactContent(
                before.Checkpoint.Continuation.Children,
                after.Checkpoint.Continuation.Children)
            || !HasExactContent(
                LeafOperations(before.Checkpoint),
                LeafOperations(after.Checkpoint))
            || !HasExactContent(
                PromotionOperations(before.Checkpoint),
                PromotionOperations(after.Checkpoint)))
        {
            return new(
                controlled.Disposition,
                MaterializationRebuildPlanSetProcessRealization.Unresolved,
                after,
                diagnostics: Add(controlled.Diagnostics, Error(
                    MaterializationRebuildPlanSetProcessLifecycleDiagnosticCodes.PreservationInexact,
                    "Pause or Continue did not retain exact parent-attempt, child, and leaf-operation evidence.",
                    "/checkpoint",
                    after.Checkpoint.ContinuationIdentity.ProcessAttemptId.Value)));
        }

        return new(
            controlled.Disposition,
            MaterializationRebuildPlanSetProcessRealization.Preserved,
            after,
            diagnostics: controlled.Diagnostics);
    }

    async Task<AttemptCleanup> CloseAttemptLeavesAsync(
        OperationContext context,
        ProcessDurableStoreSnapshot parentSnapshot,
        ProcessControlAttemptState parentAttempt)
    {
        if (parentAttempt.Closure is not { } closure)
        {
            return new(false, parentSnapshot, [], [Error(
                MaterializationRebuildPlanSetProcessLifecycleDiagnosticCodes.AttemptClosureInexact,
                "A closed parent attempt requires its exact command-linked closure cut.",
                "/checkpoint/control/attempts",
                parentAttempt.AttemptId.Value)]);
        }

        parentSnapshot = await NormalizeExpiredParentChildClaimsAsync(
            context,
            parentSnapshot,
            parentAttempt.AttemptId).ConfigureAwait(false);

        var operationByAuthority = new Dictionary<MaterializationRebuildLeafExecutionAuthority, DurableOperationState>();
        var promotionByAuthority = new Dictionary<
            MaterializationRebuildLeafExecutionAuthority,
            (DurableOperationState Operation, MaterializationReadyGenerationReference Ready)>();
        var diagnostics = ImmutableArray.CreateBuilder<DocumentValidationDiagnostic>();
        foreach (var operation in LeafOperations(parentSnapshot.Checkpoint))
        {
            if (operation.Request.Context.Origin is not ProcessInteractionOrigin origin
                || origin.Definition != artifacts.ParentPlan.DefinitionReference
                || origin.Node != MaterializationRebuildPlanSetProcessFactory.BuildLeavesNodeId
                || origin.Continuation.ProcessInstanceId != parentSnapshot.Checkpoint.ContinuationIdentity.ProcessInstanceId
                || origin.Continuation.ProcessAttemptId != parentAttempt.AttemptId)
            {
                continue;
            }
            if (!TryReadAuthority(operation.Request.Payload, out var authority)
                || authority is null
                || authority.PlanSet != planSetReference
                || !authorities.Contains(authority)
                || operation.Request.ChildTarget is not { } target
                || target.Definition != artifacts.Leaf.CoordinatorPlan.DefinitionReference)
            {
                diagnostics.Add(Error(
                    MaterializationRebuildPlanSetProcessLifecycleDiagnosticCodes.LeafEvidenceInexact,
                    "A retained leaf operation does not carry the exact plan-set authority and child target.",
                    "/checkpoint/durableOperations",
                    operation.OperationId.Value));
                continue;
            }
            if (!operationByAuthority.TryAdd(authority, operation))
            {
                diagnostics.Add(Error(
                    MaterializationRebuildPlanSetProcessLifecycleDiagnosticCodes.LeafEvidenceInexact,
                    "A parent attempt retains more than one leaf operation for the same exact authority.",
                    "/checkpoint/durableOperations",
                    authority.PlacementSlice.Id.Value));
            }
        }
        foreach (var operation in PromotionOperations(parentSnapshot.Checkpoint))
        {
            if (operation.Request.Context.Origin is not ProcessInteractionOrigin origin
                || origin.Definition != artifacts.ParentPlan.DefinitionReference
                || origin.Node != MaterializationRebuildPlanSetProcessFactory.PromoteLeavesNodeId
                || origin.Continuation.ProcessInstanceId != parentSnapshot.Checkpoint.ContinuationIdentity.ProcessInstanceId
                || origin.Continuation.ProcessAttemptId != parentAttempt.AttemptId)
            {
                continue;
            }
            if (!TryReadReady(operation.Request.Payload, out var ready)
                || ready is null
                || ready.Authority.PlanSet != planSetReference
                || !authorities.Contains(ready.Authority)
                || operation.Request.ChildTarget is not { } target
                || target.Definition != artifacts.PromotionWorkerPlan.DefinitionReference)
            {
                diagnostics.Add(Error(
                    MaterializationRebuildPlanSetProcessLifecycleDiagnosticCodes.LeafEvidenceInexact,
                    "A retained promotion operation does not carry the exact ready generation and promotion-child target.",
                    "/checkpoint/durableOperations",
                    operation.OperationId.Value));
                continue;
            }
            if (!promotionByAuthority.TryAdd(ready.Authority, (operation, ready)))
            {
                diagnostics.Add(Error(
                    MaterializationRebuildPlanSetProcessLifecycleDiagnosticCodes.LeafEvidenceInexact,
                    "A parent attempt retains more than one promotion operation for the same exact authority.",
                    "/checkpoint/durableOperations",
                    ready.Authority.PlacementSlice.Id.Value));
            }
        }

        var leaves = ImmutableArray.CreateBuilder<MaterializationRebuildPlanSetLeafClosure>(authorities.Length);
        foreach (var authority in authorities)
        {
            if (!operationByAuthority.TryGetValue(authority, out var operation))
            {
                if (promotionByAuthority.TryGetValue(authority, out var orphanedPromotion))
                {
                    diagnostics.Add(Error(
                        MaterializationRebuildPlanSetProcessLifecycleDiagnosticCodes.LeafEvidenceInexact,
                        "A retained promotion child has no exact predecessor leaf-build operation.",
                        "/checkpoint/durableOperations",
                        authority.PlacementSlice.Id.Value));
                    leaves.Add(new(
                        authority,
                        MaterializationRebuildPlanSetLeafClosureDisposition.Unresolved,
                        closure.OccurredAtUtc,
                        promotionContinuation: orphanedPromotion.Operation.Request.ChildTarget!.Continuation));
                    continue;
                }
                leaves.Add(new(
                    authority,
                    MaterializationRebuildPlanSetLeafClosureDisposition.NotStarted,
                    closure.OccurredAtUtc));
                continue;
            }
            ChildProcessClosure promotionClosure = default;
            var hasPromotion = promotionByAuthority.TryGetValue(authority, out var promotion);
            if (hasPromotion)
            {
                promotionClosure = await CloseChildProcessAsync(
                    context,
                    promotionRuntime,
                    artifacts.PromotionWorkerPlan,
                    promotion.Operation.Request,
                    promotion.Operation.Request.ChildTarget!.Continuation,
                    closure,
                    preventStartWhenAbsent: HasPotentialExternalChildConsequence(promotion.Operation))
                    .ConfigureAwait(false);
                diagnostics.AddRange(promotionClosure.Diagnostics);
                if (!promotionClosure.Conclusive)
                {
                    leaves.Add(new(
                        authority,
                        MaterializationRebuildPlanSetLeafClosureDisposition.Unresolved,
                        closure.OccurredAtUtc,
                        operation.Request.ChildTarget!.Continuation,
                        promotionContinuation: promotion.Operation.Request.ChildTarget!.Continuation,
                        promotionTerminal: promotionClosure.Terminal));
                    continue;
                }
                if (HasPotentialExternalChildConsequence(promotion.Operation)
                    && promotionClosure.Terminal is null)
                {
                    diagnostics.Add(Error(
                        MaterializationRebuildPlanSetProcessLifecycleDiagnosticCodes.ParentChildInvocationUnresolved,
                        $"Parent promotion invocation '{promotion.Operation.OperationId.Value}' remains '{promotion.Operation.Status}' and its exact child is still absent.",
                        "/checkpoint/durableOperations",
                        promotion.Operation.OperationId.Value));
                    leaves.Add(new(
                        authority,
                        MaterializationRebuildPlanSetLeafClosureDisposition.Unresolved,
                        closure.OccurredAtUtc,
                        operation.Request.ChildTarget!.Continuation,
                        promotionContinuation: promotion.Operation.Request.ChildTarget!.Continuation));
                    continue;
                }
            }
            var closed = await CloseLeafAsync(
                context,
                operation,
                authority,
                closure,
                hasPromotion && promotionClosure.Terminal is not null
                    ? promotion.Operation.Request.ChildTarget!.Continuation
                    : null,
                promotionClosure.Terminal,
                hasPromotion ? promotion.Ready : null).ConfigureAwait(false);
            leaves.Add(closed.Leaf);
            diagnostics.AddRange(closed.Diagnostics);
        }

        return new(
            diagnostics.Count == 0
                && leaves.All(static leaf => leaf.Disposition
                    != MaterializationRebuildPlanSetLeafClosureDisposition.Unresolved),
            parentSnapshot,
            leaves.MoveToImmutable(),
            diagnostics.ToImmutable());
    }

    async Task<LeafCleanup> CloseLeafAsync(
        OperationContext context,
        DurableOperationState operation,
        MaterializationRebuildLeafExecutionAuthority authority,
        ProcessAttemptClosure parentClosure,
        ProcessContinuationIdentity? promotionContinuation,
        ExecutionTerminalOutcomeKind? promotionTerminal,
        MaterializationReadyGenerationReference? promotionReady)
    {
        var target = operation.Request.ChildTarget!;
        var childClosure = await CloseChildProcessAsync(
            context,
            leafRuntime,
            artifacts.Leaf.CoordinatorPlan,
            operation.Request,
            target.Continuation,
            parentClosure,
            preventStartWhenAbsent: HasPotentialExternalChildConsequence(operation))
            .ConfigureAwait(false);
        if (!childClosure.Conclusive)
        {
            return UnresolvedLeaf(
                authority,
                target.Continuation,
                parentClosure.OccurredAtUtc,
                generation: null,
                MaterializationRebuildPlanSetProcessLifecycleDiagnosticCodes.ChildClosureUnresolved,
                "The leaf coordinator could not be conclusively closed.",
                promotionContinuation,
                promotionTerminal,
                childClosure.Diagnostics);
        }
        var childTerminal = childClosure.Terminal;
        if (childClosure.PreventedBeforeStart)
        {
            return new(
                new(
                    authority,
                    MaterializationRebuildPlanSetLeafClosureDisposition.NotStarted,
                    parentClosure.OccurredAtUtc),
                []);
        }
        if (childTerminal is null && IsConclusiveChildNonExecution(operation))
        {
            return new(
                new(
                    authority,
                    MaterializationRebuildPlanSetLeafClosureDisposition.NotStarted,
                    parentClosure.OccurredAtUtc),
                []);
        }
        if (HasPotentialExternalChildConsequence(operation) && childTerminal is null)
        {
            return UnresolvedLeaf(
                authority,
                target.Continuation,
                parentClosure.OccurredAtUtc,
                generation: null,
                MaterializationRebuildPlanSetProcessLifecycleDiagnosticCodes.ParentChildInvocationUnresolved,
                $"Parent leaf invocation '{operation.OperationId.Value}' remains '{operation.Status}' and its exact child is still absent.",
                promotionContinuation,
                promotionTerminal);
        }

        if (!executionResolver.TryResolve(authority, target.Continuation, out var execution) || execution is null)
        {
            return UnresolvedLeaf(
                authority,
                target.Continuation,
                parentClosure.OccurredAtUtc,
                generation: null,
                MaterializationRebuildPlanSetProcessLifecycleDiagnosticCodes.LeafExecutionUnavailable,
                "No execution is available to tombstone the exact old leaf candidate.",
                promotionContinuation,
                promotionTerminal);
        }
        if (execution.Authority != authority || execution.Attempt.Continuation != target.Continuation)
        {
            return UnresolvedLeaf(
                authority,
                target.Continuation,
                parentClosure.OccurredAtUtc,
                execution.Generation,
                MaterializationRebuildPlanSetProcessLifecycleDiagnosticCodes.LeafExecutionInexact,
                "The execution resolver returned another leaf authority or child continuation.",
                promotionContinuation,
                promotionTerminal);
        }
        if (promotionReady is not null
            && (promotionReady.Authority != authority
                || promotionReady.Attempt != execution.Attempt
                || promotionReady.Generation != execution.Generation))
        {
            return UnresolvedLeaf(
                authority,
                target.Continuation,
                parentClosure.OccurredAtUtc,
                execution.Generation,
                MaterializationRebuildPlanSetProcessLifecycleDiagnosticCodes.LeafEvidenceInexact,
                "The promotion child is bound to another build attempt or generation.",
                promotionContinuation,
                promotionTerminal);
        }

        var generation = new MaterializationBackendGenerationReference(
            targetId: execution.Target,
            generationId: execution.Generation,
            definitionFingerprint: authority.PlacementSlice.Materialization.DefinitionFingerprint);
        var routing = await router.InspectAsync(context, authority.PlacementSlice).ConfigureAwait(false);
        if (routing.ActiveRead?.Generation == generation || routing.ActiveWrite == generation)
        {
            return new(
                new(
                    authority,
                    MaterializationRebuildPlanSetLeafClosureDisposition.ActiveRoutePreserved,
                    parentClosure.OccurredAtUtc,
                    target.Continuation,
                    childTerminal,
                    promotionContinuation,
                    promotionTerminal,
                    execution.Generation),
                []);
        }

        var targetGeneration = await execution.InspectGenerationAsync(context).ConfigureAwait(false);
        if (targetGeneration is
            {
                State: MaterializationGenerationState.Active,
                MaterializationId: var materializationId,
                GenerationId: var generationId,
                DefinitionFingerprint: var definitionFingerprint
            }
            && materializationId == execution.Materialization
            && generationId == execution.Generation
            && definitionFingerprint == authority.PlacementSlice.Materialization.DefinitionFingerprint)
        {
            return new(
                new(
                    authority,
                    MaterializationRebuildPlanSetLeafClosureDisposition.ActiveTargetPreserved,
                    parentClosure.OccurredAtUtc,
                    target.Continuation,
                    childTerminal,
                    promotionContinuation,
                    promotionTerminal,
                    execution.Generation),
                []);
        }

        if (!await execution.AbandonAttemptAsync(context, parentClosure.OccurredAtUtc).ConfigureAwait(false))
        {
            return UnresolvedLeaf(
                authority,
                target.Continuation,
                parentClosure.OccurredAtUtc,
                execution.Generation,
                MaterializationRebuildPlanSetProcessLifecycleDiagnosticCodes.CandidateAbandonmentUnresolved,
                "The old leaf candidate could not prove permanent abandonment or absence.",
                promotionContinuation,
                promotionTerminal);
        }

        return new(
            new(
                authority,
                MaterializationRebuildPlanSetLeafClosureDisposition.CandidateAbandoned,
                parentClosure.OccurredAtUtc,
                target.Continuation,
                childTerminal,
                promotionContinuation,
                promotionTerminal,
                execution.Generation),
            []);
    }

    static LeafCleanup UnresolvedLeaf(
        MaterializationRebuildLeafExecutionAuthority authority,
        ProcessContinuationIdentity continuation,
        DateTimeOffset closedAtUtc,
        MaterializationGenerationId? generation,
        string code,
        string message,
        ProcessContinuationIdentity? promotionContinuation = null,
        ExecutionTerminalOutcomeKind? promotionTerminal = null,
        ImmutableArray<DocumentValidationDiagnostic> additionalDiagnostics = default) =>
        new(
            new(
                authority,
                MaterializationRebuildPlanSetLeafClosureDisposition.Unresolved,
                closedAtUtc,
                continuation,
                promotionContinuation: promotionContinuation,
                promotionTerminal: promotionTerminal,
                generation: generation),
            Add(
                additionalDiagnostics,
                Error(code, message, "/leaves", authority.PlacementSlice.Id.Value)));

    async Task<ChildProcessClosure> CloseChildProcessAsync(
        OperationContext context,
        ProcessDurableRuntime runtime,
        Cohesive.Processes.Compilation.CompiledProcessPlan plan,
        RequestEnvelope request,
        ProcessContinuationIdentity continuation,
        ProcessAttemptClosure parentClosure,
        bool preventStartWhenAbsent)
    {
        var inspection = await runtime.InspectAsync(context, plan, continuation).ConfigureAwait(false);
        if (inspection.Disposition == ProcessDurableRuntimeDisposition.NotFound)
        {
            if (!preventStartWhenAbsent)
            {
                return new(
                    Conclusive: true,
                    Terminal: null,
                    PreventedBeforeStart: false,
                    Diagnostics: []);
            }

            var cancellationContext = ChildCancellationContext(request, continuation, parentClosure);
            var prevention = await runtime.PreventChildStartAsync(
                    context,
                    plan,
                    request,
                    cancellationContext,
                    new(ChildCloseReason))
                .ConfigureAwait(false);
            if ((prevention.Disposition is ProcessChildStartPreventionDisposition.Prevented
                    or ProcessChildStartPreventionDisposition.Replayed)
                && prevention.Snapshot is not null)
            {
                return new(
                    Conclusive: true,
                    Terminal: ExecutionTerminalOutcomeKind.Cancelled,
                    PreventedBeforeStart: true,
                    Diagnostics: prevention.Diagnostics);
            }
            if (prevention.Disposition != ProcessChildStartPreventionDisposition.ChildAlreadyStarted
                || prevention.Snapshot is null)
            {
                return new(
                    Conclusive: false,
                    Terminal: null,
                    PreventedBeforeStart: false,
                    Diagnostics: Add(
                        prevention.Diagnostics,
                        Error(
                            MaterializationRebuildPlanSetProcessLifecycleDiagnosticCodes.ChildClosureUnresolved,
                            $"Child-start prevention returned '{prevention.Disposition}' without conclusive exact evidence.",
                            "/children",
                            continuation.ProcessInstanceId.Value)));
            }

            inspection = new(ProcessDurableRuntimeDisposition.Replayed, prevention.Snapshot);
        }
        if (inspection.Snapshot is not { } childSnapshot
            || childSnapshot.Checkpoint.ContinuationIdentity != continuation)
        {
            return new(
                Conclusive: false,
                Terminal: null,
                PreventedBeforeStart: false,
                Diagnostics: [Error(
                    MaterializationRebuildPlanSetProcessLifecycleDiagnosticCodes.ChildClosureUnresolved,
                    $"Child inspection returned '{inspection.Disposition}' without its exact continuation.",
                    "/children",
                    continuation.ProcessInstanceId.Value)]);
        }

        if (childSnapshot.Checkpoint.Continuation.Terminal.Kind == ExecutionTerminalOutcomeKind.None)
        {
            var cancel = ChildCancel(request, continuation, childSnapshot, parentClosure);
            var activationContext = new ProcessActivationContext(
                request.Context.AuthorityScope,
                request.Context.CorrelationId,
                request.Context.Delivery,
                plan.Document.Metadata.Provenance,
                causationId: request.Context.EmissionId,
                ordering: request.Context.Ordering);
            var cancelled = await runtime.CancelAsync(
                context,
                plan,
                cancel,
                activationContext).ConfigureAwait(false);
            childSnapshot = cancelled.Snapshot ?? childSnapshot;
            if (!IsCommitted(cancelled.Disposition)
                || childSnapshot.Checkpoint.Continuation.Terminal.Kind == ExecutionTerminalOutcomeKind.None)
            {
                return new(
                    Conclusive: false,
                    Terminal: null,
                    PreventedBeforeStart: false,
                    Diagnostics: [Error(
                        MaterializationRebuildPlanSetProcessLifecycleDiagnosticCodes.ChildClosureUnresolved,
                        $"Child cancellation returned '{cancelled.Disposition}' without terminal evidence.",
                        "/children",
                        continuation.ProcessInstanceId.Value)]);
            }
        }

        childSnapshot = await NormalizeExpiredChildClaimsAsync(
            context,
            runtime,
            plan,
            childSnapshot).ConfigureAwait(false);

        var consequential = childSnapshot.Checkpoint.DurableOperations.FirstOrDefault(
            HasPotentialExternalChildConsequence);
        if (consequential is not null)
        {
            return new(
                Conclusive: false,
                childSnapshot.Checkpoint.Continuation.Terminal.Kind,
                PreventedBeforeStart: false,
                Diagnostics: [Error(
                    MaterializationRebuildPlanSetProcessLifecycleDiagnosticCodes.ChildClosureUnresolved,
                    $"Cancelled child operation '{consequential.OperationId.Value}' remains '{consequential.Status}' and may retain an external consequence.",
                    "/children/durableOperations",
                    continuation.ProcessInstanceId.Value)]);
        }

        return new(
            Conclusive: true,
            childSnapshot.Checkpoint.Continuation.Terminal.Kind,
            PreventedBeforeStart: ProcessChildStartSemantics.IsExactPrevention(
                childSnapshot,
                plan,
                request,
                request.ChildTarget!,
                ChildCancellationContext(request, continuation, parentClosure),
                new(ChildCloseReason)),
            Diagnostics: []);
    }

    static CancelProcessCommand ChildCancel(
        RequestEnvelope request,
        ProcessContinuationIdentity continuation,
        ProcessDurableStoreSnapshot child,
        ProcessAttemptClosure parentClosure)
        => new(
            ProcessControlCommand.CurrentSchemaVersion,
            ChildCancellationContext(request, continuation, parentClosure),
            new(child.Checkpoint.ContinuationIdentity, child.Checkpoint.Control.Revision),
            new(ChildCloseReason));

    static ProcessControlCommandContext ChildCancellationContext(
        RequestEnvelope request,
        ProcessContinuationIdentity continuation,
        ProcessAttemptClosure parentClosure)
    {
        var identity = MaterializationStableIdentity.Digest(
            parentClosure.CommandId.Value,
            continuation.ProcessInstanceId.Value,
            continuation.ProcessAttemptId.Value);
        return new(
            new($"control/materialization-plan-set/close-child/{identity}"),
            new($"idempotency/materialization-plan-set/close-child/{identity}"),
            continuation.ProcessInstanceId,
            new(
                actor: "cohesive.storage.materialization-plan-set-lifecycle",
                authorityScope: request.Context.AuthorityScope,
                evidenceReference: parentClosure.CommandId.Value),
            parentClosure.OccurredAtUtc,
            request.Context.Provenance);
    }

    ImmutableArray<DurableOperationState> LeafOperations(ProcessDurableCheckpoint checkpoint) =>
        [.. checkpoint.DurableOperations.Where(operation => operation.Request.Contract == artifacts.LeafInvocationRequest)];

    ImmutableArray<DurableOperationState> PromotionOperations(ProcessDurableCheckpoint checkpoint) =>
        [.. checkpoint.DurableOperations.Where(operation => operation.Request.Contract == artifacts.PromotionInvocationRequest)];

    static bool HasPotentialExternalChildConsequence(DurableOperationState operation) =>
        operation.Status is DurableOperationStatus.Claimed
            or DurableOperationStatus.Dispatched
            or DurableOperationStatus.ReconciliationRequired
            or DurableOperationStatus.TerminalOutcomeRequired
            or DurableOperationStatus.EscalationRequired
        || operation.Status == DurableOperationStatus.RetryEligible
            && !HasConclusivePreCallNonExecution(operation);

    static bool IsConclusiveChildNonExecution(DurableOperationState operation) =>
        operation.Status == DurableOperationStatus.Pending
        || operation.Status == DurableOperationStatus.RetryEligible
            && HasConclusivePreCallNonExecution(operation);

    static bool HasConclusivePreCallNonExecution(DurableOperationState operation) =>
        !operation.Attempts.IsDefaultOrEmpty
        && operation.Attempts[^1] is
        {
            Stage: DurableOperationAttemptStage.Failed,
            Failure:
            {
                Phase: DurableOperationFailurePhase.PreCall,
                EffectEvidence: DurableOperationEffectEvidence.NotExecuted
            }
        };

    async Task<ProcessDurableStoreSnapshot> NormalizeExpiredParentChildClaimsAsync(
        OperationContext context,
        ProcessDurableStoreSnapshot snapshot,
        ProcessAttemptId attempt)
    {
        var operations = snapshot.Checkpoint.DurableOperations
            .Where(operation => operation.Status == DurableOperationStatus.Claimed
                && operation.CurrentAttempt is { } current
                && !current.Claim.IsLiveAt(context.UtcNow))
            .Where(operation => operation.Request.Contract == artifacts.LeafInvocationRequest
                || operation.Request.Contract == artifacts.PromotionInvocationRequest)
            .Where(operation => operation.Request.Context.Origin is ProcessInteractionOrigin origin
                && origin.Continuation.ProcessInstanceId == snapshot.Checkpoint.ContinuationIdentity.ProcessInstanceId
                && origin.Continuation.ProcessAttemptId == attempt)
            .Select(static operation => operation.OperationId)
            .ToArray();
        return await NormalizeExpiredClaimsAsync(
            context,
            parentRuntime,
            artifacts.ParentPlan,
            snapshot,
            operations).ConfigureAwait(false);
    }

    static Task<ProcessDurableStoreSnapshot> NormalizeExpiredChildClaimsAsync(
        OperationContext context,
        ProcessDurableRuntime runtime,
        Cohesive.Processes.Compilation.CompiledProcessPlan plan,
        ProcessDurableStoreSnapshot snapshot)
    {
        var operations = snapshot.Checkpoint.DurableOperations
            .Where(operation => operation.Status == DurableOperationStatus.Claimed
                && operation.CurrentAttempt is { } current
                && !current.Claim.IsLiveAt(context.UtcNow))
            .Select(static operation => operation.OperationId)
            .ToArray();
        return NormalizeExpiredClaimsAsync(context, runtime, plan, snapshot, operations);
    }

    static async Task<ProcessDurableStoreSnapshot> NormalizeExpiredClaimsAsync(
        OperationContext context,
        ProcessDurableRuntime runtime,
        Cohesive.Processes.Compilation.CompiledProcessPlan plan,
        ProcessDurableStoreSnapshot snapshot,
        IReadOnlyList<EmissionId> operations)
    {
        foreach (var operation in operations)
        {
            var normalized = await runtime.AdvanceOperationAsync(
                context,
                plan,
                snapshot.Checkpoint.ContinuationIdentity.ProcessInstanceId,
                operation).ConfigureAwait(false);
            if (normalized.Snapshot is { } updated)
                snapshot = updated;
        }
        return snapshot;
    }

    bool HasExactPlanSet(ProcessStartReceipt start) =>
        PlanSetProjection.HasExactParentStart(start, artifacts.ParentPlan, planSetReference);

    static bool TryReadAuthority(
        PortableValue value,
        out MaterializationRebuildLeafExecutionAuthority? authority)
    {
        authority = null;
        return value.State == PortableValueState.Concrete
            && value.Value is { Kind: ObservationValueKind.String, String: { } json }
            && MaterializationRebuildWorkReferenceJsonSerializer.TryDeserializeAuthority(json, out authority, out _);
    }

    static bool TryReadReady(
        PortableValue value,
        out MaterializationReadyGenerationReference? ready)
    {
        ready = null;
        if (value.State != PortableValueState.Concrete
            || value.Value is not { Kind: ObservationValueKind.String, String: { } json })
        {
            return false;
        }
        try
        {
            ready = MaterializationReadyGenerationReferenceJsonSerializer.Deserialize(json);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    async Task<MaterializationRebuildPlanSetProcessLifecycleResult> WithGateAsync(
        OperationContext context,
        ProcessInstanceId instanceId,
        Func<Task<MaterializationRebuildPlanSetProcessLifecycleResult>> action)
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

    static bool IsCommitted(ProcessDurableRuntimeDisposition disposition) =>
        disposition is ProcessDurableRuntimeDisposition.Applied or ProcessDurableRuntimeDisposition.Replayed;

    static bool HasExactContent<T>(ImmutableArray<T> first, ImmutableArray<T> second)
        where T : class
    {
        if (first.Length != second.Length)
            return false;
        for (var index = 0; index < first.Length; index++)
        {
            if (ProcessStorageContentFingerprints.Value(first[index])
                != ProcessStorageContentFingerprints.Value(second[index]))
            {
                return false;
            }
        }
        return true;
    }

    static MaterializationRebuildPlanSetProcessLifecycleResult CloseResult(
        ProcessDurableRuntimeDisposition disposition,
        ProcessDurableStoreSnapshot snapshot,
        AttemptCleanup cleanup,
        ImmutableArray<DocumentValidationDiagnostic> processDiagnostics) =>
        new(
            disposition,
            cleanup.Conclusive
                ? MaterializationRebuildPlanSetProcessRealization.Closed
                : MaterializationRebuildPlanSetProcessRealization.Unresolved,
            snapshot,
            cleanup.Leaves,
            Add(processDiagnostics, cleanup.Diagnostics));

    MaterializationRebuildPlanSetProcessLifecycleResult RejectedPlanSet(
        ProcessDurableStoreSnapshot? snapshot = null,
        ProcessDurableRuntimeDisposition? processDisposition = null,
        string? subject = null) =>
        new(
            processDisposition,
            MaterializationRebuildPlanSetProcessRealization.Rejected,
            snapshot,
            diagnostics: [Error(
                MaterializationRebuildPlanSetProcessLifecycleDiagnosticCodes.PlanSetInexact,
                "The parent start and retained checkpoint must name the exact plan set configured for this lifecycle.",
                "/checkpoint/start/request/input",
                subject ?? planSetReference.PlanSet.Value)]);

    static MaterializationRebuildPlanSetProcessLifecycleResult MissingSnapshot(
        ProcessDurableRuntimeDisposition disposition,
        ImmutableArray<DocumentValidationDiagnostic> diagnostics,
        string subject) =>
        new(
            disposition,
            MaterializationRebuildPlanSetProcessRealization.Unresolved,
            diagnostics: Add(diagnostics, Error(
                MaterializationRebuildPlanSetProcessLifecycleDiagnosticCodes.ProcessSnapshotUnavailable,
                "A committed parent Process operation returned no coherent aggregate snapshot.",
                "/process/snapshot",
                subject)));

    static ImmutableArray<DocumentValidationDiagnostic> Add(
        ImmutableArray<DocumentValidationDiagnostic> first,
        ImmutableArray<DocumentValidationDiagnostic> second)
    {
        if (first.IsDefaultOrEmpty)
            return second.IsDefault ? [] : second;
        if (second.IsDefaultOrEmpty)
            return first;
        return [.. first, .. second];
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
            code,
            DiagnosticSeverity.Error,
            message,
            location,
            Evidence: new(
                stage: "materialization-rebuild-plan-set-process-lifecycle",
                subject: subject));

    readonly record struct AttemptCleanup(
        bool Conclusive,
        ProcessDurableStoreSnapshot Snapshot,
        ImmutableArray<MaterializationRebuildPlanSetLeafClosure> Leaves,
        ImmutableArray<DocumentValidationDiagnostic> Diagnostics);

    readonly record struct LeafCleanup(
        MaterializationRebuildPlanSetLeafClosure Leaf,
        ImmutableArray<DocumentValidationDiagnostic> Diagnostics);

    readonly record struct ChildProcessClosure(
        bool Conclusive,
        ExecutionTerminalOutcomeKind? Terminal,
        bool PreventedBeforeStart,
        ImmutableArray<DocumentValidationDiagnostic> Diagnostics);
}
