using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Cohesive.Execution;
using Cohesive.Model.Serialization;

namespace Cohesive.Storage.Materialization;

/// <summary>Stable diagnostic codes emitted by synchronization-work stores.</summary>
public static class MaterializationSynchronizationWorkDiagnosticCodes
{
    /// <summary>No synchronization-work aggregate exists for the exact requested key.</summary>
    public const string NotFound = "materialization.synchronizationWork.notFound";

    /// <summary>The expected synchronization-work revision is stale.</summary>
    public const string RevisionConflict = "materialization.synchronizationWork.revisionConflict";

    /// <summary>The supplied synchronization worker owner or fence has been superseded.</summary>
    public const string StaleFence = "materialization.synchronizationWork.staleFence";

    /// <summary>A stable synchronization-work mutation identity was reused for different content.</summary>
    public const string IdentityConflict = "materialization.synchronizationWork.identityConflict";

    /// <summary>A prepare or completion operation conflicts with the exact pending prepared work.</summary>
    public const string PendingWorkConflict = "materialization.synchronizationWork.pendingConflict";

    /// <summary>Synchronization work conflicts with an incomplete generation-activation protocol.</summary>
    public const string ActivationConflict = "materialization.synchronizationWork.activationConflict";
}

/// <summary>
/// Exact durable identity of one generation-wide incremental synchronization-work aggregate.
/// </summary>
/// <remarks>
/// The key deliberately excludes a source partition. One aggregate therefore allocates versions across every
/// incremental input that can write the same target generation. All fingerprints are semantic fences rather than
/// informational metadata.
/// </remarks>
public sealed record MaterializationSynchronizationWorkKey
{
    /// <summary>Creates one exact synchronization-work key.</summary>
    /// <param name="materialization">Logical materialization definition.</param>
    /// <param name="definitionFingerprint">Exact canonical execution-definition content.</param>
    /// <param name="rebuildPlanFingerprint">Exact persisted rebuild realization governing the generation.</param>
    /// <param name="impactPlanFingerprint">Exact persisted incremental-impact plan governing root invalidation.</param>
    /// <param name="generation">Candidate or active target generation receiving the prepared work.</param>
    /// <exception cref="ArgumentNullException">
    /// A required fingerprint is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="materialization"/> or <paramref name="generation"/> is default.
    /// </exception>
    [JsonConstructor]
    public MaterializationSynchronizationWorkKey(
        MaterializationId materialization,
        ExecutionDefinitionFingerprint definitionFingerprint,
        MaterializationRebuildPlanFingerprint rebuildPlanFingerprint,
        MaterializationImpactPlanFingerprint impactPlanFingerprint,
        MaterializationGenerationId generation)
    {
        MaterializationContract.RequireDefinedIdentity(materialization.Value, nameof(materialization));
        MaterializationContract.RequireDefinedIdentity(generation.Value, nameof(generation));
        DefinitionFingerprint = definitionFingerprint ?? throw new ArgumentNullException(nameof(definitionFingerprint));
        RebuildPlanFingerprint = rebuildPlanFingerprint
            ?? throw new ArgumentNullException(nameof(rebuildPlanFingerprint));
        ImpactPlanFingerprint = impactPlanFingerprint
            ?? throw new ArgumentNullException(nameof(impactPlanFingerprint));
        Materialization = materialization;
        Generation = generation;
    }

    /// <summary>Logical materialization definition.</summary>
    public MaterializationId Materialization { get; }

    /// <summary>Exact canonical execution-definition content.</summary>
    public ExecutionDefinitionFingerprint DefinitionFingerprint { get; }

    /// <summary>Exact persisted rebuild realization governing the generation.</summary>
    public MaterializationRebuildPlanFingerprint RebuildPlanFingerprint { get; }

    /// <summary>Exact persisted incremental-impact plan governing root invalidation.</summary>
    public MaterializationImpactPlanFingerprint ImpactPlanFingerprint { get; }

    /// <summary>Candidate or active target generation receiving prepared work.</summary>
    public MaterializationGenerationId Generation { get; }
}

/// <summary>One unversioned canonical synchronization intent for a materialized item.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$intent")]
[JsonDerivedType(typeof(MaterializationSynchronizationUpsertIntent), "upsert")]
[JsonDerivedType(typeof(MaterializationSynchronizationDeleteIntent), "delete")]
public abstract record MaterializationSynchronizationItemIntent
{
    /// <summary>Creates one unversioned item intent.</summary>
    /// <param name="itemId">Stable logical output key.</param>
    /// <exception cref="ArgumentException"><paramref name="itemId"/> is default.</exception>
    protected MaterializationSynchronizationItemIntent(MaterializationItemId itemId)
    {
        MaterializationContract.RequireDefinedIdentity(itemId.Value, nameof(itemId));
        ItemId = itemId;
    }

    /// <summary>Stable logical output key.</summary>
    public MaterializationItemId ItemId { get; }

    /// <summary>Intent kind projected authoritatively from the concrete subtype.</summary>
    [JsonIgnore]
    public abstract MaterializationItemMutationKind Kind { get; }
}

