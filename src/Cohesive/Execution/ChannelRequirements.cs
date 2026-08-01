using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Cohesive.Execution;

/// <summary>Closed provider-neutral algebra of observable Channel requirements.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = ChannelWireNames.RequirementDiscriminator)]
[JsonDerivedType(typeof(ChannelTopologyRequirement), ChannelWireNames.TopologyRequirement)]
[JsonDerivedType(typeof(ChannelRoutingRequirement), ChannelWireNames.RoutingRequirement)]
[JsonDerivedType(typeof(ChannelFramingRequirement), ChannelWireNames.FramingRequirement)]
[JsonDerivedType(typeof(ChannelPersistenceRequirement), ChannelWireNames.PersistenceRequirement)]
[JsonDerivedType(typeof(ChannelProgressRequirement), ChannelWireNames.ProgressRequirement)]
[JsonDerivedType(typeof(ChannelDeliveryRequirement), ChannelWireNames.DeliveryRequirement)]
[JsonDerivedType(typeof(ChannelReliabilityRequirement), ChannelWireNames.ReliabilityRequirement)]
[JsonDerivedType(typeof(ChannelSettlementRequirement), ChannelWireNames.SettlementRequirement)]
[JsonDerivedType(typeof(ChannelFlowRequirement), ChannelWireNames.FlowRequirement)]
[JsonDerivedType(typeof(ChannelAtomicityRequirement), ChannelWireNames.AtomicityRequirement)]
[JsonDerivedType(typeof(ChannelSecurityRequirement), ChannelWireNames.SecurityRequirement)]
[JsonDerivedType(typeof(ChannelLimitRequirement), ChannelWireNames.LimitRequirement)]
public abstract record ChannelRequirement
{
    /// <summary>Creates one scoped canonical Channel requirement.</summary>
    /// <param name="id">Stable definition-local requirement identity.</param>
    /// <param name="scope">Logical exchange or direction scope.</param>
    /// <exception cref="ArgumentException"><paramref name="id"/> is default.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="scope"/> is <see langword="null"/>.</exception>
    private protected ChannelRequirement(ChannelRequirementId id, ChannelRequirementScope scope)
    {
        if (string.IsNullOrWhiteSpace(id.Value))
            throw new ArgumentException("A Channel requirement requires a stable identity.", nameof(id));

        Id = id;
        Scope = Guard.RequireNotNull(scope);
    }

    /// <summary>Stable definition-local requirement identity.</summary>
    public ChannelRequirementId Id { get; }

    /// <summary>Logical exchange or direction scope.</summary>
    public ChannelRequirementScope Scope { get; }

    internal abstract string WireName { get; }

    internal abstract void EnsureDeclaredVariant();
}

/// <summary>Distribution and interaction shape required of a logical exchange.</summary>
public sealed record ChannelTopologyRequirement : ChannelRequirement
{
    internal override string WireName => ChannelWireNames.TopologyRequirement;
    internal override void EnsureDeclaredVariant() { }

    /// <summary>Creates a Channel topology requirement.</summary>
    /// <param name="id">Stable requirement identity.</param>
    /// <param name="scope">Exchange-wide requirement scope.</param>
    /// <param name="distribution">Required consumer distribution.</param>
    /// <param name="interaction">Required protocol-neutral interaction shape.</param>
    /// <exception cref="ArgumentNullException"><paramref name="scope"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="id"/> is default.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="distribution"/> or <paramref name="interaction"/> is unsupported.
    /// </exception>
    [JsonConstructor]
    public ChannelTopologyRequirement(
        ChannelRequirementId id,
        ChannelRequirementScope scope,
        ChannelDistributionKind distribution,
        ChannelInteractionShape interaction)
        : base(id, scope)
    {
        RequireDefined(distribution, nameof(distribution));
        RequireDefined(interaction, nameof(interaction));
        Distribution = distribution;
        Interaction = interaction;
    }

    /// <summary>Required consumer distribution.</summary>
    public ChannelDistributionKind Distribution { get; }

    /// <summary>Required protocol-neutral interaction shape.</summary>
    public ChannelInteractionShape Interaction { get; }

    internal static void RequireDefined<TEnum>(TEnum value, string parameterName)
        where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
            throw new ArgumentOutOfRangeException(parameterName, value, $"Unsupported {typeof(TEnum).Name} value.");
    }
}

/// <summary>Semantic routing form and destructive-acquisition isolation requirement.</summary>
public sealed record ChannelRoutingRequirement : ChannelRequirement
{
    internal override string WireName => ChannelWireNames.RoutingRequirement;
    internal override void EnsureDeclaredVariant() { }

