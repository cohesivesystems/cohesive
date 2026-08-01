namespace Cohesive.Execution;

/// <summary>A provider-neutral Channel payload paired with its stable logical emission identity.</summary>
/// <typeparam name="TPayload">Portable semantic payload interpreted by the bound Channel adapter.</typeparam>
/// <remarks>
/// The logical identity belongs to the payload and is preserved when the same payload crosses different physical
/// attempts, protocols, or Channel ports. Provider delivery and attempt identities belong to delivery evidence and
/// are intentionally not represented here. The payload value is retained without copying; producers and adapters
/// must therefore treat it as immutable for the lifetime of this carrier. <see cref="LogicalIdentity"/>, rather
/// than object or structural record equality, is the deduplication and replay lookup identity; conflicting reuse
/// for different canonical content must still be rejected.
/// </remarks>
public sealed record ChannelPayload<TPayload>
{
    /// <summary>Creates a logically identified Channel payload.</summary>
    /// <param name="logicalIdentity">Stable identity of the logical payload across retry and replay.</param>
    /// <param name="value">Non-null semantic payload value.</param>
    /// <exception cref="ArgumentException"><paramref name="logicalIdentity"/> is a default value.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    public ChannelPayload(EmissionId logicalIdentity, TPayload value)
    {
        if (string.IsNullOrWhiteSpace(logicalIdentity.Value))
        {
            throw new ArgumentException(
                "A Channel payload requires a stable logical emission identity.",
                nameof(logicalIdentity));
        }

        ArgumentNullException.ThrowIfNull(value);
        LogicalIdentity = logicalIdentity;
        Value = value;
    }

    /// <summary>Stable identity of the logical payload across retry and replay.</summary>
    public EmissionId LogicalIdentity { get; }

    /// <summary>Semantic payload value.</summary>
    public TPayload Value { get; }
}

/// <summary>A delivered payload carrying the mandatory replay position of a positioned Channel.</summary>
/// <typeparam name="TPayload">Portable semantic payload interpreted by the bound Channel adapter.</typeparam>
public sealed record ChannelPositionedDelivery<TPayload>
{
    /// <summary>Creates one positioned Channel delivery.</summary>
    /// <param name="payload">Logically identified semantic payload.</param>
    /// <param name="attempt">Physical delivery evidence containing the exact replay cursor.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="payload"/> or <paramref name="attempt"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="attempt"/> has no replay cursor.</exception>
    public ChannelPositionedDelivery(
        ChannelPayload<TPayload> payload,
        ChannelDeliveryAttemptEvidence attempt)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(attempt);
        Payload = payload;
        Attempt = attempt;
        if (attempt.ReplayCursor is null)
        {
            throw new ArgumentException(
                "A positioned Channel delivery requires exact replay-cursor evidence.",
                nameof(attempt));
        }
    }

    /// <summary>Logically identified semantic payload.</summary>
    public ChannelPayload<TPayload> Payload { get; }

    /// <summary>Physical delivery evidence containing the exact replay cursor.</summary>
    public ChannelDeliveryAttemptEvidence Attempt { get; }

    /// <summary>Exact non-null replay position carried by this delivery.</summary>
    public ChannelReplayCursor ReplayCursor => Attempt.ReplayCursor!;
}