/// <summary>Unversioned intent to insert or replace one portable materialized value.</summary>
public sealed record MaterializationSynchronizationUpsertIntent : MaterializationSynchronizationItemIntent
{
    /// <summary>Creates one unversioned upsert intent.</summary>
    /// <param name="itemId">Stable logical output key.</param>
    /// <param name="value">Portable replacement value.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="itemId"/> is default or <paramref name="value"/> is undefined.
    /// </exception>
    public MaterializationSynchronizationUpsertIntent(
        MaterializationItemId itemId,
        ObservationValue value)
        : base(itemId)
    {
        if (value.Kind == ObservationValueKind.Undefined)
        {
            throw new ArgumentException("A synchronization upsert value cannot be undefined.", nameof(value));
        }

        Value = value;
    }

    /// <summary>Portable replacement value.</summary>
    public ObservationValue Value { get; }

    /// <inheritdoc />
    [JsonIgnore]
    public override MaterializationItemMutationKind Kind => MaterializationItemMutationKind.Upsert;
}

/// <summary>Unversioned intent to remove one materialized item.</summary>
public sealed record MaterializationSynchronizationDeleteIntent : MaterializationSynchronizationItemIntent
{
    /// <summary>Creates one unversioned delete intent.</summary>
    /// <param name="itemId">Stable logical output key.</param>
    /// <exception cref="ArgumentException"><paramref name="itemId"/> is default.</exception>
    public MaterializationSynchronizationDeleteIntent(MaterializationItemId itemId)
        : base(itemId)
    {
    }

    /// <inheritdoc />
    [JsonIgnore]
    public override MaterializationItemMutationKind Kind => MaterializationItemMutationKind.Delete;
}

/// <summary>Durable source-page evidence coupled to one prepared target write.</summary>
/// <remarks>
/// Retaining this evidence with the prepared mutations lets recovery finish the exact application checkpoint after
/// an ambiguous target write without re-reading a source page that may have changed in the meantime.
/// </remarks>
public sealed record MaterializationSynchronizationPageIntent
{
    /// <summary>Creates exact source-page application evidence.</summary>
    /// <param name="feed">Persisted change feed that produced the page.</param>
    /// <param name="checkpoint">Stable application-checkpoint identity for this page.</param>
    /// <param name="throughPosition">Exact positioned boundary examined by the source.</param>
    /// <param name="appliedDeliveries">Stable delivery identities covered by the page effects.</param>
    /// <param name="state">Whether the read caught up or requires another bounded read.</param>
    /// <param name="readStartedAtUtc">UTC time immediately before the source read began.</param>
    /// <param name="readCompletedAtUtc">UTC time immediately after the source read completed.</param>
    /// <exception cref="ArgumentNullException"><paramref name="throughPosition"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// An identity or timestamp is invalid, delivery identities repeat, or the delivery set conflicts with
    /// <paramref name="state"/>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="state"/> is unsupported.</exception>
    [JsonConstructor]
    public MaterializationSynchronizationPageIntent(
        MaterializationChangeFeedId feed,
        MaterializationCheckpointId checkpoint,
        MaterializationSourcePosition throughPosition,
        ImmutableArray<MaterializationDeliveryId> appliedDeliveries,
        MaterializationChangePageState state,
        DateTimeOffset readStartedAtUtc,
        DateTimeOffset readCompletedAtUtc)
    {
        MaterializationContract.RequireDefinedIdentity(feed.Value, nameof(feed));
        MaterializationContract.RequireDefinedIdentity(checkpoint.Value, nameof(checkpoint));
        ThroughPosition = throughPosition ?? throw new ArgumentNullException(nameof(throughPosition));
        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state), state, "Unsupported synchronization page state.");
        }

        AppliedDeliveries = MaterializationApplicationCheckpoint.NormalizeDeliveries(
            appliedDeliveries,
            nameof(appliedDeliveries),
            "Synchronization-page delivery identities");
        if (state == MaterializationChangePageState.MoreAvailable && AppliedDeliveries.IsDefaultOrEmpty)
        {
            throw new ArgumentException(
                "A synchronization page with more available changes requires at least one delivery.",
                nameof(appliedDeliveries));
        }
        if (state == MaterializationChangePageState.Progressed && !AppliedDeliveries.IsDefaultOrEmpty)
        {
            throw new ArgumentException(
                "A progressed synchronization page cannot contain a delivery.",
                nameof(appliedDeliveries));
        }

        MaterializationContract.RequireUtc(readStartedAtUtc, nameof(readStartedAtUtc));
        MaterializationContract.RequireUtc(readCompletedAtUtc, nameof(readCompletedAtUtc));
        if (readCompletedAtUtc < readStartedAtUtc)
        {
            throw new ArgumentException(
                "A synchronization source read cannot complete before it starts.",
                nameof(readCompletedAtUtc));
        }

        Feed = feed;
        Checkpoint = checkpoint;
        State = state;
        ReadStartedAtUtc = readStartedAtUtc;
        ReadCompletedAtUtc = readCompletedAtUtc;
    }

    /// <summary>Persisted change feed that produced the page.</summary>
    public MaterializationChangeFeedId Feed { get; }

    /// <summary>Stable application-checkpoint identity for this page.</summary>
    public MaterializationCheckpointId Checkpoint { get; }

    /// <summary>Exact positioned boundary examined by the source.</summary>
    public MaterializationSourcePosition ThroughPosition { get; }

    /// <summary>Stable delivery identities covered by the page effects in canonical order.</summary>
    public ImmutableArray<MaterializationDeliveryId> AppliedDeliveries { get; }

    /// <summary>Whether the read caught up or requires another bounded read.</summary>
    public MaterializationChangePageState State { get; }

    /// <summary>UTC time immediately before the source read began.</summary>
    public DateTimeOffset ReadStartedAtUtc { get; }

    /// <summary>UTC time immediately after the source read completed.</summary>
    public DateTimeOffset ReadCompletedAtUtc { get; }
}

