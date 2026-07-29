using System.Collections.Immutable;
using Cohesive.Execution;

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

/// <summary>Explicit evidence supplied to one finite Process activation.</summary>
public sealed record ProcessActivation
{
    /// <summary>Creates a finite activation request.</summary>
    /// <param name="id">Caller-assigned stable activation identity.</param>
    /// <param name="cause">Closed activation cause.</param>
    /// <param name="observedAtUtc">Explicit UTC observation time used for timer eligibility.</param>
    /// <param name="context">Explicit context for interaction emissions.</param>
    /// <param name="inputs">Canonical token-addressed interactions presented to this activation.</param>
    /// <param name="cancellation">Optional previously accepted cancellation intent, observed only at a safe point.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="id"/> is default, <paramref name="observedAtUtc"/> is not UTC, or an input is null.
    /// </exception>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="cause"/> is unspecified or unsupported.</exception>
    public ProcessActivation(
        ActivationId id,
        ProcessActivationCause cause,
        DateTimeOffset observedAtUtc,
        ProcessActivationContext context,
        ImmutableArray<ProcessActivationInput> inputs = default,
        ProcessCancellationIntent? cancellation = null)
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

        Id = id;
        Cause = cause;
        ObservedAtUtc = observedAtUtc;
        Context = context ?? throw new ArgumentNullException(nameof(context));
        Inputs = normalized;
        Cancellation = cancellation;
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
}