    /// <summary>Creates a Channel routing requirement.</summary>
    /// <param name="id">Stable requirement identity.</param>
    /// <param name="scope">Logical exchange or direction scope.</param>
    /// <param name="routing">Provider-neutral routing form.</param>
    /// <param name="isolation">Required acquisition-isolation proof.</param>
    /// <exception cref="ArgumentNullException"><paramref name="scope"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="id"/> is default.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="routing"/> or <paramref name="isolation"/> is unsupported.
    /// </exception>
    [JsonConstructor]
    public ChannelRoutingRequirement(
        ChannelRequirementId id,
        ChannelRequirementScope scope,
        ChannelRoutingKind routing,
        ChannelRoutingIsolationKind isolation)
        : base(id, scope)
    {
        ChannelTopologyRequirement.RequireDefined(routing, nameof(routing));
        ChannelTopologyRequirement.RequireDefined(isolation, nameof(isolation));
        Routing = routing;
        Isolation = isolation;
    }

    /// <summary>Provider-neutral routing form.</summary>
    public ChannelRoutingKind Routing { get; }

    /// <summary>Required destructive-acquisition isolation proof.</summary>
    public ChannelRoutingIsolationKind Isolation { get; }
}

/// <summary>Application framing and boundary-preservation requirement.</summary>
public sealed record ChannelFramingRequirement : ChannelRequirement
{
    internal override string WireName => ChannelWireNames.FramingRequirement;
    internal override void EnsureDeclaredVariant() { }

    /// <summary>Creates a Channel framing requirement.</summary>
    /// <param name="id">Stable requirement identity.</param>
    /// <param name="scope">Logical exchange or direction scope.</param>
    /// <param name="framing">Typed-message, frame, datagram, or byte-stream form.</param>
    /// <param name="boundaries">Required application-boundary semantics.</param>
    /// <param name="codec">Optional stable codec binding used to reconstruct boundaries.</param>
    /// <exception cref="ArgumentNullException"><paramref name="scope"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="id"/> is default, <paramref name="codec"/> is white-space, or codec presence conflicts with
    /// <paramref name="boundaries"/>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="framing"/> or <paramref name="boundaries"/> is unsupported.
    /// </exception>
    [JsonConstructor]
    public ChannelFramingRequirement(
        ChannelRequirementId id,
        ChannelRequirementScope scope,
        ChannelFramingKind framing,
        ChannelBoundarySemantics boundaries,
        string? codec = null)
        : base(id, scope)
    {
        ChannelTopologyRequirement.RequireDefined(framing, nameof(framing));
        ChannelTopologyRequirement.RequireDefined(boundaries, nameof(boundaries));
        if (codec is not null && string.IsNullOrWhiteSpace(codec))
            throw new ArgumentException("An optional Channel codec identity cannot be white-space.", nameof(codec));
        if ((boundaries == ChannelBoundarySemantics.CodecReconstructed) != (codec is not null))
        {
            throw new ArgumentException(
                "Codec-reconstructed boundaries require one codec and other boundary modes omit it.",
                nameof(codec));
        }

        Framing = framing;
        Boundaries = boundaries;
        Codec = codec;
    }

    /// <summary>Typed-message, frame, datagram, or byte-stream form.</summary>
    public ChannelFramingKind Framing { get; }

    /// <summary>Required application-boundary semantics.</summary>
    public ChannelBoundarySemantics Boundaries { get; }

    /// <summary>Stable codec binding for reconstructed boundaries.</summary>
    public string? Codec { get; }
}

/// <summary>Durability, retention, and independently selectable replay requirement.</summary>
public sealed record ChannelPersistenceRequirement : ChannelRequirement
{
    internal override string WireName => ChannelWireNames.PersistenceRequirement;
    internal override void EnsureDeclaredVariant() { }

    /// <summary>Creates a Channel persistence requirement.</summary>
    /// <param name="id">Stable requirement identity.</param>
    /// <param name="scope">Logical exchange or direction scope.</param>
    /// <param name="retention">Required durability and retention model.</param>
    /// <param name="replay">Required replay operation, independently of application progress.</param>
    /// <param name="minimumRetention">Optional positive minimum retained or resumable duration.</param>
    /// <exception cref="ArgumentNullException"><paramref name="scope"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="id"/> is default.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// An enum value is unsupported or <paramref name="minimumRetention"/> is non-positive.
    /// </exception>
    [JsonConstructor]
    public ChannelPersistenceRequirement(
        ChannelRequirementId id,
        ChannelRequirementScope scope,
        ChannelRetentionKind retention,
        ChannelReplayKind replay,
        TimeSpan? minimumRetention = null)
        : base(id, scope)
    {
        ChannelTopologyRequirement.RequireDefined(retention, nameof(retention));
        ChannelTopologyRequirement.RequireDefined(replay, nameof(replay));
        if (minimumRetention is { } duration && duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(minimumRetention),
                minimumRetention,
                "A minimum retention duration must be positive.");
        }

