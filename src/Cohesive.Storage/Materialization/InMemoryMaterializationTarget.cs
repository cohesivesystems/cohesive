using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Model.Serialization;

namespace Cohesive.Storage.Materialization;

/// <summary>Immutable deterministic fault plan for the in-memory target reference implementation.</summary>
public sealed record InMemoryMaterializationTargetFaultPlan
{
    /// <summary>Gets an empty fault plan.</summary>
    public static InMemoryMaterializationTargetFaultPlan None { get; } = new();

    /// <summary>Creates a deterministic fault plan.</summary>
    /// <param name="retryableRejections">
    /// Number of initial attempts to reject transiently for each item, independent of batch identity.
    /// </param>
    /// <param name="permanentFailures">Items whose mutations always fail permanently.</param>
    /// <param name="validationFailures">Generations whose target-native validation always fails.</param>
    /// <exception cref="ArgumentException">
    /// A retry count is not positive or a supplied collection contains a default identity or duplicate entry.
    /// </exception>
    public InMemoryMaterializationTargetFaultPlan(
        IEnumerable<KeyValuePair<MaterializationItemId, int>>? retryableRejections = null,
        IEnumerable<MaterializationItemId>? permanentFailures = null,
        IEnumerable<MaterializationGenerationId>? validationFailures = null)
    {
        var retryBuilder = ImmutableDictionary.CreateBuilder<MaterializationItemId, int>();
        foreach (var entry in retryableRejections ?? [])
        {
            MaterializationContract.RequireDefinedIdentity(entry.Key.Value, nameof(retryableRejections));
            if (entry.Value <= 0)
            {
                throw new ArgumentException("A retryable rejection count must be positive.", nameof(retryableRejections));
            }

            if (!retryBuilder.TryAdd(entry.Key, entry.Value))
            {
                throw new ArgumentException($"Retry fault item '{entry.Key.Value}' is duplicated.", nameof(retryableRejections));
            }
        }

        var permanentBuilder = ImmutableHashSet.CreateBuilder<MaterializationItemId>();
        foreach (var itemId in permanentFailures ?? [])
        {
            MaterializationContract.RequireDefinedIdentity(itemId.Value, nameof(permanentFailures));
            if (!permanentBuilder.Add(itemId))
            {
                throw new ArgumentException($"Permanent fault item '{itemId.Value}' is duplicated.", nameof(permanentFailures));
            }
        }

        var validationBuilder = ImmutableHashSet.CreateBuilder<MaterializationGenerationId>();
        foreach (var generationId in validationFailures ?? [])
        {
            MaterializationContract.RequireDefinedIdentity(generationId.Value, nameof(validationFailures));
            if (!validationBuilder.Add(generationId))
            {
                throw new ArgumentException(
                    $"Validation fault generation '{generationId.Value}' is duplicated.",
                    nameof(validationFailures));
            }
        }

        RetryableRejections = retryBuilder.ToImmutable();
        PermanentFailures = permanentBuilder.ToImmutable();
        ValidationFailures = validationBuilder.ToImmutable();
    }

    /// <summary>Gets snapshotted retryable rejection counts by item.</summary>
    public ImmutableDictionary<MaterializationItemId, int> RetryableRejections { get; }

    /// <summary>Gets snapshotted permanent item failures.</summary>
    public ImmutableHashSet<MaterializationItemId> PermanentFailures { get; }

    /// <summary>Gets snapshotted target-native validation failures.</summary>
    public ImmutableHashSet<MaterializationGenerationId> ValidationFailures { get; }
}

/// <summary>Immutable retained-item snapshot exposed only by the in-memory semantic test oracle.</summary>
public sealed record InMemoryMaterializationTargetItemSnapshot
{
    /// <summary>Creates a retained-item snapshot.</summary>
    /// <param name="itemId">Stable logical item key.</param>
    /// <param name="version">Latest retained logical version.</param>
    /// <param name="mutationId">Mutation identity that produced the retained version.</param>
    /// <param name="value">Portable value, or null when the retained version is a delete tombstone.</param>
    /// <exception cref="ArgumentException">An identity or version is default, or <paramref name="value"/> is undefined.</exception>
    [System.Text.Json.Serialization.JsonConstructor]
    public InMemoryMaterializationTargetItemSnapshot(
        MaterializationItemId itemId,
        MaterializationItemVersion version,
        MaterializationItemMutationId mutationId,
        ObservationValue? value)
    {
        MaterializationContract.RequireDefinedIdentity(itemId.Value, nameof(itemId));
        MaterializationContract.RequireDefinedIdentity(version.Value, nameof(version));
        MaterializationContract.RequireDefinedIdentity(mutationId.Value, nameof(mutationId));
        if (value is { Kind: ObservationValueKind.Undefined })
        {
            throw new ArgumentException("A retained materialized value cannot be undefined.", nameof(value));
        }

        ItemId = itemId;
        Version = version;
        MutationId = mutationId;
        Value = value;
    }

    /// <summary>Gets the stable logical item key.</summary>
    public MaterializationItemId ItemId { get; }

    /// <summary>Gets the latest retained logical version.</summary>
    public MaterializationItemVersion Version { get; }

    /// <summary>Gets the mutation identity that produced the retained version.</summary>
    public MaterializationItemMutationId MutationId { get; }

    /// <summary>Gets the portable value, or null for a delete tombstone.</summary>
    public ObservationValue? Value { get; }
}

/// <summary>Bounded, deterministic item page exposed only by the in-memory semantic test oracle.</summary>
public sealed record InMemoryMaterializationTargetItemPage
{
    /// <summary>Creates a bounded item-inspection page.</summary>
    /// <param name="generationId">Inspected generation identity.</param>
    /// <param name="items">Ordinally ordered retained items and tombstones.</param>
    /// <param name="nextAfterItemId">Last returned key when another page exists; otherwise null.</param>
    /// <exception cref="ArgumentException">The generation identity is default, items are null, unordered, duplicated, or inconsistent with the continuation.</exception>
    [System.Text.Json.Serialization.JsonConstructor]
    public InMemoryMaterializationTargetItemPage(
        MaterializationGenerationId generationId,
        ImmutableArray<InMemoryMaterializationTargetItemSnapshot> items,
        MaterializationItemId? nextAfterItemId)
    {
        MaterializationContract.RequireDefinedIdentity(generationId.Value, nameof(generationId));
        Items = items.IsDefault ? [] : items;
        for (var index = 0; index < Items.Length; index++)
        {
            if (Items[index] is null)
            {
                throw new ArgumentException("An inspection page cannot contain null items.", nameof(items));
            }

            if (index > 0
                && string.CompareOrdinal(Items[index - 1].ItemId.Value, Items[index].ItemId.Value) >= 0)
            {
                throw new ArgumentException("An inspection page must contain unique item keys in ordinal order.", nameof(items));
            }
        }
        if (nextAfterItemId is { } continuation)
        {
            MaterializationContract.RequireDefinedIdentity(continuation.Value, nameof(nextAfterItemId));
            if (Items.IsDefaultOrEmpty || Items[^1].ItemId != continuation)
            {
                throw new ArgumentException("A continuation must equal the last returned item key.", nameof(nextAfterItemId));
            }
        }
        GenerationId = generationId;
        NextAfterItemId = nextAfterItemId;
    }

    /// <summary>Gets the inspected generation identity.</summary>
    public MaterializationGenerationId GenerationId { get; }

    /// <summary>Gets the bounded ordinally ordered retained items and tombstones.</summary>
    public ImmutableArray<InMemoryMaterializationTargetItemSnapshot> Items { get; }

    /// <summary>Gets the last returned key when another page exists; otherwise null.</summary>
    public MaterializationItemId? NextAfterItemId { get; }
}

