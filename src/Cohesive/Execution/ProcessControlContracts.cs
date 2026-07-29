using System.Text.Json.Serialization;

namespace Cohesive.Execution;

/// <summary>Stable wire names for the closed Process-control command and intent families.</summary>
public static class ExecutionControlWireNames
{
    /// <summary>Canonical semantic authority that owns Process lifecycle control.</summary>
    public const string SemanticAuthority = "cohesive.execution.process-control";

    /// <summary>JSON property that discriminates Process-control command variants.</summary>
    public const string CommandDiscriminator = "$command";

    /// <summary>Inspect command discriminator.</summary>
    public const string Inspect = "inspect";

    /// <summary>Signal command discriminator.</summary>
    public const string Signal = "signal";

    /// <summary>Pause command discriminator.</summary>
    public const string Pause = "pause";

    /// <summary>Continue command discriminator.</summary>
    public const string Continue = "continue";

    /// <summary>Restart-attempt command discriminator.</summary>
    public const string RestartAttempt = "restartAttempt";

    /// <summary>Cancel command discriminator.</summary>
    public const string Cancel = "cancel";

    /// <summary>Terminate command discriminator.</summary>
    public const string Terminate = "terminate";

    /// <summary>JSON property that discriminates Process-control intent variants.</summary>
    public const string IntentDiscriminator = "$intent";

    /// <summary>Signal-admission intent discriminator.</summary>
    public const string AdmitSignal = "admitSignal";

    /// <summary>Safe-point intent discriminator.</summary>
    public const string ReachSafePoint = "reachSafePoint";

    /// <summary>Attempt-restart intent discriminator.</summary>
    public const string RestartAttemptIntent = "restartAttempt";

    /// <summary>Cancellation intent discriminator.</summary>
    public const string CancelIntent = "cancel";

    /// <summary>Termination intent discriminator.</summary>
    public const string TerminateIntent = "terminate";

    /// <summary>Gets the canonical semantic path of a closed Process-control command.</summary>
    /// <param name="action">One of the canonical Process-control command action names.</param>
    /// <returns>The semantic path owned by the Process-control command family.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="action"/> is not a closed command action.</exception>
    public static ExecutionSemanticPath CommandPath(string action) => action switch
    {
        Inspect or Signal or Pause or Continue or RestartAttempt or Cancel or Terminate => new(["commands", action]),
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unsupported Process-control action.")
    };
}

/// <summary>Portable attributable authorization evidence carried by one control command.</summary>
/// <remarks>
/// This is an audit snapshot, not an ambient principal or an authorization engine. API and identity projections
/// decide whether to issue the command and retain their stable decision reference here.
/// </remarks>
public sealed record ProcessControlAuthorizationContext
{
    /// <summary>Creates attributable command authorization context.</summary>
    /// <param name="actor">Stable identity of the actor or system principal issuing the command.</param>
    /// <param name="authorityScope">Authority and optional tenant boundary in which the decision applies.</param>
    /// <param name="evidenceReference">Stable reference to the admitting policy decision or credential evidence.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="actor"/>, <paramref name="authorityScope"/>, or <paramref name="evidenceReference"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="actor"/> or <paramref name="evidenceReference"/> is empty or white-space.
    /// </exception>
    [JsonConstructor]
    public ProcessControlAuthorizationContext(
        string actor,
        InteractionAuthorityScope authorityScope,
        string evidenceReference)
    {
        Actor = Guard.RequireNotNullOrWhiteSpace(actor);
        AuthorityScope = Guard.RequireNotNull(authorityScope);
        EvidenceReference = Guard.RequireNotNullOrWhiteSpace(evidenceReference);
    }

    /// <summary>Stable actor or system-principal identity.</summary>
    public string Actor { get; }

    /// <summary>Authority and optional tenant boundary of the authorization decision.</summary>
    public InteractionAuthorityScope AuthorityScope { get; }

    /// <summary>Stable attributable reference to the external authorization decision.</summary>
    public string EvidenceReference { get; }
}