        Retention = retention;
        Replay = replay;
        MinimumRetention = minimumRetention;
    }

    /// <summary>Required durability and retention model.</summary>
    public ChannelRetentionKind Retention { get; }

    /// <summary>Required replay operation, independently of application progress.</summary>
    public ChannelReplayKind Replay { get; }

    /// <summary>Optional positive minimum retained or resumable duration.</summary>
    public TimeSpan? MinimumRetention { get; }
}

/// <summary>Orthogonal durable floor and pending-delivery progress requirement.</summary>
public sealed record ChannelProgressRequirement : ChannelRequirement
{
    internal override string WireName => ChannelWireNames.ProgressRequirement;
    internal override void EnsureDeclaredVariant() { }

    /// <summary>Creates a durable Channel progress requirement.</summary>
    /// <param name="id">Stable requirement identity.</param>
    /// <param name="scope">Logical exchange or direction scope.</param>
    /// <param name="floor">Required cumulative floor evidence.</param>
    /// <param name="pending">Required exact or target-managed pending-delivery evidence.</param>
    /// <exception cref="ArgumentNullException"><paramref name="scope"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="id"/> is default, both progress dimensions are <c>None</c>, or unresolved-gap progress has
    /// no cumulative or target-managed floor.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="floor"/> or <paramref name="pending"/> is unsupported.
    /// </exception>
    [JsonConstructor]
    public ChannelProgressRequirement(
        ChannelRequirementId id,
        ChannelRequirementScope scope,
        ChannelProgressFloorKind floor,
        ChannelPendingProgressKind pending)
        : base(id, scope)
    {
        ChannelTopologyRequirement.RequireDefined(floor, nameof(floor));
        ChannelTopologyRequirement.RequireDefined(pending, nameof(pending));
        if (floor == ChannelProgressFloorKind.None && pending == ChannelPendingProgressKind.None)
        {
            throw new ArgumentException(
                "A durable progress requirement must request a floor, pending evidence, or both.",
                nameof(pending));
        }
        if (pending == ChannelPendingProgressKind.PrefixWithUnresolvedGaps
            && floor is not (ChannelProgressFloorKind.CumulativePrefix or ChannelProgressFloorKind.TargetManaged))
        {
            throw new ArgumentException(
                "Prefix-with-unresolved-gaps progress requires a cumulative or target-managed floor.",
                nameof(floor));
        }

        Floor = floor;
        Pending = pending;
    }

    /// <summary>Required cumulative floor evidence.</summary>
    public ChannelProgressFloorKind Floor { get; }

    /// <summary>Required exact or target-managed pending-delivery evidence.</summary>
    public ChannelPendingProgressKind Pending { get; }
}

/// <summary>Delivery guarantee and scoped ordering requirement.</summary>
public sealed record ChannelDeliveryRequirement : ChannelRequirement
{
    internal override string WireName => ChannelWireNames.DeliveryRequirement;
    internal override void EnsureDeclaredVariant() { }

    /// <summary>Creates a Channel delivery requirement.</summary>
    /// <param name="id">Stable requirement identity.</param>
    /// <param name="scope">Logical exchange or direction scope.</param>
    /// <param name="guarantee">Required attempt, at-most-once, at-least-once, or protocol-scoped exactly-once delivery semantics.</param>
    /// <param name="ordering">Required ordering scope.</param>
    /// <param name="namedOrderingScope">Stable semantic scope identity when <paramref name="ordering"/> is named.</param>
    /// <exception cref="ArgumentNullException"><paramref name="scope"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="id"/> is default or named ordering-scope presence conflicts with <paramref name="ordering"/>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="guarantee"/> or <paramref name="ordering"/> is unsupported.
    /// </exception>
    [JsonConstructor]
    public ChannelDeliveryRequirement(
        ChannelRequirementId id,
        ChannelRequirementScope scope,
        ChannelDeliveryGuaranteeKind guarantee,
        ChannelOrderingScopeKind ordering,
        string? namedOrderingScope = null)
        : base(id, scope)
    {
        ChannelTopologyRequirement.RequireDefined(guarantee, nameof(guarantee));
        ChannelTopologyRequirement.RequireDefined(ordering, nameof(ordering));
        if (namedOrderingScope is not null && string.IsNullOrWhiteSpace(namedOrderingScope))
        {
            throw new ArgumentException(
                "An optional named ordering scope cannot be white-space.",
                nameof(namedOrderingScope));
        }
        if ((ordering == ChannelOrderingScopeKind.Named) != (namedOrderingScope is not null))
        {
            throw new ArgumentException(
                "Named ordering requires one semantic scope identity and other ordering modes omit it.",
                nameof(namedOrderingScope));
        }

        Guarantee = guarantee;
        Ordering = ordering;
        NamedOrderingScope = namedOrderingScope;
    }

    /// <summary>Required attempt, at-most-once, at-least-once, or protocol-scoped exactly-once delivery semantics.</summary>
    public ChannelDeliveryGuaranteeKind Guarantee { get; }