/// <summary>Canonical bounded source page and optional item work awaiting durable admission.</summary>
public sealed record MaterializationSynchronizationWorkIntent
{
    /// <summary>Creates and canonically orders one bounded synchronization-work intent.</summary>
    /// <param name="page">Exact source page whose effects the prepared mutations realize.</param>
    /// <param name="items">
    /// Optional unversioned upsert and delete intents. Input order is not semantic; retained items are ordered by
    /// canonical Unicode scalar-value item identity. An empty set still durably serializes page checkpoint ordering.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="page"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="items"/> contains a null entry or repeats an item identity.
    /// </exception>
    [JsonConstructor]
    public MaterializationSynchronizationWorkIntent(
        MaterializationSynchronizationPageIntent page,
        ImmutableArray<MaterializationSynchronizationItemIntent> items)
    {
        Page = page ?? throw new ArgumentNullException(nameof(page));
        var normalized = items.IsDefault ? [] : items;

        var isCanonical = true;
        for (var index = 0; index < normalized.Length; index++)
        {
            if (normalized[index] is null)
            {
                throw new ArgumentException("Synchronization work cannot contain a null item intent.", nameof(items));
            }

            if (index == 0)
            {
                continue;
            }

            var comparison = MaterializationSealContentOrder.Compare(
                normalized[index - 1].ItemId,
                normalized[index].ItemId);
            if (comparison == 0)
            {
                throw new ArgumentException(
                    $"Item '{normalized[index].ItemId.Value}' occurs more than once in synchronization work.",
                    nameof(items));
            }

            if (comparison > 0)
            {
                isCanonical = false;
            }
        }

        if (isCanonical)
        {
            Items = normalized;
            return;
        }

        var builder = ImmutableArray.CreateBuilder<MaterializationSynchronizationItemIntent>(normalized.Length);
        builder.AddRange(normalized);
        builder.Sort(static (left, right) => MaterializationSealContentOrder.Compare(left.ItemId, right.ItemId));
        for (var index = 1; index < builder.Count; index++)
        {
            if (builder[index - 1].ItemId == builder[index].ItemId)
            {
                throw new ArgumentException(
                    $"Item '{builder[index].ItemId.Value}' occurs more than once in synchronization work.",
                    nameof(items));
            }
        }

        Items = builder.MoveToImmutable();
    }

    /// <summary>Exact source page whose effects the item intents realize.</summary>
    public MaterializationSynchronizationPageIntent Page { get; }

    /// <summary>Canonical item intents in strictly increasing Unicode scalar-value item identity order.</summary>
    public ImmutableArray<MaterializationSynchronizationItemIntent> Items { get; }
}

