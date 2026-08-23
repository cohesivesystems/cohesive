using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Execution;

/// <summary>Stable diagnostic codes emitted by the Process-control reference protocol.</summary>
public static class ProcessControlDiagnosticCodes
{
    /// <summary>The command targets another logical Process instance.</summary>
    public const string TargetMismatch = "execution.control.target.mismatch";

    /// <summary>The command authority does not match the Process authority.</summary>
    public const string AuthorityMismatch = "execution.control.authority.mismatch";

    /// <summary>The expected attempt is no longer current.</summary>
    public const string StaleAttempt = "execution.control.expectation.attempt.stale";

    /// <summary>The expected semantic revision/fence is not current.</summary>
    public const string StaleRevision = "execution.control.expectation.revision.stale";

    /// <summary>A stable command identity was reused for different canonical content.</summary>
    public const string CommandIdentityConflict = "execution.control.command.identityConflict";

    /// <summary>A command idempotency key was reused for a different semantic intent.</summary>
    public const string CommandIdempotencyConflict = "execution.control.command.idempotencyConflict";

    /// <summary>The command is not legal in the current lifecycle mode or execution phase.</summary>
    public const string InvalidState = "execution.control.state.invalidCommand";

    /// <summary>The command or observation violates its canonical contract.</summary>
    public const string InvalidCommand = "execution.control.command.invalid";

    /// <summary>A Signal emission or scoped idempotency identity conflicts with prior admitted content.</summary>
    public const string SignalConflict = "execution.control.signal.conflict";

    /// <summary>A Signal submitted for durable admission declares activation-local delivery.</summary>
    public const string SignalDurabilityMismatch = "execution.control.signal.durabilityMismatch";

    /// <summary>An affinity slot was rebound to different attempt-scoped evidence.</summary>
    public const string AffinityConflict = "execution.control.affinity.conflict";

    /// <summary>A safe-point identity was reused for conflicting durable-cut evidence.</summary>
    public const string SafePointConflict = "execution.control.safePoint.conflict";

    /// <summary>An activation identity was reused for conflicting start evidence.</summary>
    public const string ActivationConflict = "execution.control.activation.conflict";
}

/// <summary>Observable disposition of a deterministic Process-control decision.</summary>
public enum ProcessControlDecisionDisposition
{
    /// <summary>No disposition was supplied; invalid in a decision.</summary>
    Unspecified = 0,

    /// <summary>Current control state was inspected without mutation.</summary>
    Inspected = 1,

    /// <summary>A lifecycle command changed state immediately.</summary>
    Applied = 2,

    /// <summary>A lifecycle command was accepted and awaits a safe point.</summary>
    DeferredToSafePoint = 3,

    /// <summary>A prior command or observation decision was deterministically reused.</summary>
    Replayed = 4,

    /// <summary>The requested lifecycle condition was already satisfied.</summary>
    AlreadySatisfied = 5,

    /// <summary>The requested lifecycle change was already pending at a safe point.</summary>
    AlreadyRequested = 6,

    /// <summary>A canonical Signal was admitted for active consumption.</summary>
    SignalAccepted = 7,

    /// <summary>A canonical Signal was admitted for buffering while paused or pausing.</summary>
    SignalBuffered = 8,

    /// <summary>The same logical Signal was previously admitted; no new admission intent was emitted.</summary>
    SignalDuplicate = 9,

    /// <summary>A finite activation was fenced and marked in flight.</summary>
    ActivationStarted = 10,

    /// <summary>An invariant-preserving safe point was recorded and any pending action applied.</summary>
    SafePointReached = 11,

    /// <summary>A write-once attempt affinity was bound.</summary>
    AffinityBound = 12,

    /// <summary>Authored cancellation finalization produced terminal acknowledgement or failure evidence.</summary>
    CancellationFinalized = 23,

    /// <summary>The command authority does not match current Process authority.</summary>
    Unauthorized = 13,

    /// <summary>The command targets another logical Process instance.</summary>
    TargetMismatch = 14,

    /// <summary>The expected attempt is stale.</summary>
    StaleAttempt = 15,

    /// <summary>The expected semantic revision/fence is stale.</summary>
    StaleRevision = 16,

    /// <summary>A stable command identity was reused for conflicting content.</summary>
    IdentityConflict = 17,

    /// <summary>A command idempotency key was reused for a different semantic intent.</summary>
    IdempotencyConflict = 18,

    /// <summary>A Signal identity conflicts with prior admitted Signal content.</summary>
    SignalConflict = 19,

    /// <summary>An affinity slot conflicts with its write-once prior value.</summary>
    AffinityConflict = 20,

    /// <summary>The operation is not legal in current lifecycle state.</summary>
    InvalidState = 21,

    /// <summary>The command or observation violates its canonical contract.</summary>
    InvalidCommand = 22
}

/// <summary>Closed external intent produced by a first-time Process-control decision.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = ExecutionControlWireNames.IntentDiscriminator)]
[JsonDerivedType(typeof(ProcessSignalAdmissionIntent), ExecutionControlWireNames.AdmitSignal)]
[JsonDerivedType(typeof(ProcessReachSafePointIntent), ExecutionControlWireNames.ReachSafePoint)]
[JsonDerivedType(typeof(ProcessAttemptRestartIntent), ExecutionControlWireNames.RestartAttemptIntent)]
[JsonDerivedType(typeof(ProcessCancellationIntent), ExecutionControlWireNames.CancelIntent)]
[JsonDerivedType(typeof(ProcessTerminationIntent), ExecutionControlWireNames.TerminateIntent)]
public abstract record ProcessControlIntent
{
    private protected ProcessControlIntent()
    {
    }

    internal abstract void EnsureDeclaredVariant();
}

/// <summary>Intent to durably admit one canonical Signal exactly once.</summary>
public sealed record ProcessSignalAdmissionIntent : ProcessControlIntent
{
    internal override void EnsureDeclaredVariant() { }

    /// <summary>Creates a Signal-admission intent.</summary>
    /// <param name="admission">
    /// Exact Signal-admission value projected from the same decision represented by its authoritative receipt.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="admission"/> is <see langword="null"/>.</exception>
    [JsonConstructor]
    public ProcessSignalAdmissionIntent(ProcessSignalAdmission admission) =>
        Admission = Guard.RequireNotNull(admission);

    /// <summary>Exact Signal admission to realize through a durable inbox.</summary>
    public ProcessSignalAdmission Admission { get; }
}

/// <summary>Intent to drain current activation work and reach a safe point for a pending command.</summary>
public sealed record ProcessReachSafePointIntent : ProcessControlIntent
{
    internal override void EnsureDeclaredVariant() { }

    /// <summary>Creates a pending safe-point intent.</summary>
    /// <param name="commandId">Command whose action is pending.</param>
    /// <param name="action">Lifecycle action to apply at the cut.</param>
    /// <exception cref="ArgumentException"><paramref name="commandId"/> is default.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="action"/> is None or unsupported.</exception>
    [JsonConstructor]
    public ProcessReachSafePointIntent(
        ProcessControlCommandId commandId,
        ProcessControlPendingAction action)
    {
        if (string.IsNullOrWhiteSpace(commandId.Value))
        {
            throw new ArgumentException("A safe-point intent requires its command.", nameof(commandId));
        }

        if (!Enum.IsDefined(action) || action == ProcessControlPendingAction.None)
        {
            throw new ArgumentOutOfRangeException(nameof(action), action, "A safe-point intent requires an action.");
        }

        CommandId = commandId;
        Action = action;
    }

    /// <summary>Command whose action is pending.</summary>
    public ProcessControlCommandId CommandId { get; }

    /// <summary>Lifecycle action to apply at the safe point.</summary>
    public ProcessControlPendingAction Action { get; }
}

/// <summary>Intent to initialize one stable replacement attempt and allocate its fresh affinities.</summary>
public sealed record ProcessAttemptRestartIntent : ProcessControlIntent
{
    internal override void EnsureDeclaredVariant() { }

    /// <summary>Creates an attempt-restart realization intent.</summary>
    /// <param name="processInstanceId">Logical Process instance retained by restart.</param>
    /// <param name="abandonedAttemptId">Attempt explicitly abandoned.</param>
    /// <param name="replacementAttemptId">Stable replacement attempt selected once.</param>
    /// <param name="cleanup">Explicit old-attempt cleanup obligation.</param>
    /// <exception cref="ArgumentException">An identity is default or both attempts are equal.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="cleanup"/> is invalid.</exception>
    [JsonConstructor]
    public ProcessAttemptRestartIntent(
        ProcessInstanceId processInstanceId,
        ProcessAttemptId abandonedAttemptId,
        ProcessAttemptId replacementAttemptId,
        ProcessAttemptCleanupRequirement cleanup)
    {
        if (string.IsNullOrWhiteSpace(processInstanceId.Value))
        {
            throw new ArgumentException("A restart intent requires its Process instance.", nameof(processInstanceId));
        }

        if (string.IsNullOrWhiteSpace(abandonedAttemptId.Value)
            || string.IsNullOrWhiteSpace(replacementAttemptId.Value)
            || abandonedAttemptId == replacementAttemptId)
        {
            throw new ArgumentException("A restart intent requires distinct stable attempts.", nameof(replacementAttemptId));
        }
        if (!Enum.IsDefined(cleanup) || cleanup == ProcessAttemptCleanupRequirement.Unspecified)
        {
            throw new ArgumentOutOfRangeException(nameof(cleanup), cleanup, "Restart cleanup must be explicit.");
        }

        ProcessInstanceId = processInstanceId;
        AbandonedAttemptId = abandonedAttemptId;
        ReplacementAttemptId = replacementAttemptId;
        Cleanup = cleanup;
    }

    /// <summary>Logical Process instance retained by restart.</summary>
    public ProcessInstanceId ProcessInstanceId { get; }

    /// <summary>Attempt explicitly abandoned.</summary>
    public ProcessAttemptId AbandonedAttemptId { get; }

    /// <summary>Stable replacement attempt selected once.</summary>
    public ProcessAttemptId ReplacementAttemptId { get; }

    /// <summary>Explicit old-attempt cleanup obligation.</summary>
    public ProcessAttemptCleanupRequirement Cleanup { get; }
}

/// <summary>Intent to realize cooperative cancellation after its safe-point decision commits.</summary>
public sealed record ProcessCancellationIntent : ProcessControlIntent
{
    internal override void EnsureDeclaredVariant() { }

    /// <summary>Creates compatibility cancellation evidence without a retained command identity.</summary>
    /// <param name="attemptId">Attempt cancelled without replacement.</param>
    /// <param name="reason">Typed cancellation reason.</param>
    public ProcessCancellationIntent(
        ProcessAttemptId attemptId,
        ProcessControlReason reason)
        : this(attemptId, reason, commandId: null)
    {
    }