    /// <summary>Required ordering scope.</summary>
    public ChannelOrderingScopeKind Ordering { get; }

    /// <summary>Stable semantic ordering-scope identity for named ordering.</summary>
    public string? NamedOrderingScope { get; }
}

/// <summary>Reliable, unreliable, or explicitly bounded partially reliable delivery requirement.</summary>
public sealed record ChannelReliabilityRequirement : ChannelRequirement
{
    internal override string WireName => ChannelWireNames.ReliabilityRequirement;
    internal override void EnsureDeclaredVariant() { }

    /// <summary>Creates a Channel reliability requirement.</summary>
    /// <param name="id">Stable requirement identity.</param>
    /// <param name="scope">Logical exchange or direction scope.</param>
    /// <param name="reliability">Required reliability class.</param>
    /// <param name="maximumLifetime">Optional positive partial-delivery lifetime.</param>
    /// <param name="maximumRetransmissions">Optional non-negative partial-delivery retransmission bound.</param>
    /// <exception cref="ArgumentNullException"><paramref name="scope"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="id"/> is default or partial limits are missing or supplied for another reliability class.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="reliability"/> is unsupported, <paramref name="maximumLifetime"/> is non-positive, or
    /// <paramref name="maximumRetransmissions"/> is negative.
    /// </exception>
    [JsonConstructor]
    public ChannelReliabilityRequirement(
        ChannelRequirementId id,
        ChannelRequirementScope scope,
        ChannelReliabilityKind reliability,
        TimeSpan? maximumLifetime = null,
        int? maximumRetransmissions = null)
        : base(id, scope)
    {
        ChannelTopologyRequirement.RequireDefined(reliability, nameof(reliability));
        if (maximumLifetime is { } duration && duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumLifetime),
                maximumLifetime,
                "A partial-delivery lifetime must be positive.");
        }
        if (maximumRetransmissions is < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumRetransmissions),
                maximumRetransmissions,
                "A retransmission bound cannot be negative.");
        }
        var hasPartialLimit = maximumLifetime is not null || maximumRetransmissions is not null;
        if ((reliability == ChannelReliabilityKind.PartiallyReliable) != hasPartialLimit)
        {
            throw new ArgumentException(
                "Partial reliability requires a lifetime or retransmission bound and other reliability classes omit both.",
                nameof(maximumLifetime));
        }

        Reliability = reliability;
        MaximumLifetime = maximumLifetime;
        MaximumRetransmissions = maximumRetransmissions;
    }

    /// <summary>Required reliability class.</summary>
    public ChannelReliabilityKind Reliability { get; }

    /// <summary>Optional positive partial-delivery lifetime.</summary>
    public TimeSpan? MaximumLifetime { get; }

    /// <summary>Optional non-negative partial-delivery retransmission bound.</summary>
    public int? MaximumRetransmissions { get; }
}

/// <summary>One exact completion or provider-settlement mode required by a Channel scope.</summary>
/// <remarks>
/// A requirement represents exactly one legal operation and coupling pair. A direction that needs several settlement
/// modes declares several independently identified requirements so each mode can be capability-matched and attributed
/// without assigning one ambiguous coupling scope to an operation bag.
/// </remarks>
public sealed record ChannelSettlementRequirement : ChannelRequirement
{
    internal override string WireName => ChannelWireNames.SettlementRequirement;
    internal override void EnsureDeclaredVariant() { }

    /// <summary>Creates a Channel settlement requirement.</summary>
    /// <param name="id">Stable requirement identity.</param>
    /// <param name="scope">Logical exchange or direction scope.</param>
    /// <param name="coupling">Provider state changed by one settlement operation.</param>
    /// <param name="operation">Exact required settlement operation.</param>
    /// <exception cref="ArgumentNullException"><paramref name="scope"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="id"/> is default or <paramref name="operation"/> is incompatible with
    /// <paramref name="coupling"/>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="coupling"/> or <paramref name="operation"/> is unsupported.
    /// </exception>
    [JsonConstructor]
    public ChannelSettlementRequirement(
        ChannelRequirementId id,
        ChannelRequirementScope scope,
        ChannelSettlementCouplingKind coupling,
        ChannelSettlementKind operation)
        : base(id, scope)
    {
        ChannelTopologyRequirement.RequireDefined(coupling, nameof(coupling));
        ChannelTopologyRequirement.RequireDefined(operation, nameof(operation));
        if (!IsLegal(operation, coupling))
        {
            throw new ArgumentException(
                $"Settlement operation '{operation}' cannot use coupling '{coupling}'.",
                nameof(coupling));
        }

        Coupling = coupling;
        Operation = operation;
    }

    /// <summary>Provider state changed by one settlement operation.</summary>
    public ChannelSettlementCouplingKind Coupling { get; }