/// <summary>A delivered payload carrying the mandatory ephemeral authority of a leased Channel receipt.</summary>
/// <typeparam name="TPayload">Portable semantic payload interpreted by the bound Channel adapter.</typeparam>
public sealed record ChannelLeasedDelivery<TPayload>
{
    /// <summary>Creates one leased Channel delivery.</summary>
    /// <param name="payload">Logically identified semantic payload.</param>
    /// <param name="attempt">Physical delivery evidence containing expiring settlement authority.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="payload"/> or <paramref name="attempt"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="attempt"/> has no settlement authority or the authority has no expiry.
    /// </exception>
    public ChannelLeasedDelivery(
        ChannelPayload<TPayload> payload,
        ChannelDeliveryAttemptEvidence attempt)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(attempt);
        Payload = payload;
        Attempt = attempt;
        if (attempt.SettlementAuthority is not { ExpiresAtUtc: not null })
        {
            throw new ArgumentException(
                "A leased Channel delivery requires expiring settlement authority.",
                nameof(attempt));
        }
    }

    /// <summary>Logically identified semantic payload.</summary>
    public ChannelPayload<TPayload> Payload { get; }

    /// <summary>Physical delivery evidence containing expiring settlement authority.</summary>
    public ChannelDeliveryAttemptEvidence Attempt { get; }

    /// <summary>Exact non-null expiring authority supplied for this delivery attempt.</summary>
    public ChannelSettlementAuthority SettlementAuthority => Attempt.SettlementAuthority!;
}

/// <summary>Capability port for admitting one-way publications to an exact realized Channel direction.</summary>
/// <typeparam name="TPayload">Portable semantic payload interpreted by the bound Channel adapter.</typeparam>
/// <remarks>
/// Completion means that the adapter admitted the publication according to its selected realization plan. It does
/// not imply delivery, durable replay, or settlement unless those guarantees are established independently. A port
/// instance is bound to one exact realized direction; the direction is therefore not repeated on every call. Unless
/// an implementation supplies stronger attributable evidence, a failed call does not prove that no publication was
/// admitted; retry and reconciliation policy must use the stable logical identity and selected realization.
/// </remarks>
public interface IChannelPublicationPort<TPayload>
{
    /// <summary>Admits one logically identified publication.</summary>
    /// <param name="payload">Logically identified semantic payload.</param>
    /// <param name="cancellationToken">Cancellation requested by the caller.</param>
    /// <returns>An operation that completes when adapter admission has completed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="payload"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is canceled.</exception>
    ValueTask PublishAsync(
        ChannelPayload<TPayload> payload,
        CancellationToken cancellationToken);
}

/// <summary>Capability port for one Request with one invocation-correlated terminal response.</summary>
/// <typeparam name="TRequest">Portable semantic request payload.</typeparam>
/// <typeparam name="TResponse">Portable semantic terminal response payload.</typeparam>
/// <remarks>
/// A failed call does not by itself prove that the target did not execute. Durable retry or reconciliation must use
/// the Request's stable logical identity and the guarantees of the selected realization.
/// </remarks>
public interface IChannelUnaryInvocationPort<TRequest, TResponse>
{
    /// <summary>Invokes one unary operation.</summary>
    /// <param name="request">Logically identified request payload.</param>
    /// <param name="cancellationToken">Cancellation requested by the caller.</param>
    /// <returns>The independently identified terminal response payload.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is canceled.</exception>
    ValueTask<ChannelPayload<TResponse>> InvokeAsync(
        ChannelPayload<TRequest> request,
        CancellationToken cancellationToken);
}

/// <summary>Capability port for a request-scoped stream whose iterator advancement expresses downstream demand.</summary>
/// <typeparam name="TRequest">Portable semantic stream request payload.</typeparam>
/// <typeparam name="TItem">Portable semantic stream item payload.</typeparam>
/// <remarks>
/// Advancing the returned iterator requests the next item. Implementations may prefetch only within the selected
/// bounded realization and must not reinterpret this port as an unbounded push buffer. Explicit credit windows are
/// a distinct capability and are not implied by this single-item demand surface.
/// </remarks>
public interface IChannelDemandStreamPort<TRequest, TItem>
{
    /// <summary>Opens one demand-driven response stream.</summary>
    /// <param name="request">Logically identified stream request payload.</param>
    /// <param name="cancellationToken">Cancellation applied to stream establishment and enumeration.</param>
    /// <returns>A demand-driven sequence of independently identified stream items.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> is canceled while establishing or enumerating the stream.
    /// </exception>
    IAsyncEnumerable<ChannelPayload<TItem>> StreamAsync(
        ChannelPayload<TRequest> request,
        CancellationToken cancellationToken);
}

