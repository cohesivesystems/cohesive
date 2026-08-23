using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Cohesive.Execution;
using Cohesive.Model.Serialization;

namespace Cohesive.Processes.Execution;

/// <summary>Closed cause of one finite canonical Process activation.</summary>
public enum ProcessActivationCause
{
    /// <summary>No cause was supplied; invalid for activation.</summary>
    Unspecified = 0,

    /// <summary>The Process was newly admitted.</summary>
    Start = 1,

    /// <summary>An explicit durable continuation was resumed.</summary>
    Continue = 2,

    /// <summary>One or more canonical interactions were presented.</summary>
    Interaction = 3,

    /// <summary>An explicit observation of time may satisfy a registered timer.</summary>
    Timer = 4,

    /// <summary>Lifecycle control caused the activation.</summary>
    Control = 5,

    /// <summary>Durable state was recovered after interruption.</summary>
    Recovery = 6,

    /// <summary>A prior finite activation is being deterministically retried.</summary>
    Retry = 7
}

/// <summary>Explicit non-ambient context used to construct Process interaction emissions.</summary>
public sealed record ProcessActivationContext
{
    /// <summary>Creates emission context for one finite activation.</summary>
    /// <param name="authorityScope">Authority and optional tenant boundary.</param>
    /// <param name="correlationId">Stable correlation identity shared by causally related interactions.</param>
    /// <param name="delivery">Durability and visibility demanded of emitted interactions.</param>
    /// <param name="provenance">Attributable producer and semantic source evidence.</param>
    /// <param name="causationId">Optional interaction that directly caused this activation.</param>
    /// <param name="ordering">Optional explicit interaction ordering declaration.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="authorityScope"/>, <paramref name="delivery"/>, or <paramref name="provenance"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="correlationId"/> or a present <paramref name="causationId"/> is default.
    /// </exception>
    public ProcessActivationContext(
        InteractionAuthorityScope authorityScope,
        InteractionCorrelationId correlationId,
        InteractionDeliveryRequirements delivery,
        ExecutionProvenance provenance,
        EmissionId? causationId = null,
        InteractionOrdering? ordering = null)
    {
        if (string.IsNullOrWhiteSpace(correlationId.Value))
            throw new ArgumentException("An activation context requires a stable correlation identity.", nameof(correlationId));
        if (causationId is { } cause && string.IsNullOrWhiteSpace(cause.Value))
            throw new ArgumentException("A present causation identity cannot be default.", nameof(causationId));

        AuthorityScope = authorityScope ?? throw new ArgumentNullException(nameof(authorityScope));
        CorrelationId = correlationId;
        Delivery = delivery ?? throw new ArgumentNullException(nameof(delivery));
        Provenance = provenance ?? throw new ArgumentNullException(nameof(provenance));
        CausationId = causationId;
        Ordering = ordering;
    }

    /// <summary>Authority and optional tenant boundary.</summary>
    public InteractionAuthorityScope AuthorityScope { get; }

    /// <summary>Stable correlation identity.</summary>
    public InteractionCorrelationId CorrelationId { get; }

    /// <summary>Durability and visibility demanded of emitted interactions.</summary>
    public InteractionDeliveryRequirements Delivery { get; }

    /// <summary>Attributable producer and semantic source evidence.</summary>
    public ExecutionProvenance Provenance { get; }

    /// <summary>Optional interaction that directly caused this activation.</summary>
    public EmissionId? CausationId { get; }

    /// <summary>Optional explicit interaction ordering declaration.</summary>
    public InteractionOrdering? Ordering { get; }
}

/// <summary>One canonical interaction presented to an exact Process token.</summary>
public sealed record ProcessActivationInput
{
    /// <summary>Creates a token-addressed activation input.</summary>
    /// <param name="target">Exact Process instance, attempt, and token address.</param>
    /// <param name="envelope">Canonical interaction evidence.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="target"/> or <paramref name="envelope"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// A Signal envelope's canonical target differs from <paramref name="target"/>.
    /// </exception>
    public ProcessActivationInput(
        ProcessTokenInteractionTarget target,
        InteractionEnvelope envelope)
    {
        Target = target ?? throw new ArgumentNullException(nameof(target));
        Envelope = envelope ?? throw new ArgumentNullException(nameof(envelope));
        if (envelope is SignalEnvelope signal && signal.Target != target)
        {
            throw new ArgumentException(
                "The presented target must equal the Signal envelope's canonical target.",
                nameof(target));
        }
    }

    /// <summary>Exact Process token address.</summary>
    public ProcessTokenInteractionTarget Target { get; }

    /// <summary>Canonical interaction evidence.</summary>
    public InteractionEnvelope Envelope { get; }
}

