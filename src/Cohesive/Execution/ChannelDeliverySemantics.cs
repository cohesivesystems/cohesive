using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Cohesive.Model.Serialization;

namespace Cohesive.Execution;

/// <summary>Stable identity of one exact logical or physical Channel binding scope.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct ChannelScopeId
{
    /// <summary>Creates a Channel scope identity.</summary>
    /// <param name="value">Stable binding-scope identity.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white-space.</exception>
    [JsonConstructor]
    public ChannelScopeId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw stable scope identity.</summary>
    public string Value { get; }

    /// <summary>Returns the raw stable scope identity.</summary>
    /// <returns>The value supplied at construction.</returns>
    public override string ToString() => Value;
}

/// <summary>Stable ordering domain within one exact Channel binding scope.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct ChannelOrderingDomainId
{
    /// <summary>Creates an ordering-domain identity.</summary>
    /// <param name="value">Stable binding-local ordering-domain identity.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white-space.</exception>
    [JsonConstructor]
    public ChannelOrderingDomainId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw stable ordering-domain identity.</summary>
    public string Value { get; }

    /// <summary>Returns the raw stable ordering-domain identity.</summary>
    /// <returns>The value supplied at construction.</returns>
    public override string ToString() => Value;
}

/// <summary>Stable provider delivery identity that survives redelivery when the target supplies one.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct ChannelProviderDeliveryId
{
    /// <summary>Creates a provider delivery identity.</summary>
    /// <param name="value">Stable provider-defined delivery identity.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white-space.</exception>
    [JsonConstructor]
    public ChannelProviderDeliveryId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw stable provider delivery identity.</summary>
    public string Value { get; }

    /// <summary>Returns the raw stable provider delivery identity.</summary>
    /// <returns>The value supplied at construction.</returns>
    public override string ToString() => Value;
}

/// <summary>Identity of one physical Channel delivery attempt, distinct from logical and provider identities.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct ChannelDeliveryAttemptId
{
    /// <summary>Creates a delivery-attempt identity.</summary>
    /// <param name="value">Attempt-local identity that changes for a new physical delivery attempt.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white-space.</exception>
    [JsonConstructor]
    public ChannelDeliveryAttemptId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw delivery-attempt identity.</summary>
    public string Value { get; }

    /// <summary>Returns the raw delivery-attempt identity.</summary>
    /// <returns>The value supplied at construction.</returns>
    public override string ToString() => Value;
}

/// <summary>Identity of ephemeral provider authority used to settle one current delivery attempt.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct ChannelSettlementAuthorityId
{
    /// <summary>Creates a settlement-authority identity.</summary>
    /// <param name="value">Opaque attempt-local authority identity.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white-space.</exception>
    [JsonConstructor]
    public ChannelSettlementAuthorityId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw settlement-authority identity.</summary>
    public string Value { get; }

    /// <summary>Returns the raw settlement-authority identity.</summary>
    /// <returns>The value supplied at construction.</returns>
    public override string ToString() => Value;
}

/// <summary>Stable identity of the provider scope changed by one settlement operation.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct ChannelSettlementCouplingId
{
    /// <summary>Creates a settlement-coupling identity.</summary>
    /// <param name="value">Stable provider-neutral coupling-scope identity.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white-space.</exception>
    [JsonConstructor]
    public ChannelSettlementCouplingId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw settlement-coupling identity.</summary>
    public string Value { get; }

    /// <summary>Returns the raw settlement-coupling identity.</summary>
    /// <returns>The value supplied at construction.</returns>
    public override string ToString() => Value;
}

/// <summary>Scoped stable reference to an already-durable application checkpoint or equivalent progress record.</summary>
public sealed record ChannelApplicationProgressReference
{
    /// <summary>Creates a durable application-progress reference.</summary>
    /// <param name="scope">Exact Channel scope whose application progress is durable.</param>
    /// <param name="value">Stable reference owned by the consuming semantic block's progress authority.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="scope"/> is default or <paramref name="value"/> is empty or white-space.</exception>
    [JsonConstructor]
    public ChannelApplicationProgressReference(ChannelScopeId scope, string value)
    {
        ChannelReplayCursor.Require(scope.Value, nameof(scope));
        Scope = scope;
        Value = Guard.RequireNotNullOrWhiteSpace(value);
    }

    /// <summary>Exact Channel scope whose application progress is durable.</summary>
    public ChannelScopeId Scope { get; }

    /// <summary>Raw stable durable-progress reference.</summary>
    public string Value { get; }

