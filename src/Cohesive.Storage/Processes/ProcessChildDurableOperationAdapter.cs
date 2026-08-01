using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Compilation;
using Cohesive.Processes.Execution;

namespace Cohesive.Storage.Processes;

/// <summary>Stable failure and diagnostic codes emitted while realizing a child Process Request.</summary>
public static class ProcessChildDurableOperationDiagnosticCodes
{
    /// <summary>The adapter was invoked for a Request contract outside its declared exact capability set.</summary>
    public const string RequestUnsupported = "storage.processes.childAdapter.request.unsupported";

    /// <summary>The canonical Request did not carry an exact child Process target.</summary>
    public const string ChildTargetMissing = "storage.processes.childAdapter.target.missing";

    /// <summary>No compiled plan was available for the exact pinned child Process definition.</summary>
    public const string PlanUnavailable = "storage.processes.childAdapter.plan.unavailable";

    /// <summary>The plan resolver returned a plan other than the exact pinned child Process definition.</summary>
    public const string PlanInexact = "storage.processes.childAdapter.plan.inexact";

    /// <summary>The retained child aggregate is incompatible with the exact child Request target or start intent.</summary>
    public const string ChildIncompatible = "storage.processes.childAdapter.child.incompatible";

    /// <summary>The child runtime rejected or could not safely commit required work.</summary>
    public const string ChildRuntimeRejected = "storage.processes.childAdapter.runtime.rejected";

    /// <summary>The child reached a nonterminal wait that this autonomous adapter cannot satisfy.</summary>
    public const string ChildBlocked = "storage.processes.childAdapter.drive.blocked";

    /// <summary>The explicit finite activation or durable-operation advancement budget was exhausted.</summary>
    public const string DriveLimitExceeded = "storage.processes.childAdapter.drive.limitExceeded";

    /// <summary>The terminal child evidence could not be projected to its typed parent Request outcome.</summary>
    public const string TerminalEvidenceInvalid = "storage.processes.childAdapter.terminal.invalid";

    /// <summary>The expected continuation used for a read-only runtime inspection is no longer current.</summary>
    public const string InspectionContinuationMismatch =
        "storage.processes.runtime.inspection.continuationMismatch";
}

/// <summary>Resolves an exact compiled plan for one pinned child Process definition.</summary>
/// <remarks>
/// Implementations should normally index persisted and validated Process documents by their complete definition,
/// revision, and fingerprint reference. Returning a different revision is never treated as a compatible fallback.
/// </remarks>
public interface IProcessChildPlanResolver
{
    /// <summary>Attempts to resolve the exact compiled child Process plan.</summary>
    /// <param name="definition">Pinned child Process definition, revision, and semantic fingerprint.</param>
    /// <param name="plan">Receives the exact compiled plan when available.</param>
    /// <returns><see langword="true"/> when a plan is available; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    bool TryResolve(ExecutionDefinitionReference definition, out CompiledProcessPlan? plan);
}

/// <summary>Explicit finite work limits for one child-adapter execution or reconciliation interaction.</summary>
public sealed record ProcessChildDurableOperationAdapterOptions
{
    /// <summary>Creates explicit finite child-driving limits.</summary>
    /// <param name="maximumActivations">Maximum child Process activations committed by one adapter call.</param>
    /// <param name="maximumOperationAdvances">
    /// Maximum child durable Request operations advanced by one adapter call.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="maximumActivations"/> or <paramref name="maximumOperationAdvances"/> is not positive.
    /// </exception>
    public ProcessChildDurableOperationAdapterOptions(
        int maximumActivations,
        int maximumOperationAdvances)
    {
        if (maximumActivations <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumActivations),
                maximumActivations,
                "A child adapter requires a positive finite activation limit.");
        }
        if (maximumOperationAdvances <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumOperationAdvances),
                maximumOperationAdvances,
                "A child adapter requires a positive finite durable-operation advancement limit.");
        }

        MaximumActivations = maximumActivations;
        MaximumOperationAdvances = maximumOperationAdvances;
    }

    /// <summary>Conservative default limits for one bounded adapter interaction.</summary>
    public static ProcessChildDurableOperationAdapterOptions Default { get; } = new(
        maximumActivations: 64,
        maximumOperationAdvances: 256);

    /// <summary>Maximum child Process activations committed by one adapter call.</summary>
    public int MaximumActivations { get; }

    /// <summary>Maximum child durable Request operations advanced by one adapter call.</summary>
    public int MaximumOperationAdvances { get; }
}