    /// <summary>Creates a cooperative cancellation intent.</summary>
    /// <param name="attemptId">Attempt cancelled without replacement.</param>
    /// <param name="reason">Typed cancellation reason.</param>
    /// <param name="commandId">
    /// Accepted cancellation command identity. Older immediate-cancellation evidence may omit it; an authored
    /// cancellation finalizer requires it.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="attemptId"/> is default.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="reason"/> is <see langword="null"/>.</exception>
    [JsonConstructor]
    public ProcessCancellationIntent(
        ProcessAttemptId attemptId,
        ProcessControlReason reason,
        ProcessControlCommandId? commandId)
    {
        if (string.IsNullOrWhiteSpace(attemptId.Value))
        {
            throw new ArgumentException("A cancellation intent requires its attempt.", nameof(attemptId));
        }

        AttemptId = attemptId;
        Reason = Guard.RequireNotNull(reason);
        if (commandId is { } candidate && string.IsNullOrWhiteSpace(candidate.Value))
            throw new ArgumentException("A supplied cancellation command identity cannot be default.", nameof(commandId));
        CommandId = commandId;
    }

    /// <summary>Attempt cancelled without replacement.</summary>
    public ProcessAttemptId AttemptId { get; }

    /// <summary>Typed cancellation reason.</summary>
    public ProcessControlReason Reason { get; }

    /// <summary>Accepted cancellation command identity when retained by the producing control interpreter.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ProcessControlCommandId? CommandId { get; }
}

/// <summary>Intent to realize immediate forced termination and its explicit cleanup.</summary>
public sealed record ProcessTerminationIntent : ProcessControlIntent
{
    internal override void EnsureDeclaredVariant() { }

    /// <summary>Creates a forced-termination intent.</summary>
    /// <param name="attemptId">Attempt forcibly stopped.</param>
    /// <param name="reason">Typed termination reason.</param>
    /// <param name="cleanup">Explicit cleanup obligation.</param>
    /// <exception cref="ArgumentException"><paramref name="attemptId"/> is default.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="reason"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="cleanup"/> is invalid.</exception>
    [JsonConstructor]
    public ProcessTerminationIntent(
        ProcessAttemptId attemptId,
        ProcessControlReason reason,
        ProcessAttemptCleanupRequirement cleanup)
    {
        if (string.IsNullOrWhiteSpace(attemptId.Value))
        {
            throw new ArgumentException("A termination intent requires its attempt.", nameof(attemptId));
        }

        if (!Enum.IsDefined(cleanup) || cleanup == ProcessAttemptCleanupRequirement.Unspecified)
        {
            throw new ArgumentOutOfRangeException(nameof(cleanup), cleanup, "Termination cleanup must be explicit.");
        }

        AttemptId = attemptId;
        Reason = Guard.RequireNotNull(reason);
        Cleanup = cleanup;
    }

    /// <summary>Attempt forcibly stopped.</summary>
    public ProcessAttemptId AttemptId { get; }

    /// <summary>Typed termination reason.</summary>
    public ProcessControlReason Reason { get; }

    /// <summary>Explicit forced-stop cleanup obligation.</summary>
    public ProcessAttemptCleanupRequirement Cleanup { get; }
}

/// <summary>Deterministic replacement-state result of one control command or execution observation.</summary>
public sealed record ProcessControlDecision
{
    /// <summary>Current canonical Process-control decision schema version.</summary>
    public static ExecutionIrSchemaVersion CurrentSchemaVersion { get; } =
        new("cohesive-process-control-decision/v1");

    /// <summary>Creates one portable, versioned Process-control decision.</summary>
    /// <param name="schemaVersion">Exact Process-control decision schema version.</param>
    /// <param name="state">Replacement portable Process-control state.</param>
    /// <param name="disposition">Observable outcome of the evaluated command or execution observation.</param>
    /// <param name="receipt">Original or replayed durable command receipt, when applicable.</param>
    /// <param name="intent">First-time external realization intent, when applicable.</param>
    /// <param name="diagnostics">Structured deterministic diagnostics for a rejected operation.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="schemaVersion"/> is unsupported, <paramref name="receipt"/> is not retained by
    /// <paramref name="state"/>, <paramref name="diagnostics"/> contains a null entry, or the disposition,
    /// causal cut, receipt, intent, diagnostics, and replacement state do not form one coherent result.
    /// </exception>
    /// <exception cref="ArgumentNullException"><paramref name="state"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="disposition"/> is invalid.</exception>
    [JsonConstructor]
    public ProcessControlDecision(
        ExecutionIrSchemaVersion schemaVersion,
        ProcessControlState state,
        ProcessControlDecisionDisposition disposition,
        ProcessControlCommandReceipt? receipt = null,
        ProcessControlIntent? intent = null,
        ImmutableArray<DocumentValidationDiagnostic> diagnostics = default)
    {
        if (schemaVersion != CurrentSchemaVersion)
        {
            throw new ArgumentException("Unsupported Process-control decision schema version.", nameof(schemaVersion));
        }

        State = Guard.RequireNotNull(state);
        if (!Enum.IsDefined(disposition) || disposition == ProcessControlDecisionDisposition.Unspecified)
        {
            throw new ArgumentOutOfRangeException(nameof(disposition), disposition, "Control decision must be explicit.");
        }

        if (receipt is not null
            && !Equals(state.FindReceipt(receipt.Command.Context.CommandId), receipt))
        {
            throw new ArgumentException(
                "A Process-control decision receipt must be retained by its replacement state.",
                nameof(receipt));
        }

        var retainedDiagnostics = diagnostics.IsDefault ? [] : diagnostics;
        foreach (var diagnostic in retainedDiagnostics)
        {
            if (diagnostic is null)
            {
                throw new ArgumentException("Process-control diagnostics cannot contain null entries.", nameof(diagnostics));
            }
        }
        intent?.EnsureDeclaredVariant();
        ValidateResultShape(state, disposition, receipt, intent, retainedDiagnostics);

        SchemaVersion = schemaVersion;
        Disposition = disposition;
        Receipt = receipt;
        Intent = intent;
        Diagnostics = retainedDiagnostics;
    }

    internal ProcessControlDecision(
        ProcessControlState state,
        ProcessControlDecisionDisposition disposition,
        ProcessControlCommandReceipt? receipt = null,
        ProcessControlIntent? intent = null,
        ImmutableArray<DocumentValidationDiagnostic> diagnostics = default)
        : this(CurrentSchemaVersion, state, disposition, receipt, intent, diagnostics)
    {
    }

    /// <summary>Exact Process-control decision schema version.</summary>
    public ExecutionIrSchemaVersion SchemaVersion { get; }

    /// <summary>Replacement portable Process-control state.</summary>
    public ProcessControlState State { get; }

    /// <summary>Observable decision disposition.</summary>
    public ProcessControlDecisionDisposition Disposition { get; }

    /// <summary>Original or replayed durable command receipt, when applicable.</summary>
    public ProcessControlCommandReceipt? Receipt { get; }

    /// <summary>First-time external realization intent; exact replay never emits another intent.</summary>
    public ProcessControlIntent? Intent { get; }

    /// <summary>Structured deterministic diagnostics for a rejected command or observation.</summary>
    public ImmutableArray<DocumentValidationDiagnostic> Diagnostics { get; }

    /// <summary>Compares decisions by complete persisted semantic value.</summary>
    /// <param name="other">Decision to compare.</param>
    /// <returns><see langword="true"/> when every scalar, nested value, and diagnostic is equal.</returns>
    public bool Equals(ProcessControlDecision? other) =>
        ReferenceEquals(this, other)
        || other is not null
        && SchemaVersion == other.SchemaVersion
        && Equals(State, other.State)
        && Disposition == other.Disposition
        && Equals(Receipt, other.Receipt)
        && Equals(Intent, other.Intent)
        && Diagnostics.SequenceEqual(other.Diagnostics);

    /// <summary>Returns a structural hash over the complete persisted decision.</summary>
    /// <returns>A hash code derived from every scalar, nested value, and diagnostic.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(SchemaVersion);
        hash.Add(State);
        hash.Add(Disposition);
        hash.Add(Receipt);
        hash.Add(Intent);
        foreach (var diagnostic in Diagnostics)
        {
            hash.Add(diagnostic);
        }

