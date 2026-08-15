using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Cohesive.Execution;

/// <summary>Stable target-deduplication key for one canonical domain-event publication.</summary>
/// <remarks>
/// The canonical <see cref="EmissionId"/> remains the logical emission identity. This key scopes the envelope's
/// immutable idempotency value by authority and exact event contract so a target can safely deduplicate physical
/// publication redelivery without consulting mutable application state.
/// </remarks>
public sealed record DomainEventPublicationDeduplicationKey
{
    /// <summary>Creates a publication deduplication key from one exact canonical event envelope.</summary>
    /// <param name="authorityScope">Authority and optional tenant owning the event.</param>
    /// <param name="contract">Exact domain-event contract.</param>
    /// <param name="idempotencyKey">Stable canonical envelope idempotency key.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="authorityScope"/> or <paramref name="contract"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="idempotencyKey"/> is default.</exception>
    [JsonConstructor]
    public DomainEventPublicationDeduplicationKey(
        InteractionAuthorityScope authorityScope,
        DomainEventContractReference contract,
        InteractionIdempotencyKey idempotencyKey)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey.Value))
        {
            throw new ArgumentException(
                "Domain-event publication requires a stable idempotency key.",
                nameof(idempotencyKey));
        }

        AuthorityScope = Guard.RequireNotNull(authorityScope);
        Contract = Guard.RequireNotNull(contract);
        IdempotencyKey = idempotencyKey;
    }

    /// <summary>Authority and optional tenant owning the event.</summary>
    public InteractionAuthorityScope AuthorityScope { get; }

    /// <summary>Exact domain-event contract.</summary>
    public DomainEventContractReference Contract { get; }

    /// <summary>Stable canonical envelope idempotency key.</summary>
    public InteractionIdempotencyKey IdempotencyKey { get; }

    /// <summary>Derives the exact scoped key carried by one canonical domain event.</summary>
    /// <param name="domainEvent">Canonical event envelope.</param>
    /// <returns>The immutable target-deduplication key.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="domainEvent"/> is <see langword="null"/>.</exception>
    public static DomainEventPublicationDeduplicationKey From(DomainEventEnvelope domainEvent)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        return new(
            domainEvent.Context.AuthorityScope,
            domainEvent.Contract,
            domainEvent.Context.IdempotencyKey);
    }
}

/// <summary>Immutable canonical domain-event publication invocation supplied to an impure publisher.</summary>
public sealed record DomainEventPublicationInvocation
{
    /// <summary>Creates one exact target-deduplicated publication invocation.</summary>
    /// <param name="domainEvent">Canonical domain-event envelope, unchanged from its producer.</param>
    /// <param name="deduplicationKey">Scoped key derived from the envelope.</param>
    /// <exception cref="ArgumentNullException">Either argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// The key does not belong to the envelope, or the envelope does not demand durable after-origin visibility.
    /// </exception>
    [JsonConstructor]
    public DomainEventPublicationInvocation(
        DomainEventEnvelope domainEvent,
        DomainEventPublicationDeduplicationKey deduplicationKey)
    {
        DomainEvent = Guard.RequireNotNull(domainEvent);
        DeduplicationKey = Guard.RequireNotNull(deduplicationKey);
        if (deduplicationKey != DomainEventPublicationDeduplicationKey.From(domainEvent))
        {
            throw new ArgumentException(
                "Domain-event publication deduplication evidence belongs to another envelope.",
                nameof(deduplicationKey));
        }

        if (domainEvent.Context.Delivery.Durability != InteractionDurabilityDemand.Durable
            || domainEvent.Context.Delivery.Visibility != InteractionVisibilityDemand.AfterOriginCommit)
        {
            throw new ArgumentException(
                "Asynchronous domain-event publication requires durable after-origin-commit delivery.",
                nameof(domainEvent));
        }
    }

    /// <summary>Canonical domain-event envelope, unchanged from its producer.</summary>
    public DomainEventEnvelope DomainEvent { get; }

    /// <summary>Stable target-deduplication key derived from the envelope.</summary>
    public DomainEventPublicationDeduplicationKey DeduplicationKey { get; }

    /// <summary>Creates a publication invocation from one exact canonical envelope.</summary>
    /// <param name="domainEvent">Canonical domain-event envelope.</param>
    /// <returns>An exact publication invocation.</returns>
    public static DomainEventPublicationInvocation From(DomainEventEnvelope domainEvent) => new(
        domainEvent,
        DomainEventPublicationDeduplicationKey.From(domainEvent));
}