/// <summary>Read-only result from inspecting one exact durable Process continuation.</summary>
public sealed record ProcessDurableInspectionResult
{
    internal ProcessDurableInspectionResult(
        ProcessDurableRuntimeDisposition disposition,
        ProcessDurableStoreSnapshot? snapshot = null,
        ImmutableArray<DocumentValidationDiagnostic> diagnostics = default)
    {
        Disposition = disposition;
        Snapshot = snapshot;
        Diagnostics = diagnostics.IsDefault ? [] : diagnostics;
    }

    /// <summary>Observable inspection outcome.</summary>
    public ProcessDurableRuntimeDisposition Disposition { get; }

    /// <summary>Current coherent aggregate snapshot when the exact instance exists.</summary>
    public ProcessDurableStoreSnapshot? Snapshot { get; }

    /// <summary>Structured store-capability, plan-compatibility, or continuation diagnostics.</summary>
    public ImmutableArray<DocumentValidationDiagnostic> Diagnostics { get; }
}

public sealed partial class ProcessDurableRuntime
{
    /// <summary>Loads and validates one exact durable Process continuation without mutating it.</summary>
    /// <param name="context">Explicit cancellation, clock, identity, and tracing context.</param>
    /// <param name="plan">Exact compiled Process definition selected for restore validation.</param>
    /// <param name="expectedContinuation">Exact logical Process instance and attempt to inspect.</param>
    /// <returns>
    /// A replay disposition with the coherent snapshot, or not-found, incompatible, stale-attempt, or capability
    /// evidence without a durable mutation.
    /// </returns>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="expectedContinuation"/> contains a default Process instance identity.
    /// </exception>
    /// <exception cref="OperationCanceledException">The read is cancelled before completion.</exception>
    public async Task<ProcessDurableInspectionResult> InspectAsync(
        OperationContext context,
        CompiledProcessPlan plan,
        ProcessContinuationIdentity expectedContinuation)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(expectedContinuation);
        RequireInstance(expectedContinuation.ProcessInstanceId);
        context.ThrowIfCancellationRequested();

        var capabilityDiagnostics = ValidateCapabilities();
        if (!capabilityDiagnostics.IsEmpty)
        {
            return new(ProcessDurableRuntimeDisposition.Incompatible, diagnostics: capabilityDiagnostics);
        }

        var snapshot = await store.LoadAsync(context, expectedContinuation.ProcessInstanceId)
            .ConfigureAwait(false);
        if (snapshot is null)
        {
            return new(ProcessDurableRuntimeDisposition.NotFound);
        }

        var compatibility = Validate(plan, snapshot);
        if (!compatibility.IsValid)
        {
            return new(
                ProcessDurableRuntimeDisposition.Incompatible,
                snapshot,
                compatibility.Diagnostics);
        }
        if (snapshot.Checkpoint.ContinuationIdentity != expectedContinuation)
        {
            return new(
                ProcessDurableRuntimeDisposition.StaleFence,
                snapshot,
                [Error(
                    ProcessChildDurableOperationDiagnosticCodes.InspectionContinuationMismatch,
                    "The inspected Process attempt is no longer the current durable continuation.",
                    "/expectedContinuation/processAttemptId")]);
        }

        return new(ProcessDurableRuntimeDisposition.Replayed, snapshot);
    }
}

/// <summary>
/// Durable-operation adapter that realizes a canonical child-bearing Request through the Storage Process runtime.
/// </summary>
/// <remarks>
/// The child continuation embedded in <see cref="RequestEnvelope.ChildTarget"/> is the target-deduplication key.
/// Execute initializes that exact continuation at most once, while reconciliation only loads and drives that same
/// continuation. The adapter autonomously advances ready tokens, due timers, explicit durable cuts, retained Reply
/// inputs, and child durable Request operations. External Signals, future timers, human waits, and any work beyond
/// the explicit finite limits remain unresolved rather than being guessed or silently weakened.
/// </remarks>
public sealed class ProcessChildDurableOperationAdapter : IDurableOperationAdapter
{
    const string StartActor = "cohesive.storage.process-child-adapter";