    /// <summary>Returns the scope-qualified durable-progress reference.</summary>
    /// <returns>The Channel scope and authority-owned reference.</returns>
    public override string ToString() => $"{Scope.Value}/{Value}";
}

/// <summary>Opaque versioned replay position, independently of durable application progress.</summary>
public sealed record ChannelReplayCursor
{
    /// <summary>Creates a Channel replay cursor.</summary>
    /// <param name="formatVersion">Positive version of the opaque cursor representation.</param>
    /// <param name="scope">Exact Channel binding scope that issued the cursor.</param>
    /// <param name="orderingDomain">Exact domain within which the cursor has replay meaning.</param>
    /// <param name="value">Opaque non-empty cursor value.</param>
    /// <param name="validUntilUtc">Optional UTC retention or validity boundary.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="scope"/>, <paramref name="orderingDomain"/>, or <paramref name="value"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">An identity is default, the cursor is empty, or time is not UTC.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="formatVersion"/> is not positive.</exception>
    [JsonConstructor]
    public ChannelReplayCursor(
        int formatVersion,
        ChannelScopeId scope,
        ChannelOrderingDomainId orderingDomain,
        string value,
        DateTimeOffset? validUntilUtc = null)
    {
        if (formatVersion <= 0)
            throw new ArgumentOutOfRangeException(nameof(formatVersion), formatVersion, "A replay-cursor version must be positive.");
        Require(scope.Value, nameof(scope));
        Require(orderingDomain.Value, nameof(orderingDomain));
        if (validUntilUtc is { } boundary)
            RequireUtc(boundary, nameof(validUntilUtc));

        FormatVersion = formatVersion;
        Scope = scope;
        OrderingDomain = orderingDomain;
        Value = Guard.RequireNotNullOrWhiteSpace(value);
        ValidUntilUtc = validUntilUtc;
    }

    /// <summary>Positive version of the opaque cursor representation.</summary>
    public int FormatVersion { get; }

    /// <summary>Exact Channel binding scope that issued the cursor.</summary>
    public ChannelScopeId Scope { get; }

    /// <summary>Exact domain within which the cursor has replay meaning.</summary>
    public ChannelOrderingDomainId OrderingDomain { get; }

    /// <summary>Opaque cursor value.</summary>
    public string Value { get; }

    /// <summary>Optional UTC retention or validity boundary.</summary>
    public DateTimeOffset? ValidUntilUtc { get; }

    internal static void Require(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A Channel runtime identity cannot be default.", parameterName);
    }

    internal static void RequireUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
            throw new ArgumentException("A Channel runtime timestamp must be UTC.", parameterName);
    }
}

/// <summary>Ephemeral provider authority for the current delivery attempt or settlement operation.</summary>
/// <remarks>This value is runtime evidence and MUST NOT be persisted as logical identity or application progress.</remarks>
public sealed record ChannelSettlementAuthority
{
    /// <summary>Creates ephemeral settlement authority.</summary>
    /// <param name="id">Attempt-local opaque authority identity.</param>
    /// <param name="attempt">Exact delivery attempt authorized by this value.</param>
    /// <param name="coupling">Provider scope changed by settlement.</param>
    /// <param name="expiresAtUtc">Optional UTC authority-expiry time.</param>
    /// <exception cref="ArgumentException">An identity is default or time is not UTC.</exception>
    [JsonConstructor]
    public ChannelSettlementAuthority(
        ChannelSettlementAuthorityId id,
        ChannelDeliveryAttemptId attempt,
        ChannelSettlementCouplingId coupling,
        DateTimeOffset? expiresAtUtc = null)
    {
        ChannelReplayCursor.Require(id.Value, nameof(id));
        ChannelReplayCursor.Require(attempt.Value, nameof(attempt));
        ChannelReplayCursor.Require(coupling.Value, nameof(coupling));
        if (expiresAtUtc is { } expiry)
            ChannelReplayCursor.RequireUtc(expiry, nameof(expiresAtUtc));

        Id = id;
        Attempt = attempt;
        Coupling = coupling;
        ExpiresAtUtc = expiresAtUtc;
    }

    /// <summary>Attempt-local opaque authority identity.</summary>
    public ChannelSettlementAuthorityId Id { get; }

    /// <summary>Exact delivery attempt authorized by this value.</summary>
    public ChannelDeliveryAttemptId Attempt { get; }

    /// <summary>Provider scope changed by settlement.</summary>
    public ChannelSettlementCouplingId Coupling { get; }

    /// <summary>Optional UTC authority-expiry time.</summary>
    public DateTimeOffset? ExpiresAtUtc { get; }
}