    /// <summary>Exact required settlement operation.</summary>
    public ChannelSettlementKind Operation { get; }

    internal static bool IsLegal(
        ChannelSettlementKind operation,
        ChannelSettlementCouplingKind coupling) =>
        (operation, coupling) switch
        {
            (ChannelSettlementKind.InvocationCoupled, ChannelSettlementCouplingKind.Invocation) => true,
            (ChannelSettlementKind.CumulativePrefix, ChannelSettlementCouplingKind.OrderingScope
                or ChannelSettlementCouplingKind.BatchOrCallback) => true,
            (ChannelSettlementKind.Individual, ChannelSettlementCouplingKind.PerDelivery) => true,
            (ChannelSettlementKind.Batch, ChannelSettlementCouplingKind.BatchOrCallback) => true,
            (ChannelSettlementKind.Negative, ChannelSettlementCouplingKind.PerDelivery) => true,
            (ChannelSettlementKind.Defer, ChannelSettlementCouplingKind.PerDelivery) => true,
            (ChannelSettlementKind.Quarantine, ChannelSettlementCouplingKind.PerDelivery) => true,
            _ => false
        };
}

/// <summary>Minimum initiation allowance and validity required from a session-level lease.</summary>
public sealed record ChannelInitiationLease
{
    /// <summary>Creates a session initiation-lease demand or capability.</summary>
    /// <param name="minimumInitiations">Minimum number of new interactions admitted during one lease.</param>
    /// <param name="minimumValidity">Minimum positive lease validity duration.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="minimumInitiations"/> is not positive or <paramref name="minimumValidity"/> is not positive.
    /// </exception>
    [JsonConstructor]
    public ChannelInitiationLease(int minimumInitiations, TimeSpan minimumValidity)
    {
        if (minimumInitiations <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(minimumInitiations),
                minimumInitiations,
                "A session initiation lease must admit at least one interaction.");
        }
        if (minimumValidity <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(minimumValidity),
                minimumValidity,
                "A session initiation lease must have positive validity.");
        }

        MinimumInitiations = minimumInitiations;
        MinimumValidity = minimumValidity;
    }

    /// <summary>Minimum number of new interactions admitted during one lease.</summary>
    public int MinimumInitiations { get; }

    /// <summary>Minimum positive lease validity duration.</summary>
    public TimeSpan MinimumValidity { get; }
}

/// <summary>Flow control, completion, cancellation, and session-lifecycle requirement.</summary>
public sealed record ChannelFlowRequirement : ChannelRequirement
{
    internal override string WireName => ChannelWireNames.FlowRequirement;
    internal override void EnsureDeclaredVariant() { }

    /// <summary>Creates a Channel flow and lifecycle requirement.</summary>
    /// <param name="id">Stable requirement identity.</param>
    /// <param name="scope">Logical exchange or direction scope.</param>
    /// <param name="control">Required flow-control mechanism.</param>
    /// <param name="completion">Required stream completion behavior.</param>
    /// <param name="continuity">Required reconnect or bounded-resume behavior.</param>
    /// <param name="maximumInFlight">Optional positive in-flight delivery bound.</param>
    /// <param name="resumeWindow">Optional positive bounded-resume window.</param>
    /// <param name="cancellation">Required observable cancellation scope.</param>
    /// <param name="initiationLease">Optional minimum session-level interaction-initiation lease.</param>
    /// <exception cref="ArgumentNullException"><paramref name="scope"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="id"/> is default or resume-window presence conflicts with <paramref name="continuity"/>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// An enum value is unsupported, <paramref name="maximumInFlight"/> is not positive, or
    /// <paramref name="resumeWindow"/> is non-positive.
    /// </exception>
    [JsonConstructor]
    public ChannelFlowRequirement(
        ChannelRequirementId id,
        ChannelRequirementScope scope,
        ChannelFlowControlKind control,
        ChannelStreamCompletionKind completion,
        ChannelSessionContinuityKind continuity,
        int? maximumInFlight = null,
        TimeSpan? resumeWindow = null,
        ChannelCancellationKind cancellation = ChannelCancellationKind.None,
        ChannelInitiationLease? initiationLease = null)
        : base(id, scope)
    {
        ChannelTopologyRequirement.RequireDefined(control, nameof(control));
        ChannelTopologyRequirement.RequireDefined(completion, nameof(completion));
        ChannelTopologyRequirement.RequireDefined(continuity, nameof(continuity));
        ChannelTopologyRequirement.RequireDefined(cancellation, nameof(cancellation));
        if (maximumInFlight is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumInFlight),
                maximumInFlight,
                "An in-flight delivery bound must be positive.");
        }
        if (resumeWindow is { } duration && duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(resumeWindow),
                resumeWindow,
                "A bounded-resume window must be positive.");
        }
        if ((continuity == ChannelSessionContinuityKind.BoundedResume) != (resumeWindow is not null))
        {
            throw new ArgumentException(
                "Bounded resume requires one resume window and other continuity modes omit it.",
                nameof(resumeWindow));
        }

        Control = control;
        Completion = completion;
        Continuity = continuity;
        MaximumInFlight = maximumInFlight;
        ResumeWindow = resumeWindow;
        Cancellation = cancellation;
        InitiationLease = initiationLease;
    }

    /// <summary>Required flow-control mechanism.</summary>
    public ChannelFlowControlKind Control { get; }

    /// <summary>Required stream completion behavior.</summary>
    public ChannelStreamCompletionKind Completion { get; }

    /// <summary>Required reconnect or bounded-resume behavior.</summary>
    public ChannelSessionContinuityKind Continuity { get; }

    /// <summary>Optional positive in-flight delivery bound.</summary>
    public int? MaximumInFlight { get; }

    /// <summary>Optional positive bounded-resume window.</summary>
    public TimeSpan? ResumeWindow { get; }

    /// <summary>Required observable cancellation scope.</summary>
    public ChannelCancellationKind Cancellation { get; }

    /// <summary>Optional minimum session-level interaction-initiation lease.</summary>
    public ChannelInitiationLease? InitiationLease { get; }
}

