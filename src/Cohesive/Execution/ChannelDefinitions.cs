using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Cohesive.Model.Serialization;

namespace Cohesive.Execution;

/// <summary>Stable identity of one logical direction inside a canonical Channel exchange.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct ChannelDirectionId
{
    /// <summary>Creates a Channel direction identity.</summary>
    /// <param name="value">Stable definition-local direction identity.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white-space.</exception>
    [JsonConstructor]
    public ChannelDirectionId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw stable direction identity.</summary>
    public string Value { get; }

    /// <summary>Returns the raw stable direction identity.</summary>
    /// <returns>The value supplied at construction.</returns>
    public override string ToString() => Value;
}

/// <summary>Stable identity of one Channel requirement.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct ChannelRequirementId
{
    /// <summary>Creates a Channel requirement identity.</summary>
    /// <param name="value">Stable definition-local requirement identity.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white-space.</exception>
    [JsonConstructor]
    public ChannelRequirementId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw stable requirement identity.</summary>
    public string Value { get; }

    /// <summary>Returns the raw stable requirement identity.</summary>
    /// <returns>The value supplied at construction.</returns>
    public override string ToString() => Value;
}

/// <summary>Stable semantic identity of one requested atomic coupling scope.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct ChannelAtomicScopeId
{
    /// <summary>Creates an atomic-scope identity.</summary>
    /// <param name="value">Stable semantic scope identity.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white-space.</exception>
    [JsonConstructor]
    public ChannelAtomicScopeId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw stable atomic-scope identity.</summary>
    public string Value { get; }

    /// <summary>Returns the raw stable atomic-scope identity.</summary>
    /// <returns>The value supplied at construction.</returns>
    public override string ToString() => Value;
}

/// <summary>Closed logical exchange topology independent of a physical transport binding.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = ChannelWireNames.ExchangeDiscriminator)]
[JsonDerivedType(typeof(OneWayChannelExchange), ChannelWireNames.OneWayExchange)]
[JsonDerivedType(typeof(RequestReplyChannelExchange), ChannelWireNames.RequestReplyExchange)]
public abstract record ChannelExchangeDefinition
{
    /// <summary>Restricts the exchange family to variants declared by the canonical Channel schema.</summary>
    private protected ChannelExchangeDefinition()
    {
    }

    /// <summary>Gets the exchange's logical directions in deterministic semantic order.</summary>
    /// <returns>One direction for one-way exchange or Request then Reply directions for Request/Reply.</returns>
    public abstract ImmutableArray<ChannelDirectionId> GetDirections();

    internal abstract void EnsureDeclaredVariant();
}

/// <summary>One logical producer-to-consumer direction.</summary>
public sealed record OneWayChannelExchange : ChannelExchangeDefinition
{
    internal override void EnsureDeclaredVariant() { }

    /// <summary>Creates a one-way Channel exchange.</summary>
    /// <param name="direction">Stable identity of the sole logical direction.</param>
    /// <exception cref="ArgumentException"><paramref name="direction"/> is default.</exception>
    [JsonConstructor]
    public OneWayChannelExchange(ChannelDirectionId direction)
    {
        RequireDirection(direction, nameof(direction));
        Direction = direction;
    }

    /// <summary>Stable identity of the sole logical direction.</summary>
    public ChannelDirectionId Direction { get; }

    /// <inheritdoc />
    public override ImmutableArray<ChannelDirectionId> GetDirections() => [Direction];

    internal static void RequireDirection(ChannelDirectionId direction, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(direction.Value))
            throw new ArgumentException("A Channel direction identity cannot be default.", parameterName);
    }
}

/// <summary>Two independent logical directions forming a canonical Request/Reply exchange.</summary>
public sealed record RequestReplyChannelExchange : ChannelExchangeDefinition
{
    internal override void EnsureDeclaredVariant() { }

    /// <summary>Creates a two-direction Request/Reply exchange.</summary>
    /// <param name="requestDirection">Logical Request direction.</param>
    /// <param name="replyDirection">Independent logical Reply direction.</param>
    /// <exception cref="ArgumentException">
    /// A direction is default or both directions use the same identity.
    /// </exception>
    [JsonConstructor]
    public RequestReplyChannelExchange(
        ChannelDirectionId requestDirection,
        ChannelDirectionId replyDirection)
    {
        OneWayChannelExchange.RequireDirection(requestDirection, nameof(requestDirection));
        OneWayChannelExchange.RequireDirection(replyDirection, nameof(replyDirection));
        if (requestDirection == replyDirection)
        {
            throw new ArgumentException(
                "Canonical Request and Reply directions require distinct identities.",
                nameof(replyDirection));
        }

        RequestDirection = requestDirection;
        ReplyDirection = replyDirection;
    }