/// <summary>Bounded materialized acknowledgement returned by a domain-event publisher.</summary>
public sealed record DomainEventPublicationAcknowledgement
{
    /// <summary>Creates publisher acknowledgement evidence.</summary>
    /// <param name="evidence">Optional concrete target receipt.</param>
    /// <exception cref="ArgumentException"><paramref name="evidence"/> is unknown or failed.</exception>
    [JsonConstructor]
    public DomainEventPublicationAcknowledgement(PortableValue? evidence = null)
    {
        if (evidence is { State: PortableValueState.Unknown or PortableValueState.Failed })
        {
            throw new ArgumentException("Publication acknowledgement evidence must be materialized.", nameof(evidence));
        }

        Evidence = evidence;
    }

    /// <summary>Optional concrete target receipt.</summary>
    public PortableValue? Evidence { get; }
}

/// <summary>One canonical domain event durably admitted by a target-deduplicating inbox.</summary>
public sealed record DomainEventInboxEntry
{
    /// <summary>Creates immutable retained inbox evidence.</summary>
    /// <param name="deduplicationKey">Complete scoped target-deduplication identity.</param>
    /// <param name="domainEvent">Canonical retained event envelope.</param>
    /// <param name="contentFingerprint">Fingerprint of the complete canonical envelope bytes.</param>
    /// <param name="acceptedAtUtc">UTC time at which the target first admitted the entry.</param>
    /// <param name="acknowledgement">Stable materialized target receipt returned on first write and replay.</param>
    /// <exception cref="ArgumentNullException">A reference argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// The key does not belong to the envelope, the fingerprint does not match, or the accepted time is not UTC.
    /// </exception>
    [JsonConstructor]
    public DomainEventInboxEntry(
        DomainEventPublicationDeduplicationKey deduplicationKey,
        DomainEventEnvelope domainEvent,
        InteractionEnvelopeContentFingerprint contentFingerprint,
        DateTimeOffset acceptedAtUtc,
        DomainEventPublicationAcknowledgement acknowledgement)
    {
        DeduplicationKey = Guard.RequireNotNull(deduplicationKey);
        DomainEvent = Guard.RequireNotNull(domainEvent);
        ContentFingerprint = contentFingerprint;
        Acknowledgement = Guard.RequireNotNull(acknowledgement);
        if (deduplicationKey != DomainEventPublicationDeduplicationKey.From(domainEvent))
        {
            throw new ArgumentException(
                "A domain-event inbox key must belong to its retained canonical envelope.",
                nameof(deduplicationKey));
        }
        if (contentFingerprint != InteractionEnvelopeJsonSerializer.ComputeContentFingerprint(domainEvent))
        {
            throw new ArgumentException(
                "A domain-event inbox fingerprint must match its retained canonical envelope.",
                nameof(contentFingerprint));
        }
        if (acceptedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "A domain-event inbox acceptance time must use the UTC offset.",
                nameof(acceptedAtUtc));
        }

        AcceptedAtUtc = acceptedAtUtc;
    }

    /// <summary>Complete scoped target-deduplication identity.</summary>
    public DomainEventPublicationDeduplicationKey DeduplicationKey { get; }

    /// <summary>Canonical retained event envelope.</summary>
    public DomainEventEnvelope DomainEvent { get; }

    /// <summary>Fingerprint of the complete canonical envelope bytes.</summary>
    public InteractionEnvelopeContentFingerprint ContentFingerprint { get; }

    /// <summary>UTC time at which the target first admitted the entry.</summary>
    public DateTimeOffset AcceptedAtUtc { get; }

    /// <summary>Stable materialized target receipt returned on first write and replay.</summary>
    public DomainEventPublicationAcknowledgement Acknowledgement { get; }
}