/// <summary>Runtime evidence for one physical delivery attempt, excluding payload logical identity.</summary>
public sealed record ChannelDeliveryAttemptEvidence
{
    /// <summary>Creates one Channel delivery-attempt observation.</summary>
    /// <param name="attempt">Identity of this physical attempt.</param>
    /// <param name="observedAtUtc">UTC time at which the attempt became observable.</param>
    /// <param name="scope">Exact logical or physical Channel binding scope in which delivery was observed.</param>
    /// <param name="providerDelivery">Optional stable provider identity retained across redelivery.</param>
    /// <param name="replayCursor">Optional replay position associated with this delivery.</param>
    /// <param name="settlementAuthority">Optional ephemeral authority for this attempt.</param>
    /// <param name="evidenceReference">Optional opaque provider evidence reference.</param>
    /// <exception cref="ArgumentException">
    /// An identity is default, time is not UTC, evidence is white-space, settlement authority belongs to another
    /// attempt, or replay/settlement authority is already expired when the attempt is observed.
    /// </exception>
    [JsonConstructor]
    public ChannelDeliveryAttemptEvidence(
        ChannelDeliveryAttemptId attempt,
        DateTimeOffset observedAtUtc,
        ChannelScopeId scope,
        ChannelProviderDeliveryId? providerDelivery = null,
        ChannelReplayCursor? replayCursor = null,
        ChannelSettlementAuthority? settlementAuthority = null,
        string? evidenceReference = null)
    {
        ChannelReplayCursor.Require(attempt.Value, nameof(attempt));
        ChannelReplayCursor.RequireUtc(observedAtUtc, nameof(observedAtUtc));
        ChannelReplayCursor.Require(scope.Value, nameof(scope));
        if (providerDelivery is { } delivery)
            ChannelReplayCursor.Require(delivery.Value, nameof(providerDelivery));
        if (replayCursor is not null && replayCursor.Scope != scope)
        {
            throw new ArgumentException(
                "A delivery replay cursor must belong to the exact observed Channel scope.",
                nameof(replayCursor));
        }
        if (replayCursor?.ValidUntilUtc is { } cursorExpiry && cursorExpiry <= observedAtUtc)
        {
            throw new ArgumentException(
                "A delivery replay cursor must remain valid after the delivery attempt is observed.",
                nameof(replayCursor));
        }
        if (settlementAuthority is not null && settlementAuthority.Attempt != attempt)
        {
            throw new ArgumentException(
                "Settlement authority must belong to the exact delivery attempt.",
                nameof(settlementAuthority));
        }
        if (settlementAuthority?.ExpiresAtUtc is { } authorityExpiry
            && authorityExpiry <= observedAtUtc)
        {
            throw new ArgumentException(
                "Settlement authority must remain valid after the delivery attempt is observed.",
                nameof(settlementAuthority));
        }
        if (evidenceReference is not null && string.IsNullOrWhiteSpace(evidenceReference))
            throw new ArgumentException("An optional delivery evidence reference cannot be white-space.", nameof(evidenceReference));

        Attempt = attempt;
        ObservedAtUtc = observedAtUtc;
        Scope = scope;
        ProviderDelivery = providerDelivery;
        ReplayCursor = replayCursor;
        SettlementAuthority = settlementAuthority;
        EvidenceReference = evidenceReference;
    }

    /// <summary>Identity of this physical attempt.</summary>
    public ChannelDeliveryAttemptId Attempt { get; }

    /// <summary>UTC time at which the attempt became observable.</summary>
    public DateTimeOffset ObservedAtUtc { get; }

    /// <summary>Exact logical or physical Channel binding scope in which delivery was observed.</summary>
    public ChannelScopeId Scope { get; }

    /// <summary>Optional stable provider identity retained across redelivery.</summary>
    public ChannelProviderDeliveryId? ProviderDelivery { get; }

    /// <summary>Optional replay position associated with this delivery.</summary>
    public ChannelReplayCursor? ReplayCursor { get; }

    /// <summary>Optional ephemeral authority for this attempt.</summary>
    public ChannelSettlementAuthority? SettlementAuthority { get; }

    /// <summary>Optional opaque provider evidence reference.</summary>
    public string? EvidenceReference { get; }
}