/// <summary>Atomic coupling required across two or more semantic Channel operations.</summary>
public sealed record ChannelAtomicityRequirement : ChannelRequirement
{
    internal override string WireName => ChannelWireNames.AtomicityRequirement;
    internal override void EnsureDeclaredVariant() { }

    /// <summary>Creates an atomic Channel coupling requirement.</summary>
    /// <param name="id">Stable requirement identity.</param>
    /// <param name="scope">Logical exchange or direction scope.</param>
    /// <param name="atomicScope">Stable semantic identity of the demanded atomic boundary.</param>
    /// <param name="operations">At least two operations that must commit atomically.</param>
    /// <exception cref="ArgumentNullException"><paramref name="scope"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="id"/> or <paramref name="atomicScope"/> is default, or <paramref name="operations"/> has fewer
    /// than two distinct supported values.
    /// </exception>
    [JsonConstructor]
    public ChannelAtomicityRequirement(
        ChannelRequirementId id,
        ChannelRequirementScope scope,
        ChannelAtomicScopeId atomicScope,
        ImmutableArray<ChannelAtomicOperationKind> operations)
        : base(id, scope)
    {
        if (string.IsNullOrWhiteSpace(atomicScope.Value))
            throw new ArgumentException("An atomic Channel requirement requires a stable scope identity.", nameof(atomicScope));

        var normalized = ChannelRequirementCollections.NormalizeEnumSet(
            operations,
            nameof(operations),
            requireNonEmpty: true);
        if (normalized.Length < 2)
        {
            throw new ArgumentException(
                "An atomic Channel boundary must couple at least two distinct semantic operations.",
                nameof(operations));
        }

        AtomicScope = atomicScope;
        Operations = normalized;
    }

    /// <summary>Stable semantic identity of the demanded atomic boundary.</summary>
    public ChannelAtomicScopeId AtomicScope { get; }

    /// <summary>Coupled operations in deterministic enum order.</summary>
    public ImmutableArray<ChannelAtomicOperationKind> Operations { get; }

    /// <summary>Compares normalized atomicity requirements structurally.</summary>
    /// <param name="other">Other requirement.</param>
    /// <returns><see langword="true"/> when every semantic field is equal.</returns>
    public bool Equals(ChannelAtomicityRequirement? other) =>
        ReferenceEquals(this, other)
        || other is not null
        && Id == other.Id
        && Scope == other.Scope
        && AtomicScope == other.AtomicScope
        && Operations.SequenceEqual(other.Operations);

    /// <summary>Returns a structural hash code for normalized atomicity semantics.</summary>
    /// <returns>A hash code derived from all semantic fields.</returns>
    public override int GetHashCode() => ChannelRequirementCollections.Hash(Id, Scope, AtomicScope, Operations);
}

/// <summary>Set of required transport-security properties.</summary>
public sealed record ChannelSecurityRequirement : ChannelRequirement
{
    internal override string WireName => ChannelWireNames.SecurityRequirement;
    internal override void EnsureDeclaredVariant() { }

    /// <summary>Creates a Channel security requirement.</summary>
    /// <param name="id">Stable requirement identity.</param>
    /// <param name="scope">Logical exchange or direction scope.</param>
    /// <param name="properties">Required security properties.</param>
    /// <exception cref="ArgumentNullException"><paramref name="scope"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="id"/> is default or <paramref name="properties"/> is empty, duplicated, or unsupported.
    /// </exception>
    [JsonConstructor]
    public ChannelSecurityRequirement(
        ChannelRequirementId id,
        ChannelRequirementScope scope,
        ImmutableArray<ChannelSecurityKind> properties)
        : base(id, scope) =>
        Properties = ChannelRequirementCollections.NormalizeEnumSet(
            properties,
            nameof(properties),
            requireNonEmpty: true);