/// <summary>One durable replay-stable prepared synchronization write.</summary>
public sealed record MaterializationPreparedSynchronizationWork
{
    /// <summary>Creates one prepared synchronization write.</summary>
    /// <param name="preparationId">Stable progress mutation identity that prepared this exact work.</param>
    /// <param name="page">Exact source page that must be checkpointed after the target effects become durable.</param>
    /// <param name="version">
    /// One generation-wide version shared by every concrete mutation, or <see langword="null"/> for an effect-free
    /// page admission.
    /// </param>
    /// <param name="mutations">
    /// Concrete target mutations in strictly increasing canonical item-identity order, or empty with no version.
    /// </param>
    /// <exception cref="ArgumentException">
    /// An identity is default, version presence conflicts with mutation presence, <paramref name="version"/>
    /// precedes the first incremental version two, or <paramref name="mutations"/> contains a null entry, is not in
    /// strict canonical item order, repeats a mutation identity, or contains another version.
    /// </exception>
    [JsonConstructor]
    public MaterializationPreparedSynchronizationWork(
        MaterializationProgressMutationId preparationId,
        MaterializationSynchronizationPageIntent page,
        MaterializationItemVersion? version,
        ImmutableArray<MaterializationItemMutation> mutations)
    {
        MaterializationContract.RequireDefinedIdentity(preparationId.Value, nameof(preparationId));
        Page = page ?? throw new ArgumentNullException(nameof(page));
        var normalized = mutations.IsDefault ? [] : mutations;
        if ((version is null) != normalized.IsDefaultOrEmpty)
        {
            throw new ArgumentException(
                "An effect-free prepared page omits its item version; concrete mutations require one assigned version.",
                nameof(version));
        }
        if (version is { } assignedVersion)
        {
            MaterializationContract.RequireDefinedIdentity(assignedVersion.Value, nameof(version));
            if (assignedVersion.Ordinal < 2)
            {
                throw new ArgumentException(
                    "Prepared incremental synchronization work requires item version two or greater.",
                    nameof(version));
            }
        }

        HashSet<MaterializationItemMutationId> identities = [];
        for (var index = 0; index < normalized.Length; index++)
        {
            var mutation = normalized[index];
            if (mutation is null)
            {
                throw new ArgumentException("Prepared synchronization work cannot contain a null mutation.", nameof(mutations));
            }
            if (mutation.Version != version)
            {
                throw new ArgumentException(
                    "Every mutation in one prepared synchronization write must share its assigned version.",
                    nameof(mutations));
            }
            if (!identities.Add(mutation.MutationId))
            {
                throw new ArgumentException(
                    $"Mutation identity '{mutation.MutationId.Value}' occurs more than once in prepared work.",
                    nameof(mutations));
            }
            if (index > 0
                && MaterializationSealContentOrder.Compare(
                    normalized[index - 1].ItemId,
                    mutation.ItemId) >= 0)
            {
                throw new ArgumentException(
                    "Prepared synchronization mutations must use strictly increasing canonical item order.",
                    nameof(mutations));
            }
        }

        PreparationId = preparationId;
        Version = version;
        Mutations = normalized;
    }

    /// <summary>Stable progress mutation identity that prepared this exact work.</summary>
    public MaterializationProgressMutationId PreparationId { get; }

    /// <summary>Exact source page durably coupled to these concrete target mutations.</summary>
    public MaterializationSynchronizationPageIntent Page { get; }

    /// <summary>One generation-wide version shared by concrete mutations, or null for an effect-free page.</summary>
    public MaterializationItemVersion? Version { get; }

    /// <summary>Concrete target mutations in canonical item-identity order.</summary>
    public ImmutableArray<MaterializationItemMutation> Mutations { get; }
}