/// <summary>Closed durable Channel progress-floor evidence family.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = ChannelWireNames.ProgressFloorDiscriminator)]
[JsonDerivedType(typeof(ChannelReplayCursorProgressFloor), ChannelWireNames.ReplayCursorFloor)]
[JsonDerivedType(typeof(ChannelProviderDeliveryProgressFloor), ChannelWireNames.ProviderDeliveryFloor)]
[JsonDerivedType(typeof(ChannelTargetManagedProgressFloor), ChannelWireNames.TargetManagedFloor)]
public abstract record ChannelProgressFloor
{
    private protected ChannelProgressFloor()
    {
    }

    internal abstract void EnsureDeclaredVariant();
}

/// <summary>Durable cumulative progress through one replay cursor.</summary>
public sealed record ChannelReplayCursorProgressFloor : ChannelProgressFloor
{
    internal override void EnsureDeclaredVariant() { }

    /// <summary>Creates a replay-cursor progress floor.</summary>
    /// <param name="cursor">Applied or acknowledged cursor.</param>
    /// <exception cref="ArgumentNullException"><paramref name="cursor"/> is <see langword="null"/>.</exception>
    [JsonConstructor]
    public ChannelReplayCursorProgressFloor(ChannelReplayCursor cursor) => Cursor = Guard.RequireNotNull(cursor);

    /// <summary>Applied or acknowledged cursor.</summary>
    public ChannelReplayCursor Cursor { get; }
}

/// <summary>Durable cumulative progress through one stable provider delivery identity.</summary>
public sealed record ChannelProviderDeliveryProgressFloor : ChannelProgressFloor
{
    internal override void EnsureDeclaredVariant() { }

    /// <summary>Creates a provider-delivery progress floor.</summary>
    /// <param name="scope">Exact Channel binding scope owning the floor.</param>
    /// <param name="orderingDomain">Exact ordered domain advanced by the floor.</param>
    /// <param name="delivery">Stable provider delivery identity at the cumulative floor.</param>
    /// <exception cref="ArgumentException">An identity is default.</exception>
    [JsonConstructor]
    public ChannelProviderDeliveryProgressFloor(
        ChannelScopeId scope,
        ChannelOrderingDomainId orderingDomain,
        ChannelProviderDeliveryId delivery)
    {
        ChannelReplayCursor.Require(scope.Value, nameof(scope));
        ChannelReplayCursor.Require(orderingDomain.Value, nameof(orderingDomain));
        ChannelReplayCursor.Require(delivery.Value, nameof(delivery));
        Scope = scope;
        OrderingDomain = orderingDomain;
        Delivery = delivery;
    }

    /// <summary>Exact Channel binding scope owning the floor.</summary>
    public ChannelScopeId Scope { get; }

    /// <summary>Exact ordered domain advanced by the floor.</summary>
    public ChannelOrderingDomainId OrderingDomain { get; }

    /// <summary>Stable provider delivery identity at the cumulative floor.</summary>
    public ChannelProviderDeliveryId Delivery { get; }
}

/// <summary>Durable attributable target-managed acknowledgement floor.</summary>
public sealed record ChannelTargetManagedProgressFloor : ChannelProgressFloor
{
    internal override void EnsureDeclaredVariant() { }

    /// <summary>Creates a target-managed progress floor.</summary>
    /// <param name="formatVersion">Positive representation version.</param>
    /// <param name="scope">Exact Channel scope.</param>
    /// <param name="value">Opaque non-empty snapshot value.</param>
    /// <exception cref="ArgumentException">An identity or value is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="formatVersion"/> is not positive.</exception>
    [JsonConstructor]
    public ChannelTargetManagedProgressFloor(int formatVersion, ChannelScopeId scope, string value)
    {
        if (formatVersion <= 0)
            throw new ArgumentOutOfRangeException(nameof(formatVersion), formatVersion, "A target-managed floor version must be positive.");
        ChannelReplayCursor.Require(scope.Value, nameof(scope));
        FormatVersion = formatVersion;
        Scope = scope;
        Value = Guard.RequireNotNullOrWhiteSpace(value);
    }

    /// <summary>Positive representation version.</summary>
    public int FormatVersion { get; }

    /// <summary>Exact Channel scope.</summary>
    public ChannelScopeId Scope { get; }

    /// <summary>Opaque target-managed floor value.</summary>
    public string Value { get; }
}

/// <summary>Closed durable non-prefix Channel progress evidence family.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = ChannelWireNames.PendingProgressDiscriminator)]
[JsonDerivedType(typeof(ChannelStableDeliverySetProgress), ChannelWireNames.StableDeliverySet)]
[JsonDerivedType(typeof(ChannelUnresolvedGapProgress), ChannelWireNames.UnresolvedGaps)]
[JsonDerivedType(typeof(ChannelTargetManagedPendingProgress), ChannelWireNames.TargetManagedPending)]
public abstract record ChannelPendingProgress
{
    private protected ChannelPendingProgress()
    {
    }