/// <summary>One attributable effective parallelism point for bounded Process-work admission.</summary>
/// <remarks>
/// This value is explicit activation evidence, not a second Process definition or scheduler. Interpreters retain an
/// applied point on each affected occurrence so replay does not invoke a controller or reinterpret committed
/// admission decisions.
/// </remarks>
public sealed record ProcessAdmissionOperatingPoint
{
    /// <summary>Stable authority used for convention-derived canonical admission.</summary>
    public const string CanonicalAuthority = "cohesive.processes/canonical-admission";

    /// <summary>Creates one effective admission operating point.</summary>
    /// <param name="node">Canonical bounded-work node to which the point applies.</param>
    /// <param name="maximumParallelism">Effective positive simultaneous-work limit.</param>
    /// <param name="revision">Non-negative monotonic revision within <paramref name="authority"/>.</param>
    /// <param name="authority">Stable identity and version of the compiler, controller, or runtime authority.</param>
    /// <param name="evidenceReference">Stable reference to the exact policy, actuation, or convention evidence.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="node"/>, <paramref name="authority"/>, or <paramref name="evidenceReference"/> is empty.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="maximumParallelism"/> is not positive or <paramref name="revision"/> is negative.
    /// </exception>
    [JsonConstructor]
    public ProcessAdmissionOperatingPoint(
        ExecutionNodeId node,
        int maximumParallelism,
        long revision,
        string authority,
        string evidenceReference)
    {
        if (string.IsNullOrWhiteSpace(node.Value))
            throw new ArgumentException("An admission operating point requires a stable Process node.", nameof(node));
        if (maximumParallelism <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumParallelism),
                maximumParallelism,
                "Effective admission parallelism must be positive.");
        }
        if (revision < 0)
            throw new ArgumentOutOfRangeException(nameof(revision), revision, "Admission revision cannot be negative.");
        if (string.IsNullOrWhiteSpace(authority))
            throw new ArgumentException("An admission operating point requires a stable authority.", nameof(authority));
        if (string.IsNullOrWhiteSpace(evidenceReference))
        {
            throw new ArgumentException(
                "An admission operating point requires stable evidence.",
                nameof(evidenceReference));
        }

        Node = node;
        MaximumParallelism = maximumParallelism;
        Revision = revision;
        Authority = authority;
        EvidenceReference = evidenceReference;
    }

    /// <summary>Canonical bounded-work node to which the point applies.</summary>
    public ExecutionNodeId Node { get; }

    /// <summary>Effective simultaneous-work limit.</summary>
    public int MaximumParallelism { get; }

    /// <summary>Monotonic authority-local revision.</summary>
    [JsonConverter(typeof(StringEncodedInt64JsonConverter))]
    public long Revision { get; }

    /// <summary>Stable compiler, controller, or runtime authority.</summary>
    public string Authority { get; }

    /// <summary>Stable reference to the exact source evidence.</summary>
    public string EvidenceReference { get; }

    /// <summary>Creates the convention-derived operating point equal to a canonical hard maximum.</summary>
    /// <param name="node">Canonical bounded-work node.</param>
    /// <param name="maximumParallelism">Canonical hard maximum.</param>
    /// <param name="evidenceReference">Stable canonical-definition source reference.</param>
    /// <returns>A revision-zero convention-derived operating point.</returns>
    public static ProcessAdmissionOperatingPoint Canonical(
        ExecutionNodeId node,
        int maximumParallelism,
        string evidenceReference) => new(
        node,
        maximumParallelism,
        revision: 0,
        CanonicalAuthority,
        evidenceReference);
}

/// <summary>Explicit evidence supplied to one finite Process activation.</summary>
public sealed record ProcessActivation
{
    /// <summary>Creates a finite activation request without propagated-child closure observations.</summary>
    public ProcessActivation(
        ActivationId id,
        ProcessActivationCause cause,
        DateTimeOffset observedAtUtc,
        ProcessActivationContext context,
        ImmutableArray<ProcessActivationInput> inputs = default,
        ProcessCancellationIntent? cancellation = null,
        ImmutableArray<ProcessAdmissionOperatingPoint> admissionOperatingPoints = default)
        : this(
            id,
            cause,
            observedAtUtc,
            context,
            inputs,
            cancellation,
            admissionOperatingPoints,
            childCancellationClosures: default)
    {
    }