        return hash.ToHashCode();
    }

    static void ValidateResultShape(
        ProcessControlState state,
        ProcessControlDecisionDisposition disposition,
        ProcessControlCommandReceipt? receipt,
        ProcessControlIntent? intent,
        ImmutableArray<DocumentValidationDiagnostic> diagnostics)
    {
        if (IsRejection(disposition))
        {
            var hasError = false;
            foreach (var diagnostic in diagnostics)
            {
                if (diagnostic.Severity != DiagnosticSeverity.Error)
                {
                    continue;
                }

                hasError = true;
                if (!TryGetRejectionDisposition(diagnostic.Code, out var diagnosticDisposition)
                    || diagnosticDisposition != disposition)
                {
                    throw new ArgumentException(
                        "A rejected Process-control decision disposition must match every Error diagnostic code.",
                        nameof(diagnostics));
                }
            }
            if (!hasError || receipt is not null || intent is not null)
            {
                throw new ArgumentException(
                    "A rejected Process-control decision requires an Error diagnostic and cannot carry a receipt or intent.",
                    nameof(disposition));
            }
            return;
        }
        if (!diagnostics.IsEmpty)
        {
            throw new ArgumentException(
                "Only a rejected Process-control decision may carry diagnostics.",
                nameof(diagnostics));
        }

        switch (disposition)
        {
            case ProcessControlDecisionDisposition.Inspected:
                RequireNoReceiptOrIntent(receipt, intent, disposition);
                return;
            case ProcessControlDecisionDisposition.ActivationStarted:
                RequireNoReceiptOrIntent(receipt, intent, disposition);
                ValidateActivationCut(state);
                return;
            case ProcessControlDecisionDisposition.AffinityBound:
                RequireNoReceiptOrIntent(receipt, intent, disposition);
                ValidateAffinityCut(state);
                return;
            case ProcessControlDecisionDisposition.CancellationFinalized:
                RequireNoReceiptOrIntent(receipt, intent, disposition);
                if (state.CancellationFinalization is null
                    || state.Mode is not (ProcessControlMode.Cancelled or ProcessControlMode.CancellationFailed))
                {
                    throw new ArgumentException(
                        "A cancellation-finalized decision requires terminal authored finalization evidence.",
                        nameof(state));
                }
                return;
            case ProcessControlDecisionDisposition.Replayed:
                if (intent is not null)
                {
                    throw new ArgumentException("A replayed decision cannot emit another intent.", nameof(intent));
                }

                return;
            case ProcessControlDecisionDisposition.SafePointReached:
                ValidateSafePointResult(state, receipt, intent);
                return;
        }

        var expectedReceipt = disposition switch
        {
            ProcessControlDecisionDisposition.Applied => ProcessControlReceiptDisposition.Applied,
            ProcessControlDecisionDisposition.DeferredToSafePoint =>
                ProcessControlReceiptDisposition.DeferredToSafePoint,
            ProcessControlDecisionDisposition.AlreadySatisfied => ProcessControlReceiptDisposition.AlreadySatisfied,
            ProcessControlDecisionDisposition.AlreadyRequested => ProcessControlReceiptDisposition.AlreadyRequested,
            ProcessControlDecisionDisposition.SignalAccepted => ProcessControlReceiptDisposition.SignalAccepted,
            ProcessControlDecisionDisposition.SignalBuffered => ProcessControlReceiptDisposition.SignalBuffered,
            ProcessControlDecisionDisposition.SignalDuplicate => ProcessControlReceiptDisposition.SignalDuplicate,
            _ => throw new ArgumentOutOfRangeException(
                nameof(disposition),
                disposition,
                "Unsupported Process-control decision disposition.")
        };
        if (receipt?.Disposition != expectedReceipt)
        {
            throw new ArgumentException(
                "The Process-control decision disposition contradicts its durable receipt.",
                nameof(receipt));
        }

        ValidateReceiptCut(state, receipt);
        ValidateCausalIntent(state, receipt, intent, completedAtSafePoint: false);
    }

    static void ValidateSafePointResult(
        ProcessControlState state,
        ProcessControlCommandReceipt? receipt,
        ProcessControlIntent? intent)
    {
        if (receipt is null)
        {
            if (intent is not null
                || state.CurrentAttempt.Phase != ProcessControlExecutionPhase.AtSafePoint)
            {
                throw new ArgumentException(
                    "A receipt-free safe-point decision requires an attempt at a safe point and cannot carry an intent.",
                    nameof(intent));
            }
            ValidateSafePointCut(state, receipt: null);
            return;
        }
        if (receipt.Disposition != ProcessControlReceiptDisposition.DeferredToSafePoint)
        {
            throw new ArgumentException(
                "A safe-point completion may reference only the original deferred receipt.",
                nameof(receipt));
        }

        ValidateSafePointCut(state, receipt);
        ValidateCausalIntent(state, receipt, intent, completedAtSafePoint: true);
    }

    static void ValidateReceiptCut(
        ProcessControlState state,
        ProcessControlCommandReceipt receipt)
    {
        if (state.Receipts.IsEmpty
            || state.Receipts[^1] != receipt
            || state.Revision != receipt.AfterRevision
            || state.UpdatedAtUtc != receipt.RecordedAtUtc)
        {
            throw new ArgumentException(
                "A first-time command result must represent its exact latest durable receipt cut.",
                nameof(state));
        }
    }

    static void ValidateActivationCut(ProcessControlState state)
    {
        var activation = state.CurrentAttempt.ActiveActivation;
        if (state.CurrentAttempt.Phase != ProcessControlExecutionPhase.InActivation
            || activation is null
            || state.Revision != activation.Expectation.Revision.Next()
            || state.UpdatedAtUtc != activation.ObservedAtUtc
            || HasReceiptAtRevision(state, state.Revision))
        {
            throw new ArgumentException(
                "An activation-start result must represent its exact latest observation cut.",
                nameof(state));
        }
    }

    static void ValidateAffinityCut(ProcessControlState state)
    {
        ProcessAttemptAffinityObservation? latest = null;
        foreach (var binding in state.CurrentAttempt.AffinityBindings)
        {
            if (binding.Expectation.Revision.Next() == state.Revision
                && binding.ObservedAtUtc == state.UpdatedAtUtc)
            {
                latest = binding;
                break;
            }
        }
        if (latest is null || HasReceiptAtRevision(state, state.Revision))
        {
            throw new ArgumentException(
                "An affinity-binding result must represent its exact latest observation cut.",
                nameof(state));
        }
    }

    static void ValidateSafePointCut(
        ProcessControlState state,
        ProcessControlCommandReceipt? receipt)
    {
        ProcessControlSafePoint? resolving = null;
        foreach (var attempt in state.Attempts)
        {
            if (receipt is not null && attempt.AttemptId != receipt.BeforeAttemptId)
            {
                continue;
            }

            foreach (var safePoint in attempt.SafePoints)
            {
                if (receipt is null
                    || (safePoint.Activation.ObservedAtUtc <= receipt.RecordedAtUtc
                        && receipt.RecordedAtUtc <= safePoint.ObservedAtUtc
                        && safePoint.Activation.Expectation.Revision.Ordinal < receipt.BeforeRevision.Ordinal
                        && receipt.AfterRevision.Ordinal
                            <= safePoint.Observation.Expectation.Revision.Ordinal))
                {
                    resolving = safePoint;
                }
            }
        }

        if (resolving is null
            || state.Revision != resolving.Observation.Expectation.Revision.Next()
            || state.UpdatedAtUtc != resolving.ObservedAtUtc
            || HasReceiptAtRevision(state, state.Revision)
            || (receipt is null
                && state.CurrentAttempt.Phase != ProcessControlExecutionPhase.AtSafePoint))
        {
            throw new ArgumentException(
                "A safe-point result must represent its exact latest durable-cut observation.",
                nameof(state));
        }
    }

    static bool HasReceiptAtRevision(
        ProcessControlState state,
        ProcessControlRevision revision)
    {
        foreach (var candidate in state.Receipts)
        {
            if (candidate.BeforeRevision == revision)
            {
                return true;
            }
        }
        return false;
    }

    static void ValidateCausalIntent(
        ProcessControlState state,
        ProcessControlCommandReceipt receipt,
        ProcessControlIntent? intent,
        bool completedAtSafePoint)
    {
        ValidateCausalState(state, receipt, completedAtSafePoint);
        switch (receipt.Command, receipt.Disposition, completedAtSafePoint)
        {
            case (SignalProcessCommand signal, ProcessControlReceiptDisposition.SignalAccepted, false):
                ValidateSignalIntent(receipt, signal, intent, ProcessSignalAdmissionDisposition.Active);
                return;
            case (SignalProcessCommand signal, ProcessControlReceiptDisposition.SignalBuffered, false):
                ValidateSignalIntent(receipt, signal, intent, ProcessSignalAdmissionDisposition.Buffered);
                return;
            case (SignalProcessCommand, ProcessControlReceiptDisposition.SignalDuplicate, false):
            case (PauseProcessCommand or ContinueProcessCommand,
                ProcessControlReceiptDisposition.Applied
                    or ProcessControlReceiptDisposition.AlreadySatisfied
                    or ProcessControlReceiptDisposition.AlreadyRequested,
                false):
            case (CancelProcessCommand or TerminateProcessCommand,
                ProcessControlReceiptDisposition.AlreadySatisfied
                    or ProcessControlReceiptDisposition.AlreadyRequested,
                false):
                RequireNoIntent(intent);
                return;
            case (PauseProcessCommand,
                ProcessControlReceiptDisposition.DeferredToSafePoint,
                false):
                ValidateSafePointIntent(receipt, intent, ProcessControlPendingAction.Pause);
                return;
            case (RestartProcessAttemptCommand,
                ProcessControlReceiptDisposition.DeferredToSafePoint,
                false):
                ValidateSafePointIntent(receipt, intent, ProcessControlPendingAction.RestartAttempt);
                return;
            case (CancelProcessCommand,
                ProcessControlReceiptDisposition.DeferredToSafePoint,
                false):
                ValidateSafePointIntent(receipt, intent, ProcessControlPendingAction.Cancel);
                return;
            case (PauseProcessCommand,
                ProcessControlReceiptDisposition.DeferredToSafePoint,
                true):
                RequireNoIntent(intent);
                return;
            case (RestartProcessAttemptCommand,
                ProcessControlReceiptDisposition.Applied,
                false):
            case (RestartProcessAttemptCommand,
                ProcessControlReceiptDisposition.DeferredToSafePoint,
                true):
                ValidateRestartIntent(
                    state,
                    receipt,
                    (RestartProcessAttemptCommand)receipt.Command,
                    intent);
                return;
            case (CancelProcessCommand,
                ProcessControlReceiptDisposition.Applied,
                false):
            case (CancelProcessCommand,
                ProcessControlReceiptDisposition.DeferredToSafePoint,
                true):
                ValidateCancellationIntent(receipt, (CancelProcessCommand)receipt.Command, intent);
                return;
            case (TerminateProcessCommand termination,
                ProcessControlReceiptDisposition.Applied,
                false):
                ValidateTerminationIntent(receipt, termination, intent);
                return;
            default:
                throw new ArgumentException(
                    "The Process-control receipt and intent do not form a supported causal result.",
                    nameof(intent));
        }
    }

    static void ValidateCausalState(
        ProcessControlState state,
        ProcessControlCommandReceipt receipt,
        bool completedAtSafePoint)
    {
        if (completedAtSafePoint)
        {
            var completed = state.PendingCommandId is null
                && receipt.Command switch
                {
                    PauseProcessCommand => state.Mode == ProcessControlMode.Paused,
                    CancelProcessCommand => state.Mode is
                        ProcessControlMode.Cancelled or ProcessControlMode.Cancelling,
                    RestartProcessAttemptCommand restart =>
                        state.Mode == ProcessControlMode.Running
                        && state.CurrentAttempt.AttemptId == restart.Plan.NewAttemptId,
                    _ => false
                };
            if (!completed)
            {
                throw new ArgumentException(
                    "A safe-point result state must reflect completion of its deferred command.",
                    nameof(state));
            }
            return;
        }

        var coherent = (receipt.Command, receipt.Disposition) switch
        {
            (PauseProcessCommand, ProcessControlReceiptDisposition.Applied) =>
                state.Mode == ProcessControlMode.Paused,
            (ContinueProcessCommand, ProcessControlReceiptDisposition.Applied) =>
                state.Mode == ProcessControlMode.Running,
            (RestartProcessAttemptCommand restart, ProcessControlReceiptDisposition.Applied) =>
                state.CurrentAttempt.AttemptId == restart.Plan.NewAttemptId,
            (CancelProcessCommand, ProcessControlReceiptDisposition.Applied) =>
                state.Mode is ProcessControlMode.Cancelled or ProcessControlMode.Cancelling,
            (TerminateProcessCommand, ProcessControlReceiptDisposition.Applied) =>
                state.Mode == ProcessControlMode.Terminated,
            (_, ProcessControlReceiptDisposition.DeferredToSafePoint) =>
                state.PendingCommandId == receipt.Command.Context.CommandId
                && (receipt.Command, state.Mode) is
                    (PauseProcessCommand, ProcessControlMode.PauseRequested)
                    or (RestartProcessAttemptCommand, ProcessControlMode.RestartRequested)
                    or (CancelProcessCommand, ProcessControlMode.CancellationRequested),
            _ => true
        };
        if (!coherent)
        {
            throw new ArgumentException(
                "The replacement state does not reflect its causal Process-control receipt.",
                nameof(state));
        }
    }

    static void ValidateSignalIntent(
        ProcessControlCommandReceipt receipt,
        SignalProcessCommand command,
        ProcessControlIntent? intent,
        ProcessSignalAdmissionDisposition expectedDisposition)
    {
        if (intent is not ProcessSignalAdmissionIntent signal
            || signal.Admission.CommandId != receipt.Command.Context.CommandId
            || signal.Admission.Signal != command.Signal
            || signal.Admission.Disposition != expectedDisposition
            || signal.Admission.AdmittedAtUtc != receipt.RecordedAtUtc)
        {
            throw new ArgumentException(
                "A Signal decision requires an exact admission intent from its causal receipt.",
                nameof(intent));
        }
    }

    static void ValidateSafePointIntent(
        ProcessControlCommandReceipt receipt,
        ProcessControlIntent? intent,
        ProcessControlPendingAction expectedAction)
    {
        if (intent is not ProcessReachSafePointIntent safePoint
            || safePoint.CommandId != receipt.Command.Context.CommandId
            || safePoint.Action != expectedAction)
        {
            throw new ArgumentException(
                "A deferred decision requires an exact safe-point intent from its causal receipt.",
                nameof(intent));
        }
    }

    static void ValidateRestartIntent(
        ProcessControlState state,
        ProcessControlCommandReceipt receipt,
        RestartProcessAttemptCommand command,
        ProcessControlIntent? intent)
    {
        if (intent is not ProcessAttemptRestartIntent restart
            || restart.ProcessInstanceId != state.ProcessInstanceId
            || restart.AbandonedAttemptId != receipt.BeforeAttemptId
            || restart.ReplacementAttemptId != command.Plan.NewAttemptId
            || restart.Cleanup != command.Plan.Cleanup)
        {
            throw new ArgumentException(
                "A restart decision requires an exact replacement intent from its causal receipt.",
                nameof(intent));
        }
    }

    static void ValidateCancellationIntent(
        ProcessControlCommandReceipt receipt,
        CancelProcessCommand command,
        ProcessControlIntent? intent)
    {
        if (intent is not ProcessCancellationIntent cancellation
            || cancellation.AttemptId != receipt.BeforeAttemptId
            || cancellation.CommandId != receipt.Command.Context.CommandId
            || cancellation.Reason != command.Reason)
        {
            throw new ArgumentException(
                "A cancellation decision requires an exact cancellation intent from its causal receipt.",
                nameof(intent));
        }
    }

    static void ValidateTerminationIntent(
        ProcessControlCommandReceipt receipt,
        TerminateProcessCommand command,
        ProcessControlIntent? intent)
    {
        if (intent is not ProcessTerminationIntent termination
            || termination.AttemptId != receipt.BeforeAttemptId
            || termination.Reason != command.Reason
            || termination.Cleanup != command.Cleanup)
        {
            throw new ArgumentException(
                "A termination decision requires an exact termination intent from its causal receipt.",
                nameof(intent));
        }
    }

    static bool IsRejection(ProcessControlDecisionDisposition disposition) =>
        disposition is ProcessControlDecisionDisposition.Unauthorized
            or ProcessControlDecisionDisposition.TargetMismatch
            or ProcessControlDecisionDisposition.StaleAttempt
            or ProcessControlDecisionDisposition.StaleRevision
            or ProcessControlDecisionDisposition.IdentityConflict
            or ProcessControlDecisionDisposition.IdempotencyConflict
            or ProcessControlDecisionDisposition.SignalConflict
            or ProcessControlDecisionDisposition.AffinityConflict
            or ProcessControlDecisionDisposition.InvalidState
            or ProcessControlDecisionDisposition.InvalidCommand;

    internal static bool TryGetRejectionDisposition(
        string code,
        out ProcessControlDecisionDisposition disposition)
    {
        disposition = code switch
        {
            ProcessControlDiagnosticCodes.AuthorityMismatch =>
                ProcessControlDecisionDisposition.Unauthorized,
            ProcessControlDiagnosticCodes.TargetMismatch =>
                ProcessControlDecisionDisposition.TargetMismatch,
            ProcessControlDiagnosticCodes.StaleAttempt =>
                ProcessControlDecisionDisposition.StaleAttempt,
            ProcessControlDiagnosticCodes.StaleRevision =>
                ProcessControlDecisionDisposition.StaleRevision,
            ProcessControlDiagnosticCodes.CommandIdentityConflict =>
                ProcessControlDecisionDisposition.IdentityConflict,
            ProcessControlDiagnosticCodes.CommandIdempotencyConflict =>
                ProcessControlDecisionDisposition.IdempotencyConflict,
            ProcessControlDiagnosticCodes.SignalConflict =>
                ProcessControlDecisionDisposition.SignalConflict,
            ProcessControlDiagnosticCodes.AffinityConflict =>
                ProcessControlDecisionDisposition.AffinityConflict,
            ProcessControlDiagnosticCodes.InvalidState =>
                ProcessControlDecisionDisposition.InvalidState,
            ProcessControlDiagnosticCodes.InvalidCommand
                or ProcessControlDiagnosticCodes.SignalDurabilityMismatch
                or ProcessControlDiagnosticCodes.SafePointConflict
                or ProcessControlDiagnosticCodes.ActivationConflict
                or PortableExecutionDiagnosticCodes.OpaqueRuntimeType
                or PortableExecutionDiagnosticCodes.UnsupportedType
                or PortableExecutionDiagnosticCodes.UnresolvedType
                or PortableExecutionDiagnosticCodes.UnsupportedExpression
                or PortableExecutionDiagnosticCodes.InvalidNode
                or PortableExecutionDiagnosticCodes.UntypedContract
                or PortableExecutionDiagnosticCodes.UnresolvedShape
                or PortableExecutionDiagnosticCodes.PresenceMismatch
                or PortableExecutionDiagnosticCodes.NullabilityMismatch
                or PortableExecutionDiagnosticCodes.ConcreteTypeMismatch
                or PortableExecutionDiagnosticCodes.UndefinedObservation
                or PortableExecutionDiagnosticCodes.NonFiniteNumber
                or PortableExecutionDiagnosticCodes.MalformedObservation
                or InteractionEnvelopeDiagnosticCodes.SchemaVersionUnsupported
                or InteractionEnvelopeDiagnosticCodes.SchemaVersionInterpreterUnsupported
                or InteractionEnvelopeDiagnosticCodes.ValueInvalid
                or InteractionEnvelopeDiagnosticCodes.PayloadContractMismatch
                or InteractionEnvelopeDiagnosticCodes.OutcomeUnknown
                or InteractionEnvelopeDiagnosticCodes.OutcomeKindMismatch
                or InteractionEnvelopeDiagnosticCodes.OutcomeContractMismatch
                or InteractionContractCatalogDiagnosticCodes.DocumentInvalid
                or InteractionContractCatalogDiagnosticCodes.DuplicateRevision
                or InteractionContractCatalogDiagnosticCodes.DefinitionUnknown
                or InteractionContractCatalogDiagnosticCodes.RevisionUnknown
                or InteractionContractCatalogDiagnosticCodes.FingerprintMismatch
                or InteractionContractCatalogDiagnosticCodes.ContractKindMismatch
                or InteractionContractCatalogDiagnosticCodes.ReplyOutcomeUnknown =>
                ProcessControlDecisionDisposition.InvalidCommand,
            _ => ProcessControlDecisionDisposition.Unspecified
        };
        return disposition != ProcessControlDecisionDisposition.Unspecified;
    }

    static void RequireNoReceiptOrIntent(
        ProcessControlCommandReceipt? receipt,
        ProcessControlIntent? intent,
        ProcessControlDecisionDisposition disposition)
    {
        if (receipt is not null || intent is not null)
        {
            throw new ArgumentException(
                $"Observation-only disposition '{disposition}' cannot carry a receipt or intent.",
                nameof(disposition));
        }
    }

    static void RequireNoIntent(ProcessControlIntent? intent)
    {
        if (intent is not null)
        {
            throw new ArgumentException("This Process-control disposition cannot emit an intent.", nameof(intent));
        }
    }
}