    internal abstract void EnsureDeclaredVariant();
}

/// <summary>Exact durable set of stable provider delivery identities.</summary>
public sealed record ChannelStableDeliverySetProgress : ChannelPendingProgress
{
    internal override void EnsureDeclaredVariant() { }

    /// <summary>Creates exact stable-delivery set progress.</summary>
    /// <param name="scope">Exact Channel binding scope owning the stable identities.</param>
    /// <param name="deliveries">The complete, possibly empty, set of stable provider delivery identities.</param>
    /// <exception cref="ArgumentException">The scope is default or <paramref name="deliveries"/> contains a duplicated or default identity.</exception>
    [JsonConstructor]
    public ChannelStableDeliverySetProgress(
        ChannelScopeId scope,
        ImmutableArray<ChannelProviderDeliveryId> deliveries)
    {
        ChannelReplayCursor.Require(scope.Value, nameof(scope));
        Scope = scope;
        Deliveries = NormalizeDeliveries(deliveries, nameof(deliveries), requireNonEmpty: false);
    }

    /// <summary>Exact Channel binding scope owning the stable identities.</summary>
    public ChannelScopeId Scope { get; }

    /// <summary>Stable provider delivery identities in deterministic ordinal order.</summary>
    [JsonRequired]
    [JsonInclude]
    public ImmutableArray<ChannelProviderDeliveryId> Deliveries { get; private init; }

    /// <summary>Compares exact stable-delivery sets structurally.</summary>
    /// <param name="other">Other progress evidence.</param>
    /// <returns><see langword="true"/> when both normalized sets are equal.</returns>
    public bool Equals(ChannelStableDeliverySetProgress? other) =>
        ReferenceEquals(this, other)
        || other is not null && Scope == other.Scope && Deliveries.SequenceEqual(other.Deliveries);

    /// <summary>Returns a structural hash code for the stable-delivery set.</summary>
    /// <returns>A hash code derived from every stable delivery identity.</returns>
    public override int GetHashCode() => Hash(Scope, Deliveries);

    internal static ImmutableArray<ChannelProviderDeliveryId> NormalizeDeliveries(
        ImmutableArray<ChannelProviderDeliveryId> deliveries,
        string parameterName,
        bool requireNonEmpty = true)
    {
        if (deliveries.IsDefaultOrEmpty)
        {
            if (requireNonEmpty)
                throw new ArgumentException("Delivery evidence requires at least one stable identity.", parameterName);
            return [];
        }

        HashSet<ChannelProviderDeliveryId> observed = [];
        var canonical = true;
        ChannelProviderDeliveryId? previous = null;
        foreach (var delivery in deliveries)
        {
            ChannelReplayCursor.Require(delivery.Value, parameterName);
            if (!observed.Add(delivery))
                throw new ArgumentException($"Stable delivery identity '{delivery.Value}' is duplicated.", parameterName);
            if (previous is { } prior && StringComparer.Ordinal.Compare(prior.Value, delivery.Value) > 0)
                canonical = false;
            previous = delivery;
        }
        if (canonical)
            return deliveries;

        var sorted = ImmutableArray.CreateBuilder<ChannelProviderDeliveryId>(deliveries.Length);
        sorted.AddRange(deliveries);
        sorted.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Value, right.Value));
        return sorted.MoveToImmutable();
    }

    internal static int Hash(
        ChannelScopeId scope,
        ImmutableArray<ChannelProviderDeliveryId> deliveries)
    {
        var hash = new HashCode();
        hash.Add(scope);
        foreach (var delivery in deliveries)
            hash.Add(delivery);
        return hash.ToHashCode();
    }
}

/// <summary>Exact unresolved delivery gaps retained alongside an independent cumulative floor.</summary>
public sealed record ChannelUnresolvedGapProgress : ChannelPendingProgress
{
    internal override void EnsureDeclaredVariant() { }

    /// <summary>Creates unresolved-gap progress evidence.</summary>
    /// <param name="scope">Exact Channel binding scope owning the unresolved identities.</param>
    /// <param name="deliveries">The complete, possibly empty, set of unresolved stable provider delivery identities.</param>
    /// <exception cref="ArgumentException">The scope is default or <paramref name="deliveries"/> contains a duplicated or default identity.</exception>
    [JsonConstructor]
    public ChannelUnresolvedGapProgress(
        ChannelScopeId scope,
        ImmutableArray<ChannelProviderDeliveryId> deliveries)
    {
        ChannelReplayCursor.Require(scope.Value, nameof(scope));
        Scope = scope;
        Deliveries = ChannelStableDeliverySetProgress.NormalizeDeliveries(
            deliveries,
            nameof(deliveries),
            requireNonEmpty: false);
    }