/// <summary>Exact attempt and semantic revision expected by a Process control operation.</summary>
public sealed record ProcessControlExpectation
{
    /// <summary>Creates an optimistic Process-control expectation.</summary>
    /// <param name="continuation">Exact logical Process instance and current attempt.</param>
    /// <param name="revision">Expected semantic control revision, which also acts as the control fence.</param>
    /// <exception cref="ArgumentNullException"><paramref name="continuation"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="revision"/> is default.</exception>
    [JsonConstructor]
    public ProcessControlExpectation(
        ProcessContinuationIdentity continuation,
        ProcessControlRevision revision)
    {
        Continuation = Guard.RequireNotNull(continuation);
        if (string.IsNullOrWhiteSpace(revision.Value))
        {
            throw new ArgumentException("A Process-control expectation requires a revision.", nameof(revision));
        }

        Revision = revision;
    }

    /// <summary>Exact Process instance and attempt expected by the operation.</summary>
    public ProcessContinuationIdentity Continuation { get; }

    /// <summary>Expected semantic control revision and optimistic fence.</summary>
    public ProcessControlRevision Revision { get; }
}

/// <summary>Common immutable identity, authority, and provenance carried by every lifecycle command.</summary>
public sealed record ProcessControlCommandContext
{
    /// <summary>Creates common Process-control command context.</summary>
    /// <param name="commandId">Stable command identity.</param>
    /// <param name="idempotencyKey">Logical command-deduplication key.</param>
    /// <param name="processInstanceId">Logical Process instance targeted by the command.</param>
    /// <param name="authorization">Portable authorization evidence.</param>
    /// <param name="issuedAtUtc">Explicit UTC command-issuance observation.</param>
    /// <param name="provenance">Producer and source attribution for the command.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="commandId"/>, <paramref name="idempotencyKey"/>, or
    /// <paramref name="processInstanceId"/> is default, or <paramref name="issuedAtUtc"/> is not UTC.
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="authorization"/> or <paramref name="provenance"/> is <see langword="null"/>.
    /// </exception>
    [JsonConstructor]
    public ProcessControlCommandContext(
        ProcessControlCommandId commandId,
        ProcessControlIdempotencyKey idempotencyKey,
        ProcessInstanceId processInstanceId,
        ProcessControlAuthorizationContext authorization,
        DateTimeOffset issuedAtUtc,
        ExecutionProvenance provenance)
    {
        if (string.IsNullOrWhiteSpace(commandId.Value))
        {
            throw new ArgumentException("A Process control command requires a stable identity.", nameof(commandId));
        }

        if (string.IsNullOrWhiteSpace(idempotencyKey.Value))
        {
            throw new ArgumentException("A Process control command requires an idempotency key.", nameof(idempotencyKey));
        }

        if (string.IsNullOrWhiteSpace(processInstanceId.Value))
        {
            throw new ArgumentException("A Process control command requires a Process instance.", nameof(processInstanceId));
        }

        ExecutionObservationRequirements.RequireUtc(issuedAtUtc, nameof(issuedAtUtc));

        CommandId = commandId;
        IdempotencyKey = idempotencyKey;
        ProcessInstanceId = processInstanceId;
        Authorization = Guard.RequireNotNull(authorization);
        IssuedAtUtc = issuedAtUtc;
        Provenance = Guard.RequireNotNull(provenance);
    }

    /// <summary>Stable identity of this command occurrence.</summary>
    public ProcessControlCommandId CommandId { get; }

    /// <summary>Stable logical command-deduplication key.</summary>
    public ProcessControlIdempotencyKey IdempotencyKey { get; }

    /// <summary>Logical Process instance targeted by the command.</summary>
    public ProcessInstanceId ProcessInstanceId { get; }

    /// <summary>Portable attributable authorization evidence.</summary>
    public ProcessControlAuthorizationContext Authorization { get; }

    /// <summary>Explicit UTC time at which this canonical command was issued.</summary>
    public DateTimeOffset IssuedAtUtc { get; }

    /// <summary>Producer and source attribution for this command.</summary>
    public ExecutionProvenance Provenance { get; }
}