/// <summary>Deterministic reference state machine for protocol-neutral Process lifecycle control.</summary>
/// <remarks>
/// Every operation consumes explicit state and observations and returns replacement semantic state. The caller
/// chooses and persists each durable cut. Physical CAS, checkpoint, inbox, worker fencing, and affinity allocation
/// remain Storage and runtime realization responsibilities. This conformance-oriented interpreter revalidates
/// complete replacement state; compiled runtime indexing and incremental persistence belong to those realizations.
/// </remarks>
public sealed class ProcessControlReferenceExecutor
{
    readonly InteractionContractCatalog contracts;

    /// <summary>Creates the reference controller over exact canonical interaction contracts.</summary>
    /// <param name="contracts">
    /// Catalog and contextual shape graph used to validate Signals, reason details, and attempt affinities.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="contracts"/> is <see langword="null"/>.</exception>
    public ProcessControlReferenceExecutor(InteractionContractCatalog contracts) =>
        this.contracts = Guard.RequireNotNull(contracts);

    /// <summary>Evaluates one canonical lifecycle command.</summary>
    /// <param name="state">Current portable Process-control state.</param>
    /// <param name="command">Canonical lifecycle command to evaluate.</param>
    /// <param name="observedAtUtc">Explicit UTC evaluation observation.</param>
    /// <param name="cancellationPolicy">Whether cancellation closes immediately or awaits authored finalization.</param>
    /// <returns>Replacement state, observable disposition, durable receipt, and first-time intent.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="state"/> or <paramref name="command"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="observedAtUtc"/> is not UTC or precedes persisted state or first-time command issuance.
    /// </exception>
    /// <exception cref="OverflowException">A required next semantic control revision cannot be represented.</exception>
    public ProcessControlDecision Apply(
        ProcessControlState state,
        ProcessControlCommand command,
        DateTimeOffset observedAtUtc,
        ProcessCancellationCompletionPolicy cancellationPolicy = ProcessCancellationCompletionPolicy.Immediate)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(command);
        command.EnsureDeclaredVariant();
        ExecutionObservationRequirements.RequireUtc(observedAtUtc, nameof(observedAtUtc));
        if (!Enum.IsDefined(cancellationPolicy))
            throw new ArgumentOutOfRangeException(nameof(cancellationPolicy), cancellationPolicy, "Unsupported cancellation completion policy.");