/// <summary>Exact domain-event contracts one publisher durably deduplicates by canonical publication key.</summary>
public sealed record DomainEventPublisherCapabilities
{
    /// <summary>Creates publisher capabilities.</summary>
    /// <param name="targetDeduplicatedContracts">
    /// Exact contracts whose target suppresses repeated physical publication for the supplied canonical key.
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="targetDeduplicatedContracts"/> contains a null or repeated exact contract.
    /// </exception>
    [JsonConstructor]
    public DomainEventPublisherCapabilities(
        ImmutableArray<DomainEventContractReference> targetDeduplicatedContracts)
    {
        var normalized = targetDeduplicatedContracts.IsDefault ? [] : targetDeduplicatedContracts;
        if (normalized.Any(static contract => contract is null))
        {
            throw new ArgumentException("Domain-event publisher capabilities cannot contain null contracts.",
                nameof(targetDeduplicatedContracts));
        }

        var ordered = normalized.Sort(Comparer<DomainEventContractReference>.Create(static (left, right) =>
            ExecutionDefinitionReference.CompareCanonical(left.Definition, right.Definition)));
        if (ordered.Distinct().Count() != ordered.Length)
        {
            throw new ArgumentException("Domain-event publisher capabilities cannot repeat an exact contract.",
                nameof(targetDeduplicatedContracts));
        }

        TargetDeduplicatedContracts = ordered;
    }

    /// <summary>Exact contracts with target-deduplication evidence.</summary>
    public ImmutableArray<DomainEventContractReference> TargetDeduplicatedContracts { get; }

    /// <summary>Whether this publisher declares target deduplication for one exact event contract.</summary>
    /// <param name="contract">Exact contract to inspect.</param>
    /// <returns><see langword="true"/> only for a declared exact contract.</returns>
    public bool Supports(DomainEventContractReference contract)
    {
        ArgumentNullException.ThrowIfNull(contract);
        return TargetDeduplicatedContracts.Contains(contract);
    }

    /// <summary>Compares capabilities by normalized exact contract set.</summary>
    /// <param name="other">Capabilities to compare.</param>
    /// <returns><see langword="true"/> when exact supported contract sets are equal.</returns>
    public bool Equals(DomainEventPublisherCapabilities? other) =>
        ReferenceEquals(this, other)
        || other is not null
        && TargetDeduplicatedContracts.SequenceEqual(other.TargetDeduplicatedContracts);

    /// <summary>Returns a structural hash over the normalized exact contract set.</summary>
    /// <returns>Structural hash code.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var contract in TargetDeduplicatedContracts)
        {
            hash.Add(contract);
        }
        return hash.ToHashCode();
    }
}

/// <summary>Impure provider-neutral boundary for target-deduplicated canonical domain-event publication.</summary>
/// <remarks>
/// Implementations MUST publish the supplied envelope unchanged, preserve its ordering requirements, and use the
/// supplied scoped key for durable target deduplication. Physical invocation may be repeated after an ambiguous
/// failure; declaring a contract in <see cref="Capabilities"/> is an operational guarantee, not a hint.
/// </remarks>
public interface IDomainEventPublisher
{
    /// <summary>Exact target-deduplicated event contracts supported by this publisher.</summary>
    DomainEventPublisherCapabilities Capabilities { get; }

    /// <summary>Publishes one exact canonical domain event.</summary>
    /// <param name="context">Explicit cancellation, time, identity, and tracing context.</param>
    /// <param name="invocation">Immutable canonical event and target-deduplication key.</param>
    /// <returns>Bounded materialized target acknowledgement.</returns>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="context"/> requests cancellation before acknowledgement.
    /// </exception>
    /// <remarks>
    /// Implementations may also throw a provider-specific exception. A thrown exception supplies no publication
    /// acknowledgement and may represent an ambiguous target outcome; a caller may redeliver the same invocation.
    /// </remarks>
    ValueTask<DomainEventPublicationAcknowledgement> PublishAsync(
        OperationContext context,
        DomainEventPublicationInvocation invocation);
}

/// <summary>Durable target-deduplicating publisher whose retained canonical entries are addressable by exact key.</summary>
/// <remarks>
/// The inbox is a publication target and durable handoff boundary, not a competing domain-event authority. It must
/// retain the supplied canonical envelope unchanged. Repeating an exact invocation returns the original stable
/// acknowledgement; reusing the same key for different canonical content fails closed.
/// </remarks>
public interface IDomainEventInbox : IDomainEventPublisher
{
    /// <summary>Validates that the physical target is available and preserves the declared durable semantics.</summary>
    /// <param name="context">Explicit cancellation, time, identity, and tracing context.</param>
    /// <returns>A task that completes only when the target is ready and semantically compatible.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="context"/> requests cancellation.</exception>
    /// <remarks>
    /// Applications should complete this admission check before starting a worker that can publish to the inbox.
    /// Implementations throw a provider or configuration exception when availability, identity, or retention cannot
    /// preserve <see cref="IDomainEventPublisher.Capabilities"/>.
    /// </remarks>
    ValueTask ValidateAsync(OperationContext context);