/// <summary>Portable typed explanation for cancellation, termination, or attempt abandonment.</summary>
public sealed record ProcessControlReason
{
    /// <summary>Creates a Process-control reason.</summary>
    /// <param name="code">Stable machine-readable reason code.</param>
    /// <param name="detail">Optional materialized portable typed detail.</param>
    /// <exception cref="ArgumentNullException"><paramref name="code"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="code"/> is empty or white-space, or <paramref name="detail"/> is unknown or failed.
    /// </exception>
    [JsonConstructor]
    public ProcessControlReason(string code, PortableValue? detail = null)
    {
        if (detail is { State: PortableValueState.Unknown or PortableValueState.Failed })
        {
            throw new ArgumentException("A Process-control reason cannot contain unknown or failed detail.", nameof(detail));
        }

        Code = Guard.RequireNotNullOrWhiteSpace(code);
        Detail = detail;
    }

    /// <summary>Stable machine-readable reason code.</summary>
    public string Code { get; }

    /// <summary>Optional materialized portable typed reason detail.</summary>
    public PortableValue? Detail { get; }
}

/// <summary>One write-once attempt-scoped affinity defined by a stable Process semantic slot.</summary>
/// <remarks>
/// Index candidate generation, model-training allocation, and similar attempt-bound resources are projections of
/// this generic hook. The owning block defines the value contract and physical lifecycle.
/// </remarks>
public sealed record ProcessAttemptAffinity
{
    /// <summary>Creates one attempt-scoped affinity.</summary>
    /// <param name="slot">Stable Process node that declares the affinity slot.</param>
    /// <param name="value">Concrete portable affinity value.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="slot"/> is default or <paramref name="value"/> is not concrete.
    /// </exception>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    [JsonConstructor]
    public ProcessAttemptAffinity(ExecutionNodeId slot, PortableValue value)
    {
        if (string.IsNullOrWhiteSpace(slot.Value))
        {
            throw new ArgumentException("An attempt affinity requires a stable Process slot.", nameof(slot));
        }

        ArgumentNullException.ThrowIfNull(value);
        if (value.State != PortableValueState.Concrete)
        {
            throw new ArgumentException("An attempt affinity requires a concrete portable value.", nameof(value));
        }

        Slot = slot;
        Value = value;
    }

    /// <summary>Stable Process node declaring this affinity slot.</summary>
    public ExecutionNodeId Slot { get; }

    /// <summary>Concrete portable attempt-scoped affinity value.</summary>
    public PortableValue Value { get; }
}

/// <summary>Explicit cleanup obligation attached to an abandoned or forcibly terminated attempt.</summary>
public enum ProcessAttemptCleanupRequirement
{
    /// <summary>No cleanup decision was supplied; invalid for restart or termination.</summary>
    Unspecified = 0,

    /// <summary>Retain durable evidence; no owned external resource release is required.</summary>
    RetainEvidence = 1,

    /// <summary>Release resources owned by the attempt while retaining durable evidence.</summary>
    ReleaseAttemptResources = 2,

    /// <summary>Abandon attempt affinities and release attempt-owned resources while retaining evidence.</summary>
    AbandonAffinitiesAndReleaseResources = 3
}

/// <summary>Stable restart decision and explicit old-attempt cleanup plan.</summary>
public sealed record ProcessAttemptRestartPlan
{
    /// <summary>Creates one deterministic attempt-restart plan.</summary>
    /// <param name="newAttemptId">Stable replacement attempt identity selected once by the caller.</param>
    /// <param name="cleanup">Explicit cleanup obligation for the abandoned attempt.</param>
    /// <param name="reason">Typed reason for abandoning the prior attempt.</param>
    /// <exception cref="ArgumentException"><paramref name="newAttemptId"/> is default.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="cleanup"/> is unspecified or unsupported.
    /// </exception>
    /// <exception cref="ArgumentNullException"><paramref name="reason"/> is <see langword="null"/>.</exception>
    [JsonConstructor]
    public ProcessAttemptRestartPlan(
        ProcessAttemptId newAttemptId,
        ProcessAttemptCleanupRequirement cleanup,
        ProcessControlReason reason)
    {
        if (string.IsNullOrWhiteSpace(newAttemptId.Value))
        {
            throw new ArgumentException("A restart plan requires a stable replacement attempt.", nameof(newAttemptId));
        }

        if (!Enum.IsDefined(cleanup) || cleanup == ProcessAttemptCleanupRequirement.Unspecified)
        {
            throw new ArgumentOutOfRangeException(nameof(cleanup), cleanup, "Attempt cleanup must be explicit.");
        }

        NewAttemptId = newAttemptId;
        Cleanup = cleanup;
        Reason = Guard.RequireNotNull(reason);
    }