/// <summary>
/// Durable prefix-ordered activation protocol retaining exact seal, validation, and promotion intents and receipts.
/// </summary>
/// <remarks>
/// The target-pointer compare-and-swap inputs are discovered only after validation and target inspection. Retaining
/// the exact request before each effect makes a post-effect crash replay the same target intent instead of deriving
/// a new expectation from later target state.
/// </remarks>
public sealed record MaterializationGenerationActivationState
{
    /// <summary>Creates one coherent activation protocol prefix.</summary>
    /// <param name="convergence">Fresh catalog-complete convergence evidence authorizing activation.</param>
    /// <param name="sealRequest">Exact persisted seal intent.</param>
    /// <param name="sealReceipt">Exact seal receipt after the effect is reconciled.</param>
    /// <param name="validationRequest">Exact validation intent, present with a seal receipt.</param>
    /// <param name="validationReceipt">Validation result after the effect is reconciled.</param>
    /// <param name="promotionRequest">Exact target-pointer CAS intent after successful validation.</param>
    /// <param name="promotionReceipt">Successful promotion evidence completing activation.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="convergence"/> or <paramref name="sealRequest"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">The supplied intents and receipts do not form one exact valid prefix.</exception>
    /// <exception cref="OverflowException">A target or generation revision is already at its maximum value.</exception>
    [JsonConstructor]
    public MaterializationGenerationActivationState(
        MaterializationConvergenceReceipt convergence,
        MaterializationSealGenerationRequest sealRequest,
        MaterializationSealReceipt? sealReceipt = null,
        MaterializationValidateGenerationRequest? validationRequest = null,
        MaterializationValidationReceipt? validationReceipt = null,
        MaterializationPromoteGenerationRequest? promotionRequest = null,
        MaterializationPromotionReceipt? promotionReceipt = null)
    {
        Convergence = convergence ?? throw new ArgumentNullException(nameof(convergence));
        SealRequest = sealRequest ?? throw new ArgumentNullException(nameof(sealRequest));
        if (sealRequest.GenerationId != convergence.Generation)
            throw new ArgumentException("A seal request must address the converged generation.", nameof(sealRequest));
        if ((sealReceipt is null) != (validationRequest is null))
        {
            throw new ArgumentException(
                "A reconciled seal receipt and its prepared validation request must be persisted atomically.",
                nameof(validationRequest));
        }
        if (sealReceipt is not null
            && (sealReceipt.SealId != sealRequest.SealId
                || sealReceipt.GenerationId != sealRequest.GenerationId
                || sealReceipt.GenerationRevision.Ordinal != checked(sealRequest.ExpectedRevision.Ordinal + 1)
                || sealReceipt.SealedAtUtc != sealRequest.SealedAtUtc))
        {
            throw new ArgumentException("Seal evidence must exactly realize the persisted seal request.", nameof(sealReceipt));
        }
        if (validationRequest is not null
            && (validationRequest.GenerationId != sealRequest.GenerationId
                || validationRequest.ExpectedRevision != sealReceipt!.GenerationRevision
                || validationRequest.ExpectedSealFingerprint != sealReceipt.Fingerprint
                || validationRequest.ExpectedVisibleItemCount != sealReceipt.VisibleItemCount
                || validationRequest.ValidatedAtUtc < sealReceipt.SealedAtUtc))
        {
            throw new ArgumentException(
                "A validation request must exactly consume the retained immutable seal evidence.",
                nameof(validationRequest));
        }
        if (validationReceipt is not null
            && (validationRequest is null
                || validationReceipt.ValidationId != validationRequest.ValidationId
                || validationReceipt.GenerationId != validationRequest.GenerationId
                || validationReceipt.GenerationRevision.Ordinal != checked(validationRequest.ExpectedRevision.Ordinal + 1)
                || validationReceipt.SealFingerprint != validationRequest.ExpectedSealFingerprint
                || validationReceipt.ValidatedAtUtc != validationRequest.ValidatedAtUtc))
        {
            throw new ArgumentException(
                "Validation evidence must exactly realize the persisted validation request.",
                nameof(validationReceipt));
        }
        if ((promotionRequest is not null)
            != (validationReceipt is { Validation.IsValid: true }))
        {
            throw new ArgumentException(
                "Exactly successful validation requires a prepared promotion request.",
                nameof(promotionRequest));
        }
        if (promotionRequest is not null
            && (promotionRequest.GenerationId != validationReceipt!.GenerationId
                || promotionRequest.ExpectedGenerationRevision != validationReceipt.GenerationRevision
                || promotionRequest.ValidationFingerprint != validationReceipt.Fingerprint
                || promotionRequest.PromotedAtUtc < validationReceipt.ValidatedAtUtc))
        {
            throw new ArgumentException(
                "A promotion request must exactly consume successful validation evidence.",
                nameof(promotionRequest));
        }
        if (promotionReceipt is not null
            && (promotionRequest is null
                || promotionReceipt.PromotionId != promotionRequest.PromotionId
                || promotionReceipt.GenerationId != promotionRequest.GenerationId
                || promotionReceipt.PreviousGenerationId != promotionRequest.ExpectedActiveGenerationId
                || promotionReceipt.TargetRevision.Ordinal != checked(promotionRequest.ExpectedTargetRevision.Ordinal + 1)
                || promotionReceipt.GenerationWorkerFence != promotionRequest.GenerationWorkerFence
                || promotionReceipt.PromotionFence != promotionRequest.PromotionFence
                || promotionReceipt.ValidationFingerprint != promotionRequest.ValidationFingerprint
                || promotionReceipt.PromotedAtUtc != promotionRequest.PromotedAtUtc))
        {
            throw new ArgumentException(
                "Promotion evidence must exactly realize the persisted target-pointer CAS request.",
                nameof(promotionReceipt));
        }

        SealReceipt = sealReceipt;
        ValidationRequest = validationRequest;
        ValidationReceipt = validationReceipt;
        PromotionRequest = promotionRequest;
        PromotionReceipt = promotionReceipt;
    }

    /// <summary>Fresh catalog-complete convergence evidence authorizing this protocol.</summary>
    public MaterializationConvergenceReceipt Convergence { get; }

    /// <summary>Exact persisted seal intent.</summary>
    public MaterializationSealGenerationRequest SealRequest { get; }

    /// <summary>Reconciled seal evidence, when sealing completed.</summary>
    public MaterializationSealReceipt? SealReceipt { get; }

    /// <summary>Exact validation intent prepared atomically with seal evidence.</summary>
    public MaterializationValidateGenerationRequest? ValidationRequest { get; }

    /// <summary>Reconciled validation evidence, including an invalid result.</summary>
    public MaterializationValidationReceipt? ValidationReceipt { get; }

    /// <summary>Exact target-pointer CAS intent prepared after successful validation.</summary>
    public MaterializationPromoteGenerationRequest? PromotionRequest { get; }

    /// <summary>Successful promotion evidence completing activation.</summary>
    public MaterializationPromotionReceipt? PromotionReceipt { get; }

    /// <summary>Whether successful validation and its exact promotion intent are retained without visibility.</summary>
    [JsonIgnore]
    public bool IsReady => Convergence.IsValid
        && ValidationReceipt is { Validation.IsValid: true }
        && PromotionRequest is not null
        && PromotionReceipt is null;

    /// <summary>Whether the exact candidate was successfully promoted.</summary>
    [JsonIgnore]
    public bool IsComplete => PromotionReceipt is not null;
}