    /// <summary>Point-reads one retained canonical event by its complete scoped publication key.</summary>
    /// <param name="context">Explicit cancellation, time, identity, and tracing context.</param>
    /// <param name="deduplicationKey">Complete target-deduplication identity.</param>
    /// <returns>The retained entry, or <see langword="null"/> when the exact key is absent.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="context"/> requests cancellation.</exception>
    ValueTask<DomainEventInboxEntry?> TryReadAsync(
        OperationContext context,
        DomainEventPublicationDeduplicationKey deduplicationKey);
}

/// <summary>Resolves one publisher for an exact canonical domain-event contract.</summary>
/// <remarks>
/// Resolution is deployment policy. A replaying runtime must resolve the same effective publisher capabilities for
/// the same exact contract and must reject ambiguous or missing registrations before external I/O.
/// </remarks>
public interface IDomainEventPublisherResolver
{
    /// <summary>Attempts to resolve one publisher for an exact event contract.</summary>
    /// <param name="contract">Exact canonical event contract.</param>
    /// <param name="publisher">Receives the resolved publisher when available.</param>
    /// <returns><see langword="true"/> when exactly one publisher is available.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="contract"/> is <see langword="null"/>.</exception>
    bool TryResolve(DomainEventContractReference contract, out IDomainEventPublisher? publisher);
}

/// <summary>Immutable exact-contract resolver derived from publisher capability declarations.</summary>
/// <remarks>
/// Publisher capabilities are the sole registration authority. The catalog rejects overlapping declarations so
/// resolution remains deterministic without a parallel application-maintained contract-to-publisher map.
/// </remarks>
public sealed class DomainEventPublisherCatalog : IDomainEventPublisherResolver
{
    readonly ImmutableDictionary<DomainEventContractReference, IDomainEventPublisher> publishers;

    /// <summary>Builds one exact resolver from publisher capability declarations.</summary>
    /// <param name="publishers">Publishers whose exact supported contracts form the catalog.</param>
    /// <exception cref="ArgumentNullException"><paramref name="publishers"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// A publisher or its capabilities are null, or multiple publishers declare the same exact contract.
    /// </exception>
    public DomainEventPublisherCatalog(IEnumerable<IDomainEventPublisher> publishers)
    {
        ArgumentNullException.ThrowIfNull(publishers);
        var catalog = ImmutableDictionary.CreateBuilder<DomainEventContractReference, IDomainEventPublisher>();
        foreach (var publisher in publishers)
        {
            if (publisher is null)
                throw new ArgumentException("A domain-event publisher catalog cannot contain null entries.", nameof(publishers));
            if (publisher.Capabilities is null)
                throw new ArgumentException("A domain-event publisher catalog requires explicit capabilities.", nameof(publishers));

            foreach (var contract in publisher.Capabilities.TargetDeduplicatedContracts)
            {
                if (!catalog.TryAdd(contract, publisher))
                {
                    throw new ArgumentException(
                        $"Exact domain-event contract '{Describe(contract)}' is declared by multiple publishers.",
                        nameof(publishers));
                }
            }
        }

        this.publishers = catalog.ToImmutable();
    }

    /// <summary>Number of exact contract registrations derived from publisher capabilities.</summary>
    public int Count => publishers.Count;

    /// <inheritdoc />
    public bool TryResolve(DomainEventContractReference contract, out IDomainEventPublisher? publisher)
    {
        ArgumentNullException.ThrowIfNull(contract);
        return publishers.TryGetValue(contract, out publisher);
    }

    static string Describe(DomainEventContractReference contract) =>
        $"{contract.Definition.DefinitionId.Value}@{contract.Definition.RevisionId.Value}#{contract.Definition.Fingerprint.Value}";
}

/// <summary>Publisher resolver that deliberately supports no domain-event contract.</summary>
public sealed class EmptyDomainEventPublisherResolver : IDomainEventPublisherResolver
{
    /// <summary>Shared stateless empty resolver.</summary>
    public static EmptyDomainEventPublisherResolver Instance { get; } = new();

    EmptyDomainEventPublisherResolver()
    {
    }

    /// <inheritdoc />
    public bool TryResolve(DomainEventContractReference contract, out IDomainEventPublisher? publisher)
    {
        ArgumentNullException.ThrowIfNull(contract);
        publisher = null;
        return false;
    }
}