    /// <summary>Logical direction carrying canonical Request envelopes.</summary>
    public ChannelDirectionId RequestDirection { get; }

    /// <summary>Logical direction carrying canonical Reply envelopes.</summary>
    public ChannelDirectionId ReplyDirection { get; }

    /// <inheritdoc />
    public override ImmutableArray<ChannelDirectionId> GetDirections() => [RequestDirection, ReplyDirection];
}

/// <summary>Whether a requirement applies to the complete exchange or one exact logical direction.</summary>
[JsonConverter(typeof(StrictStringEnumJsonConverterFactory))]
public enum ChannelRequirementScopeKind
{
    /// <summary>The requirement applies to the logical exchange as a whole.</summary>
    Exchange = 0,

    /// <summary>The requirement applies to one named logical direction.</summary>
    Direction = 1
}

/// <summary>Exact logical scope to which one Channel requirement applies.</summary>
public sealed record ChannelRequirementScope
{
    /// <summary>Creates a Channel requirement scope.</summary>
    /// <param name="kind">Exchange-wide or direction-local scope.</param>
    /// <param name="direction">Direction identity when <paramref name="kind"/> is direction-local.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is unsupported.</exception>
    /// <exception cref="ArgumentException">Direction presence conflicts with <paramref name="kind"/>.</exception>
    [JsonConstructor]
    public ChannelRequirementScope(
        ChannelRequirementScopeKind kind,
        ChannelDirectionId? direction = null)
    {
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported Channel requirement scope.");
        if ((kind == ChannelRequirementScopeKind.Direction) != direction.HasValue)
        {
            throw new ArgumentException(
                "A direction-local requirement requires one direction and an exchange-wide requirement omits it.",
                nameof(direction));
        }
        if (direction is { } directionId)
            OneWayChannelExchange.RequireDirection(directionId, nameof(direction));

        Kind = kind;
        Direction = direction;
    }

    /// <summary>Exchange-wide or direction-local scope kind.</summary>
    public ChannelRequirementScopeKind Kind { get; }

    /// <summary>Exact logical direction for a direction-local requirement.</summary>
    public ChannelDirectionId? Direction { get; }

    /// <summary>Shared exchange-wide scope.</summary>
    public static ChannelRequirementScope Exchange { get; } = new(ChannelRequirementScopeKind.Exchange);

    /// <summary>Creates a direction-local requirement scope.</summary>
    /// <param name="direction">Exact logical direction.</param>
    /// <returns>A scope bound to <paramref name="direction"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="direction"/> is default.</exception>
    public static ChannelRequirementScope ForDirection(ChannelDirectionId direction) =>
        new(ChannelRequirementScopeKind.Direction, direction);
}

/// <summary>Delivery distribution demanded of a Channel direction or exchange.</summary>
[JsonConverter(typeof(StrictStringEnumJsonConverterFactory))]
public enum ChannelDistributionKind
{
    /// <summary>One selected consumer receives each delivery.</summary>
    PointToPoint = 0,

    /// <summary>Several eligible consumers compete for each delivery.</summary>
    CompetingConsumers = 1,

    /// <summary>Each eligible subscriber receives its own logical delivery.</summary>
    FanOut = 2,

    /// <summary>Routing selects consumers by a declared key, filter, session, or target.</summary>
    Selective = 3
}

/// <summary>Protocol-neutral interaction shape carried by one logical exchange.</summary>
[JsonConverter(typeof(StrictStringEnumJsonConverterFactory))]
public enum ChannelInteractionShape
{
    /// <summary>Fire-and-forget discrete delivery with no emitter-side response path.</summary>
    FireAndForget = 0,

    /// <summary>One-way publication to one or more consumers.</summary>
    Publication = 1,

    /// <summary>One Request and one coupled or correlated terminal Reply.</summary>
    UnaryInvocation = 2,

    /// <summary>A finite or open client-to-server Request stream with one terminal Reply.</summary>
    RequestStream = 3,

    /// <summary>One Request followed by a finite or open server-to-client stream.</summary>
    ResponseStream = 4,

    /// <summary>Independent ordered streams in both logical directions.</summary>
    BidirectionalStream = 5,

    /// <summary>Message-boundary-preserving unreliable or partially reliable datagram delivery.</summary>
    Datagram = 6,

    /// <summary>Request and Reply are correlated across independent logical directions.</summary>
    CorrelatedRequestReply = 7
}