    /// <summary>Required security properties in deterministic enum order.</summary>
    public ImmutableArray<ChannelSecurityKind> Properties { get; }

    /// <summary>Compares normalized security requirements structurally.</summary>
    /// <param name="other">Other requirement.</param>
    /// <returns><see langword="true"/> when every semantic field is equal.</returns>
    public bool Equals(ChannelSecurityRequirement? other) =>
        ReferenceEquals(this, other)
        || other is not null
        && Id == other.Id
        && Scope == other.Scope
        && Properties.SequenceEqual(other.Properties);

    /// <summary>Returns a structural hash code for normalized security semantics.</summary>
    /// <returns>A hash code derived from all semantic fields.</returns>
    public override int GetHashCode() => ChannelRequirementCollections.Hash(Id, Scope, Properties);
}

/// <summary>Positive operating capacity required from a Channel target.</summary>
public sealed record ChannelLimitRequirement : ChannelRequirement
{
    internal override string WireName => ChannelWireNames.LimitRequirement;
    internal override void EnsureDeclaredVariant() { }

    /// <summary>Creates a Channel operating-limit requirement.</summary>
    /// <param name="id">Stable requirement identity.</param>
    /// <param name="scope">Logical exchange or direction scope.</param>
    /// <param name="kind">Unit-bearing operating dimension.</param>
    /// <param name="value">Positive capacity or duration required in the unit defined by <paramref name="kind"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="scope"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="id"/> is default.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="kind"/> is unsupported or <paramref name="value"/> is not positive.
    /// </exception>
    [JsonConstructor]
    public ChannelLimitRequirement(
        ChannelRequirementId id,
        ChannelRequirementScope scope,
        ChannelLimitKind kind,
        long value)
        : base(id, scope)
    {
        ChannelTopologyRequirement.RequireDefined(kind, nameof(kind));
        if (value <= 0)
            throw new ArgumentOutOfRangeException(nameof(value), value, "A Channel operating limit must be positive.");

        Kind = kind;
        Value = value;
    }

    /// <summary>Unit-bearing operating dimension.</summary>
    public ChannelLimitKind Kind { get; }

    /// <summary>Positive capacity or duration required in the unit defined by <see cref="Kind"/>.</summary>
    public long Value { get; }
}

/// <summary>Canonical provider-neutral Channel definition.</summary>
/// <remarks>
/// The definition describes logical topology and observable requirements only. Canonical payload contracts and
/// envelopes retain their own identities and are bound by an interpretation rather than copied into Channel IR.
/// Requirement order is non-semantic and is normalized by scope, requirement family, mode or dimension, and stable
/// identity.
/// </remarks>
public sealed record ChannelDefinition
{
    /// <summary>Creates a normalized canonical Channel definition.</summary>
    /// <param name="exchange">One-way or two-direction Request/Reply logical topology.</param>
    /// <param name="requirements">Provider-neutral semantic requirements.</param>
    /// <exception cref="ArgumentNullException"><paramref name="exchange"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="requirements"/> contains a null, an undeclared runtime variant, or a duplicate identity.
    /// </exception>
    [JsonConstructor]
    public ChannelDefinition(
        ChannelExchangeDefinition exchange,
        ImmutableArray<ChannelRequirement> requirements)
    {
        Exchange = Guard.RequireNotNull(exchange);
        exchange.EnsureDeclaredVariant();
        Requirements = NormalizeRequirements(requirements);
    }

    /// <summary>One-way or two-direction Request/Reply logical topology.</summary>
    public ChannelExchangeDefinition Exchange { get; }

    /// <summary>Requirements in deterministic scope, family, mode or dimension, and identity order.</summary>
    public ImmutableArray<ChannelRequirement> Requirements { get; }

    /// <summary>Finds one requirement by stable identity.</summary>
    /// <param name="id">Stable definition-local requirement identity.</param>
    /// <returns>The matching requirement, or <see langword="null"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="id"/> is default.</exception>
    public ChannelRequirement? Find(ChannelRequirementId id)
    {
        if (string.IsNullOrWhiteSpace(id.Value))
            throw new ArgumentException("A requirement lookup identity cannot be default.", nameof(id));

        foreach (var requirement in Requirements)
        {
            if (requirement.Id == id)
                return requirement;
        }

        return null;
    }

    /// <summary>Compares normalized Channel definitions structurally.</summary>
    /// <param name="other">Other definition.</param>
    /// <returns><see langword="true"/> when exchange and every normalized requirement are equal.</returns>
    public bool Equals(ChannelDefinition? other) =>
        ReferenceEquals(this, other)
        || other is not null
        && Exchange == other.Exchange
        && Requirements.SequenceEqual(other.Requirements);