    /// <summary>Exact Channel binding scope owning the unresolved identities.</summary>
    public ChannelScopeId Scope { get; }

    /// <summary>Unresolved stable provider delivery identities in deterministic ordinal order.</summary>
    [JsonRequired]
    [JsonInclude]
    public ImmutableArray<ChannelProviderDeliveryId> Deliveries { get; private init; }

    /// <summary>Compares unresolved-gap evidence structurally.</summary>
    /// <param name="other">Other progress evidence.</param>
    /// <returns><see langword="true"/> when both normalized sets are equal.</returns>
    public bool Equals(ChannelUnresolvedGapProgress? other) =>
        ReferenceEquals(this, other)
        || other is not null && Scope == other.Scope && Deliveries.SequenceEqual(other.Deliveries);

    /// <summary>Returns a structural hash code for unresolved gaps.</summary>
    /// <returns>A hash code derived from every stable delivery identity.</returns>
    public override int GetHashCode() => ChannelStableDeliverySetProgress.Hash(Scope, Deliveries);
}

/// <summary>Opaque attributable target-managed pending-delivery snapshot.</summary>
public sealed record ChannelTargetManagedPendingProgress : ChannelPendingProgress
{
    internal override void EnsureDeclaredVariant() { }

    /// <summary>Creates target-managed pending progress.</summary>
    /// <param name="formatVersion">Positive representation version.</param>
    /// <param name="scope">Exact Channel scope.</param>
    /// <param name="value">Opaque non-empty pending-state value.</param>
    /// <exception cref="ArgumentException">An identity or value is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="formatVersion"/> is not positive.</exception>
    [JsonConstructor]
    public ChannelTargetManagedPendingProgress(int formatVersion, ChannelScopeId scope, string value)
    {
        if (formatVersion <= 0)
            throw new ArgumentOutOfRangeException(nameof(formatVersion), formatVersion, "A pending-progress version must be positive.");
        ChannelReplayCursor.Require(scope.Value, nameof(scope));
        FormatVersion = formatVersion;
        Scope = scope;
        Value = Guard.RequireNotNullOrWhiteSpace(value);
    }

    /// <summary>Positive representation version.</summary>
    public int FormatVersion { get; }

    /// <summary>Exact Channel scope.</summary>
    public ChannelScopeId Scope { get; }

    /// <summary>Opaque target-managed pending-state value.</summary>
    public string Value { get; }
}

/// <summary>Orthogonal durable replay, floor, and pending-delivery progress evidence.</summary>
public sealed record ChannelDurableProgressEvidence
{
    /// <summary>Creates durable Channel progress evidence.</summary>
    /// <param name="replayCursor">Optional applied replay cursor.</param>
    /// <param name="floor">Optional cumulative or target-managed progress floor.</param>
    /// <param name="pending">Optional exact, gap-aware, or target-managed pending-delivery evidence.</param>
    /// <exception cref="ArgumentException">
    /// Every axis is absent, a runtime variant is undeclared, or supplied axes belong to conflicting scopes.
    /// </exception>
    [JsonConstructor]
    public ChannelDurableProgressEvidence(
        ChannelReplayCursor? replayCursor = null,
        ChannelProgressFloor? floor = null,
        ChannelPendingProgress? pending = null)
    {
        if (replayCursor is null && floor is null && pending is null)
            throw new ArgumentException("Durable Channel progress requires replay, floor, pending evidence, or a combination.");
        floor?.EnsureDeclaredVariant();
        pending?.EnsureDeclaredVariant();

        var scope = replayCursor?.Scope;
        var orderingDomain = replayCursor?.OrderingDomain;
        if (floor is ChannelReplayCursorProgressFloor cursorFloor)
        {
            RequireScope(ref scope, cursorFloor.Cursor.Scope, nameof(floor));
            RequireOrderingDomain(ref orderingDomain, cursorFloor.Cursor.OrderingDomain, nameof(floor));
        }
        else if (floor is ChannelProviderDeliveryProgressFloor deliveryFloor)
        {
            RequireScope(ref scope, deliveryFloor.Scope, nameof(floor));
            RequireOrderingDomain(ref orderingDomain, deliveryFloor.OrderingDomain, nameof(floor));
        }
        else if (floor is ChannelTargetManagedProgressFloor managedFloor)
            RequireScope(ref scope, managedFloor.Scope, nameof(floor));
        if (pending is ChannelStableDeliverySetProgress stablePending)
            RequireScope(ref scope, stablePending.Scope, nameof(pending));
        else if (pending is ChannelUnresolvedGapProgress gapPending)
            RequireScope(ref scope, gapPending.Scope, nameof(pending));
        else if (pending is ChannelTargetManagedPendingProgress managedPending)
            RequireScope(ref scope, managedPending.Scope, nameof(pending));
        if (pending is ChannelUnresolvedGapProgress && floor is null)
        {
            throw new ArgumentException(
                "Unresolved-gap progress requires an independent cumulative or target-managed floor.",
                nameof(pending));
        }

        ReplayCursor = replayCursor;
        Floor = floor;
        Pending = pending;
    }