/// <summary>Bounded immutable durable state of one synchronization-work aggregate.</summary>
public sealed record MaterializationSynchronizationWorkSnapshot
{
    /// <summary>Creates one coherent synchronization-work snapshot.</summary>
    /// <param name="key">Exact fingerprint- and generation-fenced synchronization identity.</param>
    /// <param name="revision">Current compare-and-swap revision.</param>
    /// <param name="fence">Current single-writer ownership fence.</param>
    /// <param name="fenceOwner">Stable current worker identity.</param>
    /// <param name="nextItemVersion">Next generation-wide version to assign; starts at two after baseline version one.</param>
    /// <param name="pendingWork">At most one prepared target write awaiting exact completion.</param>
    /// <param name="activation">Durable activation prefix, when candidate activation has begun.</param>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// A revision, fence, or owner identity is invalid; <paramref name="nextItemVersion"/> is less than two; or
    /// <paramref name="pendingWork"/> does not immediately precede <paramref name="nextItemVersion"/>.
    /// </exception>
    [JsonConstructor]
    public MaterializationSynchronizationWorkSnapshot(
        MaterializationSynchronizationWorkKey key,
        MaterializationProgressRevision revision,
        MaterializationProgressFence fence,
        string fenceOwner,
        MaterializationItemVersion nextItemVersion,
        MaterializationPreparedSynchronizationWork? pendingWork = null,
        MaterializationGenerationActivationState? activation = null)
    {
        Key = key ?? throw new ArgumentNullException(nameof(key));
        MaterializationContract.RequireDefinedIdentity(revision.Value, nameof(revision));
        MaterializationContract.RequireDefinedIdentity(fence.Value, nameof(fence));
        FenceOwner = MaterializationContract.RequireUnicodeIdentity(fenceOwner, nameof(fenceOwner));
        MaterializationContract.RequireDefinedIdentity(nextItemVersion.Value, nameof(nextItemVersion));
        if (nextItemVersion.Ordinal < 2)
        {
            throw new ArgumentException(
                "Incremental synchronization item versions must begin at two after baseline version one.",
                nameof(nextItemVersion));
        }
        if (pendingWork?.Version is { } pendingVersion
            && (pendingVersion.Ordinal == long.MaxValue
                || pendingVersion.Ordinal + 1 != nextItemVersion.Ordinal))
        {
            throw new ArgumentException(
                "Pending synchronization work must own the version immediately preceding the next allocatable version.",
                nameof(pendingWork));
        }
        if (activation is not null && activation.Convergence.Synchronization != key)
        {
            throw new ArgumentException(
                "Generation activation must retain convergence for the exact synchronization-work key.",
                nameof(activation));
        }
        if (pendingWork is not null && activation is { IsComplete: false })
        {
            throw new ArgumentException(
                "Incremental target work cannot overlap an incomplete generation activation.",
                nameof(pendingWork));
        }

        Revision = revision;
        Fence = fence;
        NextItemVersion = nextItemVersion;
        PendingWork = pendingWork;
        Activation = activation;
    }

    /// <summary>Exact fingerprint- and generation-fenced synchronization identity.</summary>
    public MaterializationSynchronizationWorkKey Key { get; }

    /// <summary>Current compare-and-swap revision.</summary>
    public MaterializationProgressRevision Revision { get; }

    /// <summary>Current single-writer ownership fence.</summary>
    public MaterializationProgressFence Fence { get; }

    /// <summary>Stable current worker identity.</summary>
    public string FenceOwner { get; }

    /// <summary>Next generation-wide item version that a successful prepare will assign.</summary>
    public MaterializationItemVersion NextItemVersion { get; }

    /// <summary>Prepared target write awaiting completion, or <see langword="null"/> when another may be prepared.</summary>
    public MaterializationPreparedSynchronizationWork? PendingWork { get; }

    /// <summary>Durable exact generation-activation prefix, when activation has begun.</summary>
    public MaterializationGenerationActivationState? Activation { get; }
}

/// <summary>Observable disposition of one synchronization-work store mutation.</summary>
[JsonConverter(typeof(StrictStringEnumJsonConverterFactory))]
public enum MaterializationSynchronizationWorkMutationDisposition
{
    /// <summary>The requested mutation committed atomically.</summary>
    Applied = 0,

    /// <summary>The exact prior committed mutation was replayed.</summary>
    Replayed = 1,

    /// <summary>No aggregate exists for the exact requested key.</summary>
    NotFound = 2,

    /// <summary>The expected compare-and-swap revision is stale.</summary>
    RevisionConflict = 3,

    /// <summary>The supplied worker owner or fence has been superseded.</summary>
    StaleFence = 4,

    /// <summary>A stable mutation identity was reused for different content.</summary>
    IdentityConflict = 5,

    /// <summary>Another prepared write is pending, or completion does not identify the exact pending write.</summary>
    PendingWorkConflict = 6,

    /// <summary>The requested work or activation prefix conflicts with an incomplete durable activation.</summary>
    ActivationConflict = 7
}