    /// <summary>Stable replacement attempt selected by the restart decision.</summary>
    public ProcessAttemptId NewAttemptId { get; }

    /// <summary>Explicit cleanup obligation for the abandoned attempt.</summary>
    public ProcessAttemptCleanupRequirement Cleanup { get; }

    /// <summary>Typed reason for abandoning the prior attempt.</summary>
    public ProcessControlReason Reason { get; }
}

/// <summary>Closed versioned family of canonical Process lifecycle commands.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = ExecutionControlWireNames.CommandDiscriminator)]
[JsonDerivedType(typeof(InspectProcessCommand), ExecutionControlWireNames.Inspect)]
[JsonDerivedType(typeof(SignalProcessCommand), ExecutionControlWireNames.Signal)]
[JsonDerivedType(typeof(PauseProcessCommand), ExecutionControlWireNames.Pause)]
[JsonDerivedType(typeof(ContinueProcessCommand), ExecutionControlWireNames.Continue)]
[JsonDerivedType(typeof(RestartProcessAttemptCommand), ExecutionControlWireNames.RestartAttempt)]
[JsonDerivedType(typeof(CancelProcessCommand), ExecutionControlWireNames.Cancel)]
[JsonDerivedType(typeof(TerminateProcessCommand), ExecutionControlWireNames.Terminate)]
public abstract record ProcessControlCommand
{
    /// <summary>Current canonical Process-control command schema version.</summary>
    public static ExecutionIrSchemaVersion CurrentSchemaVersion { get; } =
        new("cohesive-process-control-command/v1");

    private protected ProcessControlCommand(
        ExecutionIrSchemaVersion schemaVersion,
        ProcessControlCommandContext context,
        ProcessControlExpectation? expectation,
        bool expectationRequired)
    {
        if (string.IsNullOrWhiteSpace(schemaVersion.Value))
        {
            throw new ArgumentException("A Process-control command requires an exact schema version.", nameof(schemaVersion));
        }

        Context = Guard.RequireNotNull(context);
        if (expectationRequired && expectation is null)
        {
            throw new ArgumentNullException(nameof(expectation), "A mutating Process-control command requires an expectation.");
        }

        if (expectation is not null
            && expectation.Continuation.ProcessInstanceId != context.ProcessInstanceId)
        {
            throw new ArgumentException(
                "A Process-control expectation must address the command's Process instance.",
                nameof(expectation));
        }

        SchemaVersion = schemaVersion;
        Expectation = expectation;
    }

    /// <summary>Exact Process-control command schema version.</summary>
    public ExecutionIrSchemaVersion SchemaVersion { get; }

    /// <summary>Stable identity, authority, target, time, and provenance shared by the command.</summary>
    public ProcessControlCommandContext Context { get; }

    /// <summary>Optional exact attempt and revision expectation; mutating commands always carry one.</summary>
    public ProcessControlExpectation? Expectation { get; }

    internal abstract void EnsureDeclaredVariant();
}

/// <summary>Read-only inspection of current Process control state.</summary>
public sealed record InspectProcessCommand : ProcessControlCommand
{
    internal override void EnsureDeclaredVariant() { }

    /// <summary>Creates an Inspect command.</summary>
    /// <param name="schemaVersion">Exact Process-control command schema version.</param>
    /// <param name="context">Stable command context.</param>
    /// <param name="expectation">Optional conditional attempt and control revision.</param>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="schemaVersion"/> is default or <paramref name="expectation"/> addresses another instance.
    /// </exception>
    [JsonConstructor]
    public InspectProcessCommand(
        ExecutionIrSchemaVersion schemaVersion,
        ProcessControlCommandContext context,
        ProcessControlExpectation? expectation = null)
        : base(schemaVersion, context, expectation, expectationRequired: false)
    {
    }
}