/// <summary>Provider-neutral routing form required by one logical direction.</summary>
[JsonConverter(typeof(StrictStringEnumJsonConverterFactory))]
public enum ChannelRoutingKind
{
    /// <summary>An operation or service endpoint selects the consumer.</summary>
    OperationEndpoint = 0,

    /// <summary>A topic and optional semantic filter select subscribers.</summary>
    TopicOrFilter = 1,

    /// <summary>A semantic key or session affinity selects an ordering and ownership domain.</summary>
    KeyOrSessionAffinity = 2,

    /// <summary>An established connection or stream selects the peer.</summary>
    ConnectionOrStream = 3,

    /// <summary>A canonical response target selects the Reply consumer.</summary>
    ExplicitResponseTarget = 4
}

/// <summary>Evidence required to prevent an unintended consumer from destructively acquiring a delivery.</summary>
[JsonConverter(typeof(StrictStringEnumJsonConverterFactory))]
public enum ChannelRoutingIsolationKind
{
    /// <summary>No exclusive acquisition guarantee is required.</summary>
    None = 0,

    /// <summary>The physical invocation path exclusively returns to its originating Request.</summary>
    InvocationScoped = 1,

    /// <summary>A dedicated logical or durable target isolates the intended consumer.</summary>
    DedicatedTarget = 2,

    /// <summary>Target-side selection or session ownership prevents acquisition by another consumer.</summary>
    SelectiveAcquisition = 3,

    /// <summary>A durable exclusive dispatcher consumes and routes by canonical target identity.</summary>
    DurableDispatcher = 4
}

/// <summary>Physical boundary model required for values carried by a Channel.</summary>
[JsonConverter(typeof(StrictStringEnumJsonConverterFactory))]
public enum ChannelFramingKind
{
    /// <summary>The binding carries one typed value per delivery.</summary>
    TypedMessage = 0,

    /// <summary>The binding preserves explicit application frames.</summary>
    FramedMessage = 1,

    /// <summary>The binding preserves datagram message boundaries.</summary>
    Datagram = 2,

    /// <summary>The binding exposes a byte stream without inherent application message boundaries.</summary>
    ByteStream = 3
}

/// <summary>How application boundaries are preserved or reconstructed.</summary>
[JsonConverter(typeof(StrictStringEnumJsonConverterFactory))]
public enum ChannelBoundarySemantics
{
    /// <summary>The target preserves every application boundary natively.</summary>
    Preserved = 0,

    /// <summary>An attributable codec reconstructs application boundaries.</summary>
    CodecReconstructed = 1,

    /// <summary>The semantic use does not require application boundary preservation.</summary>
    Unpreserved = 2
}

/// <summary>Durability and retained-history model required of a Channel.</summary>
[JsonConverter(typeof(StrictStringEnumJsonConverterFactory))]
public enum ChannelRetentionKind
{
    /// <summary>Delivery exists only for the current activation, connection, or session.</summary>
    ActivationLocal = 0,

    /// <summary>Delivery survives interruption until its declared completion or settlement.</summary>
    DurableUntilSettled = 1,

    /// <summary>Ordered or restorable history remains available inside a bounded retention window.</summary>
    RetainedHistory = 2,

    /// <summary>Only the latest publication for a semantic key is retained.</summary>
    RetainedLatest = 3
}

/// <summary>Replay operation required independently of durable delivery progress.</summary>
[JsonConverter(typeof(StrictStringEnumJsonConverterFactory))]
public enum ChannelReplayKind
{
    /// <summary>No retained replay operation is required.</summary>
    None = 0,

    /// <summary>An opaque ordered position can select subsequent retained input.</summary>
    OrderedPosition = 1,

    /// <summary>A retained subscription or stream state can be restored from a named snapshot.</summary>
    SnapshotRestore = 2,

    /// <summary>Retained input can be restored from a declared time boundary.</summary>
    TimeRestore = 3
}

/// <summary>Kind of durable prefix evidence required from Channel delivery progress.</summary>
[JsonConverter(typeof(StrictStringEnumJsonConverterFactory))]
public enum ChannelProgressFloorKind
{
    /// <summary>No cumulative delivery-progress floor is required.</summary>
    None = 0,

    /// <summary>A cumulative applied or acknowledged prefix is retained durably.</summary>
    CumulativePrefix = 1,

    /// <summary>The target retains an attributable acknowledgement floor.</summary>
    TargetManaged = 2
}

/// <summary>Kind of durable non-prefix delivery evidence required alongside any floor.</summary>
[JsonConverter(typeof(StrictStringEnumJsonConverterFactory))]
public enum ChannelPendingProgressKind
{
    /// <summary>No exact pending or stable-delivery set is required.</summary>
    None = 0,