/// <summary>Copy-on-write in-memory semantic reference implementation of <see cref="IMaterializationTarget"/>.</summary>
/// <remarks>
/// The implementation is a deterministic test oracle, not a production durability claim. It keeps every retained
/// generation in isolated immutable state, publishes mutations under one lock, and never exposes mutable backing
/// collections. A candidate first becomes visible to readers through one validated, fenced compare-and-swap
/// promotion; the active generation then accepts separately fenced incremental mutations.
/// </remarks>
public sealed class InMemoryMaterializationTarget : IMaterializationTarget
{
    const string RetryableFaultCode = "materialization.target.item.retryableRejected";
    const string PermanentFaultCode = "materialization.target.item.permanentFailure";
    const string VersionConflictCode = "materialization.target.item.versionConflict";
    const string IdempotencyConflictCode = "materialization.target.item.idempotencyConflict";
    const string GenerationMissingCode = "materialization.target.generation.notFound";
    const string GenerationNotWritableCode = "materialization.target.generation.notWritable";
    const string StaleFenceCode = "materialization.target.worker.staleFence";
    const string BatchLimitCode = "materialization.target.batch.limitExceeded";
    const string ValidationWriteFailureCode = "materialization.target.validation.permanentWriteFailure";
    const string ValidationCountCode = "materialization.target.validation.itemCountMismatch";
    const string ValidationInjectedCode = "materialization.target.validation.injectedFailure";

    readonly Lock gate = new();
    readonly ImmutableHashSet<MaterializationItemId> permanentFailures;
    readonly ImmutableHashSet<MaterializationGenerationId> validationFailures;
    readonly Dictionary<MaterializationItemId, int> remainingRetryableRejections;
    readonly Dictionary<MaterializationGenerationId, StoredGeneration> generations = [];
    readonly Dictionary<MaterializationGenerationId, GenerationIdentity> generationIdentities = [];
    readonly Dictionary<MaterializationBatchId, BatchReceipt> batchReceipts = [];
    readonly Dictionary<MaterializationSealId, SealOperationReceipt> sealReceipts = [];
    readonly Dictionary<MaterializationValidationId, ValidationOperationReceipt> validationReceipts = [];
    readonly Dictionary<MaterializationPromotionId, PromotionOperationReceipt> promotionReceipts = [];
    readonly Dictionary<MaterializationAbandonmentId, MaterializationTargetIntentFingerprint> abandonmentReservations = [];
    readonly Dictionary<MaterializationAbandonmentId, AbandonmentOperationReceipt> abandonmentReceipts = [];
    readonly Dictionary<MaterializationGenerationId, MaterializationAbandonmentReceipt> generationAbandonments = [];
    readonly Dictionary<MaterializationRetirementId, RetirementOperationReceipt> retirementReceipts = [];
    readonly Dictionary<MaterializationCleanupId, CleanupOperationReceipt> cleanupReceipts = [];

    MaterializationTargetRevision targetRevision = MaterializationTargetRevision.Initial;
    MaterializationGenerationId? activeGenerationId;
    MaterializationPromotionFence? latestPromotionFence;
    DateTimeOffset? latestPromotionAtUtc;

    /// <summary>Creates an empty in-memory materialization target.</summary>
    /// <param name="descriptor">Target identity and complete advertised capability evidence.</param>
    /// <param name="faultPlan">Optional immutable deterministic fault plan.</param>
    /// <exception cref="ArgumentNullException"><paramref name="descriptor"/> is <see langword="null"/>.</exception>
    public InMemoryMaterializationTarget(
        MaterializationTargetDescriptor descriptor,
        InMemoryMaterializationTargetFaultPlan? faultPlan = null)
    {
        Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
        faultPlan ??= InMemoryMaterializationTargetFaultPlan.None;
        permanentFailures = faultPlan.PermanentFailures;
        validationFailures = faultPlan.ValidationFailures;
        remainingRetryableRejections = faultPlan.RetryableRejections.ToDictionary();
    }

    /// <inheritdoc />
    public MaterializationTargetDescriptor Descriptor { get; }