/// <summary>Admission of one already-canonical Signal into the targeted Process attempt.</summary>
public sealed record SignalProcessCommand : ProcessControlCommand
{
    internal override void EnsureDeclaredVariant() { }

    /// <summary>Creates a Signal lifecycle command.</summary>
    /// <param name="schemaVersion">Exact Process-control command schema version.</param>
    /// <param name="context">Stable command context.</param>
    /// <param name="expectation">Exact current attempt and control revision.</param>
    /// <param name="signal">Canonical typed Signal envelope to admit.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="context"/>, <paramref name="expectation"/>, or <paramref name="signal"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="schemaVersion"/> is default or the expectation addresses another instance.
    /// </exception>
    [JsonConstructor]
    public SignalProcessCommand(
        ExecutionIrSchemaVersion schemaVersion,
        ProcessControlCommandContext context,
        ProcessControlExpectation expectation,
        SignalEnvelope signal)
        : base(schemaVersion, context, expectation, expectationRequired: true) =>
        Signal = Guard.RequireNotNull(signal);

    /// <summary>Canonical typed Signal submitted for durable admission.</summary>
    public SignalEnvelope Signal { get; }
}

/// <summary>Request to stop ordinary Process work at an invariant-preserving safe point.</summary>
public sealed record PauseProcessCommand : ProcessControlCommand
{
    internal override void EnsureDeclaredVariant() { }

    /// <summary>Creates a Pause command.</summary>
    /// <param name="schemaVersion">Exact Process-control command schema version.</param>
    /// <param name="context">Stable command context.</param>
    /// <param name="expectation">Exact current attempt and control revision.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="context"/> or <paramref name="expectation"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="schemaVersion"/> is default or the expectation addresses another instance.
    /// </exception>
    [JsonConstructor]
    public PauseProcessCommand(
        ExecutionIrSchemaVersion schemaVersion,
        ProcessControlCommandContext context,
        ProcessControlExpectation expectation)
        : base(schemaVersion, context, expectation, expectationRequired: true)
    {
    }
}

/// <summary>Request to resume a paused Process without replacing its attempt.</summary>
public sealed record ContinueProcessCommand : ProcessControlCommand
{
    internal override void EnsureDeclaredVariant() { }

    /// <summary>Creates a Continue command.</summary>
    /// <param name="schemaVersion">Exact Process-control command schema version.</param>
    /// <param name="context">Stable command context.</param>
    /// <param name="expectation">Exact paused attempt and control revision.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="context"/> or <paramref name="expectation"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="schemaVersion"/> is default or the expectation addresses another instance.
    /// </exception>
    [JsonConstructor]
    public ContinueProcessCommand(
        ExecutionIrSchemaVersion schemaVersion,
        ProcessControlCommandContext context,
        ProcessControlExpectation expectation)
        : base(schemaVersion, context, expectation, expectationRequired: true)
    {
    }
}

/// <summary>Explicit abandonment of the current attempt and creation of one stable replacement attempt.</summary>
public sealed record RestartProcessAttemptCommand : ProcessControlCommand
{
    internal override void EnsureDeclaredVariant() { }

    /// <summary>Creates a RestartAttempt command.</summary>
    /// <param name="schemaVersion">Exact Process-control command schema version.</param>
    /// <param name="context">Stable command context.</param>
    /// <param name="expectation">Exact current attempt and control revision.</param>
    /// <param name="plan">Stable replacement attempt and cleanup decision.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="context"/>, <paramref name="expectation"/>, or <paramref name="plan"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="schemaVersion"/> is default or the expectation addresses another instance.
    /// </exception>
    [JsonConstructor]
    public RestartProcessAttemptCommand(
        ExecutionIrSchemaVersion schemaVersion,
        ProcessControlCommandContext context,
        ProcessControlExpectation expectation,
        ProcessAttemptRestartPlan plan)
        : base(schemaVersion, context, expectation, expectationRequired: true) =>
        Plan = Guard.RequireNotNull(plan);