    /// <summary>An exact set of stable delivery identities is retained.</summary>
    ExactStableDeliverySet = 1,

    /// <summary>A cumulative prefix is retained together with exact unresolved gaps.</summary>
    PrefixWithUnresolvedGaps = 2,

    /// <summary>An attributable target-managed acknowledgement snapshot is retained.</summary>
    TargetManagedSnapshot = 3
}

/// <summary>Observable delivery guarantee required at one explicitly scoped protocol boundary.</summary>
[JsonConverter(typeof(StrictStringEnumJsonConverterFactory))]
public enum ChannelDeliveryGuaranteeKind
{
    /// <summary>Only one physical invocation attempt is described; completion may remain ambiguous.</summary>
    InvocationAttempt = 0,

    /// <summary>A logical delivery is attempted no more than once.</summary>
    AtMostOnce = 1,

    /// <summary>A logical delivery may repeat until durable progress or settlement converges.</summary>
    AtLeastOnce = 2,

    /// <summary>
    /// The concrete protocol suppresses duplicate logical delivery inside the exact evidenced Channel boundary.
    /// </summary>
    /// <remarks>
    /// This is a protocol-delivery guarantee only. It never proves exactly-once application effects, state mutation,
    /// publication, checkpointing, or settlement without an independently realized atomicity requirement.
    /// </remarks>
    ProtocolExactlyOnce = 3
}

/// <summary>Ordering scope required of delivery observations.</summary>
[JsonConverter(typeof(StrictStringEnumJsonConverterFactory))]
public enum ChannelOrderingScopeKind
{
    /// <summary>No stable ordering is required.</summary>
    None = 0,

    /// <summary>One total order is required within the complete logical Channel.</summary>
    Channel = 1,

    /// <summary>Order is required only inside one partition, key, or session.</summary>
    PartitionKeyOrSession = 2,

    /// <summary>Order is required only inside one connection.</summary>
    Connection = 3,

    /// <summary>Order is required only inside one RPC or reactive stream.</summary>
    RpcStream = 4,

    /// <summary>A named semantic ordering scope is required.</summary>
    Named = 5
}

/// <summary>Reliability and timeliness class required independently of delivery guarantee.</summary>
[JsonConverter(typeof(StrictStringEnumJsonConverterFactory))]
public enum ChannelReliabilityKind
{
    /// <summary>Transport delivery is reliable inside its declared lifecycle and failure boundary.</summary>
    Reliable = 0,

    /// <summary>Transport delivery may be dropped without retry.</summary>
    Unreliable = 1,

    /// <summary>Delivery is bounded by age, lifetime, or retransmission count.</summary>
    PartiallyReliable = 2
}

/// <summary>Completion or provider-settlement operation required by one Channel direction.</summary>
[JsonConverter(typeof(StrictStringEnumJsonConverterFactory))]
public enum ChannelSettlementKind
{
    /// <summary>Completion is coupled to an invocation response or status.</summary>
    InvocationCoupled = 0,

    /// <summary>One operation acknowledges an ordered cumulative prefix.</summary>
    CumulativePrefix = 1,

    /// <summary>One stable delivery is acknowledged independently.</summary>
    Individual = 2,

    /// <summary>Several explicitly identified deliveries are acknowledged together.</summary>
    Batch = 3,

    /// <summary>A negative acknowledgement or release makes delivery eligible again.</summary>
    Negative = 4,

    /// <summary>Delivery is deferred without completing it.</summary>
    Defer = 5,

    /// <summary>Delivery is moved to a quarantine or dead-letter path.</summary>
    Quarantine = 6
}

/// <summary>Scope within which one settlement operation changes provider delivery state.</summary>
[JsonConverter(typeof(StrictStringEnumJsonConverterFactory))]
public enum ChannelSettlementCouplingKind
{
    /// <summary>Each delivery attempt settles independently.</summary>
    PerDelivery = 0,

    /// <summary>Settlement advances one cumulative ordering scope.</summary>
    OrderingScope = 1,

    /// <summary>One provider callback or batch couples several delivery or ordering scopes.</summary>
    BatchOrCallback = 2,

    /// <summary>Settlement is coupled to one invocation or session response.</summary>
    Invocation = 3
}

/// <summary>Flow-regulation mechanism required by one Channel.</summary>
[JsonConverter(typeof(StrictStringEnumJsonConverterFactory))]
public enum ChannelFlowControlKind
{
    /// <summary>No semantic demand or credit protocol is required.</summary>
    None = 0,

    /// <summary>A declared bounded buffer limits admitted in-flight data.</summary>
    BoundedBuffer = 1,

