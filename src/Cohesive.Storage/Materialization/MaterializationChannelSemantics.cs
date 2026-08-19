using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cohesive.Execution;
using Cohesive.Model.Serialization;

namespace Cohesive.Storage.Materialization;

/// <summary>
/// Deterministic projection from materialization-scoped change semantics to the shared canonical Channel algebra.
/// </summary>
/// <remarks>
/// Materialization records remain the authority for the semantic change and its durable progress aggregate. These
/// projections expose shared replay, provider-delivery, attempt, progress, and settlement meaning without creating a
/// second checkpoint store or weakening exact Relations-plan and generation affinity.
/// </remarks>
public static class MaterializationChannelSemantics
{
    const string ScopePrefix = "materialization-channel-scope:v2:sha256:";
    const string CouplingPrefix = "materialization-channel-settlement:v2:sha256:";
    static readonly JsonSerializerOptions CanonicalJsonOptions = MaterializationJsonSerializer.CreateOptions();

    /// <summary>Projects one exact materialization source scope into a stable Channel binding scope.</summary>
    /// <param name="scope">Exact Relations plan, placement, partition, and ordering scope.</param>
    /// <returns>A deterministic opaque Channel scope identity.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="scope"/> is <see langword="null"/>.</exception>
    public static ChannelScopeId ToChannelScopeId(MaterializationSourceScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var canonicalScope = StrictDocumentJson.GetCanonicalBytes(scope, CanonicalJsonOptions);
        return new(ScopePrefix + Convert.ToHexStringLower(SHA256.HashData(canonicalScope)));
    }

    /// <summary>Projects one materialization ordering domain into a Channel ordering-domain identity.</summary>
    /// <param name="scope">Exact materialization source scope.</param>
    /// <returns>The adapter-stable ordering domain already owned by the materialization scope.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="scope"/> is <see langword="null"/>.</exception>
    public static ChannelOrderingDomainId ToChannelOrderingDomainId(MaterializationSourceScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return new(scope.OrderingScope.Value);
    }

    /// <summary>Projects an opaque materialization source position into independent Channel replay control.</summary>
    /// <param name="position">Scope-bound materialization source position.</param>
    /// <param name="validUntilUtc">Optional UTC retention or validity boundary supplied by target evidence.</param>
    /// <returns>A Channel replay cursor retaining the position's exact scope and opaque representation.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="position"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="validUntilUtc"/> is not UTC.</exception>
    public static ChannelReplayCursor ToChannelReplayCursor(
        MaterializationSourcePosition position,
        DateTimeOffset? validUntilUtc = null)
    {
        ArgumentNullException.ThrowIfNull(position);
        return new(
            formatVersion: position.FormatVersion,
            scope: ToChannelScopeId(position.Scope),
            orderingDomain: ToChannelOrderingDomainId(position.Scope),
            value: position.Value,
            validUntilUtc: validUntilUtc);
    }

    /// <summary>Projects a materialization delivery identity into stable provider-delivery identity evidence.</summary>
    /// <param name="delivery">Stable materialization delivery identity used across redelivery.</param>
    /// <returns>The same stable identity under the shared Channel type.</returns>
    /// <exception cref="ArgumentException"><paramref name="delivery"/> is default.</exception>
    public static ChannelProviderDeliveryId ToChannelProviderDeliveryId(MaterializationDeliveryId delivery)
    {
        MaterializationContract.RequireDefinedIdentity(delivery.Value, nameof(delivery));
        return new(delivery.Value);
    }