    readonly ProcessDurableRuntime runtime;
    readonly IProcessChildPlanResolver planResolver;
    readonly ProcessChildDurableOperationAdapterOptions options;

    /// <summary>Creates a child Process adapter over an exact plan resolver and durable runtime.</summary>
    /// <param name="runtime">Durable runtime that owns child checkpoints and child durable operations.</param>
    /// <param name="planResolver">Resolver for exact pinned compiled child Process plans.</param>
    /// <param name="supportedRequests">Non-empty exact child-start Request contracts handled by this adapter.</param>
    /// <param name="options">Optional explicit finite driving limits.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="runtime"/> or <paramref name="planResolver"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="supportedRequests"/> is default, empty, contains null, or contains duplicates.
    /// </exception>
    public ProcessChildDurableOperationAdapter(
        ProcessDurableRuntime runtime,
        IProcessChildPlanResolver planResolver,
        ImmutableArray<RequestContractReference> supportedRequests,
        ProcessChildDurableOperationAdapterOptions? options = null)
    {
        this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        this.planResolver = planResolver ?? throw new ArgumentNullException(nameof(planResolver));
        this.options = options ?? ProcessChildDurableOperationAdapterOptions.Default;
        Capabilities = new(
            DurableOperationIdempotencyEvidence.TargetDeduplication,
            DurableOperationReconciliationCapability.Supported,
            supportedRequests);
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
        context.ThrowIfCancellationRequested();

        var driven = await DriveAsync(
                context,
                invocation.Request,
                initializeWhenAbsent: true)
            .ConfigureAwait(false);
        if (driven.Outcome is not null && driven.Origin is not null)
        {
            return new DurableOperationOutcomeObservation(
                driven.Outcome,
                replyOrigin: driven.Origin);
        }

        return new DurableOperationFailureObservation(
            driven.Failure
            ?? throw new InvalidOperationException(
                "Child Process execution returned neither a terminal outcome nor explicit failure evidence."));
    }

    /// <inheritdoc />
    public async ValueTask<DurableOperationReconciliationObservation> ReconcileAsync(
        OperationContext context,
        DurableOperationReconciliationRequest request)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        context.ThrowIfCancellationRequested();

        var driven = await DriveAsync(
                context,
                request.Request,
                initializeWhenAbsent: false)
            .ConfigureAwait(false);
        if (driven.ChildAbsent)
        {
            return new DurableOperationConfirmedNotExecuted();
        }
        if (driven.Outcome is not null && driven.Origin is not null)
        {
            return new DurableOperationReconciledOutcome(
                driven.Outcome,
                replyOrigin: driven.Origin);
        }