/// <summary>Result of one atomic synchronization-work store mutation.</summary>
public sealed record MaterializationSynchronizationWorkMutationResult
{
    /// <summary>Creates one synchronization-work mutation result.</summary>
    /// <param name="disposition">Observable mutation disposition.</param>
    /// <param name="snapshot">Current coherent snapshot, when an aggregate exists.</param>
    /// <param name="preparedWork">
    /// Replay-stable work assigned by a successful prepare mutation, including when that mutation is replayed after
    /// the compact snapshot has advanced.
    /// </param>
    /// <param name="diagnostics">Structured deterministic diagnostics for a rejected mutation.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="disposition"/> is unsupported.</exception>
    /// <exception cref="ArgumentException">
    /// A successful result lacks a snapshot or carries diagnostics, a not-found result carries a snapshot, another
    /// rejection lacks a snapshot, a rejection exposes prepared work, or a rejection has no diagnostic.
    /// </exception>
    [JsonConstructor]
    public MaterializationSynchronizationWorkMutationResult(
        MaterializationSynchronizationWorkMutationDisposition disposition,
        MaterializationSynchronizationWorkSnapshot? snapshot,
        MaterializationPreparedSynchronizationWork? preparedWork = null,
        ImmutableArray<DocumentValidationDiagnostic> diagnostics = default)
    {
        if (!Enum.IsDefined(disposition))
        {
            throw new ArgumentOutOfRangeException(
                nameof(disposition),
                disposition,
                "Unsupported synchronization-work mutation disposition.");
        }

        var normalizedDiagnostics = MaterializationContract.NormalizeDiagnostics(diagnostics, nameof(diagnostics));
        var succeeded = disposition is MaterializationSynchronizationWorkMutationDisposition.Applied
            or MaterializationSynchronizationWorkMutationDisposition.Replayed;
        if (succeeded)
        {
            if (snapshot is null)
            {
                throw new ArgumentException(
                    "An applied or replayed synchronization-work mutation requires a snapshot.",
                    nameof(snapshot));
            }
            if (!normalizedDiagnostics.IsDefaultOrEmpty)
            {
                throw new ArgumentException(
                    "An applied or replayed synchronization-work mutation cannot carry diagnostics.",
                    nameof(diagnostics));
            }
        }
        else
        {
            var aggregateExists = disposition != MaterializationSynchronizationWorkMutationDisposition.NotFound;
            if (aggregateExists != (snapshot is not null))
            {
                throw new ArgumentException(
                    "Only a not-found synchronization-work mutation omits the current snapshot.",
                    nameof(snapshot));
            }
            if (preparedWork is not null)
            {
                throw new ArgumentException("A rejected mutation cannot expose prepared work.", nameof(preparedWork));
            }
            if (normalizedDiagnostics.IsDefaultOrEmpty)
            {
                throw new ArgumentException("A rejected mutation requires a diagnostic.", nameof(diagnostics));
            }
        }

        Disposition = disposition;
        Snapshot = snapshot;
        PreparedWork = preparedWork;
        Diagnostics = normalizedDiagnostics;
    }

    /// <summary>Observable mutation disposition.</summary>
    public MaterializationSynchronizationWorkMutationDisposition Disposition { get; }

    /// <summary>Current coherent snapshot, when an aggregate exists.</summary>
    public MaterializationSynchronizationWorkSnapshot? Snapshot { get; }

    /// <summary>Replay-stable work assigned by a successful prepare mutation, when applicable.</summary>
    public MaterializationPreparedSynchronizationWork? PreparedWork { get; }

    /// <summary>Structured deterministic diagnostics for a rejected mutation.</summary>
    public ImmutableArray<DocumentValidationDiagnostic> Diagnostics { get; }
}

/// <summary>
/// Atomic provider-neutral persistence port for generation-wide incremental synchronization work.
/// </summary>
/// <remarks>
/// A caller prepares at most one bounded work item, durably applies its concrete mutations to the target, and then
/// completes that exact prepared work before another version can be allocated. Preparing and completing are kept
/// separate so ambiguous target writes and process crashes recover the same mutation identities and item version.
/// </remarks>
public interface IMaterializationSynchronizationWorkStore
{
    /// <summary>Loads the latest coherent bounded synchronization-work state.</summary>
    /// <param name="context">Operation context carrying tracing and cancellation.</param>
    /// <param name="key">Exact fingerprint- and generation-fenced synchronization identity.</param>
    /// <returns>The latest snapshot, or <see langword="null"/> when absent.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> or <paramref name="key"/> is null.</exception>
    /// <exception cref="OperationCanceledException">The context is canceled before reading.</exception>
    Task<MaterializationSynchronizationWorkSnapshot?> LoadAsync(
        OperationContext context,
        MaterializationSynchronizationWorkKey key);