    /// <summary>Optional applied replay cursor, independently of acknowledgement progress.</summary>
    public ChannelReplayCursor? ReplayCursor { get; }

    /// <summary>Optional cumulative or target-managed progress floor.</summary>
    public ChannelProgressFloor? Floor { get; }

    /// <summary>Optional exact, gap-aware, or target-managed pending-delivery evidence.</summary>
    public ChannelPendingProgress? Pending { get; }

    static void RequireScope(ref ChannelScopeId? scope, ChannelScopeId candidate, string parameterName)
    {
        if (scope is { } established && established != candidate)
            throw new ArgumentException("Durable Channel progress axes must belong to one exact scope.", parameterName);
        scope ??= candidate;
    }

    static void RequireOrderingDomain(
        ref ChannelOrderingDomainId? orderingDomain,
        ChannelOrderingDomainId candidate,
        string parameterName)
    {
        if (orderingDomain is { } established && established != candidate)
            throw new ArgumentException("Durable Channel progress floors must belong to one exact ordering domain.", parameterName);
        orderingDomain ??= candidate;
    }
}

/// <summary>Attributable receipt proving provider settlement after one durable application-progress record.</summary>
public sealed record ChannelSettlementReceipt
{
    /// <summary>Creates a Channel settlement receipt.</summary>
    /// <param name="kind">Settlement operation completed by the provider.</param>
    /// <param name="couplingKind">Semantic kind of provider scope changed by settlement.</param>
    /// <param name="coupling">Provider scope changed by settlement.</param>
    /// <param name="applicationProgress">Already-durable application progress authorizing settlement.</param>
    /// <param name="settledAtUtc">UTC settlement completion time.</param>
    /// <param name="throughCursor">Optional cumulative replay position covered by settlement.</param>
    /// <param name="deliveries">Optional exact stable delivery identities covered by settlement.</param>
    /// <param name="evidenceReference">Optional opaque provider acknowledgement evidence.</param>
    /// <exception cref="ArgumentException">
    /// An identity or time is invalid, <paramref name="kind"/> conflicts with <paramref name="couplingKind"/> or its
    /// exact coverage shape, delivery identities repeat, or evidence is white-space.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="kind"/> or <paramref name="couplingKind"/> is unsupported.
    /// </exception>
    [JsonConstructor]
    public ChannelSettlementReceipt(
        ChannelSettlementKind kind,
        ChannelSettlementCouplingKind couplingKind,
        ChannelSettlementCouplingId coupling,
        ChannelApplicationProgressReference applicationProgress,
        DateTimeOffset settledAtUtc,
        ChannelReplayCursor? throughCursor = null,
        ImmutableArray<ChannelProviderDeliveryId> deliveries = default,
        string? evidenceReference = null)
    {
        ChannelTopologyRequirement.RequireDefined(kind, nameof(kind));
        ChannelTopologyRequirement.RequireDefined(couplingKind, nameof(couplingKind));
        if (!ChannelSettlementRequirement.IsLegal(kind, couplingKind))
        {
            throw new ArgumentException(
                $"Settlement operation '{kind}' cannot use coupling kind '{couplingKind}'.",
                nameof(couplingKind));
        }
        ChannelReplayCursor.Require(coupling.Value, nameof(coupling));
        ArgumentNullException.ThrowIfNull(applicationProgress);
        ChannelReplayCursor.Require(applicationProgress.Value, nameof(applicationProgress));
        ChannelReplayCursor.RequireUtc(settledAtUtc, nameof(settledAtUtc));
        var normalizedDeliveries = deliveries.IsDefaultOrEmpty
            ? []
            : ChannelStableDeliverySetProgress.NormalizeDeliveries(deliveries, nameof(deliveries));
        var validCoverage = kind switch
        {
            ChannelSettlementKind.CumulativePrefix => throughCursor is not null
                && normalizedDeliveries.IsDefaultOrEmpty,
            ChannelSettlementKind.Individual or ChannelSettlementKind.Negative
                or ChannelSettlementKind.Defer or ChannelSettlementKind.Quarantine => throughCursor is null
                && normalizedDeliveries.Length == 1,
            ChannelSettlementKind.Batch => throughCursor is null && normalizedDeliveries.Length >= 2,
            ChannelSettlementKind.InvocationCoupled => throughCursor is null && normalizedDeliveries.IsDefaultOrEmpty,
            _ => false
        };
        if (!validCoverage)
        {
            throw new ArgumentException(
                "Cumulative settlement requires only a cursor; individual, negative, defer, and quarantine settlement require exactly one delivery; batch settlement requires at least two deliveries; and invocation completion omits both.",
                nameof(kind));
        }
        if (throughCursor is not null && throughCursor.Scope != applicationProgress.Scope)
        {
            throw new ArgumentException(
                "Cumulative settlement and its authorizing durable application progress must belong to one exact Channel scope.",
                nameof(throughCursor));
        }
        if (evidenceReference is not null && string.IsNullOrWhiteSpace(evidenceReference))
            throw new ArgumentException("An optional settlement evidence reference cannot be white-space.", nameof(evidenceReference));

        Kind = kind;
        CouplingKind = couplingKind;
        Coupling = coupling;
        ApplicationProgress = applicationProgress;
        SettledAtUtc = settledAtUtc;
        ThroughCursor = throughCursor;
        Deliveries = normalizedDeliveries;
        EvidenceReference = evidenceReference;
    }