/// <summary>Capability port for sending one independent boundary-preserving datagram.</summary>
/// <typeparam name="TPayload">Portable semantic datagram payload.</typeparam>
/// <remarks>
/// Datagram framing is independent of reliability. Successful completion does not by itself imply receipt,
/// reliability, ordering, replay, or settlement. A failed call likewise supplies no portable non-delivery proof.
/// </remarks>
public interface IChannelDatagramSendPort<TPayload>
{
    /// <summary>Sends one logically identified datagram.</summary>
    /// <param name="payload">Logically identified datagram payload.</param>
    /// <param name="cancellationToken">Cancellation requested by the caller.</param>
    /// <returns>An operation that completes after the adapter accepts the send attempt.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="payload"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is canceled.</exception>
    ValueTask SendAsync(
        ChannelPayload<TPayload> payload,
        CancellationToken cancellationToken);
}

/// <summary>Capability port for receiving one independent boundary-preserving datagram.</summary>
/// <typeparam name="TPayload">Portable semantic datagram payload.</typeparam>
/// <remarks>
/// Datagram framing is independent of reliability. Receipt does not by itself imply reliability, ordering, replay,
/// redelivery identity, or settlement authority.
/// </remarks>
public interface IChannelDatagramReceivePort<TPayload>
{
    /// <summary>Waits for one logically identified datagram.</summary>
    /// <param name="cancellationToken">Cancellation requested by the caller.</param>
    /// <returns>The next datagram accepted by the adapter.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is canceled.</exception>
    ValueTask<ChannelPayload<TPayload>> ReceiveAsync(CancellationToken cancellationToken);
}

/// <summary>Capability port for replaying positioned deliveries after an exact replay cursor.</summary>
/// <typeparam name="TPayload">Portable semantic payload stored by the positioned Channel.</typeparam>
/// <remarks>
/// This port deliberately requires a cursor and returns cursor-bearing deliveries. Bootstrap cursor discovery and
/// transient subscription are separate capabilities and are not implied by this contract.
/// </remarks>
public interface IChannelPositionedReplayPort<TPayload>
{
    /// <summary>Reads positioned deliveries strictly after one exact cursor.</summary>
    /// <param name="cursor">Exact exclusive replay position issued by the same Channel scope.</param>
    /// <param name="cancellationToken">Cancellation applied to replay establishment and enumeration.</param>
    /// <returns>A sequence whose every delivery contains exact replay-cursor evidence.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="cursor"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="cursor"/> belongs to another binding scope.</exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> is canceled while establishing or enumerating replay.
    /// </exception>
    IAsyncEnumerable<ChannelPositionedDelivery<TPayload>> ReplayAfterAsync(
        ChannelReplayCursor cursor,
        CancellationToken cancellationToken);
}

/// <summary>Capability port for receiving one delivery under expiring leased settlement authority.</summary>
/// <typeparam name="TPayload">Portable semantic payload carried by the leased delivery.</typeparam>
public interface IChannelLeasedReceiptPort<TPayload>
{
    /// <summary>Waits for one leased delivery.</summary>
    /// <param name="cancellationToken">Cancellation requested by the caller.</param>
    /// <returns>A delivery whose settlement authority expires after its attempt observation time.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is canceled.</exception>
    ValueTask<ChannelLeasedDelivery<TPayload>> ReceiveAsync(CancellationToken cancellationToken);
}