    /// <summary>Creates or supersedes single-writer ownership under compare-and-swap.</summary>
    /// <param name="context">Operation context carrying tracing and cancellation.</param>
    /// <param name="key">Exact fingerprint- and generation-fenced synchronization identity.</param>
    /// <param name="mutationId">Stable identity reused only for an exact acquisition retry.</param>
    /// <param name="expectedRevision"><see langword="null"/> requires absence; otherwise the exact current revision.</param>
    /// <param name="owner">Stable physical worker identity.</param>
    /// <returns>Applied, replayed, missing, revision-conflict, or identity-conflict evidence.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> or <paramref name="key"/> is null.</exception>
    /// <exception cref="ArgumentException">A mutation or owner identity is invalid.</exception>
    /// <exception cref="OperationCanceledException">The context is canceled before the atomic boundary.</exception>
    Task<MaterializationSynchronizationWorkMutationResult> AcquireFenceAsync(
        OperationContext context,
        MaterializationSynchronizationWorkKey key,
        MaterializationProgressMutationId mutationId,
        MaterializationProgressRevision? expectedRevision,
        string owner);

    /// <summary>
    /// Atomically allocates one replay-stable generation-wide item version and prepares concrete target mutations.
    /// </summary>
    /// <param name="context">Operation context carrying tracing and cancellation.</param>
    /// <param name="key">Exact fingerprint- and generation-fenced synchronization identity.</param>
    /// <param name="mutationId">Stable identity reused only for an exact prepare retry.</param>
    /// <param name="expectedRevision">Exact current compare-and-swap revision.</param>
    /// <param name="owner">Stable current worker identity.</param>
    /// <param name="fence">Exact current worker fence.</param>
    /// <param name="intent">Canonical unversioned item work.</param>
    /// <returns>
    /// Applied or replayed prepared work, or explicit missing, revision, fence, identity, or pending-work conflict
    /// evidence.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="context"/>, <paramref name="key"/>, or <paramref name="intent"/> is null.
    /// </exception>
    /// <exception cref="ArgumentException">A mutation, revision, owner, or fence identity is invalid.</exception>
    /// <exception cref="OverflowException">The generation-wide 64-bit item-version sequence is exhausted.</exception>
    /// <exception cref="OperationCanceledException">The context is canceled before the atomic boundary.</exception>
    Task<MaterializationSynchronizationWorkMutationResult> PrepareAsync(
        OperationContext context,
        MaterializationSynchronizationWorkKey key,
        MaterializationProgressMutationId mutationId,
        MaterializationProgressRevision expectedRevision,
        string owner,
        MaterializationProgressFence fence,
        MaterializationSynchronizationWorkIntent intent);

    /// <summary>Atomically clears one exact prepared write after its target effects are durable.</summary>
    /// <param name="context">Operation context carrying tracing and cancellation.</param>
    /// <param name="key">Exact fingerprint- and generation-fenced synchronization identity.</param>
    /// <param name="mutationId">Stable identity reused only for an exact completion retry.</param>
    /// <param name="expectedRevision">Exact current compare-and-swap revision.</param>
    /// <param name="owner">Stable current worker identity.</param>
    /// <param name="fence">Exact current worker fence.</param>
    /// <param name="preparationId">Preparation mutation identity of the exact pending work.</param>
    /// <param name="version">Assigned item version, or null for an effect-free prepared page.</param>
    /// <returns>
    /// Applied or replayed completion, or explicit missing, revision, fence, identity, or pending-work conflict
    /// evidence.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> or <paramref name="key"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// A mutation, revision, owner, fence, preparation, or version identity is invalid.
    /// </exception>
    /// <exception cref="OperationCanceledException">The context is canceled before the atomic boundary.</exception>
    Task<MaterializationSynchronizationWorkMutationResult> CompleteAsync(
        OperationContext context,
        MaterializationSynchronizationWorkKey key,
        MaterializationProgressMutationId mutationId,
        MaterializationProgressRevision expectedRevision,
        string owner,
        MaterializationProgressFence fence,
        MaterializationProgressMutationId preparationId,
        MaterializationItemVersion? version);

    /// <summary>Atomically begins or advances the exact prefix-ordered generation-activation protocol.</summary>
    /// <param name="context">Operation context carrying tracing and cancellation.</param>
    /// <param name="key">Exact fingerprint- and generation-fenced synchronization identity.</param>
    /// <param name="mutationId">Stable identity reused only for an exact activation-state retry.</param>
    /// <param name="expectedRevision">Exact current compare-and-swap revision.</param>
    /// <param name="owner">Stable current worker identity.</param>
    /// <param name="fence">Exact current worker fence.</param>
    /// <param name="activation">Complete next durable activation prefix.</param>
    /// <returns>
    /// Applied or replayed state, or explicit missing, revision, fence, identity, pending-work, or activation conflict
    /// evidence.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="context"/>, <paramref name="key"/>, or <paramref name="activation"/> is null.
    /// </exception>
    /// <exception cref="ArgumentException">A mutation, revision, owner, or fence identity is invalid.</exception>
    /// <exception cref="OperationCanceledException">The context is canceled before the atomic boundary.</exception>
    Task<MaterializationSynchronizationWorkMutationResult> SaveActivationAsync(
        OperationContext context,
        MaterializationSynchronizationWorkKey key,
        MaterializationProgressMutationId mutationId,
        MaterializationProgressRevision expectedRevision,
        string owner,
        MaterializationProgressFence fence,
        MaterializationGenerationActivationState activation);
}