    /// <summary>Creates physical attempt evidence for one materialization change delivery.</summary>
    /// <param name="delivery">Canonical materialization change delivery retaining stable redelivery identity.</param>
    /// <param name="attempt">Identity of this physical callback, receive, read, or invocation attempt.</param>
    /// <param name="settlementAuthority">Optional ephemeral authority for this exact attempt.</param>
    /// <returns>
    /// Runtime Channel attempt evidence. The logical change identity remains
    /// <see cref="MaterializationChangeEnvelope.Id"/> and is not duplicated.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="delivery"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="attempt"/> is default or <paramref name="settlementAuthority"/> belongs to another attempt.
    /// </exception>
    public static ChannelDeliveryAttemptEvidence ToChannelDeliveryAttemptEvidence(
        MaterializationChangeDelivery delivery,
        ChannelDeliveryAttemptId attempt,
        ChannelSettlementAuthority? settlementAuthority = null)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        var scope = ToChannelScopeId(delivery.Change.Scope);
        return new(
            attempt: attempt,
            observedAtUtc: delivery.DeliveredAtUtc,
            scope: scope,
            providerDelivery: ToChannelProviderDeliveryId(delivery.Id),
            replayCursor: delivery.Change.Position is null
                ? null
                : ToChannelReplayCursor(delivery.Change.Position),
            settlementAuthority: settlementAuthority,
            evidenceReference: delivery.EvidenceReference);
    }

    /// <summary>Creates complete Channel progress for a positioned materialization checkpoint.</summary>
    /// <param name="position">Applied materialization source position.</param>
    /// <returns>Replay and cumulative cursor evidence for the exact positioned boundary.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="position"/> is <see langword="null"/>.</exception>
    public static ChannelDurableProgressEvidence CreatePositionedDurableProgress(
        MaterializationSourcePosition position)
    {
        ArgumentNullException.ThrowIfNull(position);
        var cursor = ToChannelReplayCursor(position);
        return new(
            replayCursor: cursor,
            floor: new ChannelReplayCursorProgressFloor(cursor),
            pending: null);
    }

    /// <summary>Projects one incremental application checkpoint into its authoritative Channel progress.</summary>
    /// <param name="checkpoint">Already-durable materialization change checkpoint.</param>
    /// <returns>The checkpoint's complete replay, floor, and pending-delivery progress evidence.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="checkpoint"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="checkpoint"/> is a batch checkpoint or lacks incremental Channel progress.
    /// </exception>
    public static ChannelDurableProgressEvidence ToChannelDurableProgress(
        MaterializationApplicationCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        if (checkpoint.Kind != MaterializationCheckpointKind.ChangeProgress
            || checkpoint.ChannelProgress is null)
        {
            throw new ArgumentException(
                "Only an incremental materialization checkpoint projects to Channel delivery progress.",
                nameof(checkpoint));
        }

        return checkpoint.ChannelProgress;
    }

    /// <summary>Projects one validated materialization settlement observation into a Channel receipt.</summary>
    /// <param name="observation">
    /// Settlement observation whose exact application-checkpoint coverage and causal ordering were already
    /// validated.
    /// </param>
    /// <returns>An individual or cumulative Channel receipt with exact checkpoint-attributed coverage.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="observation"/> is <see langword="null"/>.</exception>
    public static ChannelSettlementReceipt ToChannelSettlementReceipt(
        MaterializationChangeSettlementObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var checkpoint = observation.Progress.LatestChangeCheckpoint!;
        var scope = observation.Progress.Key.Scope;
        var settlement = observation.Settlement;
        var channelScope = ToChannelScopeId(scope);
        ImmutableArray<ChannelProviderDeliveryId> deliveries = [];
        if (!settlement.Deliveries.IsDefaultOrEmpty)
        {
            var builder = ImmutableArray.CreateBuilder<ChannelProviderDeliveryId>(settlement.Deliveries.Length);
            foreach (var delivery in settlement.Deliveries)
                builder.Add(ToChannelProviderDeliveryId(delivery));
            deliveries = builder.MoveToImmutable();
        }
        return new(
            kind: settlement.Kind,
            couplingKind: settlement.Kind switch
            {
                ChannelSettlementKind.CumulativePrefix => ChannelSettlementCouplingKind.OrderingScope,
                ChannelSettlementKind.Individual => ChannelSettlementCouplingKind.PerDelivery,
                _ => throw new InvalidOperationException(
                    $"Unsupported materialization settlement kind '{settlement.Kind}'.")
            },
            coupling: ToChannelSettlementCouplingId(scope),
            applicationProgress: new ChannelApplicationProgressReference(
                scope: channelScope,
                value: checkpoint.Id.Value),
            settledAtUtc: settlement.SettledAtUtc,
            throughCursor: settlement.Position is null
                ? null
                : ToChannelReplayCursor(settlement.Position),
            deliveries: deliveries,
            evidenceReference: settlement.EvidenceReference);
    }

    /// <summary>Derives the default settlement-coupling scope for one materialization source.</summary>
    /// <remarks>
    /// Managed adapters that couple several ordering scopes in one provider callback must supply a broader explicit
    /// coupling identity as runtime evidence; ordering scope alone is not assumed to be the settlement scope.
    /// </remarks>
    /// <param name="scope">Exact materialization source scope.</param>
    /// <returns>A deterministic settlement-coupling identity for this exact source scope.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="scope"/> is <see langword="null"/>.</exception>
    public static ChannelSettlementCouplingId ToChannelSettlementCouplingId(MaterializationSourceScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return new(CouplingPrefix + Fingerprint(ToChannelScopeId(scope).Value));
    }

    internal static bool IsSameReplayPosition(
        ChannelReplayCursor cursor,
        MaterializationSourcePosition position) =>
        cursor.FormatVersion == position.FormatVersion
        && cursor.Scope == ToChannelScopeId(position.Scope)
        && cursor.OrderingDomain == ToChannelOrderingDomainId(position.Scope)
        && string.Equals(cursor.Value, position.Value, StringComparison.Ordinal);

    internal static ChannelScopeId GetChannelScope(ChannelDurableProgressEvidence progress) =>
        progress.ReplayCursor?.Scope
        ?? progress.Floor switch
        {
            ChannelReplayCursorProgressFloor cursor => cursor.Cursor.Scope,
            ChannelProviderDeliveryProgressFloor delivery => delivery.Scope,
            ChannelTargetManagedProgressFloor managed => managed.Scope,
            _ => progress.Pending switch
            {
                ChannelStableDeliverySetProgress stable => stable.Scope,
                ChannelUnresolvedGapProgress gaps => gaps.Scope,
                ChannelTargetManagedPendingProgress managed => managed.Scope,
                _ => throw new ArgumentException("Channel progress has no declared scope.", nameof(progress))
            }
        };

    static string Fingerprint(params string[] values)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[sizeof(int)];
        foreach (var value in values)
        {
            var byteCount = Encoding.UTF8.GetByteCount(value);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, byteCount);
            hash.AppendData(length);
            var bytes = Encoding.UTF8.GetBytes(value);
            hash.AppendData(bytes);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