    /// <summary>The consumer explicitly requests additional elements.</summary>
    Demand = 2,

    /// <summary>Delivery consumes explicit credits granted by the consumer.</summary>
    Credit = 3
}

/// <summary>Observable completion behavior of a streaming Channel.</summary>
[JsonConverter(typeof(StrictStringEnumJsonConverterFactory))]
public enum ChannelStreamCompletionKind
{
    /// <summary>The Channel has one terminal completion boundary.</summary>
    Terminal = 0,

    /// <summary>One direction may half-close while the other continues.</summary>
    HalfClose = 1,

    /// <summary>Both logical directions complete independently before terminal closure.</summary>
    IndependentDirections = 2
}

/// <summary>Connection or session continuity required after interruption.</summary>
[JsonConverter(typeof(StrictStringEnumJsonConverterFactory))]
public enum ChannelSessionContinuityKind
{
    /// <summary>No reconnect or resume continuity is required.</summary>
    None = 0,

    /// <summary>A new session may reconnect without claiming delivery continuity.</summary>
    Reconnect = 1,

    /// <summary>A bounded session protocol resumes delivery continuity inside a declared window.</summary>
    BoundedResume = 2
}

/// <summary>Observable scope affected by one semantic cancellation operation.</summary>
[JsonConverter(typeof(StrictStringEnumJsonConverterFactory))]
public enum ChannelCancellationKind
{
    /// <summary>No semantic cancellation operation is required.</summary>
    None = 0,

    /// <summary>Cancellation terminates one invocation or logical stream without implying session termination.</summary>
    InvocationOrStream = 1,

    /// <summary>Cancellation terminates one logical direction while the opposite direction may continue.</summary>
    Direction = 2,

    /// <summary>Cancellation terminates the complete connection or interaction session.</summary>
    Session = 3
}

/// <summary>Semantic operation participating in one demanded atomic Channel boundary.</summary>
[JsonConverter(typeof(StrictStringEnumJsonConverterFactory))]
public enum ChannelAtomicOperationKind
{
    /// <summary>Authoritative semantic state mutation.</summary>
    StateMutation = 0,

    /// <summary>Outgoing Channel publication.</summary>
    Publication = 1,

    /// <summary>Incoming Channel delivery consumption.</summary>
    Consumption = 2,

    /// <summary>Produced message, event, signal, or Reply.</summary>
    ProducedOutput = 3,

    /// <summary>Durable application checkpoint advancement.</summary>
    ApplicationCheckpoint = 4,

    /// <summary>Provider completion or settlement.</summary>
    Settlement = 5,

    /// <summary>Durable Request admission.</summary>
    RequestAdmission = 6,

    /// <summary>Creation of the corresponding durable Reply obligation.</summary>
    ReplyObligation = 7
}

/// <summary>Transport-security property demanded of a Channel realization.</summary>
[JsonConverter(typeof(StrictStringEnumJsonConverterFactory))]
public enum ChannelSecurityKind
{
    /// <summary>Payload and metadata confidentiality in transit.</summary>
    Confidentiality = 0,

    /// <summary>Payload and metadata integrity in transit.</summary>
    Integrity = 1,

    /// <summary>Attributable authentication of the remote peer.</summary>
    PeerAuthentication = 2,

    /// <summary>Attributable propagation of canonical authority and tenant context.</summary>
    AuthorityPropagation = 3
}

/// <summary>Unit-bearing operating dimension constrained by a Channel target.</summary>
[JsonConverter(typeof(StrictStringEnumJsonConverterFactory))]
public enum ChannelLimitKind
{
    /// <summary>Maximum canonical payload bytes accepted by one logical delivery.</summary>
    PayloadBytes = 0,

    /// <summary>Maximum bytes accepted by one physical frame.</summary>
    FrameBytes = 1,

    /// <summary>Maximum bytes accepted by one datagram.</summary>
    DatagramBytes = 2,

    /// <summary>Maximum messages accepted by one settlement or delivery batch.</summary>
    BatchItems = 3,

    /// <summary>Maximum encoded bytes accepted by one settlement or delivery batch.</summary>
    BatchBytes = 4,

    /// <summary>Maximum simultaneously admitted but incomplete deliveries.</summary>
    InFlightDeliveries = 5,

    /// <summary>Minimum retained-history duration in milliseconds.</summary>
    RetentionMilliseconds = 6,

    /// <summary>Minimum bounded-resume duration in milliseconds.</summary>
    ResumeMilliseconds = 7,

    /// <summary>Minimum settlement-authority or lease duration in milliseconds.</summary>
    SettlementAuthorityMilliseconds = 8
}