        var replay = ResolveCommandReplay(state, command);
        if (replay is not null)
        {
            return replay;
        }

        if (command.SchemaVersion != ProcessControlCommand.CurrentSchemaVersion)
        {
            return Reject(
                state,
                ProcessControlDiagnosticCodes.InvalidCommand,
                "The command schema version is not supported by this reference interpreter.",
                "/schemaVersion",
                ProcessControlCommand.CurrentSchemaVersion.Value,
                command.SchemaVersion.Value);
        }
        if (command.Context.Authorization.AuthorityScope != state.AuthorityScope)
        {
            return Reject(
                state,
                ProcessControlDiagnosticCodes.AuthorityMismatch,
                "The command authorization scope does not match the Process authority.",
                "/context/authorization/authorityScope");
        }
        if (command.Context.ProcessInstanceId != state.ProcessInstanceId)
        {
            return Reject(
                state,
                ProcessControlDiagnosticCodes.TargetMismatch,
                "The command targets another logical Process instance.",
                "/context/processInstanceId",
                state.ProcessInstanceId.Value,
                command.Context.ProcessInstanceId.Value);
        }
        if (observedAtUtc < state.UpdatedAtUtc || observedAtUtc < command.Context.IssuedAtUtc)
        {
            throw new ArgumentException(
                "A first-time command observation cannot precede persisted state or command issuance.",
                nameof(observedAtUtc));
        }

        var portableValueValidation = ValidateCommandPortableValues(command);
        if (!portableValueValidation.IsValid)
        {
            return new(
                state,
                ProcessControlDecisionDisposition.InvalidCommand,
                diagnostics: portableValueValidation.Diagnostics);
        }

        if (command is SignalProcessCommand signalCommand)
        {
            var signalValidation = ValidateSignalEnvelope(signalCommand.Signal);
            if (!signalValidation.IsValid)
            {
                return new(
                    state,
                    ProcessControlDecisionDisposition.InvalidCommand,
                    diagnostics: signalValidation.Diagnostics);
            }
            if (signalCommand.Signal.Context.Delivery.Durability != InteractionDurabilityDemand.Durable)
            {
                return Reject(
                    state,
                    ProcessControlDiagnosticCodes.SignalDurabilityMismatch,
                    "Process control requires a Signal with durable delivery semantics.",
                    "/signal/context/delivery/durability",
                    InteractionDurabilityDemand.Durable.ToString(),
                    signalCommand.Signal.Context.Delivery.Durability.ToString());
            }
        }

        if (command.Expectation is { } expectation)
        {
            var expectationFailure = ValidateExpectation(state, expectation);
            if (expectationFailure is not null)
            {
                return expectationFailure;
            }
        }