    /// <summary>Stable replacement attempt and explicit old-attempt cleanup decision.</summary>
    public ProcessAttemptRestartPlan Plan { get; }
}

/// <summary>Cooperative semantic cancellation applied at an invariant-preserving safe point.</summary>
public sealed record CancelProcessCommand : ProcessControlCommand
{
    internal override void EnsureDeclaredVariant() { }

    /// <summary>Creates a Cancel command.</summary>
    /// <param name="schemaVersion">Exact Process-control command schema version.</param>
    /// <param name="context">Stable command context.</param>
    /// <param name="expectation">Exact current attempt and control revision.</param>
    /// <param name="reason">Typed semantic cancellation reason.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="context"/>, <paramref name="expectation"/>, or <paramref name="reason"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="schemaVersion"/> is default or the expectation addresses another instance.
    /// </exception>
    [JsonConstructor]
    public CancelProcessCommand(
        ExecutionIrSchemaVersion schemaVersion,
        ProcessControlCommandContext context,
        ProcessControlExpectation expectation,
        ProcessControlReason reason)
        : base(schemaVersion, context, expectation, expectationRequired: true) =>
        Reason = Guard.RequireNotNull(reason);

    /// <summary>Typed semantic cancellation reason.</summary>
    public ProcessControlReason Reason { get; }
}

/// <summary>Immediate irreversible stop distinct from cooperative semantic cancellation.</summary>
public sealed record TerminateProcessCommand : ProcessControlCommand
{
    internal override void EnsureDeclaredVariant() { }

    /// <summary>Creates a Terminate command.</summary>
    /// <param name="schemaVersion">Exact Process-control command schema version.</param>
    /// <param name="context">Stable command context.</param>
    /// <param name="expectation">Exact current attempt and control revision.</param>
    /// <param name="reason">Typed termination reason.</param>
    /// <param name="cleanup">Explicit forced-stop cleanup obligation.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="context"/>, <paramref name="expectation"/>, or <paramref name="reason"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="schemaVersion"/> is default or the expectation addresses another instance.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="cleanup"/> is unspecified or unsupported.
    /// </exception>
    [JsonConstructor]
    public TerminateProcessCommand(
        ExecutionIrSchemaVersion schemaVersion,
        ProcessControlCommandContext context,
        ProcessControlExpectation expectation,
        ProcessControlReason reason,
        ProcessAttemptCleanupRequirement cleanup)
        : base(schemaVersion, context, expectation, expectationRequired: true)
    {
        if (!Enum.IsDefined(cleanup) || cleanup == ProcessAttemptCleanupRequirement.Unspecified)
        {
            throw new ArgumentOutOfRangeException(nameof(cleanup), cleanup, "Termination cleanup must be explicit.");
        }

        Reason = Guard.RequireNotNull(reason);
        Cleanup = cleanup;
    }

    /// <summary>Typed forced-termination reason.</summary>
    public ProcessControlReason Reason { get; }

    /// <summary>Explicit forced-stop cleanup obligation.</summary>
    public ProcessAttemptCleanupRequirement Cleanup { get; }
}

/// <summary>Stable observation that an activation has begun under an exact control fence.</summary>
public sealed record ProcessActivationStartObservation
{
    /// <summary>Creates an activation-start observation.</summary>
    /// <param name="expectation">Exact attempt and control revision observed before activation.</param>
    /// <param name="activationId">Stable finite activation identity.</param>
    /// <param name="observedAtUtc">Explicit UTC activation-start observation.</param>
    /// <exception cref="ArgumentNullException"><paramref name="expectation"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="activationId"/> is default or <paramref name="observedAtUtc"/> is not UTC.
    /// </exception>
    [JsonConstructor]
    public ProcessActivationStartObservation(
        ProcessControlExpectation expectation,
        ActivationId activationId,
        DateTimeOffset observedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(activationId.Value))
        {
            throw new ArgumentException("An activation-start observation requires a stable activation.", nameof(activationId));
        }

        ExecutionObservationRequirements.RequireUtc(observedAtUtc, nameof(observedAtUtc));