        return new DurableOperationUnresolved();
    }

    async Task<ChildDriveResult> DriveAsync(
        OperationContext context,
        RequestEnvelope request,
        bool initializeWhenAbsent)
    {
        if (!Capabilities.Supports(request.Contract))
        {
            return ChildDriveResult.Failed(
                TerminalFailure(ProcessChildDurableOperationDiagnosticCodes.RequestUnsupported));
        }
        if (request.ChildTarget is not { } target)
        {
            return ChildDriveResult.Failed(
                TerminalFailure(ProcessChildDurableOperationDiagnosticCodes.ChildTargetMissing));
        }
        if (!planResolver.TryResolve(target.Definition, out var resolvedPlan) || resolvedPlan is null)
        {
            return ChildDriveResult.Failed(
                TerminalFailure(ProcessChildDurableOperationDiagnosticCodes.PlanUnavailable));
        }
        if (resolvedPlan.DefinitionReference != target.Definition)
        {
            return ChildDriveResult.Failed(
                TerminalFailure(ProcessChildDurableOperationDiagnosticCodes.PlanInexact));
        }

        var plan = resolvedPlan;
        var inspection = await runtime.InspectAsync(context, plan, target.Continuation)
            .ConfigureAwait(false);
        ProcessDurableStoreSnapshot snapshot;
        if (inspection.Disposition == ProcessDurableRuntimeDisposition.NotFound)
        {
            if (!initializeWhenAbsent)
            {
                return ChildDriveResult.Absent();
            }

            var start = CreateStart(request, target, context.UtcNow);
            var initialized = await runtime.InitializeAsync(context, plan, start).ConfigureAwait(false);
            if (initialized.Disposition is not (
                    ProcessDurableRuntimeDisposition.Applied
                    or ProcessDurableRuntimeDisposition.Replayed)
                || initialized.Snapshot is null)
            {
                return ChildDriveResult.Failed(
                    InitializationMayHaveCommitted(initialized)
                        ? AmbiguousFailure(ProcessChildDurableOperationDiagnosticCodes.ChildRuntimeRejected)
                        : TerminalFailure(ProcessChildDurableOperationDiagnosticCodes.ChildRuntimeRejected));
            }
            snapshot = initialized.Snapshot;
        }
        else if (inspection.Disposition == ProcessDurableRuntimeDisposition.Replayed
                 && inspection.Snapshot is not null)
        {
            snapshot = inspection.Snapshot;
        }
        else
        {
            return ChildDriveResult.Failed(
                TerminalFailure(ProcessChildDurableOperationDiagnosticCodes.ChildIncompatible));
        }

        if (!MatchesStart(snapshot.Checkpoint.Start, request, target))
        {
            return ChildDriveResult.Failed(
                TerminalFailure(ProcessChildDurableOperationDiagnosticCodes.ChildIncompatible));
        }

        var remainingActivations = options.MaximumActivations;
        var remainingOperationAdvances = options.MaximumOperationAdvances;
        HashSet<EmissionId> attemptedOperationIds = [];
        while (true)
        {
            context.ThrowIfCancellationRequested();
            if (TryProjectTerminal(snapshot.Checkpoint, plan, target, out var outcome, out var origin))
            {
                return ChildDriveResult.Completed(outcome!, origin!);
            }
            if (snapshot.Checkpoint.Continuation.Terminal.Kind != ExecutionTerminalOutcomeKind.None)
            {
                return ChildDriveResult.Failed(
                    AmbiguousFailure(ProcessChildDurableOperationDiagnosticCodes.TerminalEvidenceInvalid));
            }

            var operation = SelectNextOperation(snapshot.Checkpoint, attemptedOperationIds);
            if (operation is not null)
            {
                if (remainingOperationAdvances-- <= 0)
                {
                    return ChildDriveResult.Failed(
                        AmbiguousFailure(ProcessChildDurableOperationDiagnosticCodes.DriveLimitExceeded));
                }

                attemptedOperationIds.Add(operation.OperationId);
                var advanced = await runtime.AdvanceOperationAsync(
                        context,
                        plan,
                        target.Continuation.ProcessInstanceId,
                        operation.OperationId)
                    .ConfigureAwait(false);
                if (advanced.Disposition is not (
                        ProcessDurableRuntimeDisposition.Applied
                        or ProcessDurableRuntimeDisposition.Replayed)
                    || advanced.Snapshot is null)
                {
                    return ChildDriveResult.Failed(
                        AmbiguousFailure(ProcessChildDurableOperationDiagnosticCodes.ChildRuntimeRejected));
                }
                snapshot = advanced.Snapshot;
                continue;
            }

            var activation = CreateNextActivation(
                snapshot.Checkpoint,
                request,
                plan.Document.Metadata.Provenance,
                context.UtcNow);
            if (activation is null)
            {
                return ChildDriveResult.Failed(
                    AmbiguousFailure(ProcessChildDurableOperationDiagnosticCodes.ChildBlocked));
            }
            if (remainingActivations-- <= 0)
            {
                return ChildDriveResult.Failed(
                    AmbiguousFailure(ProcessChildDurableOperationDiagnosticCodes.DriveLimitExceeded));
            }

            var activated = await runtime.ActivateAsync(
                    context,
                    plan,
                    target.Continuation,
                    activation)
                .ConfigureAwait(false);
            if (activated.Disposition is not (
                    ProcessDurableRuntimeDisposition.Applied
                    or ProcessDurableRuntimeDisposition.Replayed)
                || activated.Snapshot is null)
            {
                return ChildDriveResult.Failed(
                    AmbiguousFailure(ProcessChildDurableOperationDiagnosticCodes.ChildRuntimeRejected));
            }
            snapshot = activated.Snapshot;
        }
    }

    static ProcessStartReceipt CreateStart(
        RequestEnvelope request,
        ProcessChildRequestTarget target,
        DateTimeOffset observedAtUtc)
    {
        var start = new ProcessStartRequest(
            ProcessStartRequest.CurrentSchemaVersion,
            target.Definition,
            new(
                StartCommandId(request),
                StartIdempotencyKey(request),
                target.Continuation.ProcessInstanceId,
                StartAuthorization(request),
                observedAtUtc,
                request.Context.Provenance),
            target.Continuation,
            request.Payload);
        return new(start, observedAtUtc);
    }

    static bool MatchesStart(
        ProcessStartReceipt retained,
        RequestEnvelope request,
        ProcessChildRequestTarget target) =>
        retained.Request.Definition == target.Definition
        && retained.Request.InitialContinuation == target.Continuation
        && retained.Request.Context.CommandId == StartCommandId(request)
        && retained.Request.Context.IdempotencyKey == StartIdempotencyKey(request)
        && retained.Request.Context.ProcessInstanceId == target.Continuation.ProcessInstanceId
        && retained.Request.Context.Authorization == StartAuthorization(request)
        && retained.Request.Context.Provenance == request.Context.Provenance
        && retained.Request.Input == request.Payload;

    static bool InitializationMayHaveCommitted(ProcessDurableInitializationResult result) =>
        result.Snapshot is not null
        || result.Disposition is ProcessDurableRuntimeDisposition.Applied
            or ProcessDurableRuntimeDisposition.Replayed
            or ProcessDurableRuntimeDisposition.CommitOutcomeUnknown;

    static ProcessActivation? CreateNextActivation(
        ProcessDurableCheckpoint checkpoint,
        RequestEnvelope request,
        ExecutionProvenance childProvenance,
        DateTimeOffset observedAtUtc)
    {
        var continuation = checkpoint.Continuation;
        var pendingInputs = checkpoint.Inbox
            .Where(entry => entry.Receipt is null
                            && entry.Input.Target.Continuation == continuation.Continuation)
            .OrderBy(static entry => entry.EmissionId.Value, StringComparer.Ordinal)
            .Select(static entry => entry.Input)
            .ToImmutableArray();
        var cause = SelectActivationCause(continuation, pendingInputs, observedAtUtc);
        if (cause is null)
        {
            return null;
        }

        var ordinal = continuation.CompletedActivationCount + 1;
        return new(
            new($"process-child-activation/{request.Context.EmissionId.Value}/{ordinal}"),
            cause.Value,
            observedAtUtc,
            new(
                request.Context.AuthorityScope,
                request.Context.CorrelationId,
                request.Context.Delivery,
                childProvenance,
                causationId: request.Context.EmissionId,
                ordering: request.Context.Ordering),
            pendingInputs);
    }

    static DurableOperationState? SelectNextOperation(
        ProcessDurableCheckpoint checkpoint,
        IReadOnlySet<EmissionId> attemptedOperationIds)
    {
        DurableOperationState? selected = null;
        foreach (var candidate in checkpoint.DurableOperations)
        {
            if (candidate.Status == DurableOperationStatus.Dispositioned
                || attemptedOperationIds.Contains(candidate.OperationId))
            {
                continue;
            }

            if (selected is null
                || StringComparer.Ordinal.Compare(candidate.OperationId.Value, selected.OperationId.Value) < 0)
            {
                selected = candidate;
            }
        }

        return selected;
    }

    static ProcessActivationCause? SelectActivationCause(
        ProcessContinuationState continuation,
        ImmutableArray<ProcessActivationInput> pendingInputs,
        DateTimeOffset observedAtUtc)
    {
        if (continuation.CompletedActivationCount == 0)
        {
            return ProcessActivationCause.Start;
        }
        if (!pendingInputs.IsEmpty)
        {
            return ProcessActivationCause.Interaction;
        }
        if (continuation.Tokens.Any(static token => token.Disposition == ExecutionTokenDisposition.Ready))
        {
            return ProcessActivationCause.Continue;
        }
        if (continuation.Waits.Any(static wait => wait.Active
            && wait.Kind is ProcessWaitKind.DurableCut or ProcessWaitKind.RepeatAcrossActivation))
        {
            return ProcessActivationCause.Continue;
        }
        if (continuation.Waits.Any(wait => wait.Active
            && wait.Kind == ProcessWaitKind.Timer
            && wait.Timers.Any(timer => timer.DueAtUtc <= observedAtUtc)))
        {
            return ProcessActivationCause.Timer;
        }

        return null;
    }

    static bool TryProjectTerminal(
        ProcessDurableCheckpoint checkpoint,
        CompiledProcessPlan plan,
        ProcessChildRequestTarget target,
        out RequestTerminalOutcome? outcome,
        out ProcessInteractionOrigin? origin)
    {
        outcome = null;
        origin = null;
        var terminal = checkpoint.Continuation.Terminal;
        if (terminal.Kind == ExecutionTerminalOutcomeKind.None)
        {
            return false;
        }
        if (terminal.Detail is { Disclosure: not ExecutionStatusDisclosure.Disclosed })
        {
            return false;
        }

        var value = terminal.Detail?.Value ?? PortableValue.Missing(plan.Definition.Result);
        if (value.Contract != plan.Definition.Result
            || value.State is PortableValueState.Unknown or PortableValueState.Failed)
        {
            return false;
        }

        var terminalReceipt = checkpoint.Activations
            .Where(receipt => receipt.Continuation == target.Continuation
                              && TerminalMatches(receipt.Disposition, terminal.Kind))
            .OrderBy(static receipt => receipt.Sequence)
            .LastOrDefault();
        var terminalTrace = terminalReceipt?.Evidence.Trace
            .LastOrDefault(static trace => trace.Kind is
                ProcessTraceEventKind.TerminalReached or ProcessTraceEventKind.CancellationApplied)
            ?? terminalReceipt?.Evidence.Trace.LastOrDefault();
        if (terminalReceipt is null || terminalTrace is null)
        {
            return false;
        }

        var outcomeId = target.OutcomeMapping.For(terminal.Kind);
        outcome = terminal.Kind == ExecutionTerminalOutcomeKind.Completed
            ? new RequestResultOutcome(outcomeId, value)
            : new RequestFailureOutcome(outcomeId, value);
        origin = new(
            target.Definition,
            terminalTrace.Node,
            target.Continuation,
            terminalReceipt.Activation.Id,
            terminalTrace.Token,
            outcome: terminalTrace.Node);
        return true;
    }

    static bool TerminalMatches(
        ProcessActivationDisposition disposition,
        ExecutionTerminalOutcomeKind terminal) => (disposition, terminal) switch
    {
        (ProcessActivationDisposition.Completed, ExecutionTerminalOutcomeKind.Completed) => true,
        (ProcessActivationDisposition.Failed, ExecutionTerminalOutcomeKind.Failed or ExecutionTerminalOutcomeKind.Terminated) => true,
        (ProcessActivationDisposition.Cancelled, ExecutionTerminalOutcomeKind.Cancelled) => true,
        _ => false
    };

    static DurableOperationFailure AmbiguousFailure(string code) => new(
        DurableOperationFailurePhase.PostCommitPreAcknowledgement,
        DurableOperationEffectEvidence.Ambiguous,
        DurableOperationFailureDisposition.Retryable,
        code);

    static DurableOperationFailure TerminalFailure(string code) => new(
        DurableOperationFailurePhase.PreCall,
        DurableOperationEffectEvidence.NotExecuted,
        DurableOperationFailureDisposition.Terminal,
        code);

    static ProcessControlCommandId StartCommandId(RequestEnvelope request) =>
        new($"process-child-start/{request.Context.EmissionId.Value}");

    static ProcessControlIdempotencyKey StartIdempotencyKey(RequestEnvelope request) =>
        new($"process-child-start/{request.Context.IdempotencyKey.Value}");

    static ProcessControlAuthorizationContext StartAuthorization(RequestEnvelope request) =>
        new(
            StartActor,
            request.Context.AuthorityScope,
            $"request/{request.Context.EmissionId.Value}");

    sealed record ChildDriveResult(
        RequestTerminalOutcome? Outcome,
        ProcessInteractionOrigin? Origin,
        DurableOperationFailure? Failure,
        bool ChildAbsent)
    {
        internal static ChildDriveResult Completed(
            RequestTerminalOutcome outcome,
            ProcessInteractionOrigin origin) =>
            new(outcome, origin, Failure: null, ChildAbsent: false);

        internal static ChildDriveResult Failed(DurableOperationFailure failure) =>
            new(Outcome: null, Origin: null, failure, ChildAbsent: false);

        internal static ChildDriveResult Absent() =>
            new(Outcome: null, Origin: null, Failure: null, ChildAbsent: true);
    }
}