    /// <summary>Creates a finite activation request.</summary>
    /// <param name="id">Caller-assigned stable activation identity.</param>
    /// <param name="cause">Closed activation cause.</param>
    /// <param name="observedAtUtc">Explicit UTC observation time used for timer eligibility.</param>
    /// <param name="context">Explicit context for interaction emissions.</param>
    /// <param name="inputs">Canonical token-addressed interactions presented to this activation.</param>
    /// <param name="cancellation">Optional previously accepted cancellation intent, observed only at a safe point.</param>
    /// <param name="admissionOperatingPoints">
    /// Optional attributable effective admission points selected within canonical hard bounds.
    /// </param>
    /// <param name="childCancellationClosures">
    /// Optional exact closure observations for previously emitted propagated child-cancellation intents.
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="id"/> is default, <paramref name="observedAtUtc"/> is not UTC, or an input is null.
    /// </exception>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="cause"/> is unspecified or unsupported.</exception>
    [JsonConstructor]
    public ProcessActivation(
        ActivationId id,
        ProcessActivationCause cause,
        DateTimeOffset observedAtUtc,
        ProcessActivationContext context,
        ImmutableArray<ProcessActivationInput> inputs,
        ProcessCancellationIntent? cancellation,
        ImmutableArray<ProcessAdmissionOperatingPoint> admissionOperatingPoints,
        ImmutableArray<ProcessChildCancellationClosure> childCancellationClosures)
    {
        if (string.IsNullOrWhiteSpace(id.Value))
            throw new ArgumentException("A Process activation requires a stable identity.", nameof(id));
        if (!Enum.IsDefined(cause) || cause == ProcessActivationCause.Unspecified)
            throw new ArgumentOutOfRangeException(nameof(cause), cause, "A Process activation cause must be explicit.");
        if (observedAtUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("Activation observation time must use the UTC offset.", nameof(observedAtUtc));

        var normalized = inputs.IsDefault ? [] : inputs;
        if (normalized.Any(static input => input is null))
            throw new ArgumentException("Activation inputs cannot contain null entries.", nameof(inputs));
        var normalizedAdmission = admissionOperatingPoints.IsDefault
            ? []
            : admissionOperatingPoints;
        if (normalizedAdmission.Any(static point => point is null))
        {
            throw new ArgumentException(
                "Activation admission operating points cannot contain null entries.",
                nameof(admissionOperatingPoints));
        }
        if (normalizedAdmission.GroupBy(static point => point.Node).Any(static group => group.Count() > 1))
        {
            throw new ArgumentException(
                "An activation cannot repeat a bounded-work admission node.",
                nameof(admissionOperatingPoints));
        }
        var normalizedClosures = childCancellationClosures.IsDefault ? [] : childCancellationClosures;
        if (normalizedClosures.Any(static closure => closure is null))
        {
            throw new ArgumentException(
                "Child cancellation-closure observations cannot contain null entries.",
                nameof(childCancellationClosures));
        }
        if (normalizedClosures.GroupBy(static closure => closure.IntentId, StringComparer.Ordinal)
            .Any(static group => group.Distinct().Count() > 1))
        {
            throw new ArgumentException(
                "One activation cannot present conflicting closure evidence for a child cancellation intent.",
                nameof(childCancellationClosures));
        }

        Id = id;
        Cause = cause;
        ObservedAtUtc = observedAtUtc;
        Context = context ?? throw new ArgumentNullException(nameof(context));
        Inputs = normalized;
        Cancellation = cancellation;
        AdmissionOperatingPoints = [.. normalizedAdmission.OrderBy(static point => point.Node.Value, StringComparer.Ordinal)];
        ChildCancellationClosures = [.. normalizedClosures
            .Distinct()
            .OrderBy(static closure => closure.IntentId, StringComparer.Ordinal)];
    }

    /// <summary>Caller-assigned stable activation identity.</summary>
    public ActivationId Id { get; }

    /// <summary>Closed activation cause.</summary>
    public ProcessActivationCause Cause { get; }

    /// <summary>Explicit UTC observation time.</summary>
    public DateTimeOffset ObservedAtUtc { get; }

    /// <summary>Explicit interaction emission context.</summary>
    public ProcessActivationContext Context { get; }

    /// <summary>Presented canonical interactions.</summary>
    public ImmutableArray<ProcessActivationInput> Inputs { get; }

    /// <summary>Optional accepted cancellation intent observed only at an activation safe point.</summary>
    public ProcessCancellationIntent? Cancellation { get; }

    /// <summary>Attributable effective admission points in stable node-identity order.</summary>
    public ImmutableArray<ProcessAdmissionOperatingPoint> AdmissionOperatingPoints { get; }

    /// <summary>Exact propagated child-cancellation closures in stable intent-identity order.</summary>
    public ImmutableArray<ProcessChildCancellationClosure> ChildCancellationClosures { get; }
}