    /// <inheritdoc />
    public ValueTask<MaterializationTargetSnapshot> InspectAsync(OperationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.CancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            return ValueTask.FromResult(Snapshot());
        }
    }

    /// <inheritdoc />
    public ValueTask<MaterializationGenerationSnapshot?> InspectGenerationAsync(
        OperationContext context,
        MaterializationGenerationId generationId)
    {
        ArgumentNullException.ThrowIfNull(context);
        MaterializationContract.RequireDefinedIdentity(generationId.Value, nameof(generationId));
        context.CancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            return ValueTask.FromResult(TrySnapshot(generationId));
        }
    }

    /// <summary>Reads one bounded page of retained items from this in-memory semantic test oracle.</summary>
    /// <param name="context">Operation context carrying cancellation and trace metadata.</param>
    /// <param name="generationId">Retained generation to inspect.</param>
    /// <param name="afterItemId">Exclusive ordinal key continuation, or null for the first page.</param>
    /// <param name="maximumItems">Positive maximum number of items to return.</param>
    /// <returns>A bounded page, or null when physical generation state is not retained.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A supplied identity is default.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maximumItems"/> is not positive.</exception>
    /// <exception cref="OperationCanceledException">The operation cancellation token was canceled.</exception>
    public ValueTask<InMemoryMaterializationTargetItemPage?> InspectItemsAsync(
        OperationContext context,
        MaterializationGenerationId generationId,
        MaterializationItemId? afterItemId,
        int maximumItems)
    {
        ArgumentNullException.ThrowIfNull(context);
        MaterializationContract.RequireDefinedIdentity(generationId.Value, nameof(generationId));
        if (afterItemId is { } after)
        {
            MaterializationContract.RequireDefinedIdentity(after.Value, nameof(afterItemId));
        }

        if (maximumItems <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumItems), maximumItems, "An inspection page size must be positive.");
        }

        context.CancellationToken.ThrowIfCancellationRequested();

        lock (gate)
        {
            if (!generations.TryGetValue(generationId, out var generation))
            {
                return ValueTask.FromResult<InMemoryMaterializationTargetItemPage?>(null);
            }

            var inspectionLimit = maximumItems == int.MaxValue ? int.MaxValue : maximumItems + 1;
            var ordered = generation.Items.Values
                .Where(item => afterItemId is null || string.CompareOrdinal(item.ItemId.Value, afterItemId.Value.Value) > 0)
                .OrderBy(static item => item.ItemId.Value, StringComparer.Ordinal)
                .Take(inspectionLimit)
                .ToArray();
            var count = Math.Min(maximumItems, ordered.Length);
            var builder = ImmutableArray.CreateBuilder<InMemoryMaterializationTargetItemSnapshot>(count);
            for (var index = 0; index < count; index++)
            {
                var item = ordered[index];
                builder.Add(new(item.ItemId, item.Version, item.MutationId, item.Value));
            }
            var items = builder.MoveToImmutable();
            var next = ordered.Length > maximumItems ? items[^1].ItemId : (MaterializationItemId?)null;
            return ValueTask.FromResult<InMemoryMaterializationTargetItemPage?>(
                new(generationId, items, next));
        }
    }

    /// <inheritdoc />
    public ValueTask<MaterializationGenerationOperationResult> BeginGenerationAsync(
        OperationContext context,
        MaterializationBeginGenerationRequest request)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        context.CancellationToken.ThrowIfCancellationRequested();
        var requestFingerprint = MaterializationTargetIntentFingerprinter.Compute(request);

        lock (gate)
        {
            if (request.MaterializationId != Descriptor.MaterializationId)
            {
                return ValueTask.FromResult(new MaterializationGenerationOperationResult(
                    MaterializationTargetOperationDisposition.MaterializationConflict,
                    TrySnapshot(request.GenerationId)));
            }

            if (generationAbandonments.ContainsKey(request.GenerationId)
                && !generations.ContainsKey(request.GenerationId))
            {
                return ValueTask.FromResult(new MaterializationGenerationOperationResult(
                    MaterializationTargetOperationDisposition.StateConflict,
                    generation: null));
            }

            if (generationIdentities.TryGetValue(request.GenerationId, out var identity))
            {
                if (identity.BeginRequestFingerprint == requestFingerprint)
                {
                    AcceptGenerationFence(request.GenerationId, request.WorkerFence);
                    return ValueTask.FromResult(generations.TryGetValue(request.GenerationId, out var replayed)
                        ? new MaterializationGenerationOperationResult(
                            MaterializationTargetOperationDisposition.Replayed,
                            Snapshot(replayed))
                        : new MaterializationGenerationOperationResult(
                            MaterializationTargetOperationDisposition.AlreadyExists,
                            generation: null));
                }

                if (IsStale(request.WorkerFence, identity.LatestWorkerFence))
                {
                    return ValueTask.FromResult(new MaterializationGenerationOperationResult(
                        MaterializationTargetOperationDisposition.StaleFence,
                        TrySnapshot(request.GenerationId)));
                }
                AcceptGenerationFence(request.GenerationId, request.WorkerFence);
                return ValueTask.FromResult(new MaterializationGenerationOperationResult(
                    MaterializationTargetOperationDisposition.IdentityConflict,
                    TrySnapshot(request.GenerationId)));
            }

            StoredGeneration created = new(
                request.MaterializationId,
                request.GenerationId,
                request.DefinitionFingerprint,
                MaterializationGenerationState.Loading,
                MaterializationGenerationRevision.Initial,
                HasPermanentFailures: false,
                PendingRetryableMutations: [],
                Items: [],
                MutationReceipts: [],
                SealReceipt: null,
                ValidationReceipt: null,
                request.CreatedAtUtc,
                InactivatedAtUtc: null,
                RetiredAtUtc: null);
            generationIdentities.Add(
                request.GenerationId,
                new(requestFingerprint, request.WorkerFence));
            generations.Add(request.GenerationId, created);
            return ValueTask.FromResult(new MaterializationGenerationOperationResult(
                MaterializationTargetOperationDisposition.Applied,
                Snapshot(created)));
        }
    }

    /// <inheritdoc />
    public ValueTask<MaterializationBatchResult> ApplyBatchAsync(
        OperationContext context,
        MaterializationApplyBatchRequest request)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        context.CancellationToken.ThrowIfCancellationRequested();
        var requestIntent = MaterializationTargetIntentFingerprinter.AnalyzeBatch(request);
        var requestFingerprint = requestIntent.Fingerprint;

        lock (gate)
        {
            if (batchReceipts.TryGetValue(request.BatchId, out var prior))
            {
                if (prior.RequestFingerprint == requestFingerprint)
                {
                    AcceptGenerationFence(request.GenerationId, request.WorkerFence);
                    return ValueTask.FromResult(Replay(prior.Result, request));
                }

                AcceptGenerationFenceIfKnown(request.GenerationId, request.WorkerFence);
                return ValueTask.FromResult(RejectedBatch(
                    request,
                    MaterializationBatchDisposition.IdentityConflict,
                    generations.GetValueOrDefault(request.GenerationId)?.Revision,
                    MaterializationItemOutcomeDisposition.IdempotencyConflict,
                    IdempotencyConflictCode,
                    "The batch identity was reused for different canonical content."));
            }

            if (!generations.TryGetValue(request.GenerationId, out var generation))
            {
                if (generationAbandonments.ContainsKey(request.GenerationId))
                {
                    return ValueTask.FromResult(RejectedBatch(
                        request,
                        MaterializationBatchDisposition.GenerationNotWritable,
                        generationRevision: null,
                        MaterializationItemOutcomeDisposition.PermanentFailure,
                        GenerationNotWritableCode,
                        "The addressed generation identity is durably abandoned."));
                }

                return ValueTask.FromResult(RejectedBatch(
                    request,
                    MaterializationBatchDisposition.GenerationNotFound,
                    generationRevision: null,
                    MaterializationItemOutcomeDisposition.PermanentFailure,
                    GenerationMissingCode,
                    "The addressed generation does not exist."));
            }

            var identity = generationIdentities[request.GenerationId];
            if (IsStale(request.WorkerFence, identity.LatestWorkerFence))
            {
                return ValueTask.FromResult(RejectedBatch(
                    request,
                    MaterializationBatchDisposition.StaleFence,
                    generation.Revision,
                    MaterializationItemOutcomeDisposition.RetryableRejected,
                    StaleFenceCode,
                    "A newer worker fence superseded this generation mutation."));
            }
            AcceptGenerationFence(request.GenerationId, request.WorkerFence);

            if (generation.State is not (MaterializationGenerationState.Loading or MaterializationGenerationState.Active))
            {
                return ValueTask.FromResult(RejectedBatch(
                    request,
                    MaterializationBatchDisposition.GenerationNotWritable,
                    generation.Revision,
                    MaterializationItemOutcomeDisposition.PermanentFailure,
                    GenerationNotWritableCode,
                    "Only a loading candidate or the active generation accepts writes."));
            }

            if (!SupportsWriteBounds(
                    Descriptor.Capabilities,
                    requestIntent))
            {
                return ValueTask.FromResult(RejectedBatch(
                    request,
                    MaterializationBatchDisposition.LimitExceeded,
                    generation.Revision,
                    MaterializationItemOutcomeDisposition.RetryableRejected,
                    BatchLimitCode,
                    $"No single realization of every applicable target capability accepts the batch's {requestIntent.ItemCount} items and {requestIntent.CanonicalByteCount} canonical bytes."));
            }

            var items = generation.Items;
            var mutations = generation.MutationReceipts;
            var pendingRetryableMutations = generation.PendingRetryableMutations;
            var outcomes = ImmutableArray.CreateBuilder<MaterializationItemOutcome>(request.Mutations.Length);
            var appliedAny = false;
            var hasPermanentFailures = generation.HasPermanentFailures;
            foreach (var mutation in request.Mutations)
            {
                var mutationFingerprint = MaterializationTargetIntentFingerprinter.Compute(mutation);
                if (mutations.TryGetValue(mutation.MutationId, out var priorMutation))
                {
                    if (priorMutation.Fingerprint == mutationFingerprint)
                    {
                        pendingRetryableMutations = pendingRetryableMutations.Remove(mutation.MutationId);
                        outcomes.Add(Success(mutation, MaterializationItemOutcomeDisposition.Replayed));
                    }
                    else
                    {
                        hasPermanentFailures = true;
                        outcomes.Add(Failure(
                            mutation,
                            MaterializationItemOutcomeDisposition.IdempotencyConflict,
                            IdempotencyConflictCode,
                            "The mutation identity was reused for different canonical content."));
                    }
                    continue;
                }

                var hasPendingMutation = pendingRetryableMutations.TryGetValue(
                    mutation.MutationId,
                    out var pendingMutation);
                if (hasPendingMutation
                    && (pendingMutation!.ItemId != mutation.ItemId
                        || pendingMutation.Version != mutation.Version
                        || pendingMutation.Fingerprint != mutationFingerprint))
                {
                    hasPermanentFailures = true;
                    outcomes.Add(Failure(
                        mutation,
                        MaterializationItemOutcomeDisposition.IdempotencyConflict,
                        IdempotencyConflictCode,
                        "The pending mutation identity was reused for different canonical content."));
                    continue;
                }

                if (remainingRetryableRejections.TryGetValue(mutation.ItemId, out var remaining) && remaining > 0)
                {
                    if (remaining == 1)
                    {
                        remainingRetryableRejections.Remove(mutation.ItemId);
                    }
                    else
                    {
                        remainingRetryableRejections[mutation.ItemId] = remaining - 1;
                    }

                    pendingRetryableMutations = pendingRetryableMutations.SetItem(
                        mutation.MutationId,
                        new(mutation.ItemId, mutation.Version, mutationFingerprint));
                    outcomes.Add(Failure(
                        mutation,
                        MaterializationItemOutcomeDisposition.RetryableRejected,
                        RetryableFaultCode,
                        "The deterministic fault plan rejected this attempt transiently."));
                    continue;
                }

                if (permanentFailures.Contains(mutation.ItemId))
                {
                    hasPermanentFailures = true;
                    pendingRetryableMutations = pendingRetryableMutations.Remove(mutation.MutationId);
                    outcomes.Add(Failure(
                        mutation,
                        MaterializationItemOutcomeDisposition.PermanentFailure,
                        PermanentFaultCode,
                        "The deterministic fault plan rejected this item permanently."));
                    continue;
                }

                if (items.TryGetValue(mutation.ItemId, out var current)
                    && mutation.Version.Ordinal <= current.Version.Ordinal)
                {
                    if (mutation.Version.Ordinal < current.Version.Ordinal)
                    {
                        pendingRetryableMutations = pendingRetryableMutations.Remove(mutation.MutationId);
                    }
                    else if (hasPendingMutation)
                    {
                        hasPermanentFailures = true;
                    }

                    outcomes.Add(Failure(
                        mutation,
                        MaterializationItemOutcomeDisposition.VersionConflict,
                        VersionConflictCode,
                        $"Item version {mutation.Version.Value} does not advance retained version {current.Version.Value}."));
                    continue;
                }

                var value = mutation is MaterializationUpsert upsert ? upsert.Value : (ObservationValue?)null;
                pendingRetryableMutations = pendingRetryableMutations.Remove(mutation.MutationId);
                items = items.SetItem(
                    mutation.ItemId,
                    new StoredItem(
                        mutation.ItemId,
                        mutation.Version,
                        mutation.MutationId,
                        mutation.Kind,
                        value));
                mutations = mutations.Add(mutation.MutationId, new(mutationFingerprint));
                outcomes.Add(Success(mutation, MaterializationItemOutcomeDisposition.Applied));
                appliedAny = true;
            }

            var revision = appliedAny
                || hasPermanentFailures != generation.HasPermanentFailures
                || !PendingMutationsEqual(pendingRetryableMutations, generation.PendingRetryableMutations)
                ? generation.Revision.Next()
                : generation.Revision;
            var updated = generation with
            {
                Revision = revision,
                HasPermanentFailures = hasPermanentFailures,
                PendingRetryableMutations = pendingRetryableMutations,
                Items = items,
                MutationReceipts = mutations
            };
            generations[request.GenerationId] = updated;
            var result = MaterializationBatchResult.ForRequest(
                request,
                MaterializationBatchDisposition.Applied,
                revision,
                outcomes.MoveToImmutable());
            batchReceipts.Add(request.BatchId, new(requestFingerprint, result));
            return ValueTask.FromResult(result);
        }
    }

    /// <inheritdoc />
    public ValueTask<MaterializationSealResult> SealGenerationAsync(
        OperationContext context,
        MaterializationSealGenerationRequest request)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        context.CancellationToken.ThrowIfCancellationRequested();
        var requestFingerprint = MaterializationTargetIntentFingerprinter.Compute(request);

        lock (gate)
        {
            if (sealReceipts.TryGetValue(request.SealId, out var prior))
            {
                if (prior.RequestFingerprint != requestFingerprint)
                {
                    AcceptGenerationFenceIfKnown(request.GenerationId, request.WorkerFence);
                    return ValueTask.FromResult(new MaterializationSealResult(
                        MaterializationTargetOperationDisposition.IdentityConflict,
                        TrySnapshot(request.GenerationId),
                        receipt: null));
                }

                AcceptGenerationFence(request.GenerationId, request.WorkerFence);
                return ValueTask.FromResult(new MaterializationSealResult(
                    MaterializationTargetOperationDisposition.Replayed,
                    TrySnapshot(request.GenerationId) ?? SnapshotWithLatestFence(prior.Generation),
                    prior.Receipt));
            }

            if (!generations.TryGetValue(request.GenerationId, out var generation))
            {
                return ValueTask.FromResult(new MaterializationSealResult(MaterializationTargetOperationDisposition.NotFound, null, null));
            }
            if (request.SealedAtUtc < generation.CreatedAtUtc)
            {
                throw new ArgumentException("A seal time cannot predate generation creation.", nameof(request));
            }

            if (IsStale(request.WorkerFence, generationIdentities[request.GenerationId].LatestWorkerFence))
            {
                return ValueTask.FromResult(new MaterializationSealResult(
                    MaterializationTargetOperationDisposition.StaleFence,
                    Snapshot(generation),
                    receipt: null));
            }
            AcceptGenerationFence(request.GenerationId, request.WorkerFence);

            if (generation.Revision != request.ExpectedRevision)
            {
                return ValueTask.FromResult(new MaterializationSealResult(MaterializationTargetOperationDisposition.RevisionConflict, Snapshot(generation), null));
            }

            if (generation.State != MaterializationGenerationState.Loading)
            {
                return ValueTask.FromResult(new MaterializationSealResult(MaterializationTargetOperationDisposition.StateConflict, Snapshot(generation), null));
            }

            var revision = generation.Revision.Next();
            var receipt = new MaterializationSealReceipt(
                request.SealId,
                request.GenerationId,
                revision,
                generation.Items.Values.LongCount(static item => item.Value is not null),
                ComputeSealFingerprint(generation.Items),
                request.SealedAtUtc);
            var updated = generation with
            {
                State = MaterializationGenerationState.Sealed,
                Revision = revision,
                SealReceipt = receipt
            };
            generations[request.GenerationId] = updated;
            var snapshot = Snapshot(updated);
            sealReceipts.Add(request.SealId, new(requestFingerprint, receipt, snapshot));
            return ValueTask.FromResult(new MaterializationSealResult(
                MaterializationTargetOperationDisposition.Applied,
                snapshot,
                receipt));
        }
    }

    /// <inheritdoc />
    public ValueTask<MaterializationValidationResult> ValidateGenerationAsync(
        OperationContext context,
        MaterializationValidateGenerationRequest request)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        context.CancellationToken.ThrowIfCancellationRequested();
        var requestFingerprint = MaterializationTargetIntentFingerprinter.Compute(request);

        lock (gate)
        {
            if (validationReceipts.TryGetValue(request.ValidationId, out var prior))
            {
                if (prior.RequestFingerprint != requestFingerprint)
                {
                    AcceptGenerationFenceIfKnown(request.GenerationId, request.WorkerFence);
                    return ValueTask.FromResult(new MaterializationValidationResult(
                        MaterializationTargetOperationDisposition.IdentityConflict,
                        TrySnapshot(request.GenerationId),
                        receipt: null));
                }

                AcceptGenerationFence(request.GenerationId, request.WorkerFence);
                return ValueTask.FromResult(new MaterializationValidationResult(
                    MaterializationTargetOperationDisposition.Replayed,
                    TrySnapshot(request.GenerationId) ?? SnapshotWithLatestFence(prior.Generation),
                    prior.Receipt));
            }

            if (!generations.TryGetValue(request.GenerationId, out var generation))
            {
                return ValueTask.FromResult(new MaterializationValidationResult(MaterializationTargetOperationDisposition.NotFound, null, null));
            }
            var latestValidationBoundary = generation.ValidationReceipt?.ValidatedAtUtc
                ?? generation.SealReceipt?.SealedAtUtc;
            if (latestValidationBoundary is { } latestValidationAtUtc
                && request.ValidatedAtUtc < latestValidationAtUtc)
            {
                throw new ArgumentException(
                    "A validation time cannot predate the generation's latest seal or validation boundary.",
                    nameof(request));
            }

            if (IsStale(request.WorkerFence, generationIdentities[request.GenerationId].LatestWorkerFence))
            {
                return ValueTask.FromResult(new MaterializationValidationResult(
                    MaterializationTargetOperationDisposition.StaleFence,
                    Snapshot(generation),
                    receipt: null));
            }
            AcceptGenerationFence(request.GenerationId, request.WorkerFence);

            if (generation.Revision != request.ExpectedRevision)
            {
                return ValueTask.FromResult(new MaterializationValidationResult(MaterializationTargetOperationDisposition.RevisionConflict, Snapshot(generation), null));
            }

            if (generation.State != MaterializationGenerationState.Sealed || generation.SealReceipt is null)
            {
                return ValueTask.FromResult(new MaterializationValidationResult(MaterializationTargetOperationDisposition.StateConflict, Snapshot(generation), null));
            }

            ImmutableArray<string> validationSources = string.Equals(
                Descriptor.Capabilities.Id.Value,
                request.Validator,
                StringComparison.Ordinal)
                ? [request.Validator]
                : [Descriptor.Capabilities.Id.Value, request.Validator];
            List<DocumentValidationDiagnostic> diagnostics = [];
            if (generation.SealReceipt.Fingerprint != request.ExpectedSealFingerprint)
            {
                diagnostics.Add(MaterializationContract.CreateDiagnostic(
                    "materialization.target.validation.sealFingerprintMismatch",
                    DiagnosticSeverity.Error,
                    "The expected seal fingerprint does not match immutable generation content.",
                    "/sealFingerprint",
                    "materialization-target-validation",
                    request.GenerationId.Value,
                    validationSources,
                    request.ExpectedSealFingerprint.Value,
                    generation.SealReceipt.Fingerprint.Value));
            }
            if (generation.HasPermanentFailures)
            {
                diagnostics.Add(MaterializationContract.CreateDiagnostic(
                    ValidationWriteFailureCode,
                    DiagnosticSeverity.Error,
                    "At least one permanent write failure remains recorded for the generation.",
                    "/writes",
                    "materialization-target-validation",
                    request.GenerationId.Value,
                    validationSources,
                    "no permanent write failures",
                    "one or more permanent write failures"));
            }
            if (!generation.PendingRetryableMutations.IsEmpty)
            {
                diagnostics.Add(MaterializationContract.CreateDiagnostic(
                    "materialization.target.validation.pendingRetryableItems",
                    DiagnosticSeverity.Error,
                    $"{generation.PendingRetryableMutations.Count} retryable item mutation(s) remain unresolved.",
                    "/writes",
                    "materialization-target-validation",
                    request.GenerationId.Value,
                    validationSources,
                    "0 pending retryable item mutations",
                    generation.PendingRetryableMutations.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            }
            if (request.ExpectedVisibleItemCount is { } expected
                && expected != generation.SealReceipt.VisibleItemCount)
            {
                diagnostics.Add(MaterializationContract.CreateDiagnostic(
                    ValidationCountCode,
                    DiagnosticSeverity.Error,
                    $"Expected {expected} visible items but observed {generation.SealReceipt.VisibleItemCount}.",
                    "/visibleItemCount",
                    "materialization-target-validation",
                    request.GenerationId.Value,
                    validationSources,
                    expected.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    generation.SealReceipt.VisibleItemCount.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            }
            if (validationFailures.Contains(request.GenerationId))
            {
                diagnostics.Add(MaterializationContract.CreateDiagnostic(
                    ValidationInjectedCode,
                    DiagnosticSeverity.Error,
                    "The deterministic fault plan failed target-native validation.",
                    "/generation",
                    "materialization-target-validation",
                    request.GenerationId.Value,
                    validationSources,
                    "target-native validation succeeds",
                    "deterministic fault plan injected a validation failure"));
            }

            var normalizedDiagnostics = MaterializationContract.NormalizeDiagnostics(
                [.. diagnostics],
                nameof(diagnostics));
            var validation = normalizedDiagnostics.IsDefaultOrEmpty
                ? DocumentValidationResult.Valid
                : new DocumentValidationResult(normalizedDiagnostics);
            var revision = generation.Revision.Next();
            var validationFingerprint = MaterializationTargetIntentFingerprinter.ComputeValidationResult(
                request,
                validation);
            var receipt = new MaterializationValidationReceipt(
                request.ValidationId,
                request.GenerationId,
                revision,
                generation.SealReceipt.Fingerprint,
                validationFingerprint,
                validation,
                request.ValidatedAtUtc);
            var updated = generation with
            {
                State = validation.IsValid
                    ? MaterializationGenerationState.Validated
                    : MaterializationGenerationState.Sealed,
                Revision = revision,
                ValidationReceipt = receipt
            };
            generations[request.GenerationId] = updated;
            var snapshot = Snapshot(updated);
            validationReceipts.Add(request.ValidationId, new(requestFingerprint, receipt, snapshot));
            return ValueTask.FromResult(new MaterializationValidationResult(
                validation.IsValid
                    ? MaterializationTargetOperationDisposition.Applied
                    : MaterializationTargetOperationDisposition.ValidationFailed,
                snapshot,
                receipt));
        }
    }

    /// <inheritdoc />
    public ValueTask<MaterializationPromotionResult> PromoteGenerationAsync(
        OperationContext context,
        MaterializationPromoteGenerationRequest request)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        context.CancellationToken.ThrowIfCancellationRequested();
        var requestFingerprint = MaterializationTargetIntentFingerprinter.Compute(request);

        lock (gate)
        {
            if (promotionReceipts.TryGetValue(request.PromotionId, out var prior))
            {
                if (prior.RequestFingerprint == requestFingerprint)
                {
                    AcceptGenerationFence(request.GenerationId, request.GenerationWorkerFence);
                    AcceptPromotionFence(request.PromotionFence);
                    return ValueTask.FromResult(new MaterializationPromotionResult(
                        MaterializationTargetOperationDisposition.Replayed,
                        Snapshot(),
                        prior.Receipt));
                }

                AcceptGenerationFenceIfKnown(request.GenerationId, request.GenerationWorkerFence);
                AcceptPromotionFence(request.PromotionFence);
                return ValueTask.FromResult(new MaterializationPromotionResult(
                    MaterializationTargetOperationDisposition.IdentityConflict,
                    Snapshot(),
                    receipt: null));
            }

            var pointerFenceIsStale = latestPromotionFence is { } latest
                && request.PromotionFence.Ordinal < latest.Ordinal;
            var generationFenceIsStale = generationIdentities.TryGetValue(
                    request.GenerationId,
                    out var generationIdentity)
                && IsStale(request.GenerationWorkerFence, generationIdentity.LatestWorkerFence);
            AcceptPromotionFence(request.PromotionFence);
            AcceptGenerationFenceIfKnown(request.GenerationId, request.GenerationWorkerFence);
            if (pointerFenceIsStale || generationFenceIsStale)
            {
                return ValueTask.FromResult(new MaterializationPromotionResult(
                    MaterializationTargetOperationDisposition.StaleFence,
                    Snapshot(),
                    receipt: null));
            }
            if (!generations.TryGetValue(request.GenerationId, out var candidate))
            {
                return ValueTask.FromResult(new MaterializationPromotionResult(MaterializationTargetOperationDisposition.NotFound, Snapshot(), null));
            }

            if (latestPromotionAtUtc is { } latestPromotion
                && request.PromotedAtUtc < latestPromotion)
            {
                throw new ArgumentException(
                    "A promotion time cannot predate the latest committed target-pointer promotion.",
                    nameof(request));
            }

            if (candidate.ValidationReceipt is { } retainedValidation
                && request.PromotedAtUtc < retainedValidation.ValidatedAtUtc)
            {
                throw new ArgumentException("A promotion time cannot predate successful validation.", nameof(request));
            }

            if (targetRevision != request.ExpectedTargetRevision)
            {
                return ValueTask.FromResult(new MaterializationPromotionResult(MaterializationTargetOperationDisposition.RevisionConflict, Snapshot(), null));
            }

            if (activeGenerationId != request.ExpectedActiveGenerationId)
            {
                return ValueTask.FromResult(new MaterializationPromotionResult(MaterializationTargetOperationDisposition.ActiveGenerationConflict, Snapshot(), null));
            }

            if (candidate.Revision != request.ExpectedGenerationRevision)
            {
                return ValueTask.FromResult(new MaterializationPromotionResult(MaterializationTargetOperationDisposition.RevisionConflict, Snapshot(), null));
            }

            if (candidate.State != MaterializationGenerationState.Validated
                || candidate.ValidationReceipt is null
                || !candidate.ValidationReceipt.Validation.IsValid)
            {
                return ValueTask.FromResult(new MaterializationPromotionResult(
                    MaterializationTargetOperationDisposition.StateConflict,
                    Snapshot(),
                    receipt: null));
            }
            if (candidate.ValidationReceipt.Fingerprint != request.ValidationFingerprint
                || candidate.HasPermanentFailures)
            {
                return ValueTask.FromResult(new MaterializationPromotionResult(
                    MaterializationTargetOperationDisposition.ValidationFailed,
                    Snapshot(),
                    receipt: null));
            }
            var previousId = activeGenerationId;
            if (previousId is { } previousGenerationId)
            {
                var previous = generations[previousGenerationId];
                generations[previousGenerationId] = previous with
                {
                    State = MaterializationGenerationState.Inactive,
                    Revision = previous.Revision.Next(),
                    InactivatedAtUtc = request.PromotedAtUtc,
                    RetiredAtUtc = null
                };
            }

            generations[request.GenerationId] = candidate with
            {
                State = MaterializationGenerationState.Active,
                Revision = candidate.Revision.Next(),
                InactivatedAtUtc = null
            };
            activeGenerationId = request.GenerationId;
            targetRevision = targetRevision.Next();
            latestPromotionAtUtc = request.PromotedAtUtc;
            var receipt = new MaterializationPromotionReceipt(
                request.PromotionId,
                Descriptor.Id,
                request.GenerationId,
                previousId,
                targetRevision,
                request.GenerationWorkerFence,
                request.PromotionFence,
                request.ValidationFingerprint,
                request.PromotedAtUtc);
            promotionReceipts.Add(request.PromotionId, new(requestFingerprint, receipt));
            return ValueTask.FromResult(new MaterializationPromotionResult(
                MaterializationTargetOperationDisposition.Applied,
                Snapshot(),
                receipt));
        }
    }

    /// <inheritdoc />
    public ValueTask<MaterializationAbandonmentResult> AbandonGenerationAsync(
        OperationContext context,
        MaterializationAbandonGenerationRequest request)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        context.CancellationToken.ThrowIfCancellationRequested();
        var requestFingerprint = MaterializationTargetIntentFingerprinter.Compute(request);

        lock (gate)
        {
            if (abandonmentReceipts.TryGetValue(request.AbandonmentId, out var prior))
            {
                return ValueTask.FromResult(new MaterializationAbandonmentResult(
                    prior.RequestFingerprint == requestFingerprint
                        ? MaterializationTargetOperationDisposition.Replayed
                        : MaterializationTargetOperationDisposition.IdentityConflict,
                    prior.RequestFingerprint == requestFingerprint
                        ? TrySnapshot(request.GenerationId) ?? prior.Generation
                        : TrySnapshot(request.GenerationId),
                    prior.RequestFingerprint == requestFingerprint ? prior.Receipt : null));
            }

            if (abandonmentReservations.TryGetValue(request.AbandonmentId, out var reserved))
            {
                if (reserved != requestFingerprint)
                {
                    return ValueTask.FromResult(new MaterializationAbandonmentResult(
                        MaterializationTargetOperationDisposition.IdentityConflict,
                        TrySnapshot(request.GenerationId),
                        receipt: null));
                }
            }
            else
            {
                abandonmentReservations.Add(request.AbandonmentId, requestFingerprint);
            }

            generations.TryGetValue(request.GenerationId, out var generation);
            if (activeGenerationId == request.GenerationId
                || generation?.State == MaterializationGenerationState.Active)
            {
                return ValueTask.FromResult(new MaterializationAbandonmentResult(
                    MaterializationTargetOperationDisposition.ActiveGenerationConflict,
                    generation is null ? null : Snapshot(generation),
                    receipt: null));
            }

            if (generationAbandonments.ContainsKey(request.GenerationId))
            {
                return ValueTask.FromResult(new MaterializationAbandonmentResult(
                    MaterializationTargetOperationDisposition.StateConflict,
                    TrySnapshot(request.GenerationId),
                    receipt: null));
            }

            MaterializationGenerationSnapshot? generationSnapshot = null;
            if (generation is not null)
            {
                var latestLifecycleEvidenceAtUtc = generation.RetiredAtUtc
                    ?? generation.InactivatedAtUtc
                    ?? generation.ValidationReceipt?.ValidatedAtUtc
                    ?? generation.SealReceipt?.SealedAtUtc
                    ?? generation.CreatedAtUtc;
                if (request.AbandonedAtUtc < latestLifecycleEvidenceAtUtc)
                {
                    throw new ArgumentException(
                        "An abandonment time cannot predate the generation's latest retained lifecycle evidence.",
                        nameof(request));
                }

                var abandoned = generation.State == MaterializationGenerationState.Retired
                    ? generation
                    : generation with
                    {
                        State = MaterializationGenerationState.Retired,
                        Revision = generation.Revision.Next(),
                        RetiredAtUtc = request.AbandonedAtUtc
                    };
                generations[request.GenerationId] = abandoned;
                generationSnapshot = Snapshot(abandoned);
            }

            MaterializationAbandonmentReceipt receipt = new(
                abandonmentId: request.AbandonmentId,
                generationId: request.GenerationId,
                abandonedAtUtc: request.AbandonedAtUtc);
            generationAbandonments.Add(request.GenerationId, receipt);
            abandonmentReceipts.Add(
                request.AbandonmentId,
                new(requestFingerprint, receipt, generationSnapshot));
            return ValueTask.FromResult(new MaterializationAbandonmentResult(
                MaterializationTargetOperationDisposition.Applied,
                generationSnapshot,
                receipt));
        }
    }

    /// <inheritdoc />
    public ValueTask<MaterializationGenerationOperationResult> RetireGenerationAsync(
        OperationContext context,
        MaterializationRetireGenerationRequest request)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        context.CancellationToken.ThrowIfCancellationRequested();
        var requestFingerprint = MaterializationTargetIntentFingerprinter.Compute(request);

        lock (gate)
        {
            if (retirementReceipts.TryGetValue(request.RetirementId, out var prior))
            {
                if (prior.RequestFingerprint == requestFingerprint)
                {
                    AcceptGenerationFence(request.GenerationId, request.WorkerFence);
                    return ValueTask.FromResult(new MaterializationGenerationOperationResult(
                        MaterializationTargetOperationDisposition.Replayed,
                        TrySnapshot(request.GenerationId) ?? SnapshotWithLatestFence(prior.Generation)));
                }

                AcceptGenerationFenceIfKnown(request.GenerationId, request.WorkerFence);
                return ValueTask.FromResult(new MaterializationGenerationOperationResult(
                    MaterializationTargetOperationDisposition.IdentityConflict,
                    TrySnapshot(request.GenerationId)));
            }

            if (!generationIdentities.TryGetValue(request.GenerationId, out var identity))
            {
                return ValueTask.FromResult(new MaterializationGenerationOperationResult(MaterializationTargetOperationDisposition.NotFound, null));
            }

            if (!generations.TryGetValue(request.GenerationId, out var generation))
            {
                return ValueTask.FromResult(new MaterializationGenerationOperationResult(MaterializationTargetOperationDisposition.NotFound, null));
            }

            var latestLifecycleEvidenceAtUtc = generation.InactivatedAtUtc
                ?? generation.ValidationReceipt?.ValidatedAtUtc
                ?? generation.SealReceipt?.SealedAtUtc
                ?? generation.CreatedAtUtc;
            if (request.RetiredAtUtc < latestLifecycleEvidenceAtUtc)
            {
                throw new ArgumentException(
                    "A retirement time cannot predate the generation's latest retained lifecycle evidence.",
                    nameof(request));
            }
            if (IsStale(request.WorkerFence, identity.LatestWorkerFence))
            {
                return ValueTask.FromResult(new MaterializationGenerationOperationResult(
                    MaterializationTargetOperationDisposition.StaleFence,
                    TrySnapshot(request.GenerationId)));
            }
            AcceptGenerationFence(request.GenerationId, request.WorkerFence);
            if (activeGenerationId == request.GenerationId || generation.State == MaterializationGenerationState.Active)
            {
                return ValueTask.FromResult(new MaterializationGenerationOperationResult(MaterializationTargetOperationDisposition.ActiveGenerationConflict, Snapshot(generation)));
            }

            if (generation.Revision != request.ExpectedRevision)
            {
                return ValueTask.FromResult(new MaterializationGenerationOperationResult(MaterializationTargetOperationDisposition.RevisionConflict, Snapshot(generation)));
            }

            if (generation.State == MaterializationGenerationState.Retired)
            {
                return ValueTask.FromResult(new MaterializationGenerationOperationResult(MaterializationTargetOperationDisposition.StateConflict, Snapshot(generation)));
            }

            if (request.RetiredAtUtc < generation.CreatedAtUtc)
            {
                throw new ArgumentException("A retirement time cannot predate generation creation.", nameof(request));
            }

            var updated = generation with
            {
                State = MaterializationGenerationState.Retired,
                Revision = generation.Revision.Next(),
                RetiredAtUtc = request.RetiredAtUtc
            };
            generations[request.GenerationId] = updated;
            var snapshot = Snapshot(updated);
            retirementReceipts.Add(request.RetirementId, new(requestFingerprint, snapshot));
            return ValueTask.FromResult(new MaterializationGenerationOperationResult(
                MaterializationTargetOperationDisposition.Applied,
                snapshot));
        }
    }

    /// <inheritdoc />
    public ValueTask<MaterializationCleanupResult> CleanupGenerationAsync(
        OperationContext context,
        MaterializationCleanupGenerationRequest request)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        context.CancellationToken.ThrowIfCancellationRequested();
        var requestFingerprint = MaterializationTargetIntentFingerprinter.Compute(request);

        lock (gate)
        {
            if (cleanupReceipts.TryGetValue(request.CleanupId, out var prior))
            {
                if (prior.RequestFingerprint == requestFingerprint)
                {
                    AcceptGenerationFence(request.GenerationId, request.WorkerFence);
                }
                else
                {
                    AcceptGenerationFenceIfKnown(request.GenerationId, request.WorkerFence);
                }

                return ValueTask.FromResult(new MaterializationCleanupResult(
                    prior.RequestFingerprint == requestFingerprint
                        ? MaterializationTargetOperationDisposition.Replayed
                        : MaterializationTargetOperationDisposition.IdentityConflict,
                    Snapshot(),
                    wasRemoved: false));
            }
            if (!generationIdentities.TryGetValue(request.GenerationId, out var identity))
            {
                return ValueTask.FromResult(new MaterializationCleanupResult(MaterializationTargetOperationDisposition.NotFound, Snapshot(), false));
            }

            if (generations.TryGetValue(request.GenerationId, out var retainedGeneration)
                && retainedGeneration.RetiredAtUtc is { } retainedRetirement
                && request.CleanedAtUtc < retainedRetirement)
            {
                throw new ArgumentException("A cleanup time cannot predate generation retirement.", nameof(request));
            }
            if (IsStale(request.WorkerFence, identity.LatestWorkerFence))
            {
                return ValueTask.FromResult(new MaterializationCleanupResult(MaterializationTargetOperationDisposition.StaleFence, Snapshot(), false));
            }

            AcceptGenerationFence(request.GenerationId, request.WorkerFence);
            if (!generations.TryGetValue(request.GenerationId, out var generation))
            {
                return ValueTask.FromResult(new MaterializationCleanupResult(MaterializationTargetOperationDisposition.NotFound, Snapshot(), false));
            }

            if (activeGenerationId == request.GenerationId || generation.State == MaterializationGenerationState.Active)
            {
                return ValueTask.FromResult(new MaterializationCleanupResult(MaterializationTargetOperationDisposition.ActiveGenerationConflict, Snapshot(), false));
            }

            if (generation.Revision != request.ExpectedRevision)
            {
                return ValueTask.FromResult(new MaterializationCleanupResult(MaterializationTargetOperationDisposition.RevisionConflict, Snapshot(), false));
            }

            if (generation.State != MaterializationGenerationState.Retired)
            {
                return ValueTask.FromResult(new MaterializationCleanupResult(MaterializationTargetOperationDisposition.StateConflict, Snapshot(), false));
            }

            generations.Remove(request.GenerationId);
            cleanupReceipts.Add(request.CleanupId, new(requestFingerprint));
            return ValueTask.FromResult(new MaterializationCleanupResult(
                MaterializationTargetOperationDisposition.Applied,
                Snapshot(),
                wasRemoved: true));
        }
    }

    MaterializationTargetSnapshot Snapshot()
        => new(
            Descriptor.Id,
            Descriptor.MaterializationId,
            targetRevision,
            activeGenerationId,
            latestPromotionFence,
            generations.Count);

    MaterializationGenerationSnapshot? TrySnapshot(MaterializationGenerationId generationId) =>
        generations.TryGetValue(generationId, out var generation) ? Snapshot(generation) : null;

    MaterializationGenerationSnapshot SnapshotWithLatestFence(MaterializationGenerationSnapshot generation) =>
        new(
            generation.MaterializationId,
            generation.GenerationId,
            generation.DefinitionFingerprint,
            generation.State,
            generation.Revision,
            generationIdentities[generation.GenerationId].LatestWorkerFence,
            generation.HasPermanentFailures,
            generation.PendingRetryableMutationCount,
            generation.VisibleItemCount,
            generation.TombstoneCount,
            generation.SealReceipt,
            generation.ValidationReceipt,
            generation.CreatedAtUtc,
            generation.InactivatedAtUtc,
            generation.RetiredAtUtc);

    MaterializationGenerationSnapshot Snapshot(StoredGeneration generation)
    {
        var visibleItemCount = 0L;
        var tombstoneCount = 0L;
        foreach (var item in generation.Items.Values)
        {
            if (item.Kind == MaterializationItemMutationKind.Delete)
            {
                tombstoneCount++;
            }
            else
            {
                visibleItemCount++;
            }
        }
        return new(
            generation.MaterializationId,
            generation.GenerationId,
            generation.DefinitionFingerprint,
            generation.State,
            generation.Revision,
            generationIdentities[generation.GenerationId].LatestWorkerFence,
            generation.HasPermanentFailures,
            generation.PendingRetryableMutations.Count,
            visibleItemCount,
            tombstoneCount,
            generation.SealReceipt,
            generation.ValidationReceipt,
            generation.CreatedAtUtc,
            generation.InactivatedAtUtc,
            generation.RetiredAtUtc);
    }

    static MaterializationBatchResult RejectedBatch(
        MaterializationApplyBatchRequest request,
        MaterializationBatchDisposition batchDisposition,
        MaterializationGenerationRevision? generationRevision,
        MaterializationItemOutcomeDisposition itemDisposition,
        string code,
        string message)
    {
        var outcomes = ImmutableArray.CreateBuilder<MaterializationItemOutcome>(request.Mutations.Length);
        foreach (var mutation in request.Mutations)
        {
            outcomes.Add(Failure(mutation, itemDisposition, code, message));
        }

        return MaterializationBatchResult.ForRequest(request, batchDisposition, generationRevision, outcomes.MoveToImmutable());
    }

    static MaterializationBatchResult Replay(
        MaterializationBatchResult prior,
        MaterializationApplyBatchRequest request)
    {
        var outcomes = ImmutableArray.CreateBuilder<MaterializationItemOutcome>(prior.Outcomes.Length);
        foreach (var outcome in prior.Outcomes)
        {
            outcomes.Add(outcome.Disposition == MaterializationItemOutcomeDisposition.Applied
                ? new(outcome.ItemId, outcome.MutationId, MaterializationItemOutcomeDisposition.Replayed)
                : outcome);
        }
        return MaterializationBatchResult.ForRequest(
            request,
            MaterializationBatchDisposition.Replayed,
            prior.GenerationRevision,
            outcomes.MoveToImmutable());
    }

    static bool IsStale(MaterializationWorkerFence requested, MaterializationWorkerFence latest) =>
        requested.Ordinal < latest.Ordinal;

    static bool PendingMutationsEqual(
        ImmutableDictionary<MaterializationItemMutationId, PendingRetryableMutation> left,
        ImmutableDictionary<MaterializationItemMutationId, PendingRetryableMutation> right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left.Count != right.Count)
        {
            return false;
        }

        foreach (var entry in left)
        {
            if (!right.TryGetValue(entry.Key, out var value) || value != entry.Value)
            {
                return false;
            }
        }

        return true;
    }

    void AcceptGenerationFence(
        MaterializationGenerationId generationId,
        MaterializationWorkerFence requested)
    {
        var identity = generationIdentities[generationId];
        if (requested.Ordinal > identity.LatestWorkerFence.Ordinal)
        {
            generationIdentities[generationId] = identity with { LatestWorkerFence = requested };
        }
    }

    void AcceptGenerationFenceIfKnown(
        MaterializationGenerationId generationId,
        MaterializationWorkerFence requested)
    {
        if (generationIdentities.ContainsKey(generationId))
        {
            AcceptGenerationFence(generationId, requested);
        }
    }

    void AcceptPromotionFence(MaterializationPromotionFence requested)
    {
        if (latestPromotionFence is null || requested.Ordinal > latestPromotionFence.Value.Ordinal)
        {
            latestPromotionFence = requested;
        }
    }

    static MaterializationItemOutcome Success(
        MaterializationItemMutation mutation,
        MaterializationItemOutcomeDisposition disposition) =>
        new(mutation.ItemId, mutation.MutationId, disposition);

    static MaterializationItemOutcome Failure(
        MaterializationItemMutation mutation,
        MaterializationItemOutcomeDisposition disposition,
        string code,
        string message) =>
        new(mutation.ItemId, mutation.MutationId, disposition, code, message);

    static MaterializationSealFingerprint ComputeSealFingerprint(
        ImmutableDictionary<MaterializationItemId, StoredItem> items)
    {
        var builder = ImmutableArray.CreateBuilder<MaterializationSealContentEntry>(items.Count);
        foreach (var item in items.Values)
        {
            builder.Add(new(
                item.ItemId,
                item.Version,
                item.MutationId,
                item.Kind,
                item.Value));
        }
        return MaterializationSealFingerprinter.Compute(builder.MoveToImmutable());
    }

    static bool SupportsWriteBounds(
        MaterializationCapabilityProfile profile,
        MaterializationTargetBatchIntent intent) =>
        MaterializationTargetBatchLimits.Supports(profile, intent);

    sealed record StoredItem(
        MaterializationItemId ItemId,
        MaterializationItemVersion Version,
        MaterializationItemMutationId MutationId,
        MaterializationItemMutationKind Kind,
        ObservationValue? Value);

    sealed record MutationReceipt(MaterializationTargetIntentFingerprint Fingerprint);

    sealed record PendingRetryableMutation(
        MaterializationItemId ItemId,
        MaterializationItemVersion Version,
        MaterializationTargetIntentFingerprint Fingerprint);

    sealed record StoredGeneration(
        MaterializationId MaterializationId,
        MaterializationGenerationId GenerationId,
        ExecutionDefinitionFingerprint DefinitionFingerprint,
        MaterializationGenerationState State,
        MaterializationGenerationRevision Revision,
        bool HasPermanentFailures,
        ImmutableDictionary<MaterializationItemMutationId, PendingRetryableMutation> PendingRetryableMutations,
        ImmutableDictionary<MaterializationItemId, StoredItem> Items,
        ImmutableDictionary<MaterializationItemMutationId, MutationReceipt> MutationReceipts,
        MaterializationSealReceipt? SealReceipt,
        MaterializationValidationReceipt? ValidationReceipt,
        DateTimeOffset CreatedAtUtc,
        DateTimeOffset? InactivatedAtUtc,
        DateTimeOffset? RetiredAtUtc);

    sealed record GenerationIdentity(
        MaterializationTargetIntentFingerprint BeginRequestFingerprint,
        MaterializationWorkerFence LatestWorkerFence);

    sealed record BatchReceipt(
        MaterializationTargetIntentFingerprint RequestFingerprint,
        MaterializationBatchResult Result);

    sealed record SealOperationReceipt(
        MaterializationTargetIntentFingerprint RequestFingerprint,
        MaterializationSealReceipt Receipt,
        MaterializationGenerationSnapshot Generation);

    sealed record ValidationOperationReceipt(
        MaterializationTargetIntentFingerprint RequestFingerprint,
        MaterializationValidationReceipt Receipt,
        MaterializationGenerationSnapshot Generation);

    sealed record PromotionOperationReceipt(
        MaterializationTargetIntentFingerprint RequestFingerprint,
        MaterializationPromotionReceipt Receipt);

    sealed record AbandonmentOperationReceipt(
        MaterializationTargetIntentFingerprint RequestFingerprint,
        MaterializationAbandonmentReceipt Receipt,
        MaterializationGenerationSnapshot? Generation);

    sealed record RetirementOperationReceipt(
        MaterializationTargetIntentFingerprint RequestFingerprint,
        MaterializationGenerationSnapshot Generation);

    sealed record CleanupOperationReceipt(MaterializationTargetIntentFingerprint RequestFingerprint);

}