    /// <summary>Settlement operation completed by the provider.</summary>
    [JsonRequired]
    [JsonInclude]
    public ChannelSettlementKind Kind { get; private init; }

    /// <summary>Semantic kind of provider scope changed by settlement.</summary>
    [JsonRequired]
    [JsonInclude]
    public ChannelSettlementCouplingKind CouplingKind { get; private init; }

    /// <summary>Provider scope changed by settlement.</summary>
    [JsonRequired]
    [JsonInclude]
    public ChannelSettlementCouplingId Coupling { get; private init; }

    /// <summary>Already-durable application progress authorizing settlement.</summary>
    [JsonRequired]
    [JsonInclude]
    public ChannelApplicationProgressReference ApplicationProgress { get; private init; }

    /// <summary>UTC settlement completion time.</summary>
    [JsonRequired]
    [JsonInclude]
    public DateTimeOffset SettledAtUtc { get; private init; }

    /// <summary>Optional cumulative replay position covered by settlement.</summary>
    public ChannelReplayCursor? ThroughCursor { get; }

    /// <summary>Exact stable delivery identities covered by per-delivery settlement.</summary>
    [JsonRequired]
    [JsonInclude]
    public ImmutableArray<ChannelProviderDeliveryId> Deliveries { get; private init; }

    /// <summary>Optional opaque provider acknowledgement evidence.</summary>
    public string? EvidenceReference { get; }

    /// <summary>Compares normalized settlement receipts structurally.</summary>
    /// <param name="other">Other receipt.</param>
    /// <returns><see langword="true"/> when every semantic and evidence field is equal.</returns>
    public bool Equals(ChannelSettlementReceipt? other) =>
        ReferenceEquals(this, other)
        || other is not null
        && Kind == other.Kind
        && CouplingKind == other.CouplingKind
        && Coupling == other.Coupling
        && ApplicationProgress == other.ApplicationProgress
        && SettledAtUtc == other.SettledAtUtc
        && ThroughCursor == other.ThroughCursor
        && Deliveries.SequenceEqual(other.Deliveries)
        && string.Equals(EvidenceReference, other.EvidenceReference, StringComparison.Ordinal);

    /// <summary>Returns a structural hash code for normalized settlement evidence.</summary>
    /// <returns>A hash code derived from every persisted receipt field.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Kind);
        hash.Add(CouplingKind);
        hash.Add(Coupling);
        hash.Add(ApplicationProgress);
        hash.Add(SettledAtUtc);
        hash.Add(ThroughCursor);
        foreach (var delivery in Deliveries)
            hash.Add(delivery);
        hash.Add(EvidenceReference, StringComparer.Ordinal);
        return hash.ToHashCode();
    }
}