        return command switch
        {
            InspectProcessCommand => new(state, ProcessControlDecisionDisposition.Inspected),
            SignalProcessCommand signal => ApplySignal(state, signal, observedAtUtc),
            PauseProcessCommand pause => ApplyPause(state, pause, observedAtUtc),
            ContinueProcessCommand @continue => ApplyContinue(state, @continue, observedAtUtc),
            RestartProcessAttemptCommand restart => ApplyRestart(state, restart, observedAtUtc),
            CancelProcessCommand cancel => ApplyCancel(state, cancel, observedAtUtc, cancellationPolicy),
            TerminateProcessCommand terminate => ApplyTerminate(state, terminate, observedAtUtc),
            _ => Reject(
                state,
                ProcessControlDiagnosticCodes.InvalidCommand,
                "The command runtime variant is outside the closed Process-control family.",
                "/")
        };
    }

    /// <summary>Records that one finite activation began under an exact semantic fence.</summary>
    /// <param name="state">Current portable Process-control state.</param>
    /// <param name="observation">Stable activation-start observation.</param>
    /// <returns>Replacement state and observable activation decision.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="state"/> or <paramref name="observation"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">The observation predates persisted state.</exception>
    /// <exception cref="OverflowException">The next semantic control revision cannot be represented.</exception>
    public ProcessControlDecision BeginActivation(
        ProcessControlState state,
        ProcessActivationStartObservation observation)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(observation);

        var observedAttempt = FindAttempt(state, observation.Expectation.Continuation.ProcessAttemptId);
        var priorActivation = FindActivation(observedAttempt, observation.ActivationId);
        if (priorActivation is not null)
        {
            return priorActivation == observation
                ? new(state, ProcessControlDecisionDisposition.Replayed)
                : Reject(
                    state,
                    ProcessControlDiagnosticCodes.ActivationConflict,
                    "The activation identity was reused for conflicting start evidence.",
                    "/activationId");
        }
        if (observation.ObservedAtUtc < state.UpdatedAtUtc)
        {
            throw new ArgumentException("An activation observation cannot precede persisted state.", nameof(observation));
        }

        var expectationFailure = ValidateExpectation(state, observation.Expectation);
        if (expectationFailure is not null)
        {
            return expectationFailure;
        }

        if (!ProcessControlLifecycleSemantics.TryBeginActivation(
                LifecyclePosition(state),
                observation.Expectation.Continuation.ProcessAttemptId,
                out var lifecycle))
        {
            return InvalidState(state, "A new activation may begin only from non-pending Running state at a safe boundary.");
        }

        var current = state.CurrentAttempt;
        var started = new ProcessControlAttemptState(
            current.AttemptId,
            current.StartedAtUtc,
            current.Disposition,
            lifecycle.Phase,
            observation,
            current.SafePoints,
            current.AffinityBindings);
        var replacement = ReplaceCurrentAttempt(state.Attempts, started);
        var next = Replace(
            state,
            revision: state.Revision.Next(),
            attempts: replacement,
            updatedAtUtc: observation.ObservedAtUtc);
        return new(next, ProcessControlDecisionDisposition.ActivationStarted);
    }

    /// <summary>Records an invariant-preserving safe point and applies one pending lifecycle action.</summary>
    /// <param name="state">Current portable Process-control state.</param>
    /// <param name="observation">Exact safe-point observation.</param>
    /// <param name="cancellationPolicy">Whether pending cancellation closes immediately or awaits authored finalization.</param>
    /// <returns>Replacement state, observable disposition, and any first-time cancellation or restart intent.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="state"/> or <paramref name="observation"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">The observation predates persisted state.</exception>
    /// <exception cref="OverflowException">The next semantic control revision cannot be represented.</exception>
    public ProcessControlDecision ReachSafePoint(
        ProcessControlState state,
        ProcessSafePointObservation observation,
        ProcessCancellationCompletionPolicy cancellationPolicy = ProcessCancellationCompletionPolicy.Immediate)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(observation);
        if (!Enum.IsDefined(cancellationPolicy))
            throw new ArgumentOutOfRangeException(nameof(cancellationPolicy), cancellationPolicy, "Unsupported cancellation completion policy.");

        var observedAttempt = FindAttempt(state, observation.Expectation.Continuation.ProcessAttemptId);
        var prior = FindSafePoint(observedAttempt, observation.SafePointId);
        if (prior is not null)
        {
            return prior.Observation == observation
                ? new(state, ProcessControlDecisionDisposition.Replayed)
                : Reject(
                    state,
                    ProcessControlDiagnosticCodes.SafePointConflict,
                    "The safe-point identity was reused for conflicting durable-cut evidence.",
                    "/safePointId");
        }
        if (FindCompletedActivation(observedAttempt, observation.ActivationId) is not null)
        {
            return Reject(
                state,
                ProcessControlDiagnosticCodes.SafePointConflict,
                "The completed activation identity was reused for another safe point.",
                "/activationId");
        }
        if (observation.ObservedAtUtc < state.UpdatedAtUtc)
        {
            throw new ArgumentException("A safe-point observation cannot precede persisted state.", nameof(observation));
        }

        var expectationFailure = ValidateExpectation(state, observation.Expectation);
        if (expectationFailure is not null)
        {
            return expectationFailure;
        }

        ProcessControlCommandReceipt? pendingReceipt = null;
        ProcessAttemptId? restartAttemptId = null;
        if (state.PendingCommandId is { } pendingId)
        {
            pendingReceipt = state.FindReceipt(pendingId)
                ?? throw new InvalidOperationException("Validated pending command receipt is missing.");
            if (pendingReceipt.Command is RestartProcessAttemptCommand restart)
            {
                restartAttemptId = restart.Plan.NewAttemptId;
            }
        }
        if (!ProcessControlLifecycleSemantics.TryReachSafePoint(
                LifecyclePosition(state),
                observation.Expectation.Continuation.ProcessAttemptId,
                restartAttemptId,
                out var lifecycle,
                cancellationPolicy)
            || state.CurrentAttempt.ActiveActivationId != observation.ActivationId)
        {
            return InvalidState(state, "A safe point must close the exact activation currently in flight.");
        }

        var current = state.CurrentAttempt;
        var safePoint = new ProcessControlSafePoint(
            current.ActiveActivation
                ?? throw new InvalidOperationException("Validated active activation evidence is missing."),
            observation);
        var atSafePoint = new ProcessControlAttemptState(
            current.AttemptId,
            current.StartedAtUtc,
            current.Disposition,
            ProcessControlExecutionPhase.AtSafePoint,
            activeActivation: null,
            [.. current.SafePoints, safePoint],
            current.AffinityBindings);
        var attempts = ReplaceCurrentAttempt(state.Attempts, atSafePoint);
        var revision = state.Revision.Next();

        if (pendingReceipt is null)
        {
            var reached = Replace(
                state,
                revision: revision,
                mode: lifecycle.Mode,
                attempts: attempts,
                updatedAtUtc: observation.ObservedAtUtc);
            return new(reached, ProcessControlDecisionDisposition.SafePointReached);
        }

        return pendingReceipt.Command switch
        {
            PauseProcessCommand => new(
                Replace(
                    state,
                    revision: revision,
                    mode: lifecycle.Mode,
                    attempts: attempts,
                    pendingCommandId: null,
                    setPendingCommandId: true,
                    updatedAtUtc: observation.ObservedAtUtc),
                ProcessControlDecisionDisposition.SafePointReached,
                pendingReceipt),
            CancelProcessCommand cancel => CompletePendingCancellation(
                state,
                attempts,
                revision,
                lifecycle,
                pendingReceipt,
                cancel,
                observation.ObservedAtUtc,
                cancellationPolicy),
            RestartProcessAttemptCommand restart => CompletePendingRestart(
                state,
                attempts,
                revision,
                lifecycle,
                pendingReceipt,
                restart,
                observation.ObservedAtUtc),
            _ => throw new InvalidOperationException("Validated pending command kind is unsupported.")
        };
    }

    /// <summary>Binds one concrete attempt affinity exactly once.</summary>
    /// <param name="state">Current portable Process-control state.</param>
    /// <param name="observation">Exact write-once affinity observation.</param>
    /// <returns>Replacement state and observable binding, replay, conflict, or fencing disposition.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="state"/> or <paramref name="observation"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">The observation predates persisted state.</exception>
    /// <exception cref="OverflowException">The next semantic control revision cannot be represented.</exception>
    public ProcessControlDecision BindAttemptAffinity(
        ProcessControlState state,
        ProcessAttemptAffinityObservation observation)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(observation);

        if (observation.Expectation.Continuation.ProcessInstanceId != state.ProcessInstanceId)
        {
            return Reject(
                state,
                ProcessControlDiagnosticCodes.TargetMismatch,
                "The affinity observation targets another logical Process instance.",
                "/expectation/continuation/processInstanceId");
        }


        var portability = PortableExecutionValidator.Validate(
            observation.Affinity.Value,
            contracts.ShapeGraph);
        if (!portability.IsValid)
        {
            return new(
                state,
                ProcessControlDecisionDisposition.InvalidCommand,
                diagnostics: portability.Diagnostics);
        }

        var observedAttempt = FindAttempt(state, observation.Expectation.Continuation.ProcessAttemptId);
        var prior = observedAttempt?.FindAffinityBinding(observation.Affinity.Slot);
        if (prior is not null)
        {
            return prior == observation
                ? new(state, ProcessControlDecisionDisposition.Replayed)
                : Reject(
                    state,
                    ProcessControlDiagnosticCodes.AffinityConflict,
                    "The attempt affinity slot is write-once and already carries different evidence.",
                    "/affinity");
        }
        if (observation.ObservedAtUtc < state.UpdatedAtUtc)
        {
            throw new ArgumentException("An affinity observation cannot precede persisted state.", nameof(observation));
        }

        var expectationFailure = ValidateExpectation(state, observation.Expectation);
        if (expectationFailure is not null)
        {
            return expectationFailure;
        }

        if (!ProcessControlLifecycleSemantics.TryBindAttemptAffinity(
                LifecyclePosition(state),
                observation.Expectation.Continuation.ProcessAttemptId,
                out _))
        {
            return InvalidState(state, "An affinity cannot be bound while the current attempt is retiring or terminal.");
        }

        var current = state.CurrentAttempt;
        var affinityAttempt = new ProcessControlAttemptState(
            current.AttemptId,
            current.StartedAtUtc,
            current.Disposition,
            current.Phase,
            current.ActiveActivation,
            current.SafePoints,
            [.. current.AffinityBindings, observation]);
        var next = Replace(
            state,
            revision: state.Revision.Next(),
            attempts: ReplaceCurrentAttempt(state.Attempts, affinityAttempt),
            updatedAtUtc: observation.ObservedAtUtc);
        return new(next, ProcessControlDecisionDisposition.AffinityBound);
    }

    ProcessControlDecision? ResolveCommandReplay(
        ProcessControlState state,
        ProcessControlCommand command)
    {
        ProcessControlCommandReceipt? sameIdentity = null;
        ProcessControlCommandReceipt? sameIdempotency = null;
        foreach (var receipt in state.Receipts)
        {
            if (receipt.Command.Context.CommandId == command.Context.CommandId)
            {
                sameIdentity = receipt;
            }

            if (receipt.Command.Context.IdempotencyKey == command.Context.IdempotencyKey)
            {
                sameIdempotency = receipt;
            }
        }

        if (sameIdentity is not null)
        {
            return sameIdentity.Command == command
                ? new(state, ProcessControlDecisionDisposition.Replayed, sameIdentity)
                : Reject(
                    state,
                    ProcessControlDiagnosticCodes.CommandIdentityConflict,
                    "The stable command identity was reused for different canonical content.",
                    "/context/commandId");
        }
        if (sameIdempotency is not null)
        {
            return HasSameIdempotentIntent(sameIdempotency.Command, command)
                ? new(state, ProcessControlDecisionDisposition.Replayed, sameIdempotency)
                : Reject(
                    state,
                    ProcessControlDiagnosticCodes.CommandIdempotencyConflict,
                    "The command idempotency key was reused for a different semantic intent.",
                    "/context/idempotencyKey");
        }
        return null;
    }

    ProcessControlDecision? ValidateExpectation(
        ProcessControlState state,
        ProcessControlExpectation expectation)
    {
        if (expectation.Continuation.ProcessInstanceId != state.ProcessInstanceId)
        {
            return Reject(
                state,
                ProcessControlDiagnosticCodes.TargetMismatch,
                "The expectation targets another logical Process instance.",
                "/expectation/continuation/processInstanceId");
        }
        if (expectation.Continuation.ProcessAttemptId != state.CurrentAttempt.AttemptId)
        {
            return Reject(
                state,
                ProcessControlDiagnosticCodes.StaleAttempt,
                "The expected Process attempt is no longer current.",
                "/expectation/continuation/processAttemptId",
                state.CurrentAttempt.AttemptId.Value,
                expectation.Continuation.ProcessAttemptId.Value);
        }
        if (expectation.Revision != state.Revision)
        {
            return Reject(
                state,
                ProcessControlDiagnosticCodes.StaleRevision,
                "The expected semantic control revision/fence is stale.",
                "/expectation/revision",
                state.Revision.Value,
                expectation.Revision.Value);
        }
        return null;
    }

    ProcessControlDecision ApplySignal(
        ProcessControlState state,
        SignalProcessCommand command,
        DateTimeOffset observedAtUtc)
    {
        if (command.Signal.Context.AuthorityScope != state.AuthorityScope)
        {
            return Reject(
                state,
                ProcessControlDiagnosticCodes.AuthorityMismatch,
                "The Signal authority does not match the Process authority.",
                "/signal/context/authorityScope");
        }
        if (command.Signal.Target is not ProcessTokenInteractionTarget target)
        {
            return Reject(
                state,
                ProcessControlDiagnosticCodes.InvalidCommand,
                "Process control can admit only a Signal addressed to a Process token.",
                "/signal/target");
        }
        if (target.Continuation.ProcessInstanceId != state.ProcessInstanceId)
        {
            return Reject(
                state,
                ProcessControlDiagnosticCodes.TargetMismatch,
                "The Signal targets another logical Process instance.",
                "/signal/target/continuation/processInstanceId",
                state.ProcessInstanceId.Value,
                target.Continuation.ProcessInstanceId.Value);
        }
        if (target.Continuation.ProcessAttemptId != state.CurrentAttempt.AttemptId)
        {
            return Reject(
                state,
                ProcessControlDiagnosticCodes.StaleAttempt,
                "The Signal targets a Process attempt that is no longer current.",
                "/signal/target/continuation/processAttemptId",
                state.CurrentAttempt.AttemptId.Value,
                target.Continuation.ProcessAttemptId.Value);
        }
        var position = LifecyclePosition(state);
        if (!ProcessControlLifecycleSemantics.TryClassifyCommand(
                position,
                command,
                duplicateSignal: false,
                out _,
                out _))
        {
            return InvalidState(state, "Signals cannot enter an attempt that is retiring, closing, or terminal.");
        }

        var priorSignal = ResolveSignalIdentity(state, command.Signal, out var signalConflict);
        if (signalConflict)
        {
            return Reject(
                state,
                ProcessControlDiagnosticCodes.SignalConflict,
                "The Signal emission or scoped idempotency identity conflicts with prior admitted content.",
                "/signal/context");
        }
        _ = ProcessControlLifecycleSemantics.TryClassifyCommand(
            position,
            command,
            duplicateSignal: priorSignal is not null,
            out var receiptDisposition,
            out var lifecycle);
        if (receiptDisposition == ProcessControlReceiptDisposition.SignalDuplicate)
        {
            return RecordCommand(
                state,
                command,
                receiptDisposition,
                lifecycle.Mode,
                state.Attempts,
                state.PendingCommandId,
                observedAtUtc,
                intent: null);
        }

        var admissionDisposition = receiptDisposition == ProcessControlReceiptDisposition.SignalBuffered
            ? ProcessSignalAdmissionDisposition.Buffered
            : ProcessSignalAdmissionDisposition.Active;
        var admission = new ProcessSignalAdmission(
            command.Context.CommandId,
            command.Signal,
            admissionDisposition,
            observedAtUtc);
        return RecordCommand(
            state,
            command,
            receiptDisposition,
            lifecycle.Mode,
            state.Attempts,
            state.PendingCommandId,
            observedAtUtc,
            new ProcessSignalAdmissionIntent(admission));
    }

    ProcessControlDecision ApplyPause(
        ProcessControlState state,
        PauseProcessCommand command,
        DateTimeOffset observedAtUtc)
    {
        if (!ProcessControlLifecycleSemantics.TryClassifyCommand(
                LifecyclePosition(state),
                command,
                duplicateSignal: false,
                out var disposition,
                out var lifecycle))
        {
            return InvalidState(state, "Pause is legal only while Running, pausing, or Paused.");
        }

        var deferred = disposition == ProcessControlReceiptDisposition.DeferredToSafePoint;
        return RecordCommand(
            state,
            command,
            disposition,
            lifecycle.Mode,
            state.Attempts,
            deferred ? command.Context.CommandId : state.PendingCommandId,
            observedAtUtc,
            deferred
                ? new ProcessReachSafePointIntent(command.Context.CommandId, ProcessControlPendingAction.Pause)
                : null);
    }

    ProcessControlDecision ApplyContinue(
        ProcessControlState state,
        ContinueProcessCommand command,
        DateTimeOffset observedAtUtc)
    {
        if (!ProcessControlLifecycleSemantics.TryClassifyCommand(
                LifecyclePosition(state),
                command,
                duplicateSignal: false,
                out var disposition,
                out var lifecycle))
        {
            return InvalidState(state, "Continue is legal only for a Paused Process.");
        }

        return RecordCommand(
            state,
            command,
            disposition,
            lifecycle.Mode,
            state.Attempts,
            state.PendingCommandId,
            observedAtUtc,
            intent: null);
    }

    ProcessControlDecision ApplyRestart(
        ProcessControlState state,
        RestartProcessAttemptCommand command,
        DateTimeOffset observedAtUtc)
    {
        if (FindAttempt(state, command.Plan.NewAttemptId) is not null)
        {
            return Reject(
                state,
                ProcessControlDiagnosticCodes.InvalidCommand,
                "The replacement attempt identity already exists in Process lineage.",
                "/plan/newAttemptId");
        }
        if (!state.CurrentAttempt.AffinityBindings.IsEmpty
            && command.Plan.Cleanup != ProcessAttemptCleanupRequirement.AbandonAffinitiesAndReleaseResources)
        {
            return Reject(
                state,
                ProcessControlDiagnosticCodes.InvalidCommand,
                "Restarting an attempt with affinities requires explicit affinity abandonment and resource cleanup.",
                "/plan/cleanup");
        }
        if (!ProcessControlLifecycleSemantics.TryClassifyCommand(
                LifecyclePosition(state),
                command,
                duplicateSignal: false,
                out var disposition,
                out var lifecycle))
        {
            return InvalidState(state, "RestartAttempt is legal only while Running or Paused.");
        }

        if (disposition == ProcessControlReceiptDisposition.DeferredToSafePoint)
        {
            return RecordCommand(
                state,
                command,
                disposition,
                lifecycle.Mode,
                state.Attempts,
                command.Context.CommandId,
                observedAtUtc,
                new ProcessReachSafePointIntent(command.Context.CommandId, ProcessControlPendingAction.RestartAttempt));
        }

        var replacement = RestartAttempts(state.Attempts, command, observedAtUtc);
        return RecordCommand(
            state,
            command,
            disposition,
            lifecycle.Mode,
            replacement,
            pendingCommandId: null,
            observedAtUtc,
            RestartIntent(state, command));
    }

    ProcessControlDecision ApplyCancel(
        ProcessControlState state,
        CancelProcessCommand command,
        DateTimeOffset observedAtUtc,
        ProcessCancellationCompletionPolicy cancellationPolicy)
    {
        if (!ProcessControlLifecycleSemantics.TryClassifyCommand(
                LifecyclePosition(state),
                command,
                duplicateSignal: false,
                out var disposition,
                out var lifecycle,
                cancellationPolicy))
        {
            return InvalidState(
                state,
                state.Mode switch
                {
                    ProcessControlMode.Terminated => "Cancelled and Terminated are distinct terminal states.",
                    ProcessControlMode.PauseRequested => "Cancellation cannot replace an already pending pause.",
                    ProcessControlMode.RestartRequested =>
                        "Cancellation cannot replace an already pending attempt restart.",
                    _ => "Cancellation requires a nonterminal Process at a safe boundary or in activation."
                });
        }

        if (disposition is ProcessControlReceiptDisposition.AlreadyRequested
            or ProcessControlReceiptDisposition.AlreadySatisfied)
        {
            return RecordCommand(
                state,
                command,
                disposition,
                lifecycle.Mode,
                state.Attempts,
                state.PendingCommandId,
                observedAtUtc,
                intent: null);
        }
        if (disposition == ProcessControlReceiptDisposition.DeferredToSafePoint)
        {
            return RecordCommand(
                state,
                command,
                disposition,
                lifecycle.Mode,
                state.Attempts,
                command.Context.CommandId,
                observedAtUtc,
                new ProcessReachSafePointIntent(command.Context.CommandId, ProcessControlPendingAction.Cancel));
        }

        var cancelledAttempts = cancellationPolicy == ProcessCancellationCompletionPolicy.AuthoredFinalization
            ? ReplaceCurrentAttempt(
                state.Attempts,
                new ProcessControlAttemptState(
                    state.CurrentAttempt.AttemptId,
                    state.CurrentAttempt.StartedAtUtc,
                    state.CurrentAttempt.Disposition,
                    state.CurrentAttempt.Phase,
                    activeActivation: null,
                    state.CurrentAttempt.SafePoints,
                    state.CurrentAttempt.AffinityBindings))
            : CloseCurrentAttempt(
                state.Attempts,
                ProcessControlAttemptDisposition.Cancelled,
                new ProcessAttemptClosure(command.Context.CommandId, observedAtUtc));
        return RecordCommand(
            state,
            command,
            disposition,
            lifecycle.Mode,
            cancelledAttempts,
            pendingCommandId: null,
            observedAtUtc,
            new ProcessCancellationIntent(
                state.CurrentAttempt.AttemptId,
                command.Reason,
                command.Context.CommandId));
    }

    ProcessControlDecision ApplyTerminate(
        ProcessControlState state,
        TerminateProcessCommand command,
        DateTimeOffset observedAtUtc)
    {
        if (!ProcessControlLifecycleSemantics.TryClassifyCommand(
                LifecyclePosition(state),
                command,
                duplicateSignal: false,
                out var disposition,
                out var lifecycle))
        {
            return InvalidState(state, "Cancelled and Terminated are distinct terminal states.");
        }
        if (disposition == ProcessControlReceiptDisposition.AlreadySatisfied)
        {
            return RecordCommand(
                state,
                command,
                disposition,
                lifecycle.Mode,
                state.Attempts,
                state.PendingCommandId,
                observedAtUtc,
                intent: null);
        }
        if (!state.CurrentAttempt.AffinityBindings.IsEmpty
            && command.Cleanup != ProcessAttemptCleanupRequirement.AbandonAffinitiesAndReleaseResources)
        {
            return Reject(
                state,
                ProcessControlDiagnosticCodes.InvalidCommand,
                "Terminating an attempt with affinities requires explicit affinity abandonment and resource cleanup.",
                "/cleanup");
        }

        var terminatedAttempts = CloseCurrentAttempt(
            state.Attempts,
            ProcessControlAttemptDisposition.Terminated,
            new ProcessAttemptClosure(
                command.Context.CommandId,
                observedAtUtc,
                state.CurrentAttempt.ActiveActivation));
        return RecordCommand(
            state,
            command,
            disposition,
            lifecycle.Mode,
            terminatedAttempts,
            pendingCommandId: null,
            observedAtUtc,
            new ProcessTerminationIntent(state.CurrentAttempt.AttemptId, command.Reason, command.Cleanup));
    }

    ProcessControlDecision CompletePendingCancellation(
        ProcessControlState state,
        ImmutableArray<ProcessControlAttemptState> safeAttempts,
        ProcessControlRevision revision,
        ProcessControlLifecycleSemantics.Position lifecycle,
        ProcessControlCommandReceipt receipt,
        CancelProcessCommand command,
        DateTimeOffset observedAtUtc,
        ProcessCancellationCompletionPolicy cancellationPolicy)
    {
        var closed = cancellationPolicy == ProcessCancellationCompletionPolicy.AuthoredFinalization
            ? safeAttempts
            : CloseCurrentAttempt(
                safeAttempts,
                ProcessControlAttemptDisposition.Cancelled,
                new ProcessAttemptClosure(command.Context.CommandId, observedAtUtc));
        var next = Replace(
            state,
            revision: revision,
            mode: lifecycle.Mode,
            attempts: closed,
            pendingCommandId: null,
            setPendingCommandId: true,
            updatedAtUtc: observedAtUtc);
        return new(
            next,
            ProcessControlDecisionDisposition.SafePointReached,
            receipt,
            intent: new ProcessCancellationIntent(
                state.CurrentAttempt.AttemptId,
                command.Reason,
                command.Context.CommandId));
    }

    /// <summary>Records terminal evidence from one exact authored cancellation-finalizer occurrence.</summary>
    /// <param name="state">Current cancelling control state.</param>
    /// <param name="observation">Acknowledged cancellation or explicit finalization failure.</param>
    /// <returns>Replacement terminal control state, or a replay/conflict diagnostic.</returns>
    /// <exception cref="ArgumentNullException">Either argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The observation predates retained control evidence.</exception>
    /// <exception cref="OverflowException">The next semantic control revision cannot be represented.</exception>
    public ProcessControlDecision CompleteCancellationFinalization(
        ProcessControlState state,
        ProcessCancellationFinalizationObservation observation)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(observation);
        if (state.CancellationFinalization is { } retained)
        {
            return retained == observation
                ? new(state, ProcessControlDecisionDisposition.Replayed)
                : InvalidState(state, "Cancellation-finalization terminal evidence conflicts with the retained outcome.");
        }
        if (observation.ObservedAtUtc < state.UpdatedAtUtc)
            throw new ArgumentException("Cancellation-finalization evidence cannot predate retained control state.", nameof(observation));
        if (observation.Intent.AttemptId != state.CurrentAttempt.AttemptId
            || observation.Intent.CommandId is not { } commandId
            || state.FindReceipt(commandId)?.Command is not CancelProcessCommand command
            || command.Reason != observation.Intent.Reason
            || !ProcessControlLifecycleSemantics.TryCompleteCancellationFinalization(
                LifecyclePosition(state),
                observation.Outcome,
                out var lifecycle))
        {
            return InvalidState(state, "Cancellation-finalization evidence does not match the active authored cancellation occurrence.");
        }

        var disposition = observation.Outcome == ExecutionTerminalOutcomeKind.Cancelled
            ? ProcessControlAttemptDisposition.Cancelled
            : ProcessControlAttemptDisposition.CancellationFailed;
        var closed = CloseCurrentAttempt(
            state.Attempts,
            disposition,
            new ProcessAttemptClosure(commandId, observation.ObservedAtUtc));
        var next = Replace(
            state,
            revision: state.Revision.Next(),
            mode: lifecycle.Mode,
            attempts: closed,
            updatedAtUtc: observation.ObservedAtUtc,
            cancellationFinalization: observation,
            setCancellationFinalization: true);
        return new(next, ProcessControlDecisionDisposition.CancellationFinalized);
    }

    ProcessControlDecision CompletePendingRestart(
        ProcessControlState state,
        ImmutableArray<ProcessControlAttemptState> safeAttempts,
        ProcessControlRevision revision,
        ProcessControlLifecycleSemantics.Position lifecycle,
        ProcessControlCommandReceipt receipt,
        RestartProcessAttemptCommand command,
        DateTimeOffset observedAtUtc)
    {
        var restarted = RestartAttempts(safeAttempts, command, observedAtUtc);
        var next = Replace(
            state,
            revision: revision,
            mode: lifecycle.Mode,
            attempts: restarted,
            pendingCommandId: null,
            setPendingCommandId: true,
            updatedAtUtc: observedAtUtc);
        return new(
            next,
            ProcessControlDecisionDisposition.SafePointReached,
            receipt,
            intent: RestartIntent(state, command));
    }

    static ProcessControlLifecycleSemantics.Position LifecyclePosition(ProcessControlState state) =>
        new(state.Mode, state.CurrentAttempt.Phase, state.CurrentAttempt.AttemptId);

    ProcessControlDecision RecordCommand(
        ProcessControlState state,
        ProcessControlCommand command,
        ProcessControlReceiptDisposition receiptDisposition,
        ProcessControlMode mode,
        ImmutableArray<ProcessControlAttemptState> attempts,
        ProcessControlCommandId? pendingCommandId,
        DateTimeOffset observedAtUtc,
        ProcessControlIntent? intent)
    {
        var receipt = new ProcessControlCommandReceipt(
            command,
            receiptDisposition,
            observedAtUtc);
        var afterRevision = receipt.AfterRevision;
        var next = new ProcessControlState(
            state.SchemaVersion,
            state.Definition,
            state.AuthorityScope,
            state.ProcessInstanceId,
            afterRevision,
            mode,
            attempts,
            pendingCommandId,
            [.. state.Receipts, receipt],
            state.CreatedAtUtc,
            observedAtUtc);
        return new(next, ToDecisionDisposition(receiptDisposition), receipt, intent);
    }

    static ProcessControlDecisionDisposition ToDecisionDisposition(
        ProcessControlReceiptDisposition disposition) =>
        disposition switch
        {
            ProcessControlReceiptDisposition.Applied => ProcessControlDecisionDisposition.Applied,
            ProcessControlReceiptDisposition.DeferredToSafePoint =>
                ProcessControlDecisionDisposition.DeferredToSafePoint,
            ProcessControlReceiptDisposition.AlreadySatisfied =>
                ProcessControlDecisionDisposition.AlreadySatisfied,
            ProcessControlReceiptDisposition.AlreadyRequested =>
                ProcessControlDecisionDisposition.AlreadyRequested,
            ProcessControlReceiptDisposition.SignalAccepted =>
                ProcessControlDecisionDisposition.SignalAccepted,
            ProcessControlReceiptDisposition.SignalBuffered =>
                ProcessControlDecisionDisposition.SignalBuffered,
            ProcessControlReceiptDisposition.SignalDuplicate =>
                ProcessControlDecisionDisposition.SignalDuplicate,
            _ => throw new ArgumentOutOfRangeException(
                nameof(disposition),
                disposition,
                "Unsupported durable receipt disposition.")
        };

    static ImmutableArray<ProcessControlAttemptState> RestartAttempts(
        ImmutableArray<ProcessControlAttemptState> attempts,
        RestartProcessAttemptCommand command,
        DateTimeOffset observedAtUtc)
    {
        var current = attempts[^1];
        var closure = new ProcessAttemptClosure(command.Context.CommandId, observedAtUtc);
        var abandoned = new ProcessControlAttemptState(
            current.AttemptId,
            current.StartedAtUtc,
            ProcessControlAttemptDisposition.Abandoned,
            ProcessControlExecutionPhase.Stopped,
            activeActivation: null,
            current.SafePoints,
            current.AffinityBindings,
            closure);
        var replacement = new ProcessControlAttemptState(
            command.Plan.NewAttemptId,
            observedAtUtc,
            ProcessControlAttemptDisposition.Current,
            ProcessControlExecutionPhase.Ready);
        return [.. attempts[..^1], abandoned, replacement];
    }

    static ImmutableArray<ProcessControlAttemptState> CloseCurrentAttempt(
        ImmutableArray<ProcessControlAttemptState> attempts,
        ProcessControlAttemptDisposition disposition,
        ProcessAttemptClosure closure)
    {
        var current = attempts[^1];
        var closed = new ProcessControlAttemptState(
            current.AttemptId,
            current.StartedAtUtc,
            disposition,
            ProcessControlExecutionPhase.Stopped,
            activeActivation: null,
            current.SafePoints,
            current.AffinityBindings,
            closure);
        return ReplaceCurrentAttempt(attempts, closed);
    }

    static ProcessAttemptRestartIntent RestartIntent(
        ProcessControlState state,
        RestartProcessAttemptCommand command) =>
        new(
            state.ProcessInstanceId,
            state.CurrentAttempt.AttemptId,
            command.Plan.NewAttemptId,
            command.Plan.Cleanup);

    static ProcessControlState Replace(
        ProcessControlState state,
        ProcessControlRevision? revision = null,
        ProcessControlMode? mode = null,
        ImmutableArray<ProcessControlAttemptState> attempts = default,
        ProcessControlCommandId? pendingCommandId = null,
        bool setPendingCommandId = false,
        ImmutableArray<ProcessControlCommandReceipt> receipts = default,
        DateTimeOffset? updatedAtUtc = null,
        ProcessCancellationFinalizationObservation? cancellationFinalization = null,
        bool setCancellationFinalization = false) =>
        new(
            state.SchemaVersion,
            state.Definition,
            state.AuthorityScope,
            state.ProcessInstanceId,
            revision ?? state.Revision,
            mode ?? state.Mode,
            attempts.IsDefault ? state.Attempts : attempts,
            setPendingCommandId ? pendingCommandId : state.PendingCommandId,
            receipts.IsDefault ? state.Receipts : receipts,
            state.CreatedAtUtc,
            updatedAtUtc ?? state.UpdatedAtUtc,
            setCancellationFinalization ? cancellationFinalization : state.CancellationFinalization);

    static ImmutableArray<ProcessControlAttemptState> ReplaceCurrentAttempt(
        ImmutableArray<ProcessControlAttemptState> attempts,
        ProcessControlAttemptState current) =>
        [.. attempts[..^1], current];

    static ProcessControlAttemptState? FindAttempt(ProcessControlState state, ProcessAttemptId id)
    {
        foreach (var attempt in state.Attempts)
        {
            if (attempt.AttemptId == id)
            {
                return attempt;
            }
        }
        return null;
    }

    static ProcessActivationStartObservation? FindActivation(
        ProcessControlAttemptState? attempt,
        ActivationId activationId)
    {
        if (attempt?.ActiveActivation is { } active && active.ActivationId == activationId)
        {
            return active;
        }

        if (attempt is null)
        {
            return null;
        }

        if (attempt.Closure?.InterruptedActivation is { } interrupted
            && interrupted.ActivationId == activationId)
        {
            return interrupted;
        }
        foreach (var safePoint in attempt.SafePoints)
        {
            if (safePoint.ActivationId == activationId)
            {
                return safePoint.Activation;
            }
        }
        return null;
    }

    static ProcessControlSafePoint? FindSafePoint(
        ProcessControlAttemptState? attempt,
        ProcessSafePointId safePointId)
    {
        if (attempt is null)
        {
            return null;
        }

        foreach (var safePoint in attempt.SafePoints)
        {
            if (safePoint.SafePointId == safePointId)
            {
                return safePoint;
            }
        }
        return null;
    }

    static ProcessControlSafePoint? FindCompletedActivation(
        ProcessControlAttemptState? attempt,
        ActivationId activationId)
    {
        if (attempt is null)
        {
            return null;
        }

        foreach (var safePoint in attempt.SafePoints)
        {
            if (safePoint.ActivationId == activationId)
            {
                return safePoint;
            }
        }
        return null;
    }

    DocumentValidationResult ValidateSignalEnvelope(SignalEnvelope signal) =>
        InteractionEnvelopeValidator.Validate(signal, contracts, contracts.ShapeGraph);

    DocumentValidationResult ValidateCommandPortableValues(ProcessControlCommand command)
    {
        var detail = command switch
        {
            RestartProcessAttemptCommand restart => restart.Plan.Reason.Detail,
            CancelProcessCommand cancel => cancel.Reason.Detail,
            TerminateProcessCommand terminate => terminate.Reason.Detail,
            _ => null
        };
        return detail is null
            ? DocumentValidationResult.Valid
            : PortableExecutionValidator.Validate(detail, contracts.ShapeGraph);
    }

    static SignalEnvelope? ResolveSignalIdentity(
        ProcessControlState state,
        SignalEnvelope signal,
        out bool conflict)
    {
        foreach (var receipt in state.Receipts)
        {
            if (receipt.Command is not SignalProcessCommand priorCommand
                || receipt.Disposition is not (ProcessControlReceiptDisposition.SignalAccepted
                    or ProcessControlReceiptDisposition.SignalBuffered))
            {
                continue;
            }
            var prior = priorCommand.Signal;
            var sameEmission = prior.Context.EmissionId == signal.Context.EmissionId;
            var sameScopedIdempotency = prior.Contract == signal.Contract
                && prior.Context.IdempotencyKey == signal.Context.IdempotencyKey;
            if (!sameEmission && !sameScopedIdempotency)
            {
                continue;
            }

            conflict = prior != signal;
            return prior;
        }

        conflict = false;
        return null;
    }

    static bool HasSameIdempotentIntent(
        ProcessControlCommand prior,
        ProcessControlCommand candidate)
    {
        if (prior.GetType() != candidate.GetType()
            || prior.SchemaVersion != candidate.SchemaVersion
            || prior.Context.IdempotencyKey != candidate.Context.IdempotencyKey
            || prior.Context.ProcessInstanceId != candidate.Context.ProcessInstanceId
            || prior.Context.Authorization != candidate.Context.Authorization
            || prior.Context.Provenance != candidate.Context.Provenance
            || prior.Expectation != candidate.Expectation)
        {
            return false;
        }

        return (prior, candidate) switch
        {
            (InspectProcessCommand, InspectProcessCommand) => true,
            (SignalProcessCommand left, SignalProcessCommand right) => left.Signal == right.Signal,
            (PauseProcessCommand, PauseProcessCommand) => true,
            (ContinueProcessCommand, ContinueProcessCommand) => true,
            (RestartProcessAttemptCommand left, RestartProcessAttemptCommand right) => left.Plan == right.Plan,
            (CancelProcessCommand left, CancelProcessCommand right) => left.Reason == right.Reason,
            (TerminateProcessCommand left, TerminateProcessCommand right) =>
                left.Reason == right.Reason && left.Cleanup == right.Cleanup,
            _ => false
        };
    }

    static ProcessControlDecision InvalidState(ProcessControlState state, string message) =>
        Reject(
            state,
            ProcessControlDiagnosticCodes.InvalidState,
            message,
            "/mode");

    static ProcessControlDecision Reject(
        ProcessControlState state,
        string code,
        string message,
        string location,
        string? expected = null,
        string? observed = null)
    {
        if (!ProcessControlDecision.TryGetRejectionDisposition(code, out var disposition))
        {
            throw new ArgumentOutOfRangeException(nameof(code), code, "Unknown Process-control rejection code.");
        }

        return new(
            state,
            disposition,
            diagnostics: [new(
                code,
                DiagnosticSeverity.Error,
                message,
                location,
                Evidence: new(
                    stage: "process-control",
                    subject: state.ProcessInstanceId.Value,
                    expected: expected,
                    observed: observed))]);
    }
}