    /// <summary>Returns a structural hash code for canonical Channel semantics.</summary>
    /// <returns>A hash code derived from the exchange and every normalized requirement.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Exchange);
        foreach (var requirement in Requirements)
            hash.Add(requirement);
        return hash.ToHashCode();
    }

    static ImmutableArray<ChannelRequirement> NormalizeRequirements(
        ImmutableArray<ChannelRequirement> requirements)
    {
        if (requirements.IsDefaultOrEmpty)
            return [];

        HashSet<ChannelRequirementId> ids = [];
        var canonical = true;
        ChannelRequirement? previous = null;
        foreach (var requirement in requirements)
        {
            if (requirement is null)
                throw new ArgumentException("Channel requirements cannot contain null entries.", nameof(requirements));
            requirement.EnsureDeclaredVariant();
            if (!ids.Add(requirement.Id))
            {
                throw new ArgumentException(
                    $"Channel requirement identity '{requirement.Id.Value}' is duplicated.",
                    nameof(requirements));
            }
            if (previous is not null && CompareRequirements(previous, requirement) > 0)
                canonical = false;
            previous = requirement;
        }
        if (canonical)
            return requirements;

        var normalized = ImmutableArray.CreateBuilder<ChannelRequirement>(requirements.Length);
        normalized.AddRange(requirements);
        normalized.Sort(CompareRequirements);
        return normalized.MoveToImmutable();
    }

    static int CompareRequirements(ChannelRequirement left, ChannelRequirement right)
    {
        var comparison = left.Scope.Kind.CompareTo(right.Scope.Kind);
        if (comparison != 0)
            return comparison;
        comparison = StringComparer.Ordinal.Compare(
            left.Scope.Direction?.Value,
            right.Scope.Direction?.Value);
        if (comparison != 0)
            return comparison;
        comparison = StringComparer.Ordinal.Compare(left.WireName, right.WireName);
        if (comparison != 0)
            return comparison;
        if (left is ChannelSettlementRequirement leftSettlement
            && right is ChannelSettlementRequirement rightSettlement)
        {
            comparison = leftSettlement.Operation.CompareTo(rightSettlement.Operation);
            if (comparison != 0)
                return comparison;
            comparison = leftSettlement.Coupling.CompareTo(rightSettlement.Coupling);
            if (comparison != 0)
                return comparison;
        }
        if (left is ChannelLimitRequirement leftLimit && right is ChannelLimitRequirement rightLimit)
        {
            comparison = leftLimit.Kind.CompareTo(rightLimit.Kind);
            if (comparison != 0)
                return comparison;
        }

        return StringComparer.Ordinal.Compare(left.Id.Value, right.Id.Value);
    }
}

static class ChannelRequirementCollections
{
    public static ImmutableArray<TEnum> NormalizeEnumSet<TEnum>(
        ImmutableArray<TEnum> values,
        string parameterName,
        bool requireNonEmpty)
        where TEnum : struct, Enum
    {
        var normalizedValues = values.IsDefault ? [] : values;
        if (requireNonEmpty && normalizedValues.IsDefaultOrEmpty)
            throw new ArgumentException("A Channel semantic set cannot be empty.", parameterName);
        if (normalizedValues.IsDefaultOrEmpty)
            return [];

        HashSet<TEnum> observed = [];
        var canonical = true;
        TEnum? previous = null;
        foreach (var value in normalizedValues)
        {
            if (!Enum.IsDefined(value))
                throw new ArgumentException($"Unsupported {typeof(TEnum).Name} value '{value}'.", parameterName);
            if (!observed.Add(value))
                throw new ArgumentException($"Channel semantic value '{value}' is duplicated.", parameterName);
            if (previous is { } prior && Comparer<TEnum>.Default.Compare(prior, value) > 0)
                canonical = false;
            previous = value;
        }
        if (canonical)
            return normalizedValues;

        var sorted = ImmutableArray.CreateBuilder<TEnum>(normalizedValues.Length);
        sorted.AddRange(normalizedValues);
        sorted.Sort(Comparer<TEnum>.Default);
        return sorted.MoveToImmutable();
    }

    public static int Hash<T1, T2, T3, TEnum>(
        T1 first,
        T2 second,
        T3 third,
        ImmutableArray<TEnum> values)
        where TEnum : struct, Enum
    {
        var hash = new HashCode();
        hash.Add(first);
        hash.Add(second);
        hash.Add(third);
        foreach (var value in values)
            hash.Add(value);
        return hash.ToHashCode();
    }

    public static int Hash<T1, T2, TEnum>(
        T1 first,
        T2 second,
        ImmutableArray<TEnum> values)
        where TEnum : struct, Enum
    {
        var hash = new HashCode();
        hash.Add(first);
        hash.Add(second);
        foreach (var value in values)
            hash.Add(value);
        return hash.ToHashCode();
    }
}