        Expectation = Guard.RequireNotNull(expectation);
        ActivationId = activationId;
        ObservedAtUtc = observedAtUtc;
    }

    /// <summary>Exact attempt and control revision observed before activation.</summary>
    public ProcessControlExpectation Expectation { get; }

    /// <summary>Stable finite activation identity.</summary>
    public ActivationId ActivationId { get; }

    /// <summary>Explicit UTC activation-start observation.</summary>
    public DateTimeOffset ObservedAtUtc { get; }
}

/// <summary>Invariant-preserving durable-cut observation for one finite Process activation.</summary>
public sealed record ProcessSafePointObservation
{
    /// <summary>Creates a Process safe-point observation.</summary>
    /// <param name="safePointId">Stable identity of this durable cut.</param>
    /// <param name="expectation">Exact attempt and current control revision.</param>
    /// <param name="activationId">Activation completed at the safe point.</param>
    /// <param name="node">Stable Process node at which the durable cut was reached.</param>
    /// <param name="observedAtUtc">Explicit UTC safe-point observation.</param>
    /// <exception cref="ArgumentNullException"><paramref name="expectation"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// An identity is default or <paramref name="observedAtUtc"/> is not UTC.
    /// </exception>
    [JsonConstructor]
    public ProcessSafePointObservation(
        ProcessSafePointId safePointId,
        ProcessControlExpectation expectation,
        ActivationId activationId,
        ExecutionNodeId node,
        DateTimeOffset observedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(safePointId.Value))
        {
            throw new ArgumentException("A safe-point observation requires a stable identity.", nameof(safePointId));
        }

        if (string.IsNullOrWhiteSpace(activationId.Value))
        {
            throw new ArgumentException("A safe-point observation requires its completed activation.", nameof(activationId));
        }

        if (string.IsNullOrWhiteSpace(node.Value))
        {
            throw new ArgumentException("A safe-point observation requires a stable Process node.", nameof(node));
        }

        ExecutionObservationRequirements.RequireUtc(observedAtUtc, nameof(observedAtUtc));

        SafePointId = safePointId;
        Expectation = Guard.RequireNotNull(expectation);
        ActivationId = activationId;
        Node = node;
        ObservedAtUtc = observedAtUtc;
    }

    /// <summary>Stable identity of this invariant-preserving durable cut.</summary>
    public ProcessSafePointId SafePointId { get; }

    /// <summary>Exact attempt and control revision observed at the durable cut.</summary>
    public ProcessControlExpectation Expectation { get; }

    /// <summary>Finite activation completed at the durable cut.</summary>
    public ActivationId ActivationId { get; }

    /// <summary>Stable Process node at which the durable cut was reached.</summary>
    public ExecutionNodeId Node { get; }

    /// <summary>Explicit UTC safe-point observation.</summary>
    public DateTimeOffset ObservedAtUtc { get; }
}

/// <summary>Write-once attempt-affinity observation under an exact control fence.</summary>
public sealed record ProcessAttemptAffinityObservation
{
    /// <summary>Creates an attempt-affinity observation.</summary>
    /// <param name="expectation">Exact current attempt and control revision.</param>
    /// <param name="affinity">Concrete affinity to bind once to the attempt.</param>
    /// <param name="observedAtUtc">Explicit UTC affinity observation.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="expectation"/> or <paramref name="affinity"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="observedAtUtc"/> is not UTC.</exception>
    [JsonConstructor]
    public ProcessAttemptAffinityObservation(
        ProcessControlExpectation expectation,
        ProcessAttemptAffinity affinity,
        DateTimeOffset observedAtUtc)
    {
        ExecutionObservationRequirements.RequireUtc(observedAtUtc, nameof(observedAtUtc));
        Expectation = Guard.RequireNotNull(expectation);
        Affinity = Guard.RequireNotNull(affinity);
        ObservedAtUtc = observedAtUtc;
    }

    /// <summary>Exact current attempt and control revision.</summary>
    public ProcessControlExpectation Expectation { get; }

    /// <summary>Concrete write-once attempt affinity.</summary>
    public ProcessAttemptAffinity Affinity { get; }

    /// <summary>Explicit UTC affinity observation.</summary>
    public DateTimeOffset ObservedAtUtc { get; }
}