/// <summary>Capability port for a live subscription constrained by one already-compiled semantic selection.</summary>
/// <typeparam name="TSelection">Adapter binding of the canonical selection semantics.</typeparam>
/// <typeparam name="TPayload">Portable semantic payload selected by the subscription.</typeparam>
/// <remarks>
/// Selection is generic because different Channel realizations compile different selector languages. The type is
/// a bound selection artifact, not an invitation to pass provider query strings through the semantic layer.
/// </remarks>
public interface IChannelSelectiveSubscriptionPort<in TSelection, TPayload>
    where TSelection : notnull
{
    /// <summary>Opens one live selective subscription.</summary>
    /// <param name="selection">Compiled and bound semantic selection.</param>
    /// <param name="cancellationToken">Cancellation applied to subscription establishment and enumeration.</param>
    /// <returns>A live sequence of independently identified matching payloads.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="selection"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> is canceled while establishing or enumerating the subscription.
    /// </exception>
    IAsyncEnumerable<ChannelPayload<TPayload>> SubscribeAsync(
        TSelection selection,
        CancellationToken cancellationToken);
}

/// <summary>Capability port for one typed provider-settlement intent authorized by durable application progress.</summary>
/// <typeparam name="TSettlementIntent">
/// Binding-local intent that identifies the exact settlement operation and any required current authority.
/// </typeparam>
/// <remarks>
/// Keeping the intent typed avoids a universal acknowledgement structure with mutually exclusive optional fields.
/// Implementations must reject stale authority and progress that does not cover the settlement intent. A failed
/// call after provider dispatch can be ambiguous and must be reconciled against provider state before application
/// progress is changed or an incompatible settlement is attempted.
/// </remarks>
public interface IChannelSettlementPort<in TSettlementIntent>
    where TSettlementIntent : notnull
{
    /// <summary>Performs one provider settlement after validating durable progress coverage.</summary>
    /// <param name="intent">Exact typed settlement intent.</param>
    /// <param name="durableProgress">Durable replay, floor, and pending-delivery evidence covering the intent.</param>
    /// <param name="applicationProgress">Stable application-owned record authorizing provider settlement.</param>
    /// <param name="cancellationToken">Cancellation requested by the caller.</param>
    /// <returns>Attributable evidence of the completed provider settlement.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="intent"/>, <paramref name="durableProgress"/>, or <paramref name="applicationProgress"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The settlement authority is stale or the durable progress does not cover the exact settlement intent.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is canceled.</exception>
    ValueTask<ChannelSettlementReceipt> SettleAsync(
        TSettlementIntent intent,
        ChannelDurableProgressEvidence durableProgress,
        ChannelApplicationProgressReference applicationProgress,
        CancellationToken cancellationToken);
}

/// <summary>Capability port for executing one typed bundle inside a compiler-proven atomic Channel boundary.</summary>
/// <typeparam name="TOperation">Typed bundle of the semantic operations coupled by the boundary.</typeparam>
/// <typeparam name="TResult">Typed result of the committed atomic operation.</typeparam>
/// <remarks>
/// This port may be bound only when the realization plan proves the required operation set and atomic scope. It is
/// intentionally independent of publication, consumption, and settlement ports because none implies atomicity. An
/// exception observed after the physical commit boundary can be ambiguous; the typed operation and binding must
/// provide the identity or reconciliation evidence required by their selected realization.
/// </remarks>
public interface IChannelAtomicOperationPort<in TOperation, TResult>
    where TOperation : notnull
    where TResult : notnull
{
    /// <summary>Executes one typed operation bundle atomically within an exact semantic scope.</summary>
    /// <param name="scope">Exact atomic scope proven by the selected Channel realization.</param>
    /// <param name="operation">Typed bundle of operations that must commit together.</param>
    /// <param name="cancellationToken">Cancellation requested before the atomic commit boundary.</param>
    /// <returns>The typed committed result.</returns>
    /// <exception cref="ArgumentException"><paramref name="scope"/> is a default value.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="operation"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> is canceled before the implementation's atomic commit boundary.
    /// </exception>
    ValueTask<TResult> ExecuteAsync(
        ChannelAtomicScopeId scope,
        TOperation operation,
        CancellationToken cancellationToken);
}
