using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Storage.Materialization;

namespace Cohesive.Adapters.Elastic;

/// <summary>Durable generation-per-index Elasticsearch implementation of <see cref="IMaterializationTarget"/>.</summary>
/// <remarks>
/// Candidate generations remain concrete indexes outside the stable read alias. Deletes are retained as versioned
/// tombstone documents so retries do not depend on Elasticsearch's bounded delete-version retention. Generation
/// mutations are admitted under the external single-writer authority recorded by the binding; target publication is
/// independently fenced by an atomic Elasticsearch alias transaction that exchanges a revision/fence marker and the
/// read alias together. Public operations may be called concurrently; bounded local admission serializes each
/// generation and promotion authority. The supplied client and external coordination authority remain caller owned
/// and must outlive the target.
/// </remarks>
public sealed class ElasticMaterializationTarget : IMaterializationTarget
{
    const int StateFormatVersion = 1;
    const int ScanPageItems = 512;
    const string TargetDocumentKind = "target";
    const string GenerationDocumentKind = "generation";
    const string BatchReceiptDocumentKind = "batch-receipt";
    const string MutationReceiptDocumentKind = "mutation-receipt";
    const string PendingMutationDocumentKind = "pending-mutation";
    const string SealReceiptDocumentKind = "seal-receipt";
    const string ValidationReceiptDocumentKind = "validation-receipt";
    const string PromotionReceiptDocumentKind = "promotion-receipt";
    const string RetirementReceiptDocumentKind = "retirement-receipt";
    const string CleanupReceiptDocumentKind = "cleanup-receipt";
    const string OperationReservationDocumentKind = "operation-reservation";

    const string RetryableCode = "cohesive.adapters.elastic.materialization.retryableRejected";
    const string PermanentCode = "cohesive.adapters.elastic.materialization.permanentFailure";
    const string VersionConflictCode = "cohesive.adapters.elastic.materialization.versionConflict";
    const string IdempotencyConflictCode = "cohesive.adapters.elastic.materialization.idempotencyConflict";
    const string GenerationMissingCode = "cohesive.adapters.elastic.materialization.generationNotFound";
    const string GenerationNotWritableCode = "cohesive.adapters.elastic.materialization.generationNotWritable";
    const string StaleFenceCode = "cohesive.adapters.elastic.materialization.staleFence";
    const string BatchLimitCode = "cohesive.adapters.elastic.materialization.batchLimitExceeded";
    const string IdentityLimitCode = "cohesive.adapters.elastic.materialization.indexedIdentityLimitExceeded";
    const string ConcurrentBatchCode = "cohesive.adapters.elastic.materialization.concurrentBatch";
    const string ResponseLimitErrorType = "cohesive.elasticsearch.response.limitExceeded";

    static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    static readonly ElasticJsonObject MatchAllQuery = ElasticMaterializationWireJson.MatchAllQuery;
    static readonly ElasticJsonObject VisibleCountQuery = ElasticMaterializationWireJson.BooleanTermQuery(
        $"{ElasticMaterializationTargetBinding.MetadataField}.deleted",
        value: false);
    static readonly ElasticJsonObject TombstoneCountQuery = ElasticMaterializationWireJson.BooleanTermQuery(
        $"{ElasticMaterializationTargetBinding.MetadataField}.deleted",
        value: true);
    static readonly ElasticJsonObject RetainedGenerationCountQuery = ElasticMaterializationWireJson.FilteredQuery(
        ElasticMaterializationWireJson.StringTermQuery("documentKind", GenerationDocumentKind),
        ElasticMaterializationWireJson.BooleanTermQuery("retained", value: true));
    static readonly ElasticJsonObject PendingMutationCountQuery = ElasticMaterializationWireJson.StringTermQuery(
        "documentKind",
        PendingMutationDocumentKind);

    readonly ElasticMaterializationTargetBinding binding;
    readonly ElasticMaterializationTargetPolicy policy;
    readonly IElasticMaterializationTransport transport;
    readonly SemaphoreSlim localAdmission;
    readonly SemaphoreSlim controlInitialization = new(initialCount: 1, maxCount: 1);
    readonly SemaphoreSlim promotionAdmission = new(initialCount: 1, maxCount: 1);
    readonly SemaphoreSlim targetReconciliation = new(initialCount: 1, maxCount: 1);
    readonly SemaphoreSlim[] generationAdmissions;
    int controlIndexReady;

    /// <summary>Creates an Elasticsearch materialization target over one exact persisted binding.</summary>
    /// <param name="binding">Persisted physical generation, alias, template, and coordination evidence.</param>
    /// <param name="policy">Bounded operation policy advertised by the target.</param>
    /// <param name="runtimeBinding">Exact borrowed Elasticsearch client runtime attestation.</param>
    /// <exception cref="ArgumentNullException">A parameter is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The persisted and runtime bindings address different clusters.</exception>
    public ElasticMaterializationTarget(
        ElasticMaterializationTargetBinding binding,
        ElasticMaterializationTargetPolicy policy,
        ElasticElasticsearchRuntimeBinding runtimeBinding)
        : this(
            binding,
            policy,
            runtimeBinding,
            new ElasticsearchMaterializationTransport(
                runtimeBinding ?? throw new ArgumentNullException(nameof(runtimeBinding))))
    {
    }

    internal ElasticMaterializationTarget(
        ElasticMaterializationTargetBinding binding,
        ElasticMaterializationTargetPolicy policy,
        ElasticElasticsearchRuntimeBinding runtimeBinding,
        IElasticMaterializationTransport transport)
    {
        this.binding = binding ?? throw new ArgumentNullException(nameof(binding));
        this.policy = policy ?? throw new ArgumentNullException(nameof(policy));
        ArgumentNullException.ThrowIfNull(runtimeBinding);
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
        Descriptor = ElasticMaterializationTargetProfile.CreateDescriptor(binding, policy, runtimeBinding);
        localAdmission = new SemaphoreSlim(policy.MaximumParallelism, policy.MaximumParallelism);
        var generationAdmissionCount = (int)Math.Min(256L, (long)policy.MaximumParallelism * 4L);
        generationAdmissions = new SemaphoreSlim[generationAdmissionCount];
        for (var index = 0; index < generationAdmissions.Length; index++)
        {
            generationAdmissions[index] = new SemaphoreSlim(initialCount: 1, maxCount: 1);
        }
    }

    /// <inheritdoc />
    public MaterializationTargetDescriptor Descriptor { get; }

    /// <summary>Gets the persisted Elasticsearch target binding, including the stable Relations read alias.</summary>
    public ElasticMaterializationTargetBinding Binding => binding;

    /// <inheritdoc />
    /// <exception cref="ElasticMaterializationTransportException">Elasticsearch I/O fails or returns an invalid bounded response.</exception>
    public async ValueTask<MaterializationTargetSnapshot> InspectAsync(OperationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return await ExecuteAsync(
            context,
            operation: "inspect",
            generationId: null,
            async cancellationToken =>
            {
                var target = await LoadTargetAsync(cancellationToken).ConfigureAwait(false);
                return await SnapshotAsync(target.Value, cancellationToken).ConfigureAwait(false);
            }).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <exception cref="ElasticMaterializationTransportException">Elasticsearch I/O fails or returns an invalid bounded response.</exception>
    public async ValueTask<MaterializationGenerationSnapshot?> InspectGenerationAsync(
        OperationContext context,
        MaterializationGenerationId generationId)
    {
        ArgumentNullException.ThrowIfNull(context);
        RequireDefined(generationId.Value, nameof(generationId));
        return await ExecuteAsync(
            context,
            operation: "inspect-generation",
            generationId,
            async cancellationToken =>
            {
                _ = await LoadTargetAsync(cancellationToken).ConfigureAwait(false);
                var generation = await ReadGenerationAsync(generationId, cancellationToken).ConfigureAwait(false);
                return generation is null || !generation.Value.Retained
                    ? null
                    : await SnapshotAsync(generation.Value, cancellationToken).ConfigureAwait(false);
            }).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <exception cref="ElasticMaterializationTransportException">Elasticsearch I/O fails or returns an invalid bounded response.</exception>
    public async ValueTask<MaterializationGenerationOperationResult> BeginGenerationAsync(
        OperationContext context,
        MaterializationBeginGenerationRequest request)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        return await ExecuteAsync(
            context,
            operation: "begin-generation",
            request.GenerationId,
            cancellationToken => BeginGenerationCoreAsync(request, cancellationToken)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <exception cref="ElasticMaterializationTransportException">Elasticsearch I/O fails or returns an invalid bounded response.</exception>
    public async ValueTask<MaterializationBatchResult> ApplyBatchAsync(
        OperationContext context,
        MaterializationApplyBatchRequest request)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        return await ExecuteAsync(
            context,
            operation: "apply-batch",
            request.GenerationId,
            cancellationToken => ApplyBatchCoreAsync(request, cancellationToken)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <exception cref="ElasticMaterializationTransportException">Elasticsearch I/O fails or returns an invalid bounded response.</exception>
    public async ValueTask<MaterializationSealResult> SealGenerationAsync(
        OperationContext context,
        MaterializationSealGenerationRequest request)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        return await ExecuteAsync(
            context,
            operation: "seal-generation",
            request.GenerationId,
            cancellationToken => SealGenerationCoreAsync(request, cancellationToken)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <exception cref="ElasticMaterializationTransportException">Elasticsearch I/O fails or returns an invalid bounded response.</exception>
    public async ValueTask<MaterializationValidationResult> ValidateGenerationAsync(
        OperationContext context,
        MaterializationValidateGenerationRequest request)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        return await ExecuteAsync(
            context,
            operation: "validate-generation",
            request.GenerationId,
            cancellationToken => ValidateGenerationCoreAsync(request, cancellationToken)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <exception cref="ElasticMaterializationTransportException">Elasticsearch I/O fails or returns an invalid bounded response.</exception>
    public async ValueTask<MaterializationPromotionResult> PromoteGenerationAsync(
        OperationContext context,
        MaterializationPromoteGenerationRequest request)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        return await ExecuteAsync(
            context,
            operation: "promote-generation",
            request.GenerationId,
            cancellationToken => PromoteGenerationCoreAsync(request, cancellationToken),
            request.ExpectedActiveGenerationId).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <exception cref="ElasticMaterializationTransportException">Elasticsearch I/O fails or returns an invalid bounded response.</exception>
    public async ValueTask<MaterializationGenerationOperationResult> RetireGenerationAsync(
        OperationContext context,
        MaterializationRetireGenerationRequest request)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        return await ExecuteAsync(
            context,
            operation: "retire-generation",
            request.GenerationId,
            cancellationToken => RetireGenerationCoreAsync(request, cancellationToken)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <exception cref="ElasticMaterializationTransportException">Elasticsearch I/O fails or returns an invalid bounded response.</exception>
    public async ValueTask<MaterializationCleanupResult> CleanupGenerationAsync(
        OperationContext context,
        MaterializationCleanupGenerationRequest request)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        return await ExecuteAsync(
            context,
            operation: "cleanup-generation",
            request.GenerationId,
            cancellationToken => CleanupGenerationCoreAsync(request, cancellationToken)).ConfigureAwait(false);
    }

    async ValueTask<MaterializationGenerationOperationResult> BeginGenerationCoreAsync(
        MaterializationBeginGenerationRequest request,
        CancellationToken cancellationToken)
    {
        RequireIndexedIdentity(request.GenerationId.Value, nameof(request));
        var target = await LoadTargetAsync(cancellationToken).ConfigureAwait(false);
        if (request.MaterializationId != Descriptor.MaterializationId)
        {
            var conflicting = await ReadGenerationAsync(request.GenerationId, cancellationToken).ConfigureAwait(false);
            return new(
                MaterializationTargetOperationDisposition.MaterializationConflict,
                conflicting is { Value.Retained: true }
                    ? await SnapshotAsync(conflicting.Value, cancellationToken).ConfigureAwait(false)
                    : null);
        }

        var fingerprint = MaterializationTargetIntentFingerprinter.Compute(request);
        var existing = await ReadGenerationAsync(request.GenerationId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            var accepted = await AcceptGenerationFenceAsync(
                existing,
                request.WorkerFence,
                cancellationToken).ConfigureAwait(false);
            if (accepted.Value.BeginFingerprint == fingerprint)
            {
                if (!accepted.Value.Retained)
                {
                    return new(MaterializationTargetOperationDisposition.AlreadyExists, generation: null);
                }
                accepted = await ProvisionGenerationAsync(accepted, cancellationToken).ConfigureAwait(false);
                return new(
                    MaterializationTargetOperationDisposition.Replayed,
                    await SnapshotAsync(accepted.Value, cancellationToken).ConfigureAwait(false));
            }

            var stale = request.WorkerFence.Ordinal < existing.Value.LatestWorkerFence.Ordinal;
            return new(
                stale
                    ? MaterializationTargetOperationDisposition.StaleFence
                    : MaterializationTargetOperationDisposition.IdentityConflict,
                accepted.Value.Retained
                    ? await SnapshotAsync(accepted.Value, cancellationToken).ConfigureAwait(false)
                    : null);
        }

        var indexName = binding.GetGenerationIndexName(request.GenerationId);
        GenerationState state = new(
            StateFormatVersion,
            GenerationDocumentKind,
            binding.Fingerprint.Value,
            request.MaterializationId,
            request.GenerationId,
            request.DefinitionFingerprint,
            fingerprint,
            Retained: true,
            IsProvisioned: false,
            indexName,
            MaterializationGenerationState.Loading,
            MaterializationGenerationRevision.Initial,
            request.WorkerFence,
            HasPermanentFailures: false,
            SealReceipt: null,
            ValidationReceipt: null,
            request.CreatedAtUtc,
            InactivatedAtUtc: null,
            RetiredAtUtc: null,
            PendingBatch: null,
            PendingSeal: null,
            PendingValidation: null,
            LastRetirement: null,
            LastCleanup: null);
        var created = await CreateControlAsync(GenerationDocumentId(request.GenerationId), state, cancellationToken)
            .ConfigureAwait(false);
        if (created is null)
        {
            existing = await ReadGenerationAsync(request.GenerationId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("A concurrently created Elasticsearch generation state is unavailable.");
            var accepted = await AcceptGenerationFenceAsync(existing, request.WorkerFence, cancellationToken)
                .ConfigureAwait(false);
            if (accepted.Value.BeginFingerprint == fingerprint && accepted.Value.Retained)
            {
                accepted = await ProvisionGenerationAsync(accepted, cancellationToken).ConfigureAwait(false);
            }
            return new(
                accepted.Value.BeginFingerprint == fingerprint && accepted.Value.Retained
                    ? MaterializationTargetOperationDisposition.Replayed
                    : MaterializationTargetOperationDisposition.IdentityConflict,
                accepted.Value.Retained
                    ? await SnapshotAsync(accepted.Value, cancellationToken).ConfigureAwait(false)
                    : null);
        }

        _ = target;
        created = await ProvisionGenerationAsync(created, cancellationToken).ConfigureAwait(false);
        return new(
            MaterializationTargetOperationDisposition.Applied,
            await SnapshotAsync(created.Value, cancellationToken).ConfigureAwait(false));
    }

    async ValueTask<Stored<GenerationState>> ProvisionGenerationAsync(
        Stored<GenerationState> generation,
        CancellationToken cancellationToken)
    {
        if (generation.Value.IsProvisioned)
        {
            return generation;
        }

        var ownerAlias = GenerationOwnerAlias(generation.Value.GenerationId, generation.Value.BeginFingerprint);
        var createdIndex = await transport.CreateIndexAsync(
            generation.Value.IndexName,
            CreateGenerationIndexBody(generation.Value.GenerationId, ownerAlias),
            policy.MaximumDiagnosticBytes,
            cancellationToken).ConfigureAwait(false);
        if (createdIndex.Disposition == ElasticIndexCreateDisposition.Created)
        {
            if (!createdIndex.Acknowledged || !createdIndex.ShardsAcknowledged)
            {
                throw new InvalidOperationException(
                    "Elasticsearch did not acknowledge a usable generation index.");
            }
        }
        else
        {
            if (!await HasExactGenerationOwnershipAsync(
                    generation.Value,
                    cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException(
                    "An existing Elasticsearch generation index does not carry exact Cohesive ownership evidence.");
            }
        }

        return await ReplaceControlAsync(
            generation,
            generation.Value with { IsProvisioned = true },
            cancellationToken).ConfigureAwait(false);
    }

    async ValueTask<MaterializationBatchResult> ApplyBatchCoreAsync(
        MaterializationApplyBatchRequest request,
        CancellationToken cancellationToken)
    {
        _ = await LoadTargetAsync(cancellationToken).ConfigureAwait(false);
        var intent = MaterializationTargetIntentFingerprinter.AnalyzeBatch(request);
        var maximumBatchResponseBytes = MaximumBatchResponseBytes();
        RecordBatchInput(intent);
        var receiptId = OperationDocumentId(BatchReceiptDocumentKind, request.BatchId.Value);
        var priorReceipt = await ReadControlAsync<BatchReceiptState>(receiptId, cancellationToken)
            .ConfigureAwait(false);
        if (priorReceipt is not null)
        {
            var existingGeneration = await ReadGenerationAsync(request.GenerationId, cancellationToken)
                .ConfigureAwait(false);
            if (existingGeneration is not null)
            {
                existingGeneration = await AcceptGenerationFenceAsync(
                        existingGeneration,
                        request.WorkerFence,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (existingGeneration.Value.PendingBatch is
                    {
                        BatchId: var pendingBatchId,
                        RequestFingerprint: var pendingFingerprint,
                        Completion: { } pendingCompletion
                    }
                    && pendingBatchId == request.BatchId
                    && pendingFingerprint == priorReceipt.Value.RequestFingerprint
                    && pendingCompletion == priorReceipt.Value.Result)
                {
                    existingGeneration = await ReplaceControlAsync(
                        existingGeneration,
                        existingGeneration.Value with { PendingBatch = null },
                        cancellationToken).ConfigureAwait(false);
                }
            }

            var replay = priorReceipt.Value.RequestFingerprint == intent.Fingerprint
                ? Replay(priorReceipt.Value.Result, request)
                : RejectedBatch(
                    request,
                    MaterializationBatchDisposition.IdentityConflict,
                    existingGeneration?.Value.Revision,
                    MaterializationItemOutcomeDisposition.IdempotencyConflict,
                    IdempotencyConflictCode,
                    "The batch identity was reused for different canonical content.");
            RecordBatchOutcomes(replay);
            return replay;
        }

        if (!await ReserveOperationAsync(receiptId, intent.Fingerprint, cancellationToken).ConfigureAwait(false))
        {
            var conflictingGeneration = await ReadGenerationAsync(request.GenerationId, cancellationToken)
                .ConfigureAwait(false);
            if (conflictingGeneration is not null)
            {
                conflictingGeneration = await AcceptGenerationFenceAsync(
                    conflictingGeneration,
                    request.WorkerFence,
                    cancellationToken).ConfigureAwait(false);
            }
            var conflict = RejectedBatch(
                request,
                MaterializationBatchDisposition.IdentityConflict,
                conflictingGeneration?.Value.Revision,
                MaterializationItemOutcomeDisposition.IdempotencyConflict,
                IdempotencyConflictCode,
                "The batch identity is durably reserved for different canonical content.");
            RecordBatchOutcomes(conflict);
            return conflict;
        }

        var generation = await ReadGenerationAsync(request.GenerationId, cancellationToken).ConfigureAwait(false);
        if (generation is null || !generation.Value.Retained)
        {
            var missing = RejectedBatch(
                request,
                MaterializationBatchDisposition.GenerationNotFound,
                generationRevision: null,
                MaterializationItemOutcomeDisposition.PermanentFailure,
                GenerationMissingCode,
                "The addressed Elasticsearch generation does not exist.");
            RecordBatchOutcomes(missing);
            return missing;
        }

        if (request.WorkerFence.Ordinal < generation.Value.LatestWorkerFence.Ordinal)
        {
            var stale = RejectedBatch(
                request,
                MaterializationBatchDisposition.StaleFence,
                generation.Value.Revision,
                MaterializationItemOutcomeDisposition.RetryableRejected,
                StaleFenceCode,
                "A newer worker fence superseded this generation mutation.");
            RecordBatchOutcomes(stale);
            return stale;
        }
        generation = await AcceptGenerationFenceAsync(generation, request.WorkerFence, cancellationToken)
            .ConfigureAwait(false);

        if (generation.Value.State is not (MaterializationGenerationState.Loading or MaterializationGenerationState.Active)
            || !generation.Value.IsProvisioned
            || generation.Value.PendingSeal is not null)
        {
            var notWritable = RejectedBatch(
                request,
                MaterializationBatchDisposition.GenerationNotWritable,
                generation.Value.Revision,
                MaterializationItemOutcomeDisposition.PermanentFailure,
                GenerationNotWritableCode,
                "Only a loading candidate or active Elasticsearch generation accepts writes.");
            RecordBatchOutcomes(notWritable);
            return notWritable;
        }
        await RequireExactGenerationOwnershipAsync(generation.Value, cancellationToken).ConfigureAwait(false);

        if (generation.Value.PendingBatch is { } pending
            && (pending.BatchId != request.BatchId || pending.RequestFingerprint != intent.Fingerprint))
        {
            var concurrent = RejectedBatch(
                request,
                MaterializationBatchDisposition.Applied,
                generation.Value.Revision,
                MaterializationItemOutcomeDisposition.RetryableRejected,
                ConcurrentBatchCode,
                $"Generation recovery requires exact retry of pending batch '{pending.BatchId.Value}'.");
            RecordBatchOutcomes(concurrent);
            return concurrent;
        }

        if (generation.Value.PendingBatch is { Completion: { } completedBatch })
        {
            await EnsureBatchReceiptAsync(receiptId, intent.Fingerprint, completedBatch, cancellationToken)
                .ConfigureAwait(false);
            generation = await ReplaceControlAsync(
                generation,
                generation.Value with { PendingBatch = null },
                cancellationToken).ConfigureAwait(false);
            var completedReplay = Replay(completedBatch, request);
            RecordBatchOutcomes(completedReplay);
            return completedReplay;
        }

        if (generation.Value.PendingBatch is null
            && !MaterializationTargetBatchLimits.Supports(Descriptor.Capabilities, intent))
        {
            var limited = RejectedBatch(
                request,
                MaterializationBatchDisposition.LimitExceeded,
                generation.Value.Revision,
                MaterializationItemOutcomeDisposition.RetryableRejected,
                BatchLimitCode,
                $"No single target realization accepts {intent.ItemCount} items and {intent.CanonicalByteCount} canonical bytes.");
            RecordBatchOutcomes(limited);
            return limited;
        }

        if (generation.Value.PendingBatch is null)
        {
            generation = await ReplaceControlAsync(
                generation,
                generation.Value with
                {
                    PendingBatch = new(
                        request.BatchId,
                        intent.Fingerprint,
                        request.WorkerFence,
                        generation.Value.Revision,
                        IsInitialized: false,
                        PreexistingMutationIds: [],
                        PreexistingPendingMutationIds: [],
                        Completion: null)
                },
                cancellationToken).ConfigureAwait(false);
        }

        var works = await ReadBatchWorkAsync(
            generation.Value,
            request,
            maximumBatchResponseBytes,
            cancellationToken).ConfigureAwait(false);
        var pendingBatch = generation.Value.PendingBatch
            ?? throw new InvalidOperationException("A durable pending batch disappeared before mutation admission.");
        if (pendingBatch.StartedRevision != generation.Value.Revision)
        {
            throw new InvalidOperationException(
                "A pending Elasticsearch batch crossed an unexpected generation-revision boundary.");
        }
        if (!pendingBatch.IsInitialized)
        {
            var preexistingMutationIds = ImmutableArray.CreateBuilder<MaterializationItemMutationId>(works.Length);
            var preexistingPendingMutationIds = ImmutableArray.CreateBuilder<MaterializationItemMutationId>(works.Length);
            foreach (var work in works)
            {
                if (work.ReceiptAlreadyApplied)
                {
                    preexistingMutationIds.Add(work.Mutation.MutationId);
                }
                if (work.PendingAlreadyApplied)
                {
                    preexistingPendingMutationIds.Add(work.Mutation.MutationId);
                }
            }

            pendingBatch = pendingBatch with
            {
                IsInitialized = true,
                PreexistingMutationIds = preexistingMutationIds.ToImmutable(),
                PreexistingPendingMutationIds = preexistingPendingMutationIds.ToImmutable()
            };
            generation = await ReplaceControlAsync(
                generation,
                generation.Value with { PendingBatch = pendingBatch },
                cancellationToken).ConfigureAwait(false);
        }
        ApplyPendingBatchBaseline(works, pendingBatch);

        foreach (var work in works)
        {
            if (work.InitialDisposition is null
                && work.Mutation.ItemId.Value.Length
                    > ElasticMaterializationTargetBinding.MaximumIndexedIdentityCharacters)
            {
                work.InitialDisposition = MaterializationItemOutcomeDisposition.PermanentFailure;
                work.Code = IdentityLimitCode;
                work.Message =
                    $"The item identity exceeds the adapter's {ElasticMaterializationTargetBinding.MaximumIndexedIdentityCharacters}-character indexed-key bound.";
            }
        }

        var dataOperations = ImmutableArray.CreateBuilder<ElasticBulkOperation>(request.Mutations.Length);
        foreach (var work in works)
        {
            if (work.InitialDisposition is null && !work.DataAlreadyApplied)
            {
                dataOperations.Add(new(
                    ElasticBulkOperationKind.Index,
                    generation.Value.IndexName,
                    DataDocumentId(work.Mutation.ItemId),
                    work.Mutation.Version.Ordinal,
                    SerializeItemDocument(generation.Value.GenerationId, work.Mutation, work.Fingerprint)));
                work.DataOperationOrdinal = dataOperations.Count - 1;
            }
        }

        ElasticBulkResult? dataBulk = null;
        ImmutableArray<ElasticBulkOperation> submittedDataOperations = [];
        if (dataOperations.Count > 0)
        {
            submittedDataOperations = dataOperations.ToImmutable();
            dataBulk = await transport.BulkAsync(
                submittedDataOperations,
                MaximumWireBytes(intent),
                maximumBatchResponseBytes,
                cancellationToken).ConfigureAwait(false);
            RequireCompleteBulk(dataBulk, submittedDataOperations, request.GenerationId);
        }

        var currentAfter = await transport.MultiGetAsync(
            generation.Value.IndexName,
            [.. request.Mutations.Select(static mutation => DataDocumentId(mutation.ItemId))],
            ElasticMultiGetSourceProjection.MaterializationMetadata,
            maximumBatchResponseBytes,
            cancellationToken).ConfigureAwait(false);
        RequireCompleteMultiGet(currentAfter, request.Mutations.Length, "generation items");

        for (var index = 0; index < works.Length; index++)
        {
            var work = works[index];
            if (work.InitialDisposition is not null || work.FinalDisposition is not null)
            {
                continue;
            }

            if (work.DataOperationOrdinal is { } ordinal)
            {
                var item = dataBulk!.Items[ordinal];
                if (ElasticMaterializationRetryPolicy.IsRetryableStatus(item.StatusCode))
                {
                    work.InitialDisposition = MaterializationItemOutcomeDisposition.RetryableRejected;
                    work.Code = RetryableCode;
                    work.Message = ProviderMessage(item, "Elasticsearch transiently rejected the item write.");
                    continue;
                }
                if (item.StatusCode is >= 400 and not 409)
                {
                    work.InitialDisposition = MaterializationItemOutcomeDisposition.PermanentFailure;
                    work.Code = PermanentCode;
                    work.Message = ProviderMessage(item, "Elasticsearch permanently rejected the item write.");
                    continue;
                }

                work.DataAppliedThisAttempt = item.StatusCode is >= 200 and < 300;
            }

            if (!TryReadItem(currentAfter.Documents[index], out var current)
                || current.Metadata.Version < work.Mutation.Version.Ordinal)
            {
                work.InitialDisposition = MaterializationItemOutcomeDisposition.RetryableRejected;
                work.Code = RetryableCode;
                work.Message = "Elasticsearch did not expose the attempted item version after bulk evaluation.";
                continue;
            }
            if (current.Metadata.GenerationId != request.GenerationId.Value
                || current.Metadata.ItemId != work.Mutation.ItemId.Value)
            {
                throw new InvalidOperationException(
                    "An Elasticsearch generation item contradicts its durable document identity.");
            }
            if (current.Metadata.Version == work.Mutation.Version.Ordinal
                && current.Metadata.MutationId == work.Mutation.MutationId.Value
                && current.Metadata.MutationFingerprint == work.Fingerprint.Value)
            {
                work.DataAlreadyApplied = true;
                continue;
            }

            work.InitialDisposition = current.Metadata.MutationId == work.Mutation.MutationId.Value
                ? MaterializationItemOutcomeDisposition.IdempotencyConflict
                : MaterializationItemOutcomeDisposition.VersionConflict;
            work.Code = work.InitialDisposition == MaterializationItemOutcomeDisposition.IdempotencyConflict
                ? IdempotencyConflictCode
                : VersionConflictCode;
            work.Message = work.InitialDisposition == MaterializationItemOutcomeDisposition.IdempotencyConflict
                ? "The mutation identity was reused for different Elasticsearch item content."
                : $"Item version {work.Mutation.Version.Value} does not advance retained version {current.Metadata.Version.ToString(CultureInfo.InvariantCulture)}.";
            work.VersionConflictResolvesPending = work.InitialDisposition == MaterializationItemOutcomeDisposition.VersionConflict
                && work.Mutation.Version.Ordinal < current.Metadata.Version;
        }

        var receiptOperations = ImmutableArray.CreateBuilder<ElasticBulkOperation>(works.Length);
        foreach (var work in works)
        {
            if (work.InitialDisposition is null && !work.ReceiptAlreadyApplied && work.DataAlreadyApplied)
            {
                MutationReceiptState receipt = new(
                    StateFormatVersion,
                    MutationReceiptDocumentKind,
                    request.GenerationId,
                    work.Mutation.MutationId,
                    work.Fingerprint);
                receiptOperations.Add(new(
                    ElasticBulkOperationKind.Index,
                    binding.ControlIndexName,
                    MutationDocumentId(request.GenerationId, work.Mutation.MutationId),
                    externalVersion: 1,
                    SerializeControlDocument(receipt)));
                work.ReceiptOperationOrdinal = receiptOperations.Count - 1;
            }
        }

        ElasticBulkResult? receiptBulk = null;
        ImmutableArray<ElasticBulkOperation> submittedReceiptOperations = [];
        if (receiptOperations.Count > 0)
        {
            submittedReceiptOperations = receiptOperations.ToImmutable();
            receiptBulk = await transport.BulkAsync(
                submittedReceiptOperations,
                MaximumWireBytes(intent),
                maximumBatchResponseBytes,
                cancellationToken).ConfigureAwait(false);
            RequireCompleteBulk(receiptBulk, submittedReceiptOperations, request.GenerationId);
        }

        var receiptsAfter = await transport.MultiGetAsync(
            binding.ControlIndexName,
            [.. request.Mutations.Select(mutation => MutationDocumentId(request.GenerationId, mutation.MutationId))],
            ElasticMultiGetSourceProjection.Full,
            maximumBatchResponseBytes,
            cancellationToken).ConfigureAwait(false);
        RequireCompleteMultiGet(receiptsAfter, request.Mutations.Length, "mutation receipts");

        for (var index = 0; index < works.Length; index++)
        {
            var work = works[index];
            if (work.InitialDisposition is not null)
            {
                continue;
            }

            if (TryReadMutationReceipt(receiptsAfter.Documents[index], out var retainedReceipt))
            {
                if (retainedReceipt.GenerationId != request.GenerationId
                    || retainedReceipt.MutationId != work.Mutation.MutationId)
                {
                    throw new InvalidOperationException(
                        "An Elasticsearch mutation receipt contradicts its durable document identity.");
                }
                if (retainedReceipt.MutationFingerprint != work.Fingerprint)
                {
                    work.InitialDisposition = MaterializationItemOutcomeDisposition.IdempotencyConflict;
                    work.Code = IdempotencyConflictCode;
                    work.Message = "The mutation identity was reused for different canonical content.";
                    continue;
                }

                work.FinalDisposition = work.ReceiptPresentAtBatchStart
                    ? MaterializationItemOutcomeDisposition.Replayed
                    : MaterializationItemOutcomeDisposition.Applied;
                continue;
            }

            if (work.ReceiptOperationOrdinal is { } receiptOrdinal)
            {
                var receiptItem = receiptBulk!.Items[receiptOrdinal];
                work.InitialDisposition = ElasticMaterializationRetryPolicy.IsRetryableStatus(receiptItem.StatusCode)
                    || receiptItem.StatusCode == 409
                    ? MaterializationItemOutcomeDisposition.RetryableRejected
                    : MaterializationItemOutcomeDisposition.PermanentFailure;
                work.Code = work.InitialDisposition == MaterializationItemOutcomeDisposition.RetryableRejected
                    ? RetryableCode
                    : PermanentCode;
                work.Message = ProviderMessage(
                    receiptItem,
                    "Elasticsearch did not durably retain the mutation receipt.");
                continue;
            }

            work.InitialDisposition = MaterializationItemOutcomeDisposition.RetryableRejected;
            work.Code = RetryableCode;
            work.Message = "Elasticsearch did not retain the mutation receipt required for replay classification.";
        }

        var pendingOperations = ImmutableArray.CreateBuilder<ElasticBulkOperation>(works.Length);
        var stateChanged = false;
        var hasPermanentFailures = generation.Value.HasPermanentFailures;
        foreach (var work in works)
        {
            var disposition = work.FinalDisposition ?? work.InitialDisposition
                ?? throw new InvalidOperationException("A batch item has no terminal semantic outcome.");
            if (disposition == MaterializationItemOutcomeDisposition.RetryableRejected)
            {
                if (!work.PendingAlreadyApplied)
                {
                    PendingMutationState pendingMutation = new(
                        StateFormatVersion,
                        PendingMutationDocumentKind,
                        request.GenerationId,
                        work.Mutation.ItemId,
                        work.Mutation.MutationId,
                        work.Mutation.Version,
                        work.Fingerprint);
                    pendingOperations.Add(new(
                        ElasticBulkOperationKind.Index,
                        binding.ControlIndexName,
                        PendingMutationDocumentId(request.GenerationId, work.Mutation.MutationId),
                        externalVersion: 1,
                        SerializeControlDocument(pendingMutation)));
                }
                stateChanged |= !work.PendingPresentAtBatchStart;
                continue;
            }

            if (work.PendingAlreadyApplied && ShouldResolvePending(work, disposition))
            {
                pendingOperations.Add(new(
                    ElasticBulkOperationKind.Delete,
                    binding.ControlIndexName,
                    PendingMutationDocumentId(request.GenerationId, work.Mutation.MutationId),
                    externalVersion: 2));
            }
            stateChanged |= work.PendingPresentAtBatchStart && ShouldResolvePending(work, disposition);

            stateChanged |= disposition == MaterializationItemOutcomeDisposition.Applied;
            if (disposition is MaterializationItemOutcomeDisposition.PermanentFailure
                or MaterializationItemOutcomeDisposition.IdempotencyConflict
                || disposition == MaterializationItemOutcomeDisposition.VersionConflict && work.PendingAlreadyApplied)
            {
                hasPermanentFailures = true;
            }
        }

        if (pendingOperations.Count > 0)
        {
            var submittedPendingOperations = pendingOperations.ToImmutable();
            var pendingBulk = await transport.BulkAsync(
                submittedPendingOperations,
                MaximumWireBytes(intent),
                maximumBatchResponseBytes,
                cancellationToken).ConfigureAwait(false);
            RequireCompleteBulk(pendingBulk, submittedPendingOperations, request.GenerationId);
        }

        var pendingAfter = await transport.MultiGetAsync(
            binding.ControlIndexName,
            [.. request.Mutations.Select(mutation => PendingMutationDocumentId(request.GenerationId, mutation.MutationId))],
            ElasticMultiGetSourceProjection.Full,
            maximumBatchResponseBytes,
            cancellationToken).ConfigureAwait(false);
        RequireCompleteMultiGet(pendingAfter, request.Mutations.Length, "pending mutation postconditions");
        for (var index = 0; index < works.Length; index++)
        {
            var work = works[index];
            var disposition = work.FinalDisposition ?? work.InitialDisposition
                ?? throw new InvalidOperationException("A batch item has no terminal semantic outcome.");
            if (disposition == MaterializationItemOutcomeDisposition.RetryableRejected)
            {
                if (!TryReadPendingMutation(pendingAfter.Documents[index], out var retainedPending)
                    || retainedPending.GenerationId != request.GenerationId
                    || retainedPending.ItemId != work.Mutation.ItemId
                    || retainedPending.MutationId != work.Mutation.MutationId
                    || retainedPending.Version != work.Mutation.Version
                    || retainedPending.MutationFingerprint != work.Fingerprint)
                {
                    throw new InvalidOperationException(
                        "Elasticsearch did not retain the exact pending-mutation evidence required for retry.");
                }
            }
            else if (ShouldResolvePending(work, disposition) && pendingAfter.Documents[index].Found)
            {
                throw new InvalidOperationException(
                    "Elasticsearch retained pending-mutation evidence after its terminal resolution.");
            }
        }

        if (hasPermanentFailures != generation.Value.HasPermanentFailures)
        {
            stateChanged = true;
        }
        var revision = stateChanged
            ? Next(generation.Value.Revision)
            : generation.Value.Revision;
        var outcomes = ImmutableArray.CreateBuilder<MaterializationItemOutcome>(works.Length);
        foreach (var work in works)
        {
            var disposition = work.FinalDisposition ?? work.InitialDisposition
                ?? throw new InvalidOperationException("A batch item has no terminal semantic outcome.");
            outcomes.Add(disposition is MaterializationItemOutcomeDisposition.Applied
                    or MaterializationItemOutcomeDisposition.Replayed
                ? new(work.Mutation.ItemId, work.Mutation.MutationId, disposition)
                : new(work.Mutation.ItemId, work.Mutation.MutationId, disposition, work.Code!, work.Message!));
        }
        var result = MaterializationBatchResult.ForRequest(
            request,
            MaterializationBatchDisposition.Applied,
            revision,
            outcomes.MoveToImmutable());
        generation = await ReplaceControlAsync(
            generation,
            generation.Value with
            {
                Revision = revision,
                HasPermanentFailures = hasPermanentFailures,
                PendingBatch = pendingBatch with { Completion = result }
            },
            cancellationToken).ConfigureAwait(false);
        await EnsureBatchReceiptAsync(receiptId, intent.Fingerprint, result, cancellationToken)
            .ConfigureAwait(false);
        generation = await ReplaceControlAsync(
            generation,
            generation.Value with { PendingBatch = null },
            cancellationToken).ConfigureAwait(false);

        RecordBatchOutcomes(result);
        return result;
    }

    async ValueTask EnsureBatchReceiptAsync(
        string receiptId,
        MaterializationTargetIntentFingerprint requestFingerprint,
        MaterializationBatchResult result,
        CancellationToken cancellationToken)
    {
        BatchReceiptState receiptState = new(
            StateFormatVersion,
            BatchReceiptDocumentKind,
            requestFingerprint,
            result);
        var created = await CreateControlAsync(receiptId, receiptState, cancellationToken).ConfigureAwait(false);
        if (created is not null)
        {
            return;
        }

        var retained = await ReadControlAsync<BatchReceiptState>(receiptId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("A concurrent Elasticsearch batch receipt is unavailable.");
        if (retained.Value.RequestFingerprint != requestFingerprint || retained.Value.Result != result)
        {
            throw new InvalidOperationException(
                "The Elasticsearch batch receipt conflicts with its completed durable generation mutation.");
        }
    }

    async ValueTask<ImmutableArray<BatchWork>> ReadBatchWorkAsync(
        GenerationState generation,
        MaterializationApplyBatchRequest request,
        int maximumResponseBytes,
        CancellationToken cancellationToken)
    {
        var itemIds = ImmutableArray.CreateBuilder<string>(request.Mutations.Length);
        var receiptIds = ImmutableArray.CreateBuilder<string>(request.Mutations.Length);
        var pendingIds = ImmutableArray.CreateBuilder<string>(request.Mutations.Length);
        foreach (var mutation in request.Mutations)
        {
            itemIds.Add(DataDocumentId(mutation.ItemId));
            receiptIds.Add(MutationDocumentId(generation.GenerationId, mutation.MutationId));
            pendingIds.Add(PendingMutationDocumentId(generation.GenerationId, mutation.MutationId));
        }

        var items = await transport.MultiGetAsync(
            generation.IndexName,
            itemIds.MoveToImmutable(),
            ElasticMultiGetSourceProjection.MaterializationMetadata,
            maximumResponseBytes,
            cancellationToken).ConfigureAwait(false);
        var receipts = await transport.MultiGetAsync(
            binding.ControlIndexName,
            receiptIds.MoveToImmutable(),
            ElasticMultiGetSourceProjection.Full,
            maximumResponseBytes,
            cancellationToken).ConfigureAwait(false);
        var pending = await transport.MultiGetAsync(
            binding.ControlIndexName,
            pendingIds.MoveToImmutable(),
            ElasticMultiGetSourceProjection.Full,
            maximumResponseBytes,
            cancellationToken).ConfigureAwait(false);
        RequireCompleteMultiGet(items, request.Mutations.Length, "generation items");
        RequireCompleteMultiGet(receipts, request.Mutations.Length, "mutation receipts");
        RequireCompleteMultiGet(pending, request.Mutations.Length, "pending mutations");

        var builder = ImmutableArray.CreateBuilder<BatchWork>(request.Mutations.Length);
        for (var index = 0; index < request.Mutations.Length; index++)
        {
            var mutation = request.Mutations[index];
            var fingerprint = MaterializationTargetIntentFingerprinter.Compute(mutation);
            BatchWork work = new(mutation, fingerprint);
            if (TryReadMutationReceipt(receipts.Documents[index], out var retainedReceipt))
            {
                if (retainedReceipt.GenerationId != generation.GenerationId
                    || retainedReceipt.MutationId != mutation.MutationId
                    || retainedReceipt.MutationFingerprint != fingerprint)
                {
                    work.InitialDisposition = MaterializationItemOutcomeDisposition.IdempotencyConflict;
                    work.Code = IdempotencyConflictCode;
                    work.Message = "The mutation identity was reused for different canonical content.";
                }
                else
                {
                    work.ReceiptAlreadyApplied = true;
                    work.DataAlreadyApplied = true;
                }
            }

            if (TryReadPendingMutation(pending.Documents[index], out var retainedPending))
            {
                work.PendingAlreadyApplied = true;
                if (retainedPending.GenerationId != generation.GenerationId
                    || retainedPending.ItemId != mutation.ItemId
                    || retainedPending.MutationId != mutation.MutationId
                    || retainedPending.Version != mutation.Version
                    || retainedPending.MutationFingerprint != fingerprint)
                {
                    work.InitialDisposition = MaterializationItemOutcomeDisposition.IdempotencyConflict;
                    work.FinalDisposition = null;
                    work.Code = IdempotencyConflictCode;
                    work.Message = "The pending mutation identity was reused for different canonical content.";
                }
            }

            if (work.InitialDisposition is null && !work.ReceiptAlreadyApplied
                && TryReadItem(items.Documents[index], out var current))
            {
                if (current.Metadata.GenerationId != generation.GenerationId.Value
                    || current.Metadata.ItemId != mutation.ItemId.Value)
                {
                    throw new InvalidOperationException(
                        "An Elasticsearch generation item violates its retained identity envelope.");
                }
                if (mutation.Version.Ordinal < current.Metadata.Version)
                {
                    work.InitialDisposition = MaterializationItemOutcomeDisposition.VersionConflict;
                    work.Code = VersionConflictCode;
                    work.Message = $"Item version {mutation.Version.Value} does not advance retained version {current.Metadata.Version.ToString(CultureInfo.InvariantCulture)}.";
                    work.VersionConflictResolvesPending = true;
                }
                else if (mutation.Version.Ordinal == current.Metadata.Version)
                {
                    if (current.Metadata.MutationId == mutation.MutationId.Value
                        && current.Metadata.MutationFingerprint == fingerprint.Value)
                    {
                        work.DataAlreadyApplied = true;
                    }
                    else
                    {
                        work.InitialDisposition = current.Metadata.MutationId == mutation.MutationId.Value
                            ? MaterializationItemOutcomeDisposition.IdempotencyConflict
                            : MaterializationItemOutcomeDisposition.VersionConflict;
                        work.Code = work.InitialDisposition == MaterializationItemOutcomeDisposition.IdempotencyConflict
                            ? IdempotencyConflictCode
                            : VersionConflictCode;
                        work.Message = work.InitialDisposition == MaterializationItemOutcomeDisposition.IdempotencyConflict
                            ? "The mutation identity was reused for different Elasticsearch item content."
                            : $"Item version {mutation.Version.Value} does not advance retained version {current.Metadata.Version.ToString(CultureInfo.InvariantCulture)}.";
                    }
                }
            }

            builder.Add(work);
        }
        return builder.MoveToImmutable();
    }

    static void ApplyPendingBatchBaseline(ImmutableArray<BatchWork> works, PendingBatch pendingBatch)
    {
        if (!pendingBatch.IsInitialized
            || pendingBatch.PreexistingMutationIds.IsDefault
            || pendingBatch.PreexistingPendingMutationIds.IsDefault)
        {
            throw new InvalidOperationException("A durable pending batch omitted its mutation baseline.");
        }

        foreach (var work in works)
        {
            work.ReceiptPresentAtBatchStart = Contains(
                pendingBatch.PreexistingMutationIds,
                work.Mutation.MutationId);
            work.PendingPresentAtBatchStart = Contains(
                pendingBatch.PreexistingPendingMutationIds,
                work.Mutation.MutationId);
            if (work.ReceiptAlreadyApplied)
            {
                work.FinalDisposition = work.ReceiptPresentAtBatchStart
                    ? MaterializationItemOutcomeDisposition.Replayed
                    : MaterializationItemOutcomeDisposition.Applied;
            }
        }
    }

    static bool Contains(
        ImmutableArray<MaterializationItemMutationId> values,
        MaterializationItemMutationId value)
    {
        foreach (var candidate in values)
        {
            if (candidate == value)
            {
                return true;
            }
        }
        return false;
    }

    static ElasticJsonObject SerializeItemDocument(
        MaterializationGenerationId generationId,
        MaterializationItemMutation mutation,
        MaterializationTargetIntentFingerprint fingerprint)
    {
        ItemMetadata metadata = new(
            generationId.Value,
            mutation.ItemId.Value,
            mutation.MutationId.Value,
            fingerprint.Value,
            mutation.Version.Ordinal,
            mutation.Kind == MaterializationItemMutationKind.Delete);
        ItemDocument document = new(
            metadata,
            mutation is MaterializationUpsert upsert ? upsert.Value : null);
        return ElasticJsonObject.Serialize(document, JsonOptions);
    }

    static bool TryReadItem(ElasticMultiGetDocument document, out ItemDocument item)
    {
        if (!document.Found)
        {
            item = null!;
            return false;
        }
        try
        {
            item = JsonSerializer.Deserialize<ItemDocument>(document.Source, JsonOptions)
                ?? throw new JsonException("The generation item is null.");
            RequireValidItemEnvelope(item);
            return true;
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                "An Elasticsearch generation item violates its adapter-owned envelope.",
                exception);
        }
    }

    static void RequireValidItemEnvelope(ItemDocument item)
    {
        if (item.Metadata is null
            || string.IsNullOrWhiteSpace(item.Metadata.GenerationId)
            || string.IsNullOrWhiteSpace(item.Metadata.ItemId)
            || string.IsNullOrWhiteSpace(item.Metadata.MutationId)
            || item.Metadata.Version <= 0
            || item.Metadata.MutationFingerprint.Length != 64
            || item.Metadata.MutationFingerprint.Any(static character =>
                character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
            || item.Metadata.Deleted && item.Value is not null)
        {
            throw new JsonException("The generation item metadata envelope is invalid or contradictory.");
        }
    }

    static void ValidateRetainedItemFingerprint(ItemDocument item)
    {
        MaterializationItemMutation retained = item.Metadata.Deleted
            ? new MaterializationDelete(
                new(item.Metadata.ItemId),
                new(item.Metadata.MutationId),
                new(item.Metadata.Version.ToString(CultureInfo.InvariantCulture)))
            : new MaterializationUpsert(
                new(item.Metadata.ItemId),
                new(item.Metadata.MutationId),
                new(item.Metadata.Version.ToString(CultureInfo.InvariantCulture)),
                item.Value ?? ObservationValue.Null);
        if (MaterializationTargetIntentFingerprinter.Compute(retained).Value
            != item.Metadata.MutationFingerprint)
        {
            throw new InvalidOperationException(
                "An Elasticsearch generation item does not match its retained mutation fingerprint.");
        }
    }

    static bool TryReadMutationReceipt(
        ElasticMultiGetDocument document,
        out MutationReceiptState receipt)
    {
        if (!TryReadMultiGet(document, "mutation receipt", out receipt))
        {
            return false;
        }
        RequireControlEnvelope(receipt.FormatVersion, receipt.DocumentKind, MutationReceiptDocumentKind);
        RequireValidControl(
            IsDefined(receipt.GenerationId.Value)
            && IsDefined(receipt.MutationId.Value)
            && IsDefined(receipt.MutationFingerprint.Value));
        return true;
    }

    static bool TryReadPendingMutation(
        ElasticMultiGetDocument document,
        out PendingMutationState pending)
    {
        if (!TryReadMultiGet(document, "pending mutation", out pending))
        {
            return false;
        }
        RequireControlEnvelope(pending.FormatVersion, pending.DocumentKind, PendingMutationDocumentKind);
        RequireValidControl(
            IsDefined(pending.GenerationId.Value)
            && IsDefined(pending.ItemId.Value)
            && IsDefined(pending.MutationId.Value)
            && IsDefined(pending.Version.Value)
            && IsDefined(pending.MutationFingerprint.Value));
        return true;
    }

    static bool TryReadMultiGet<T>(ElasticMultiGetDocument document, string contract, out T value)
        where T : class
    {
        if (!document.Found)
        {
            value = null!;
            return false;
        }
        try
        {
            value = JsonSerializer.Deserialize<T>(document.Source, JsonOptions)
                ?? throw new JsonException($"The Elasticsearch {contract} is null.");
            return true;
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                $"An Elasticsearch {contract} violates its adapter schema.",
                exception);
        }
    }

    static bool ShouldResolvePending(
        BatchWork work,
        MaterializationItemOutcomeDisposition disposition) =>
        disposition is MaterializationItemOutcomeDisposition.Applied
            or MaterializationItemOutcomeDisposition.Replayed
            or MaterializationItemOutcomeDisposition.PermanentFailure
        || disposition == MaterializationItemOutcomeDisposition.VersionConflict
            && work.VersionConflictResolvesPending;

    static string ProviderMessage(ElasticBulkItemResult item, string fallback)
    {
        var providerType = SanitizeProviderErrorType(item.ErrorType);
        if (providerType is null)
        {
            return $"{fallback} Provider status: {item.StatusCode.ToString(CultureInfo.InvariantCulture)}.";
        }
        return $"{fallback} Provider type: {providerType}; status: {item.StatusCode.ToString(CultureInfo.InvariantCulture)}.";
    }

    static string? SanitizeProviderErrorType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128)
        {
            return null;
        }
        foreach (var character in value)
        {
            if (!(character is >= 'a' and <= 'z'
                    or >= 'A' and <= 'Z'
                    or >= '0' and <= '9'
                    or '.' or '_' or '-'))
            {
                return null;
            }
        }
        return value;
    }

    static void RequireCompleteBulk(
        ElasticBulkResult result,
        ImmutableArray<ElasticBulkOperation> expected,
        MaterializationGenerationId generationId)
    {
        if (result.Items.IsDefault || result.Items.Length != expected.Length)
        {
            throw new InvalidOperationException(
                $"Elasticsearch returned an incomplete bulk response for generation '{generationId.Value}'.");
        }
        for (var index = 0; index < result.Items.Length; index++)
        {
            var observed = result.Items[index];
            var submitted = expected[index];
            if (observed.Ordinal != index
                || observed.Kind != submitted.Kind
                || !string.Equals(observed.Index, submitted.Index, StringComparison.Ordinal)
                || !string.Equals(observed.Id, submitted.Id, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Elasticsearch bulk item evidence does not preserve exact request identity order.");
            }
        }
    }

    static void RequireCompleteMultiGet(ElasticMultiGetResult result, int expected, string contract)
    {
        if (result.Documents.IsDefault || result.Documents.Length != expected)
        {
            throw new InvalidOperationException(
                $"Elasticsearch returned incomplete request-order {contract} evidence.");
        }
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
            outcomes.Add(new(mutation.ItemId, mutation.MutationId, itemDisposition, code, message));
        }
        return MaterializationBatchResult.ForRequest(
            request,
            batchDisposition,
            generationRevision,
            outcomes.MoveToImmutable());
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

    long MaximumWireBytes(MaterializationTargetBatchIntent intent)
    {
        var expanded = checked(
            intent.CanonicalByteCount * 4L
            + (long)intent.ItemCount * 1_024L
            + 16_384L);
        return Math.Min(expanded, Array.MaxLength);
    }

    int MaximumBatchResponseBytes() => MaximumControlResponseBytes();

    static JsonSerializerOptions CreateJsonOptions()
    {
        var options = MaterializationJsonSerializer.CreateOptions();
        options.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
        return options;
    }

    int MaximumControlDocumentBytes()
    {
        const int responseEnvelopeBytes = 64 * 1024;
        var expanded = checked(
            policy.MaximumBatchBytes * 4L
            + (long)policy.MaximumBatchItems * 1_024L);
        return checked((int)Math.Min(
            Array.MaxLength - responseEnvelopeBytes,
            Math.Max(policy.MaximumDiagnosticBytes, expanded)));
    }

    int MaximumControlResponseBytes()
    {
        const int responseEnvelopeBytes = 64 * 1024;
        return checked((int)Math.Min(
            Array.MaxLength,
            (long)MaximumControlDocumentBytes() + responseEnvelopeBytes));
    }

    int BoundedResponseBytes(long canonicalBytes, int itemCount)
    {
        var expanded = checked(canonicalBytes * 4L + (long)itemCount * 1_024L + 64 * 1_024L);
        return checked((int)Math.Min(
            Array.MaxLength,
            Math.Max(policy.MaximumDiagnosticBytes, expanded)));
    }

    ElasticJsonObject SerializeControlDocument<T>(T value)
        where T : class
    {
        var source = ElasticJsonObject.Serialize(value, JsonOptions);
        var maximumBytes = MaximumControlDocumentBytes();
        if (source.Length > maximumBytes)
        {
            throw new InvalidOperationException(
                $"The durable Elasticsearch materialization control document exceeds its configured {maximumBytes.ToString(CultureInfo.InvariantCulture)}-byte read/write bound.");
        }
        return source;
    }

    static MaterializationGenerationRevision Next(MaterializationGenerationRevision revision) =>
        new(checked(revision.Ordinal + 1).ToString(CultureInfo.InvariantCulture));

    static MaterializationGenerationSnapshot WithLatestFence(
        MaterializationGenerationSnapshot snapshot,
        Stored<GenerationState>? generation) =>
        generation is null || snapshot.LatestWorkerFence == generation.Value.LatestWorkerFence
            ? snapshot
            : new(
                snapshot.MaterializationId,
                snapshot.GenerationId,
                snapshot.DefinitionFingerprint,
                snapshot.State,
                snapshot.Revision,
                generation.Value.LatestWorkerFence,
                snapshot.HasPermanentFailures,
                snapshot.PendingRetryableMutationCount,
                snapshot.VisibleItemCount,
                snapshot.TombstoneCount,
                snapshot.SealReceipt,
                snapshot.ValidationReceipt,
                snapshot.CreatedAtUtc,
                snapshot.InactivatedAtUtc,
                snapshot.RetiredAtUtc);

    static DocumentValidationDiagnostic Diagnostic(
        string code,
        string message,
        string location,
        string subject,
        ImmutableArray<string> sources,
        string expected,
        string observed) =>
        MaterializationContract.CreateDiagnostic(
            code,
            DiagnosticSeverity.Error,
            message,
            location,
            "elastic-materialization-target-validation",
            subject,
            sources,
            expected,
            observed);

    void RecordBatchInput(MaterializationTargetBatchIntent intent)
    {
        TagList tags = new()
        {
            { ElasticMaterializationTelemetry.TargetIdTagName, Descriptor.Id.Value },
            { ElasticMaterializationTelemetry.MaterializationIdTagName, Descriptor.MaterializationId.Value }
        };
        ElasticMaterializationTelemetry.BatchItems.Record(intent.ItemCount, tags);
        ElasticMaterializationTelemetry.BatchBytes.Record(intent.CanonicalByteCount, tags);
    }

    void RecordBatchOutcomes(MaterializationBatchResult result)
    {
        foreach (var group in result.Outcomes.GroupBy(static outcome => outcome.Disposition))
        {
            TagList tags = new()
            {
                { ElasticMaterializationTelemetry.TargetIdTagName, Descriptor.Id.Value },
                { ElasticMaterializationTelemetry.MaterializationIdTagName, Descriptor.MaterializationId.Value },
                { ElasticMaterializationTelemetry.ItemOutcomeTagName, group.Key.ToString() },
                {
                    ElasticMaterializationTelemetry.RetryableTagName,
                    group.Key == MaterializationItemOutcomeDisposition.RetryableRejected
                }
            };
            ElasticMaterializationTelemetry.ItemOutcomes.Add(group.LongCount(), tags);
        }
    }

    async ValueTask<SealContent> ReadSealContentAsync(
        GenerationState generation,
        CancellationToken cancellationToken)
    {
        using MaterializationSealFingerprintAccumulator fingerprint = new();
        string? after = null;
        MaterializationItemId? last = null;
        long visible = 0;
        var pageItems = Math.Min(ScanPageItems, policy.MaximumBatchItems);
        do
        {
            ElasticScanPage page;
            while (true)
            {
                try
                {
                    page = await transport.ScanAsync(
                        new(
                            generation.IndexName,
                            MatchAllQuery,
                            $"{ElasticMaterializationTargetBinding.MetadataField}.itemId",
                            after,
                            pageItems,
                            MaximumControlResponseBytes()),
                        cancellationToken).ConfigureAwait(false);
                    break;
                }
                catch (ElasticMaterializationTransportException exception)
                    when (exception.ErrorType == ResponseLimitErrorType && pageItems > 1)
                {
                    pageItems = Math.Max(1, pageItems / 2);
                }
            }
            foreach (var hit in page.Hits)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ItemDocument item;
                try
                {
                    item = JsonSerializer.Deserialize<ItemDocument>(hit.Source, JsonOptions)
                        ?? throw new JsonException("The scanned generation item is null.");
                    RequireValidItemEnvelope(item);
                }
                catch (JsonException exception)
                {
                    throw new InvalidOperationException(
                        "A scanned Elasticsearch generation item violates its adapter envelope.",
                        exception);
                }
                var itemId = new MaterializationItemId(item.Metadata.ItemId);
                if (item.Metadata.GenerationId != generation.GenerationId.Value
                    || !string.Equals(item.Metadata.ItemId, hit.SortValue, StringComparison.Ordinal)
                    || !string.Equals(hit.Id, DataDocumentId(itemId), StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "A scanned Elasticsearch generation item violates its adapter-owned identity envelope.");
                }
                if (last is { } prior
                    && MaterializationSealContentOrder.Compare(prior, itemId) >= 0)
                {
                    throw new InvalidOperationException(
                        "Elasticsearch returned non-canonical generation item identity order.");
                }
                ValidateRetainedItemFingerprint(item);
                last = itemId;
                var kind = item.Metadata.Deleted
                    ? MaterializationItemMutationKind.Delete
                    : MaterializationItemMutationKind.Upsert;
                ObservationValue? value = item.Metadata.Deleted
                    ? (ObservationValue?)null
                    : item.Value ?? ObservationValue.Null;
                fingerprint.Append(new(
                    itemId,
                    new(item.Metadata.Version.ToString(CultureInfo.InvariantCulture)),
                    new(item.Metadata.MutationId),
                    kind,
                    value));
                if (!item.Metadata.Deleted)
                {
                    visible++;
                }
            }
            after = page.NextAfterSortValue;
            if (after is not null && (page.Hits.IsDefaultOrEmpty || after != page.Hits[^1].SortValue))
            {
                throw new InvalidOperationException(
                    "Elasticsearch returned an invalid generation scan continuation.");
            }
        }
        while (after is not null);

        return new(fingerprint.Complete(), visible);
    }

    async ValueTask<MaterializationSealResult> SealGenerationCoreAsync(
        MaterializationSealGenerationRequest request,
        CancellationToken cancellationToken)
    {
        _ = await LoadTargetAsync(cancellationToken).ConfigureAwait(false);
        var fingerprint = MaterializationTargetIntentFingerprinter.Compute(request);
        var receiptId = OperationDocumentId(SealReceiptDocumentKind, request.SealId.Value);
        var prior = await ReadControlAsync<SealReceiptState>(receiptId, cancellationToken).ConfigureAwait(false);
        var generation = await ReadGenerationAsync(request.GenerationId, cancellationToken).ConfigureAwait(false);
        if (prior is not null)
        {
            if (generation is not null)
            {
                generation = await AcceptGenerationFenceAsync(generation, request.WorkerFence, cancellationToken)
                    .ConfigureAwait(false);
                if (generation.Value.PendingSeal is { } pending
                    && pending.SealId == request.SealId
                    && pending.RequestFingerprint == prior.Value.RequestFingerprint
                    && generation.Value.SealReceipt == prior.Value.Receipt)
                {
                    generation = await ReplaceControlAsync(
                        generation,
                        generation.Value with { PendingSeal = null },
                        cancellationToken).ConfigureAwait(false);
                }
            }
            if (prior.Value.RequestFingerprint != fingerprint)
            {
                return new(
                    MaterializationTargetOperationDisposition.IdentityConflict,
                    generation is { Value.Retained: true }
                        ? await SnapshotAsync(generation.Value, cancellationToken).ConfigureAwait(false)
                        : null,
                    receipt: null);
            }
            return new(
                MaterializationTargetOperationDisposition.Replayed,
                generation is { Value.Retained: true }
                    ? await SnapshotAsync(generation.Value, cancellationToken).ConfigureAwait(false)
                    : WithLatestFence(prior.Value.Generation, generation),
                prior.Value.Receipt);
        }

        if (!await ReserveOperationAsync(receiptId, fingerprint, cancellationToken).ConfigureAwait(false))
        {
            if (generation is not null)
            {
                generation = await AcceptGenerationFenceAsync(
                    generation,
                    request.WorkerFence,
                    cancellationToken).ConfigureAwait(false);
            }
            return new(
                MaterializationTargetOperationDisposition.IdentityConflict,
                generation is { Value.Retained: true }
                    ? await SnapshotAsync(generation.Value, cancellationToken).ConfigureAwait(false)
                    : null,
                receipt: null);
        }

        if (generation is null || !generation.Value.Retained)
        {
            return new(MaterializationTargetOperationDisposition.NotFound, generation: null, receipt: null);
        }
        if (request.SealedAtUtc < generation.Value.CreatedAtUtc)
        {
            throw new ArgumentException("A seal time cannot predate generation creation.", nameof(request));
        }
        if (request.WorkerFence.Ordinal < generation.Value.LatestWorkerFence.Ordinal)
        {
            return new(
                MaterializationTargetOperationDisposition.StaleFence,
                await SnapshotAsync(generation.Value, cancellationToken).ConfigureAwait(false),
                receipt: null);
        }
        generation = await AcceptGenerationFenceAsync(generation, request.WorkerFence, cancellationToken)
            .ConfigureAwait(false);

        if (generation.Value.PendingSeal is { } pendingCompletion
            && generation.Value.SealReceipt is { } completedSeal
            && generation.Value.State == MaterializationGenerationState.Sealed)
        {
            if (pendingCompletion.SealId != request.SealId
                || pendingCompletion.RequestFingerprint != fingerprint
                || completedSeal.SealId != request.SealId)
            {
                return new(
                    MaterializationTargetOperationDisposition.StateConflict,
                    await SnapshotAsync(generation.Value, cancellationToken).ConfigureAwait(false),
                    receipt: null);
            }
            var historical = await SnapshotAsync(generation.Value, cancellationToken).ConfigureAwait(false);
            await EnsureSealReceiptAsync(
                receiptId,
                fingerprint,
                completedSeal,
                historical,
                cancellationToken).ConfigureAwait(false);
            generation = await ReplaceControlAsync(
                generation,
                generation.Value with { PendingSeal = null },
                cancellationToken).ConfigureAwait(false);
            return new(MaterializationTargetOperationDisposition.Replayed, historical, completedSeal);
        }
        if (generation.Value.Revision != request.ExpectedRevision)
        {
            return new(
                MaterializationTargetOperationDisposition.RevisionConflict,
                await SnapshotAsync(generation.Value, cancellationToken).ConfigureAwait(false),
                receipt: null);
        }
        if (generation.Value.State != MaterializationGenerationState.Loading
            || generation.Value.PendingBatch is not null
            || !generation.Value.IsProvisioned)
        {
            return new(
                MaterializationTargetOperationDisposition.StateConflict,
                await SnapshotAsync(generation.Value, cancellationToken).ConfigureAwait(false),
                receipt: null);
        }
        await RequireExactGenerationOwnershipAsync(generation.Value, cancellationToken).ConfigureAwait(false);

        if (generation.Value.PendingSeal is { } pendingSeal)
        {
            if (pendingSeal.SealId != request.SealId || pendingSeal.RequestFingerprint != fingerprint)
            {
                return new(
                    MaterializationTargetOperationDisposition.StateConflict,
                    await SnapshotAsync(generation.Value, cancellationToken).ConfigureAwait(false),
                    receipt: null);
            }
            if (pendingSeal.StartedRevision != generation.Value.Revision)
            {
                throw new InvalidOperationException(
                    "A pending Elasticsearch seal crossed an unexpected generation-revision boundary.");
            }
        }
        else
        {
            generation = await ReplaceControlAsync(
                generation,
                generation.Value with
                {
                    PendingSeal = new(request.SealId, fingerprint, generation.Value.Revision)
                },
                cancellationToken).ConfigureAwait(false);
        }

        var blocked = await transport.AddWriteBlockAsync(
            generation.Value.IndexName,
            policy.MaximumDiagnosticBytes,
            cancellationToken).ConfigureAwait(false);
        if (blocked.Disposition != ElasticAcknowledgedDisposition.Applied || !blocked.Acknowledged)
        {
            throw new InvalidOperationException("Elasticsearch did not acknowledge the generation write barrier.");
        }
        var refreshed = await transport.RefreshAsync(
            generation.Value.IndexName,
            policy.MaximumDiagnosticBytes,
            cancellationToken).ConfigureAwait(false);
        if (refreshed.Disposition != ElasticAcknowledgedDisposition.Applied || !refreshed.Acknowledged)
        {
            throw new InvalidOperationException("Elasticsearch did not refresh the sealed generation.");
        }

        var content = await ReadSealContentAsync(generation.Value, cancellationToken).ConfigureAwait(false);
        var revision = Next(generation.Value.Revision);
        MaterializationSealReceipt receipt = new(
            request.SealId,
            request.GenerationId,
            revision,
            content.VisibleItemCount,
            content.Fingerprint,
            request.SealedAtUtc);
        generation = await ReplaceControlAsync(
            generation,
            generation.Value with
            {
                State = MaterializationGenerationState.Sealed,
                Revision = revision,
                SealReceipt = receipt,
                PendingSeal = generation.Value.PendingSeal
            },
            cancellationToken).ConfigureAwait(false);
        var snapshot = await SnapshotAsync(generation.Value, cancellationToken).ConfigureAwait(false);
        await EnsureSealReceiptAsync(
            receiptId,
            fingerprint,
            receipt,
            snapshot,
            cancellationToken).ConfigureAwait(false);
        generation = await ReplaceControlAsync(
            generation,
            generation.Value with { PendingSeal = null },
            cancellationToken).ConfigureAwait(false);
        return new(MaterializationTargetOperationDisposition.Applied, snapshot, receipt);
    }

    async ValueTask EnsureSealReceiptAsync(
        string receiptId,
        MaterializationTargetIntentFingerprint requestFingerprint,
        MaterializationSealReceipt receipt,
        MaterializationGenerationSnapshot generation,
        CancellationToken cancellationToken)
    {
        SealReceiptState receiptState = new(
            StateFormatVersion,
            SealReceiptDocumentKind,
            requestFingerprint,
            receipt,
            generation);
        var created = await CreateControlAsync(receiptId, receiptState, cancellationToken).ConfigureAwait(false);
        if (created is not null)
        {
            return;
        }

        var retained = await ReadControlAsync<SealReceiptState>(receiptId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("A concurrent Elasticsearch seal receipt is unavailable.");
        if (retained.Value != receiptState)
        {
            throw new InvalidOperationException(
                "The Elasticsearch seal receipt conflicts with its completed durable generation transition.");
        }
    }

    async ValueTask<MaterializationValidationResult> ValidateGenerationCoreAsync(
        MaterializationValidateGenerationRequest request,
        CancellationToken cancellationToken)
    {
        _ = await LoadTargetAsync(cancellationToken).ConfigureAwait(false);
        var fingerprint = MaterializationTargetIntentFingerprinter.Compute(request);
        var receiptId = OperationDocumentId(ValidationReceiptDocumentKind, request.ValidationId.Value);
        var prior = await ReadControlAsync<ValidationReceiptState>(receiptId, cancellationToken).ConfigureAwait(false);
        var generation = await ReadGenerationAsync(request.GenerationId, cancellationToken).ConfigureAwait(false);
        if (prior is not null)
        {
            if (generation is not null)
            {
                generation = await AcceptGenerationFenceAsync(generation, request.WorkerFence, cancellationToken)
                    .ConfigureAwait(false);
                if (generation.Value.PendingValidation is { } pendingValidationCleanup
                    && pendingValidationCleanup.ValidationId == request.ValidationId
                    && pendingValidationCleanup.RequestFingerprint == prior.Value.RequestFingerprint
                    && generation.Value.ValidationReceipt == prior.Value.Receipt)
                {
                    generation = await ReplaceControlAsync(
                        generation,
                        generation.Value with { PendingValidation = null },
                        cancellationToken).ConfigureAwait(false);
                }
            }
            if (prior.Value.RequestFingerprint != fingerprint)
            {
                return new(
                    MaterializationTargetOperationDisposition.IdentityConflict,
                    generation is { Value.Retained: true }
                        ? await SnapshotAsync(generation.Value, cancellationToken).ConfigureAwait(false)
                        : null,
                    receipt: null);
            }
            return new(
                MaterializationTargetOperationDisposition.Replayed,
                generation is { Value.Retained: true }
                    ? await SnapshotAsync(generation.Value, cancellationToken).ConfigureAwait(false)
                    : WithLatestFence(prior.Value.Generation, generation),
                prior.Value.Receipt);
        }

        if (!await ReserveOperationAsync(receiptId, fingerprint, cancellationToken).ConfigureAwait(false))
        {
            if (generation is not null)
            {
                generation = await AcceptGenerationFenceAsync(
                    generation,
                    request.WorkerFence,
                    cancellationToken).ConfigureAwait(false);
            }
            return new(
                MaterializationTargetOperationDisposition.IdentityConflict,
                generation is { Value.Retained: true }
                    ? await SnapshotAsync(generation.Value, cancellationToken).ConfigureAwait(false)
                    : null,
                receipt: null);
        }

        if (generation is null || !generation.Value.Retained)
        {
            return new(MaterializationTargetOperationDisposition.NotFound, generation: null, receipt: null);
        }
        var latestEvidenceAtUtc = generation.Value.ValidationReceipt?.ValidatedAtUtc
            ?? generation.Value.SealReceipt?.SealedAtUtc;
        if (latestEvidenceAtUtc is { } latest && request.ValidatedAtUtc < latest)
        {
            throw new ArgumentException(
                "A validation time cannot predate the generation's latest seal or validation boundary.",
                nameof(request));
        }
        if (request.WorkerFence.Ordinal < generation.Value.LatestWorkerFence.Ordinal)
        {
            return new(
                MaterializationTargetOperationDisposition.StaleFence,
                await SnapshotAsync(generation.Value, cancellationToken).ConfigureAwait(false),
                receipt: null);
        }
        generation = await AcceptGenerationFenceAsync(generation, request.WorkerFence, cancellationToken)
            .ConfigureAwait(false);

        if (generation.Value.PendingValidation is { } pendingCompletion
            && generation.Value.ValidationReceipt is { } completedValidation
            && completedValidation.ValidationId == pendingCompletion.ValidationId
            && generation.Value.Revision != pendingCompletion.StartedRevision)
        {
            if (pendingCompletion.ValidationId != request.ValidationId
                || pendingCompletion.RequestFingerprint != fingerprint)
            {
                return new(
                    MaterializationTargetOperationDisposition.StateConflict,
                    await SnapshotAsync(generation.Value, cancellationToken).ConfigureAwait(false),
                    receipt: null);
            }
            var historical = await SnapshotAsync(generation.Value, cancellationToken).ConfigureAwait(false);
            await EnsureValidationReceiptAsync(
                receiptId,
                fingerprint,
                completedValidation,
                historical,
                cancellationToken).ConfigureAwait(false);
            generation = await ReplaceControlAsync(
                generation,
                generation.Value with { PendingValidation = null },
                cancellationToken).ConfigureAwait(false);
            return new(
                MaterializationTargetOperationDisposition.Replayed,
                historical,
                completedValidation);
        }

        if (generation.Value.PendingValidation is { } pendingValidation
            && (pendingValidation.ValidationId != request.ValidationId
                || pendingValidation.RequestFingerprint != fingerprint))
        {
            return new(
                MaterializationTargetOperationDisposition.StateConflict,
                await SnapshotAsync(generation.Value, cancellationToken).ConfigureAwait(false),
                receipt: null);
        }
        if (generation.Value.Revision != request.ExpectedRevision)
        {
            return new(
                MaterializationTargetOperationDisposition.RevisionConflict,
                await SnapshotAsync(generation.Value, cancellationToken).ConfigureAwait(false),
                receipt: null);
        }
        if (generation.Value.State != MaterializationGenerationState.Sealed
            || generation.Value.SealReceipt is null
            || generation.Value.PendingSeal is not null
            || generation.Value.PendingBatch is not null)
        {
            return new(
                MaterializationTargetOperationDisposition.StateConflict,
                await SnapshotAsync(generation.Value, cancellationToken).ConfigureAwait(false),
                receipt: null);
        }
        var sealReceipt = generation.Value.SealReceipt;
        await RequireExactGenerationOwnershipAsync(generation.Value, cancellationToken).ConfigureAwait(false);

        if (generation.Value.PendingValidation is null)
        {
            generation = await ReplaceControlAsync(
                generation,
                generation.Value with
                {
                    PendingValidation = new(
                        request.ValidationId,
                        fingerprint,
                        generation.Value.Revision)
                },
                cancellationToken).ConfigureAwait(false);
        }
        else if (generation.Value.PendingValidation.StartedRevision != generation.Value.Revision)
        {
            throw new InvalidOperationException(
                "A pending Elasticsearch validation crossed an unexpected generation-revision boundary.");
        }

        await RefreshRequiredAsync(generation.Value.IndexName, cancellationToken).ConfigureAwait(false);
        await RefreshRequiredAsync(binding.ControlIndexName, cancellationToken).ConfigureAwait(false);
        var content = await ReadSealContentAsync(generation.Value, cancellationToken).ConfigureAwait(false);
        var actualFingerprint = content.Fingerprint;
        var pending = await transport.CountAsync(
            binding.ControlIndexName,
            PendingCountQuery(generation.Value.GenerationId),
            policy.MaximumDiagnosticBytes,
            cancellationToken).ConfigureAwait(false);
        ImmutableArray<string> sources =
        [
            Descriptor.Capabilities.Id.Value,
            $"elastic-target-binding:{binding.Fingerprint.Value}",
            $"elastic-index-template:{binding.IndexTemplate.Fingerprint.Value}",
            request.Validator
        ];
        List<DocumentValidationDiagnostic> diagnostics = [];
        if (sealReceipt.Fingerprint != request.ExpectedSealFingerprint)
        {
            diagnostics.Add(Diagnostic(
                "cohesive.adapters.elastic.materialization.sealFingerprintMismatch",
                "The expected seal fingerprint does not match retained seal evidence.",
                "/sealFingerprint",
                request.GenerationId.Value,
                sources,
                request.ExpectedSealFingerprint.Value,
                sealReceipt.Fingerprint.Value));
        }
        if (actualFingerprint != sealReceipt.Fingerprint)
        {
            diagnostics.Add(Diagnostic(
                "cohesive.adapters.elastic.materialization.sealedContentDrift",
                "The write-blocked Elasticsearch generation no longer matches its immutable seal.",
                "/generation/content",
                request.GenerationId.Value,
                sources,
                sealReceipt.Fingerprint.Value,
                actualFingerprint.Value));
        }
        if (generation.Value.HasPermanentFailures)
        {
            diagnostics.Add(Diagnostic(
                "cohesive.adapters.elastic.materialization.permanentWriteFailure",
                "At least one permanent item write failure remains recorded.",
                "/writes",
                request.GenerationId.Value,
                sources,
                "no permanent failures",
                "one or more permanent failures"));
        }
        if (pending.Count != 0)
        {
            diagnostics.Add(Diagnostic(
                "cohesive.adapters.elastic.materialization.pendingRetryableItems",
                $"{pending.Count} retryable mutation(s) remain unresolved.",
                "/writes",
                request.GenerationId.Value,
                sources,
                "0 pending retryable mutations",
                pending.Count.ToString(CultureInfo.InvariantCulture)));
        }
        if (request.ExpectedVisibleItemCount is { } expected
            && expected != content.VisibleItemCount)
        {
            diagnostics.Add(Diagnostic(
                "cohesive.adapters.elastic.materialization.visibleItemCountMismatch",
                $"Expected {expected} visible items but observed {content.VisibleItemCount}.",
                "/visibleItemCount",
                request.GenerationId.Value,
                sources,
                expected.ToString(CultureInfo.InvariantCulture),
                content.VisibleItemCount.ToString(CultureInfo.InvariantCulture)));
        }
        diagnostics.Sort(DocumentValidationDiagnosticComparer.Ordinal);
        var validation = diagnostics.Count == 0
            ? DocumentValidationResult.Valid
            : new DocumentValidationResult([.. diagnostics]);
        var revision = Next(generation.Value.Revision);
        MaterializationValidationReceipt receipt = new(
            request.ValidationId,
            request.GenerationId,
            revision,
            sealReceipt.Fingerprint,
            MaterializationTargetIntentFingerprinter.ComputeValidationResult(request, validation),
            validation,
            request.ValidatedAtUtc);
        generation = await ReplaceControlAsync(
            generation,
            generation.Value with
            {
                State = validation.IsValid
                    ? MaterializationGenerationState.Validated
                    : MaterializationGenerationState.Sealed,
                Revision = revision,
                ValidationReceipt = receipt,
                PendingValidation = generation.Value.PendingValidation
            },
            cancellationToken).ConfigureAwait(false);
        var snapshot = await SnapshotAsync(generation.Value, cancellationToken).ConfigureAwait(false);
        await EnsureValidationReceiptAsync(
            receiptId,
            fingerprint,
            receipt,
            snapshot,
            cancellationToken).ConfigureAwait(false);
        generation = await ReplaceControlAsync(
            generation,
            generation.Value with { PendingValidation = null },
            cancellationToken).ConfigureAwait(false);
        return new(
            validation.IsValid
                ? MaterializationTargetOperationDisposition.Applied
                : MaterializationTargetOperationDisposition.ValidationFailed,
            snapshot,
            receipt);
    }

    async ValueTask EnsureValidationReceiptAsync(
        string receiptId,
        MaterializationTargetIntentFingerprint requestFingerprint,
        MaterializationValidationReceipt receipt,
        MaterializationGenerationSnapshot generation,
        CancellationToken cancellationToken)
    {
        ValidationReceiptState receiptState = new(
            StateFormatVersion,
            ValidationReceiptDocumentKind,
            requestFingerprint,
            receipt,
            generation);
        var created = await CreateControlAsync(receiptId, receiptState, cancellationToken).ConfigureAwait(false);
        if (created is not null)
        {
            return;
        }

        var retained = await ReadControlAsync<ValidationReceiptState>(receiptId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("A concurrent Elasticsearch validation receipt is unavailable.");
        if (retained.Value != receiptState)
        {
            throw new InvalidOperationException(
                "The Elasticsearch validation receipt conflicts with its completed durable generation transition.");
        }
    }

    async ValueTask<MaterializationPromotionResult> PromoteGenerationCoreAsync(
        MaterializationPromoteGenerationRequest request,
        CancellationToken cancellationToken)
    {
        await promotionAdmission.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await PromoteGenerationExclusiveAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            promotionAdmission.Release();
        }
    }

    async ValueTask<MaterializationPromotionResult> PromoteGenerationExclusiveAsync(
        MaterializationPromoteGenerationRequest request,
        CancellationToken cancellationToken)
    {
        var target = await LoadTargetAsync(cancellationToken).ConfigureAwait(false);
        var fingerprint = MaterializationTargetIntentFingerprinter.Compute(request);
        var receiptId = OperationDocumentId(PromotionReceiptDocumentKind, request.PromotionId.Value);
        var prior = await ReadControlAsync<PromotionReceiptState>(receiptId, cancellationToken).ConfigureAwait(false);
        var pointerFenceWasStale = target.Value.LatestPromotionFence is { } latestFence
            && request.PromotionFence.Ordinal < latestFence.Ordinal;
        target = await AcceptPromotionFenceAsync(target, request.PromotionFence, cancellationToken)
            .ConfigureAwait(false);

        var generation = await ReadGenerationAsync(request.GenerationId, cancellationToken).ConfigureAwait(false);
        var generationFenceWasStale = generation is not null
            && request.GenerationWorkerFence.Ordinal < generation.Value.LatestWorkerFence.Ordinal;
        if (generation is not null)
        {
            generation = await AcceptGenerationFenceAsync(
                generation,
                request.GenerationWorkerFence,
                cancellationToken).ConfigureAwait(false);
        }

        if (prior is null
            && !await ReserveOperationAsync(receiptId, fingerprint, cancellationToken).ConfigureAwait(false))
        {
            return new(
                MaterializationTargetOperationDisposition.IdentityConflict,
                await SnapshotAsync(target.Value, cancellationToken).ConfigureAwait(false),
                receipt: null);
        }

        if (prior is not null)
        {
            return new(
                prior.Value.RequestFingerprint == fingerprint
                    ? MaterializationTargetOperationDisposition.Replayed
                    : MaterializationTargetOperationDisposition.IdentityConflict,
                await SnapshotAsync(target.Value, cancellationToken).ConfigureAwait(false),
                prior.Value.RequestFingerprint == fingerprint ? prior.Value.Receipt : null);
        }

        if (pointerFenceWasStale || generationFenceWasStale)
        {
            return new(
                MaterializationTargetOperationDisposition.StaleFence,
                await SnapshotAsync(target.Value, cancellationToken).ConfigureAwait(false),
                receipt: null);
        }
        if (generation is null || !generation.Value.Retained)
        {
            return new(
                MaterializationTargetOperationDisposition.NotFound,
                await SnapshotAsync(target.Value, cancellationToken).ConfigureAwait(false),
                receipt: null);
        }
        if (target.Value.LatestPromotionAtUtc is { } latestPromotion
            && request.PromotedAtUtc < latestPromotion)
        {
            throw new ArgumentException(
                "A promotion time cannot predate the latest target-pointer promotion.",
                nameof(request));
        }
        if (generation.Value.ValidationReceipt is { } validation
            && request.PromotedAtUtc < validation.ValidatedAtUtc)
        {
            throw new ArgumentException("A promotion time cannot predate successful validation.", nameof(request));
        }
        if (target.Value.Revision != request.ExpectedTargetRevision)
        {
            return new(
                MaterializationTargetOperationDisposition.RevisionConflict,
                await SnapshotAsync(target.Value, cancellationToken).ConfigureAwait(false),
                receipt: null);
        }
        if (target.Value.ActiveGenerationId != request.ExpectedActiveGenerationId)
        {
            return new(
                MaterializationTargetOperationDisposition.ActiveGenerationConflict,
                await SnapshotAsync(target.Value, cancellationToken).ConfigureAwait(false),
                receipt: null);
        }
        if (generation.Value.Revision != request.ExpectedGenerationRevision)
        {
            return new(
                MaterializationTargetOperationDisposition.RevisionConflict,
                await SnapshotAsync(target.Value, cancellationToken).ConfigureAwait(false),
                receipt: null);
        }
        if (generation.Value.State != MaterializationGenerationState.Validated
            || generation.Value.ValidationReceipt is not { Validation.IsValid: true }
            || generation.Value.PendingValidation is not null)
        {
            return new(
                MaterializationTargetOperationDisposition.StateConflict,
                await SnapshotAsync(target.Value, cancellationToken).ConfigureAwait(false),
                receipt: null);
        }
        if (generation.Value.ValidationReceipt.Fingerprint != request.ValidationFingerprint
            || generation.Value.HasPermanentFailures)
        {
            return new(
                MaterializationTargetOperationDisposition.ValidationFailed,
                await SnapshotAsync(target.Value, cancellationToken).ConfigureAwait(false),
                receipt: null);
        }

        var nextTargetRevision = new MaterializationTargetRevision(
            checked(target.Value.Revision.Ordinal + 1).ToString(CultureInfo.InvariantCulture));
        MaterializationPromotionReceipt receipt = new(
            request.PromotionId,
            Descriptor.Id,
            request.GenerationId,
            target.Value.ActiveGenerationId,
            nextTargetRevision,
            request.GenerationWorkerFence,
            request.PromotionFence,
            request.ValidationFingerprint,
            request.PromotedAtUtc);
        var nextMarker = MarkerAlias(nextTargetRevision, request.PromotionFence);
        var expectedReadIndex = target.Value.ActiveGenerationId is { } previousId
            ? binding.GetGenerationIndexName(previousId)
            : null;
        PendingPromotion pending = new(
            fingerprint,
            receipt,
            target.Value.MarkerAlias,
            nextMarker,
            expectedReadIndex,
            generation.Value.IndexName);
        target = await ReplaceControlAsync(
            target,
            target.Value with { PendingPromotion = pending },
            cancellationToken).ConfigureAwait(false);
        target = await ReconcileTargetAsync(cancellationToken).ConfigureAwait(false);
        return new(
            MaterializationTargetOperationDisposition.Applied,
            await SnapshotAsync(target.Value, cancellationToken).ConfigureAwait(false),
            receipt);
    }

    async ValueTask<MaterializationGenerationOperationResult> RetireGenerationCoreAsync(
        MaterializationRetireGenerationRequest request,
        CancellationToken cancellationToken)
    {
        var target = await LoadTargetAsync(cancellationToken).ConfigureAwait(false);
        var fingerprint = MaterializationTargetIntentFingerprinter.Compute(request);
        var receiptId = OperationDocumentId(RetirementReceiptDocumentKind, request.RetirementId.Value);
        var prior = await ReadControlAsync<RetirementReceiptState>(receiptId, cancellationToken).ConfigureAwait(false);
        var generation = await ReadGenerationAsync(request.GenerationId, cancellationToken).ConfigureAwait(false);
        if (prior is not null)
        {
            if (generation is not null)
            {
                generation = await AcceptGenerationFenceAsync(generation, request.WorkerFence, cancellationToken)
                    .ConfigureAwait(false);
            }
            return new(
                prior.Value.RequestFingerprint == fingerprint
                    ? MaterializationTargetOperationDisposition.Replayed
                    : MaterializationTargetOperationDisposition.IdentityConflict,
                prior.Value.RequestFingerprint == fingerprint
                    ? generation is { Value.Retained: true }
                        ? await SnapshotAsync(generation.Value, cancellationToken).ConfigureAwait(false)
                        : WithLatestFence(prior.Value.Generation, generation)
                    : generation is { Value.Retained: true }
                        ? await SnapshotAsync(generation.Value, cancellationToken).ConfigureAwait(false)
                        : null);
        }
        if (!await ReserveOperationAsync(receiptId, fingerprint, cancellationToken).ConfigureAwait(false))
        {
            if (generation is not null)
            {
                generation = await AcceptGenerationFenceAsync(
                    generation,
                    request.WorkerFence,
                    cancellationToken).ConfigureAwait(false);
            }
            return new(
                MaterializationTargetOperationDisposition.IdentityConflict,
                generation is { Value.Retained: true }
                    ? await SnapshotAsync(generation.Value, cancellationToken).ConfigureAwait(false)
                    : null);
        }
        if (generation is null || !generation.Value.Retained)
        {
            return new(MaterializationTargetOperationDisposition.NotFound, generation: null);
        }
        if (request.WorkerFence.Ordinal < generation.Value.LatestWorkerFence.Ordinal)
        {
            return new(
                MaterializationTargetOperationDisposition.StaleFence,
                await SnapshotAsync(generation.Value, cancellationToken).ConfigureAwait(false));
        }
        generation = await AcceptGenerationFenceAsync(generation, request.WorkerFence, cancellationToken)
            .ConfigureAwait(false);
        if (generation.Value.LastRetirement is { } completed
            && completed.RetirementId == request.RetirementId)
        {
            if (completed.RequestFingerprint != fingerprint)
            {
                return new(
                    MaterializationTargetOperationDisposition.IdentityConflict,
                    await SnapshotAsync(generation.Value, cancellationToken).ConfigureAwait(false));
            }
            var completedSnapshot = await SnapshotAsync(generation.Value, cancellationToken).ConfigureAwait(false);
            await EnsureRetirementReceiptAsync(
                receiptId,
                request.RetirementId,
                fingerprint,
                completedSnapshot,
                cancellationToken).ConfigureAwait(false);
            return new(MaterializationTargetOperationDisposition.Replayed, completedSnapshot);
        }

        var latestEvidenceAtUtc = generation.Value.InactivatedAtUtc
            ?? generation.Value.ValidationReceipt?.ValidatedAtUtc
            ?? generation.Value.SealReceipt?.SealedAtUtc
            ?? generation.Value.CreatedAtUtc;
        if (request.RetiredAtUtc < latestEvidenceAtUtc)
        {
            throw new ArgumentException(
                "A retirement time cannot predate the generation's latest lifecycle evidence.",
                nameof(request));
        }
        if (target.Value.ActiveGenerationId == request.GenerationId
            || generation.Value.State == MaterializationGenerationState.Active)
        {
            return new(
                MaterializationTargetOperationDisposition.ActiveGenerationConflict,
                await SnapshotAsync(generation.Value, cancellationToken).ConfigureAwait(false));
        }
        if (generation.Value.Revision != request.ExpectedRevision)
        {
            return new(
                MaterializationTargetOperationDisposition.RevisionConflict,
                await SnapshotAsync(generation.Value, cancellationToken).ConfigureAwait(false));
        }
        if (generation.Value.State == MaterializationGenerationState.Retired)
        {
            return new(
                MaterializationTargetOperationDisposition.StateConflict,
                await SnapshotAsync(generation.Value, cancellationToken).ConfigureAwait(false));
        }
        if (generation.Value.PendingBatch is not null
            || generation.Value.PendingSeal is not null
            || generation.Value.PendingValidation is not null)
        {
            return new(
                MaterializationTargetOperationDisposition.StateConflict,
                await SnapshotAsync(generation.Value, cancellationToken).ConfigureAwait(false));
        }

        var retiredState = generation.Value with
        {
            State = MaterializationGenerationState.Retired,
            Revision = Next(generation.Value.Revision),
            RetiredAtUtc = request.RetiredAtUtc,
            LastRetirement = new(request.RetirementId, fingerprint)
        };
        var snapshot = await SnapshotAsync(retiredState, cancellationToken).ConfigureAwait(false);
        generation = await ReplaceControlAsync(
            generation,
            retiredState,
            cancellationToken).ConfigureAwait(false);
        await EnsureRetirementReceiptAsync(
            receiptId,
            request.RetirementId,
            fingerprint,
            snapshot,
            cancellationToken).ConfigureAwait(false);
        return new(MaterializationTargetOperationDisposition.Applied, snapshot);
    }

    async ValueTask EnsureRetirementReceiptAsync(
        string receiptId,
        MaterializationRetirementId retirementId,
        MaterializationTargetIntentFingerprint requestFingerprint,
        MaterializationGenerationSnapshot generation,
        CancellationToken cancellationToken)
    {
        RetirementReceiptState receipt = new(
            StateFormatVersion,
            RetirementReceiptDocumentKind,
            retirementId,
            requestFingerprint,
            generation);
        var created = await CreateControlAsync(receiptId, receipt, cancellationToken).ConfigureAwait(false);
        if (created is not null)
        {
            return;
        }

        var retained = await ReadControlAsync<RetirementReceiptState>(receiptId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("A concurrent Elasticsearch retirement receipt is unavailable.");
        if (retained.Value != receipt)
        {
            throw new InvalidOperationException(
                "The Elasticsearch retirement identity conflicts with its durable lifecycle receipt.");
        }
    }

    async ValueTask<MaterializationCleanupResult> CleanupGenerationCoreAsync(
        MaterializationCleanupGenerationRequest request,
        CancellationToken cancellationToken)
    {
        var target = await LoadTargetAsync(cancellationToken).ConfigureAwait(false);
        var fingerprint = MaterializationTargetIntentFingerprinter.Compute(request);
        var receiptId = OperationDocumentId(CleanupReceiptDocumentKind, request.CleanupId.Value);
        var prior = await ReadControlAsync<CleanupReceiptState>(receiptId, cancellationToken).ConfigureAwait(false);
        var generation = await ReadGenerationAsync(request.GenerationId, cancellationToken).ConfigureAwait(false);
        if (prior is not null)
        {
            if (generation is not null)
            {
                _ = await AcceptGenerationFenceAsync(generation, request.WorkerFence, cancellationToken)
                    .ConfigureAwait(false);
            }
            target = await LoadTargetAsync(cancellationToken).ConfigureAwait(false);
            return new(
                prior.Value.RequestFingerprint == fingerprint
                    ? MaterializationTargetOperationDisposition.Replayed
                    : MaterializationTargetOperationDisposition.IdentityConflict,
                await SnapshotAsync(target.Value, cancellationToken).ConfigureAwait(false),
                wasRemoved: false);
        }
        if (!await ReserveOperationAsync(receiptId, fingerprint, cancellationToken).ConfigureAwait(false))
        {
            if (generation is not null)
            {
                generation = await AcceptGenerationFenceAsync(
                    generation,
                    request.WorkerFence,
                    cancellationToken).ConfigureAwait(false);
            }
            return new(
                MaterializationTargetOperationDisposition.IdentityConflict,
                await SnapshotAsync(target.Value, cancellationToken).ConfigureAwait(false),
                wasRemoved: false);
        }
        if (generation is null)
        {
            return new(
                MaterializationTargetOperationDisposition.NotFound,
                await SnapshotAsync(target.Value, cancellationToken).ConfigureAwait(false),
                wasRemoved: false);
        }
        if (request.WorkerFence.Ordinal < generation.Value.LatestWorkerFence.Ordinal)
        {
            return new(
                MaterializationTargetOperationDisposition.StaleFence,
                await SnapshotAsync(target.Value, cancellationToken).ConfigureAwait(false),
                wasRemoved: false);
        }
        generation = await AcceptGenerationFenceAsync(generation, request.WorkerFence, cancellationToken)
            .ConfigureAwait(false);
        if (generation.Value.LastCleanup is { } completed
            && completed.CleanupId == request.CleanupId)
        {
            if (completed.RequestFingerprint != fingerprint)
            {
                return new(
                    MaterializationTargetOperationDisposition.IdentityConflict,
                    await SnapshotAsync(target.Value, cancellationToken).ConfigureAwait(false),
                    wasRemoved: false);
            }
            await EnsureCleanupReceiptAsync(
                receiptId,
                request.CleanupId,
                fingerprint,
                cancellationToken).ConfigureAwait(false);
            return new(
                MaterializationTargetOperationDisposition.Replayed,
                await SnapshotAsync(target.Value, cancellationToken).ConfigureAwait(false),
                wasRemoved: false);
        }
        if (!generation.Value.Retained)
        {
            return new(
                MaterializationTargetOperationDisposition.NotFound,
                await SnapshotAsync(target.Value, cancellationToken).ConfigureAwait(false),
                wasRemoved: false);
        }
        if (request.CleanedAtUtc < (generation.Value.RetiredAtUtc ?? generation.Value.CreatedAtUtc))
        {
            throw new ArgumentException(
                "A cleanup time cannot predate generation retirement.",
                nameof(request));
        }
        if (target.Value.ActiveGenerationId == request.GenerationId
            || generation.Value.State == MaterializationGenerationState.Active)
        {
            return new(
                MaterializationTargetOperationDisposition.ActiveGenerationConflict,
                await SnapshotAsync(target.Value, cancellationToken).ConfigureAwait(false),
                wasRemoved: false);
        }
        if (generation.Value.Revision != request.ExpectedRevision)
        {
            return new(
                MaterializationTargetOperationDisposition.RevisionConflict,
                await SnapshotAsync(target.Value, cancellationToken).ConfigureAwait(false),
                wasRemoved: false);
        }
        if (generation.Value.State != MaterializationGenerationState.Retired)
        {
            return new(
                MaterializationTargetOperationDisposition.StateConflict,
                await SnapshotAsync(target.Value, cancellationToken).ConfigureAwait(false),
                wasRemoved: false);
        }

        if (generation.Value.LastRetirement is not { } completedRetirement)
        {
            throw new InvalidOperationException(
                "A retired Elasticsearch generation has no durable retirement completion evidence.");
        }
        var retirementSnapshot = await SnapshotAsync(generation.Value, cancellationToken).ConfigureAwait(false);
        await EnsureRetirementReceiptAsync(
            OperationDocumentId(RetirementReceiptDocumentKind, completedRetirement.RetirementId.Value),
            completedRetirement.RetirementId,
            completedRetirement.RequestFingerprint,
            retirementSnapshot,
            cancellationToken).ConfigureAwait(false);

        var ownsPhysicalIndex = await HasExactGenerationOwnershipAsync(
            generation.Value,
            cancellationToken).ConfigureAwait(false);
        if (!ownsPhysicalIndex
            && generation.Value.IsProvisioned
            && await transport.IndexExistsAsync(
                generation.Value.IndexName,
                policy.MaximumDiagnosticBytes,
                cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "The provisioned Elasticsearch generation index no longer carries exact Cohesive ownership evidence.");
        }

        if (ownsPhysicalIndex)
        {
            var ownerAlias = GenerationOwnerAlias(
                generation.Value.GenerationId,
                generation.Value.BeginFingerprint);
            var deleted = await transport.DeleteOwnedIndexAsync(
                generation.Value.IndexName,
                ownerAlias,
                policy.MaximumDiagnosticBytes,
                cancellationToken).ConfigureAwait(false);
            if (deleted.Disposition == ElasticOwnedIndexDeleteDisposition.OwnershipConflict)
            {
                var indexStillExists = await transport.IndexExistsAsync(
                    generation.Value.IndexName,
                    policy.MaximumDiagnosticBytes,
                    cancellationToken).ConfigureAwait(false);
                if (indexStillExists)
                {
                    if (await HasExactGenerationOwnershipAsync(
                            generation.Value,
                            cancellationToken).ConfigureAwait(false))
                    {
                        throw new ElasticMaterializationTransportException(
                            deleted.StatusCode,
                            "cohesive.elasticsearch.ownership.transactionConflict",
                            retryable: true,
                            "Elasticsearch did not complete the atomic owned-index deletion; exact retry is safe.");
                    }
                    throw new InvalidOperationException(
                        "The retired generation index was replaced without exact Cohesive ownership evidence.");
                }
            }
            else if (!deleted.Acknowledged)
            {
                throw new InvalidOperationException(
                    "Elasticsearch did not acknowledge removal of the retired generation index.");
            }
        }
        generation = await ReplaceControlAsync(
            generation,
            generation.Value with
            {
                Retained = false,
                LastCleanup = new(request.CleanupId, fingerprint)
            },
            cancellationToken).ConfigureAwait(false);
        await EnsureCleanupReceiptAsync(
            receiptId,
            request.CleanupId,
            fingerprint,
            cancellationToken).ConfigureAwait(false);
        target = await LoadTargetAsync(cancellationToken).ConfigureAwait(false);
        return new(
            MaterializationTargetOperationDisposition.Applied,
            await SnapshotAsync(target.Value, cancellationToken).ConfigureAwait(false),
            wasRemoved: true);
    }

    async ValueTask EnsureCleanupReceiptAsync(
        string receiptId,
        MaterializationCleanupId cleanupId,
        MaterializationTargetIntentFingerprint requestFingerprint,
        CancellationToken cancellationToken)
    {
        CleanupReceiptState receipt = new(
            StateFormatVersion,
            CleanupReceiptDocumentKind,
            cleanupId,
            requestFingerprint);
        var created = await CreateControlAsync(receiptId, receipt, cancellationToken).ConfigureAwait(false);
        if (created is not null)
        {
            return;
        }

        var retained = await ReadControlAsync<CleanupReceiptState>(receiptId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("A concurrent Elasticsearch cleanup receipt is unavailable.");
        if (retained.Value != receipt)
        {
            throw new InvalidOperationException(
                "The Elasticsearch cleanup identity conflicts with its durable lifecycle receipt.");
        }
    }

    async ValueTask<bool> HasExactGenerationOwnershipAsync(
        GenerationState generation,
        CancellationToken cancellationToken)
    {
        var ownerAlias = GenerationOwnerAlias(generation.GenerationId, generation.BeginFingerprint);
        var aliases = await transport.InspectAliasesAsync(
            [ownerAlias],
            policy.MaximumDiagnosticBytes,
            cancellationToken).ConfigureAwait(false);
        if (aliases.Bindings.IsDefaultOrEmpty)
        {
            return false;
        }

        if (aliases.Bindings is
            [
                {
                    Alias: var alias,
                    Index: var index,
                    IsHidden: true,
                    IsWriteIndex: null,
                    Routing: null,
                    SearchRouting: null,
                    IndexRouting: null,
                    Filter.Length: 0
                }
            ]
            && alias == ownerAlias
            && index == generation.IndexName)
        {
            return true;
        }

        throw new InvalidOperationException(
            "The Elasticsearch generation ownership alias conflicts with durable Cohesive generation state.");
    }

    async ValueTask RequireExactGenerationOwnershipAsync(
        GenerationState generation,
        CancellationToken cancellationToken)
    {
        if (!await HasExactGenerationOwnershipAsync(generation, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "The Elasticsearch generation index does not carry exact Cohesive ownership evidence.");
        }
    }

    async ValueTask<Stored<TargetState>> LoadTargetAsync(CancellationToken cancellationToken)
    {
        await EnsureControlIndexAsync(cancellationToken).ConfigureAwait(false);
        var target = await ReadControlAsync<TargetState>(TargetDocumentId(), cancellationToken).ConfigureAwait(false);
        if (target is null)
        {
            await RequireInitialControlOwnershipAsync(cancellationToken).ConfigureAwait(false);
            TargetState initial = new(
                StateFormatVersion,
                TargetDocumentKind,
                binding.Fingerprint.Value,
                Descriptor.Id,
                Descriptor.MaterializationId,
                MaterializationTargetRevision.Initial,
                ActiveGenerationId: null,
                LatestPromotionFence: null,
                LatestPromotionAtUtc: null,
                MarkerAlias(MaterializationTargetRevision.Initial, promotionFence: null),
                PendingFence: null,
                PendingPromotion: null,
                LastPromotionReceipt: null);
            target = await CreateControlAsync(TargetDocumentId(), initial, cancellationToken).ConfigureAwait(false)
                ?? await ReadControlAsync<TargetState>(TargetDocumentId(), cancellationToken).ConfigureAwait(false);
        }

        if (target is null
            || target.Value.FormatVersion != StateFormatVersion
            || target.Value.DocumentKind != TargetDocumentKind
            || target.Value.BindingFingerprint != binding.Fingerprint.Value
            || target.Value.TargetId != Descriptor.Id
            || target.Value.MaterializationId != Descriptor.MaterializationId)
        {
            throw new InvalidOperationException(
                "The Elasticsearch control index contains incompatible materialization target state.");
        }

        await RequireTargetPublicationEvidenceAsync(target.Value, cancellationToken).ConfigureAwait(false);

        return await ReconcileTargetAsync(cancellationToken).ConfigureAwait(false);
    }

    async ValueTask RequireTargetPublicationEvidenceAsync(
        TargetState target,
        CancellationToken cancellationToken)
    {
        var nextMarkerAlias = target.PendingFence?.NextMarkerAlias
            ?? target.PendingPromotion?.NextMarkerAlias;
        ImmutableArray<string> aliases = nextMarkerAlias is null
            ? [target.MarkerAlias, binding.ReadAlias]
            : [target.MarkerAlias, nextMarkerAlias, binding.ReadAlias];
        var snapshot = await transport.InspectAliasesAsync(
            aliases,
            policy.MaximumDiagnosticBytes,
            cancellationToken).ConfigureAwait(false);
        var markerBindings = snapshot.Bindings
            .Where(alias => alias.Alias == target.MarkerAlias || alias.Alias == nextMarkerAlias)
            .ToArray();
        if (markerBindings is not [var marker]
            || marker.Index != binding.ControlIndexName
            || marker.IsHidden is not true
            || marker.IsWriteIndex is not null
            || marker.Routing is not null
            || marker.SearchRouting is not null
            || marker.IndexRouting is not null
            || marker.Filter.Length != 0
            || marker.Alias != target.MarkerAlias && marker.Alias != nextMarkerAlias)
        {
            throw new InvalidOperationException(
                "The Elasticsearch control index does not carry exact recoverable marker ownership evidence.");
        }

        var expectedReadFilter = VisibleCountQuery;
        bool readValid;
        if (target.PendingPromotion is { } pending)
        {
            var oldPublication = pending.ExpectedReadIndex is null
                ? !snapshot.Bindings.Any(alias => alias.Alias == binding.ReadAlias)
                : IsExactReadAliasPublication(
                    snapshot,
                    binding.ReadAlias,
                    pending.ExpectedReadIndex,
                    expectedReadFilter);
            var nextPublication = IsExactReadAliasPublication(
                snapshot,
                binding.ReadAlias,
                pending.NextReadIndex,
                expectedReadFilter);
            readValid = oldPublication || nextPublication;
        }
        else if (target.ActiveGenerationId is { } active)
        {
            readValid = IsExactReadAliasPublication(
                snapshot,
                binding.ReadAlias,
                binding.GetGenerationIndexName(active),
                expectedReadFilter);
        }
        else
        {
            readValid = !snapshot.Bindings.Any(alias => alias.Alias == binding.ReadAlias);
        }

        if (!readValid)
        {
            throw new InvalidOperationException(
                "The Elasticsearch stable read alias conflicts with durable materialization target state.");
        }

        if (target.ActiveGenerationId is { } activeGenerationId)
        {
            await RequireRetainedGenerationOwnershipAsync(
                activeGenerationId,
                "active",
                cancellationToken).ConfigureAwait(false);
        }
        if (target.PendingPromotion is { Receipt.GenerationId: var candidateGenerationId }
            && candidateGenerationId != target.ActiveGenerationId)
        {
            await RequireRetainedGenerationOwnershipAsync(
                candidateGenerationId,
                "pending promotion candidate",
                cancellationToken).ConfigureAwait(false);
        }
    }

    async ValueTask RequireRetainedGenerationOwnershipAsync(
        MaterializationGenerationId generationId,
        string role,
        CancellationToken cancellationToken)
    {
        var generation = await ReadGenerationAsync(generationId, cancellationToken).ConfigureAwait(false);
        if (generation is not { Value.Retained: true, Value.IsProvisioned: true })
        {
            throw new InvalidOperationException(
                $"The Elasticsearch {role} generation is absent or lacks durable provisioning evidence.");
        }
        await RequireExactGenerationOwnershipAsync(generation.Value, cancellationToken).ConfigureAwait(false);
    }

    async ValueTask RequireInitialControlOwnershipAsync(CancellationToken cancellationToken)
    {
        var initialMarker = MarkerAlias(MaterializationTargetRevision.Initial, promotionFence: null);
        var aliases = await transport.InspectAliasesAsync(
            [initialMarker],
            policy.MaximumDiagnosticBytes,
            cancellationToken).ConfigureAwait(false);
        if (aliases.Bindings is not
            [
                {
                    Alias: var alias,
                    Index: var index,
                    IsHidden: true,
                    IsWriteIndex: null,
                    Routing: null,
                    SearchRouting: null,
                    IndexRouting: null,
                    Filter.Length: 0
                }
            ]
            || alias != initialMarker
            || index != binding.ControlIndexName)
        {
            throw new InvalidOperationException(
                "The Elasticsearch control index does not carry exact Cohesive ownership evidence.");
        }
    }

    async ValueTask EnsureControlIndexAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref controlIndexReady) != 0)
        {
            return;
        }

        await controlInitialization.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref controlIndexReady) != 0)
            {
                return;
            }

            var created = await transport.CreateIndexAsync(
                binding.ControlIndexName,
                CreateControlIndexBody(),
                policy.MaximumDiagnosticBytes,
                cancellationToken).ConfigureAwait(false);
            if (created.Disposition == ElasticIndexCreateDisposition.Created
                && (!created.Acknowledged || !created.ShardsAcknowledged))
            {
                throw new InvalidOperationException(
                    "Elasticsearch did not acknowledge a usable materialization control index.");
            }
            Volatile.Write(ref controlIndexReady, 1);
        }
        finally
        {
            controlInitialization.Release();
        }
    }

    async ValueTask<Stored<TargetState>> ReconcileTargetAsync(CancellationToken cancellationToken)
    {
        await targetReconciliation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await ReadControlAsync<TargetState>(TargetDocumentId(), cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    "The Elasticsearch materialization target state became unavailable during reconciliation.");
            return await ReconcileTargetExclusiveAsync(current, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            targetReconciliation.Release();
        }
    }

    async ValueTask<Stored<TargetState>> ReconcileTargetExclusiveAsync(
        Stored<TargetState> target,
        CancellationToken cancellationToken)
    {
        if (target.Value.PendingFence is { } pendingFence)
        {
            await CompleteMarkerExchangeAsync(
                pendingFence.ExpectedMarkerAlias,
                pendingFence.NextMarkerAlias,
                readAlias: null,
                expectedReadIndex: null,
                nextReadIndex: null,
                expectedNextOwnerAlias: null,
                cancellationToken).ConfigureAwait(false);
            target = await ReplaceControlAsync(
                target,
                target.Value with
                {
                    LatestPromotionFence = pendingFence.Fence,
                    MarkerAlias = pendingFence.NextMarkerAlias,
                    PendingFence = null
                },
                cancellationToken).ConfigureAwait(false);
        }

        if (target.Value.PendingPromotion is { } pendingPromotion)
        {
            target = await CompletePromotionAsync(target, pendingPromotion, cancellationToken).ConfigureAwait(false);
        }

        return target;
    }

    async ValueTask<Stored<TargetState>> CompletePromotionAsync(
        Stored<TargetState> target,
        PendingPromotion pending,
        CancellationToken cancellationToken)
    {
        var candidate = await RequireGenerationAsync(pending.Receipt.GenerationId, cancellationToken)
            .ConfigureAwait(false);
        if (!candidate.Value.IsProvisioned
            || candidate.Value.ValidationReceipt is not { Validation.IsValid: true } validation
            || validation.Fingerprint != pending.Receipt.ValidationFingerprint
            || candidate.Value.HasPermanentFailures
            || candidate.Value.PendingBatch is not null
            || candidate.Value.PendingSeal is not null
            || candidate.Value.PendingValidation is not null
            || candidate.Value.LatestWorkerFence.Ordinal < pending.Receipt.GenerationWorkerFence.Ordinal
            || candidate.Value.State == MaterializationGenerationState.Validated
                && candidate.Value.Revision != validation.GenerationRevision
            || candidate.Value.State == MaterializationGenerationState.Active
                && candidate.Value.Revision.Ordinal != checked(validation.GenerationRevision.Ordinal + 1)
            || candidate.Value.State is not (
                MaterializationGenerationState.Validated or MaterializationGenerationState.Active))
        {
            throw new InvalidOperationException(
                "The pending Elasticsearch promotion candidate no longer matches its validated durable evidence.");
        }
        await RequireExactGenerationOwnershipAsync(candidate.Value, cancellationToken).ConfigureAwait(false);

        Stored<GenerationState>? previous = null;
        if (pending.Receipt.PreviousGenerationId is { } previousId)
        {
            previous = await ReadGenerationAsync(previousId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    "The pending Elasticsearch promotion's previous generation is unavailable.");
            if (previous.Value.State == MaterializationGenerationState.Inactive)
            {
                if (previous.Value.InactivatedAtUtc != pending.Receipt.PromotedAtUtc)
                {
                    throw new InvalidOperationException(
                        "The pending Elasticsearch promotion's prior generation has incompatible inactivation evidence.");
                }
            }
            else if (previous.Value.State != MaterializationGenerationState.Active)
            {
                throw new InvalidOperationException(
                    "The pending Elasticsearch promotion's prior generation is not active or exactly recovered inactive state.");
            }
            await RequireExactGenerationOwnershipAsync(previous.Value, cancellationToken).ConfigureAwait(false);
        }

        await CompleteMarkerExchangeAsync(
            pending.ExpectedMarkerAlias,
            pending.NextMarkerAlias,
            binding.ReadAlias,
            pending.ExpectedReadIndex,
            pending.NextReadIndex,
            GenerationOwnerAlias(candidate.Value.GenerationId, candidate.Value.BeginFingerprint),
            cancellationToken).ConfigureAwait(false);
        var unblocked = await transport.RemoveWriteBlockAsync(
            pending.NextReadIndex,
            policy.MaximumDiagnosticBytes,
            cancellationToken).ConfigureAwait(false);
        if (unblocked.Disposition != ElasticAcknowledgedDisposition.Applied || !unblocked.Acknowledged)
        {
            throw new InvalidOperationException(
                "Elasticsearch did not acknowledge removal of the candidate generation write block.");
        }
        await RequireExactGenerationOwnershipAsync(candidate.Value, cancellationToken).ConfigureAwait(false);
        var publication = await transport.InspectAliasesAsync(
            [binding.ReadAlias],
            policy.MaximumDiagnosticBytes,
            cancellationToken).ConfigureAwait(false);
        if (!IsExactReadAliasPublication(
                publication,
                binding.ReadAlias,
                pending.NextReadIndex,
                VisibleCountQuery))
        {
            throw new InvalidOperationException(
                "The Elasticsearch read alias no longer carries the exact candidate publication.");
        }

        if (candidate.Value.State == MaterializationGenerationState.Validated)
        {
            candidate = await ReplaceControlAsync(
                candidate,
                candidate.Value with
                {
                    State = MaterializationGenerationState.Active,
                    Revision = new MaterializationGenerationRevision(
                        (candidate.Value.Revision.Ordinal + 1).ToString(CultureInfo.InvariantCulture)),
                    InactivatedAtUtc = null
                },
                cancellationToken).ConfigureAwait(false);
        }

        if (previous is { Value.State: MaterializationGenerationState.Active })
        {
            _ = await ReplaceControlAsync(
                previous,
                previous.Value with
                {
                    State = MaterializationGenerationState.Inactive,
                    Revision = new MaterializationGenerationRevision(
                        (previous.Value.Revision.Ordinal + 1).ToString(CultureInfo.InvariantCulture)),
                    InactivatedAtUtc = pending.Receipt.PromotedAtUtc,
                    RetiredAtUtc = null
                },
                cancellationToken).ConfigureAwait(false);
        }

        var promotionReceiptId = OperationDocumentId(
            PromotionReceiptDocumentKind,
            pending.Receipt.PromotionId.Value);
        var retainedPromotionReceipt = await CreateControlAsync(
            promotionReceiptId,
            new PromotionReceiptState(
                StateFormatVersion,
                PromotionReceiptDocumentKind,
                pending.RequestFingerprint,
                pending.Receipt),
            cancellationToken).ConfigureAwait(false);
        if (retainedPromotionReceipt is null)
        {
            var existingReceipt = await ReadControlAsync<PromotionReceiptState>(
                promotionReceiptId,
                cancellationToken).ConfigureAwait(false);
            if (existingReceipt is null
                || existingReceipt.Value.RequestFingerprint != pending.RequestFingerprint
                || existingReceipt.Value.Receipt != pending.Receipt)
            {
                throw new InvalidOperationException(
                    "The Elasticsearch promotion identity conflicts with its durable publication receipt.");
            }
        }

        var finalized = target.Value with
        {
            Revision = pending.Receipt.TargetRevision,
            ActiveGenerationId = pending.Receipt.GenerationId,
            LatestPromotionFence = pending.Receipt.PromotionFence,
            LatestPromotionAtUtc = pending.Receipt.PromotedAtUtc,
            MarkerAlias = pending.NextMarkerAlias,
            PendingPromotion = null,
            LastPromotionReceipt = pending.Receipt
        };
        return await ReplaceControlAsync(target, finalized, cancellationToken).ConfigureAwait(false);
    }

    async ValueTask CompleteMarkerExchangeAsync(
        string expectedMarkerAlias,
        string nextMarkerAlias,
        string? readAlias,
        string? expectedReadIndex,
        string? nextReadIndex,
        string? expectedNextOwnerAlias,
        CancellationToken cancellationToken)
    {
        ElasticAliasCasRequest request = new(
            binding.ControlIndexName,
            expectedMarkerAlias,
            nextMarkerAlias,
            readAlias,
            expectedReadIndex,
            nextReadIndex,
            policy.MaximumDiagnosticBytes,
            readAlias is null ? null : VisibleCountQuery,
            isWriteIndex: readAlias is null ? null : false,
            expectedNextOwnerAlias: expectedNextOwnerAlias);
        var exchanged = await transport.CompareExchangeAliasAsync(request, cancellationToken).ConfigureAwait(false);
        var aliases = await transport.InspectAliasesAsync(
            readAlias is null
                ? [expectedMarkerAlias, nextMarkerAlias]
                : [expectedMarkerAlias, nextMarkerAlias, readAlias],
            policy.MaximumDiagnosticBytes,
            cancellationToken).ConfigureAwait(false);
        var expectedMarkerAbsent = !aliases.Bindings.Any(alias => alias.Alias == expectedMarkerAlias);
        var markerBindings = aliases.Bindings.Where(alias => alias.Alias == nextMarkerAlias).ToArray();
        var markerApplied = markerBindings is
        [
            {
                Index: var markerIndex,
                IsHidden: true,
                IsWriteIndex: null,
                Routing: null,
                SearchRouting: null,
                IndexRouting: null,
                Filter.Length: 0
            }
        ] && markerIndex == binding.ControlIndexName;
        var readApplied = readAlias is null || IsExactReadAliasPublication(
            aliases,
            readAlias,
            nextReadIndex!,
            VisibleCountQuery);
        if (!expectedMarkerAbsent || !markerApplied || !readApplied)
        {
            throw new InvalidOperationException(
                "The Elasticsearch alias marker conflicts with the durable materialization target state.");
        }
    }

    static bool IsExactReadAliasPublication(
        ElasticAliasSnapshot aliases,
        string readAlias,
        string nextReadIndex,
        ElasticJsonObject expectedFilter)
    {
        var readBindings = aliases.Bindings.Where(alias => alias.Alias == readAlias).ToArray();
        return readBindings is
            [
                {
                    Index: var index,
                    IsWriteIndex: false,
                    Routing: null,
                    SearchRouting: null,
                    IndexRouting: null,
                    Filter: var filter
                }
            ]
            && index == nextReadIndex
            && ElasticJsonObject.DeepEquals(filter, expectedFilter.Bytes);
    }

    async ValueTask<Stored<TargetState>> AcceptPromotionFenceAsync(
        Stored<TargetState> target,
        MaterializationPromotionFence requested,
        CancellationToken cancellationToken)
    {
        target = await ReconcileTargetAsync(cancellationToken).ConfigureAwait(false);
        if (target.Value.LatestPromotionFence is { } latest && requested.Ordinal <= latest.Ordinal)
        {
            return target;
        }

        var nextMarker = MarkerAlias(target.Value.Revision, requested);
        PendingFence pending = new(requested, target.Value.MarkerAlias, nextMarker);
        target = await ReplaceControlAsync(
            target,
            target.Value with { PendingFence = pending },
            cancellationToken).ConfigureAwait(false);
        return await ReconcileTargetAsync(cancellationToken).ConfigureAwait(false);
    }

    async ValueTask<Stored<GenerationState>> AcceptGenerationFenceAsync(
        Stored<GenerationState> generation,
        MaterializationWorkerFence requested,
        CancellationToken cancellationToken)
    {
        if (requested.Ordinal <= generation.Value.LatestWorkerFence.Ordinal)
        {
            return generation;
        }

        return await ReplaceControlAsync(
            generation,
            generation.Value with { LatestWorkerFence = requested },
            cancellationToken).ConfigureAwait(false);
    }

    async ValueTask<Stored<GenerationState>> RequireGenerationAsync(
        MaterializationGenerationId generationId,
        CancellationToken cancellationToken) =>
        await ReadGenerationAsync(generationId, cancellationToken).ConfigureAwait(false)
        ?? throw new InvalidOperationException(
            $"Elasticsearch generation '{generationId.Value}' disappeared from its durable control index.");

    ValueTask<Stored<GenerationState>?> ReadGenerationAsync(
        MaterializationGenerationId generationId,
        CancellationToken cancellationToken) =>
        ReadControlAsync<GenerationState>(GenerationDocumentId(generationId), cancellationToken);

    async ValueTask<MaterializationTargetSnapshot> SnapshotAsync(
        TargetState target,
        CancellationToken cancellationToken)
    {
        await RefreshRequiredAsync(binding.ControlIndexName, cancellationToken).ConfigureAwait(false);
        var count = await transport.CountAsync(
            binding.ControlIndexName,
            RetainedGenerationCountQuery,
            policy.MaximumDiagnosticBytes,
            cancellationToken).ConfigureAwait(false);
        return new(
            Descriptor.Id,
            Descriptor.MaterializationId,
            target.Revision,
            target.ActiveGenerationId,
            target.LatestPromotionFence,
            count.Count);
    }

    async ValueTask<MaterializationGenerationSnapshot> SnapshotAsync(
        GenerationState generation,
        CancellationToken cancellationToken)
    {
        await RefreshRequiredAsync(binding.ControlIndexName, cancellationToken).ConfigureAwait(false);
        if (!generation.IsProvisioned)
        {
            return new(
                generation.MaterializationId,
                generation.GenerationId,
                generation.DefinitionFingerprint,
                generation.State,
                generation.Revision,
                generation.LatestWorkerFence,
                generation.HasPermanentFailures,
                pendingRetryableMutationCount: 0,
                visibleItemCount: 0,
                tombstoneCount: 0,
                generation.SealReceipt,
                generation.ValidationReceipt,
                generation.CreatedAtUtc,
                generation.InactivatedAtUtc,
                generation.RetiredAtUtc);
        }

        await RequireExactGenerationOwnershipAsync(generation, cancellationToken).ConfigureAwait(false);

        await RefreshRequiredAsync(generation.IndexName, cancellationToken).ConfigureAwait(false);
        var visible = await transport.CountAsync(
            generation.IndexName,
            VisibleCountQuery,
            policy.MaximumDiagnosticBytes,
            cancellationToken).ConfigureAwait(false);
        var tombstones = await transport.CountAsync(
            generation.IndexName,
            TombstoneCountQuery,
            policy.MaximumDiagnosticBytes,
            cancellationToken).ConfigureAwait(false);
        var pending = await transport.CountAsync(
            binding.ControlIndexName,
            PendingCountQuery(generation.GenerationId),
            policy.MaximumDiagnosticBytes,
            cancellationToken).ConfigureAwait(false);
        return new(
            generation.MaterializationId,
            generation.GenerationId,
            generation.DefinitionFingerprint,
            generation.State,
            generation.Revision,
            generation.LatestWorkerFence,
            generation.HasPermanentFailures,
            pending.Count,
            visible.Count,
            tombstones.Count,
            generation.SealReceipt,
            generation.ValidationReceipt,
            generation.CreatedAtUtc,
            generation.InactivatedAtUtc,
            generation.RetiredAtUtc);
    }

    async ValueTask RefreshRequiredAsync(string index, CancellationToken cancellationToken)
    {
        var refreshed = await transport.RefreshAsync(
            index,
            policy.MaximumDiagnosticBytes,
            cancellationToken).ConfigureAwait(false);
        if (refreshed.Disposition != ElasticAcknowledgedDisposition.Applied || !refreshed.Acknowledged)
        {
            throw new InvalidOperationException(
                $"Elasticsearch did not acknowledge refresh of materialization index '{index}'.");
        }
    }

    async ValueTask<Stored<T>?> ReadControlAsync<T>(string id, CancellationToken cancellationToken)
        where T : class
    {
        var result = await transport.GetDocumentAsync(
            binding.ControlIndexName,
            id,
            MaximumControlResponseBytes(),
            cancellationToken).ConfigureAwait(false);
        if (!result.Found)
        {
            return null;
        }

        if (result.ConcurrencyToken is not { } token)
        {
            throw new InvalidOperationException("An Elasticsearch control document omitted its concurrency token.");
        }

        try
        {
            var value = JsonSerializer.Deserialize<T>(result.Source, JsonOptions)
                ?? throw new JsonException("The Elasticsearch control document deserialized to null.");
            ValidateControlValue(id, value);
            return new(value, token);
        }
        catch (Exception exception) when (exception is JsonException
            or NotSupportedException
            or ArgumentException
            or OverflowException)
        {
            throw new InvalidOperationException("An Elasticsearch control document violates its adapter schema.", exception);
        }
    }

    void ValidateControlValue<T>(string id, T value)
        where T : class
    {
        switch (value)
        {
            case TargetState target:
                ValidateTargetState(id, target);
                return;
            case GenerationState generation:
                ValidateGenerationState(id, generation);
                return;
            case MutationReceiptState receipt:
                RequireControlEnvelope(receipt.FormatVersion, receipt.DocumentKind, MutationReceiptDocumentKind);
                RequireValidControl(
                    id == MutationDocumentId(receipt.GenerationId, receipt.MutationId)
                    && IsDefined(receipt.GenerationId.Value)
                    && IsDefined(receipt.MutationId.Value)
                    && IsDefined(receipt.MutationFingerprint.Value));
                return;
            case PendingMutationState pending:
                RequireControlEnvelope(pending.FormatVersion, pending.DocumentKind, PendingMutationDocumentKind);
                RequireValidControl(
                    id == PendingMutationDocumentId(pending.GenerationId, pending.MutationId)
                    && IsDefined(pending.GenerationId.Value)
                    && IsDefined(pending.ItemId.Value)
                    && IsDefined(pending.MutationId.Value)
                    && IsDefined(pending.Version.Value)
                    && IsDefined(pending.MutationFingerprint.Value));
                return;
            case OperationReservationState reservation:
                RequireControlEnvelope(
                    reservation.FormatVersion,
                    reservation.DocumentKind,
                    OperationReservationDocumentKind);
                RequireValidControl(
                    IsDefined(reservation.OperationDocumentId)
                    && IsDefined(reservation.RequestFingerprint.Value)
                    && id == OperationReservationDocumentId(reservation.OperationDocumentId));
                return;
            case BatchReceiptState receipt:
                RequireControlEnvelope(receipt.FormatVersion, receipt.DocumentKind, BatchReceiptDocumentKind);
                RequireValidControl(
                    receipt.Result is not null
                    && id == OperationDocumentId(BatchReceiptDocumentKind, receipt.Result.BatchId.Value)
                    && IsDefined(receipt.RequestFingerprint.Value));
                return;
            case SealReceiptState receipt:
                RequireControlEnvelope(receipt.FormatVersion, receipt.DocumentKind, SealReceiptDocumentKind);
                RequireValidControl(
                    receipt.Receipt is not null
                    && receipt.Generation is not null
                    && id == OperationDocumentId(SealReceiptDocumentKind, receipt.Receipt.SealId.Value)
                    && receipt.Receipt.GenerationId == receipt.Generation.GenerationId
                    && receipt.Generation.MaterializationId == Descriptor.MaterializationId
                    && receipt.Generation.SealReceipt == receipt.Receipt
                    && IsDefined(receipt.RequestFingerprint.Value));
                return;
            case ValidationReceiptState receipt:
                RequireControlEnvelope(receipt.FormatVersion, receipt.DocumentKind, ValidationReceiptDocumentKind);
                RequireValidControl(
                    receipt.Receipt is not null
                    && receipt.Generation is not null
                    && id == OperationDocumentId(ValidationReceiptDocumentKind, receipt.Receipt.ValidationId.Value)
                    && receipt.Receipt.GenerationId == receipt.Generation.GenerationId
                    && receipt.Generation.MaterializationId == Descriptor.MaterializationId
                    && receipt.Generation.ValidationReceipt == receipt.Receipt
                    && IsDefined(receipt.RequestFingerprint.Value));
                return;
            case PromotionReceiptState receipt:
                RequireControlEnvelope(receipt.FormatVersion, receipt.DocumentKind, PromotionReceiptDocumentKind);
                RequireValidControl(
                    receipt.Receipt is not null
                    && id == OperationDocumentId(PromotionReceiptDocumentKind, receipt.Receipt.PromotionId.Value)
                    && receipt.Receipt.TargetId == Descriptor.Id
                    && IsDefined(receipt.RequestFingerprint.Value));
                return;
            case RetirementReceiptState receipt:
                RequireControlEnvelope(receipt.FormatVersion, receipt.DocumentKind, RetirementReceiptDocumentKind);
                RequireValidControl(
                    receipt.Generation is not null
                    && id == OperationDocumentId(RetirementReceiptDocumentKind, receipt.RetirementId.Value)
                    && receipt.Generation.MaterializationId == Descriptor.MaterializationId
                    && receipt.Generation.State == MaterializationGenerationState.Retired
                    && IsDefined(receipt.RequestFingerprint.Value));
                return;
            case CleanupReceiptState receipt:
                RequireControlEnvelope(receipt.FormatVersion, receipt.DocumentKind, CleanupReceiptDocumentKind);
                RequireValidControl(
                    id == OperationDocumentId(CleanupReceiptDocumentKind, receipt.CleanupId.Value)
                    && IsDefined(receipt.RequestFingerprint.Value));
                return;
            default:
                throw InvalidControlState();
        }
    }

    void ValidateTargetState(string id, TargetState target)
    {
        RequireControlEnvelope(target.FormatVersion, target.DocumentKind, TargetDocumentKind);
        RequireValidControl(
            id == TargetDocumentId()
            && target.BindingFingerprint == binding.Fingerprint.Value
            && target.TargetId == Descriptor.Id
            && target.MaterializationId == Descriptor.MaterializationId
            && IsDefined(target.Revision.Value)
            && target.MarkerAlias == MarkerAlias(target.Revision, target.LatestPromotionFence)
            && IsUtc(target.LatestPromotionAtUtc)
            && !(target.PendingFence is not null && target.PendingPromotion is not null));

        if (target.ActiveGenerationId is null)
        {
            RequireValidControl(
                target.Revision == MaterializationTargetRevision.Initial
                && target.LatestPromotionAtUtc is null
                && target.LastPromotionReceipt is null);
        }
        else
        {
            RequireValidControl(
                IsDefined(target.ActiveGenerationId.Value.Value)
                && target.Revision.Ordinal > 0
                && target.LatestPromotionFence is not null
                && target.LatestPromotionAtUtc is not null
                && target.LastPromotionReceipt is { } last
                && last.TargetId == Descriptor.Id
                && last.GenerationId == target.ActiveGenerationId.Value
                && last.TargetRevision == target.Revision
                && last.PromotionFence.Ordinal <= target.LatestPromotionFence.Value.Ordinal
                && last.PromotedAtUtc == target.LatestPromotionAtUtc.Value);
        }

        if (target.PendingFence is { } pendingFence)
        {
            RequireValidControl(
                IsDefined(pendingFence.Fence.Value)
                && pendingFence.ExpectedMarkerAlias == target.MarkerAlias
                && pendingFence.NextMarkerAlias == MarkerAlias(target.Revision, pendingFence.Fence)
                && (target.LatestPromotionFence is null
                    || pendingFence.Fence.Ordinal > target.LatestPromotionFence.Value.Ordinal));
        }

        if (target.PendingPromotion is { } pendingPromotion)
        {
            var receipt = pendingPromotion.Receipt;
            var expectedPriorIndex = target.ActiveGenerationId is { } active
                ? binding.GetGenerationIndexName(active)
                : null;
            RequireValidControl(
                receipt is not null
                && IsDefined(pendingPromotion.RequestFingerprint.Value)
                && receipt.TargetId == Descriptor.Id
                && receipt.PreviousGenerationId == target.ActiveGenerationId
                && receipt.TargetRevision.Ordinal == checked(target.Revision.Ordinal + 1)
                && target.LatestPromotionFence == receipt.PromotionFence
                && pendingPromotion.ExpectedMarkerAlias == target.MarkerAlias
                && pendingPromotion.NextMarkerAlias == MarkerAlias(receipt.TargetRevision, receipt.PromotionFence)
                && pendingPromotion.ExpectedReadIndex == expectedPriorIndex
                && pendingPromotion.NextReadIndex == binding.GetGenerationIndexName(receipt.GenerationId));
        }
    }

    void ValidateGenerationState(string id, GenerationState generation)
    {
        RequireControlEnvelope(generation.FormatVersion, generation.DocumentKind, GenerationDocumentKind);
        RequireValidControl(
            id == GenerationDocumentId(generation.GenerationId)
            && generation.BindingFingerprint == binding.Fingerprint.Value
            && generation.MaterializationId == Descriptor.MaterializationId
            && IsDefined(generation.GenerationId.Value)
            && generation.DefinitionFingerprint is not null
            && IsDefined(generation.DefinitionFingerprint.Algorithm)
            && IsDefined(generation.DefinitionFingerprint.Canonicalization)
            && IsDefined(generation.DefinitionFingerprint.Value)
            && IsDefined(generation.BeginFingerprint.Value)
            && (generation.IsProvisioned
                || generation.State is MaterializationGenerationState.Loading or MaterializationGenerationState.Retired)
            && generation.IndexName == binding.GetGenerationIndexName(generation.GenerationId)
            && Enum.IsDefined(generation.State)
            && IsDefined(generation.Revision.Value)
            && IsDefined(generation.LatestWorkerFence.Value)
            && IsUtc(generation.CreatedAtUtc)
            && IsUtc(generation.InactivatedAtUtc)
            && IsUtc(generation.RetiredAtUtc)
            && (generation.InactivatedAtUtc is not { } inactivated || inactivated >= generation.CreatedAtUtc));
        RequireValidControl(
            generation.RetiredAtUtc is not { } retired || retired >= (generation.InactivatedAtUtc ?? generation.CreatedAtUtc));

        var pendingCount = (generation.PendingBatch is null ? 0 : 1)
            + (generation.PendingSeal is null ? 0 : 1)
            + (generation.PendingValidation is null ? 0 : 1);
        RequireValidControl(pendingCount <= 1);
        if (!generation.IsProvisioned)
        {
            RequireValidControl(
                generation.SealReceipt is null
                && generation.ValidationReceipt is null
                && generation.PendingBatch is null
                && generation.PendingSeal is null
                && generation.PendingValidation is null);
        }

        ValidateGenerationLifecycle(generation);
        ValidatePendingGenerationOperation(generation);
    }

    static void ValidateGenerationLifecycle(GenerationState generation)
    {
        if (generation.SealReceipt is { } seal)
        {
            RequireValidControl(
                seal.GenerationId == generation.GenerationId
                && seal.GenerationRevision.Ordinal <= generation.Revision.Ordinal
                && seal.SealedAtUtc >= generation.CreatedAtUtc);
        }
        if (generation.ValidationReceipt is { } validation)
        {
            RequireValidControl(
                generation.SealReceipt is { } retainedSeal
                && validation.GenerationId == generation.GenerationId
                && validation.GenerationRevision.Ordinal <= generation.Revision.Ordinal
                && validation.GenerationRevision.Ordinal > retainedSeal.GenerationRevision.Ordinal
                && validation.SealFingerprint == retainedSeal.Fingerprint
                && validation.ValidatedAtUtc >= retainedSeal.SealedAtUtc);
        }

        switch (generation.State)
        {
            case MaterializationGenerationState.Loading:
                RequireValidControl(
                    generation.SealReceipt is null
                    && generation.ValidationReceipt is null
                    && generation.InactivatedAtUtc is null
                    && generation.RetiredAtUtc is null);
                break;
            case MaterializationGenerationState.Sealed:
                RequireValidControl(
                    generation.SealReceipt is not null
                    && generation.ValidationReceipt is not { Validation.IsValid: true }
                    && generation.InactivatedAtUtc is null
                    && generation.RetiredAtUtc is null);
                break;
            case MaterializationGenerationState.Validated:
                RequireValidControl(
                    generation.SealReceipt is not null
                    && generation.ValidationReceipt is { Validation.IsValid: true }
                    && !generation.HasPermanentFailures
                    && generation.InactivatedAtUtc is null
                    && generation.RetiredAtUtc is null);
                break;
            case MaterializationGenerationState.Active:
                RequireValidControl(
                    generation.SealReceipt is not null
                    && generation.ValidationReceipt is { Validation.IsValid: true }
                    && generation.InactivatedAtUtc is null
                    && generation.RetiredAtUtc is null);
                break;
            case MaterializationGenerationState.Inactive:
                RequireValidControl(
                    generation.SealReceipt is not null
                    && generation.ValidationReceipt is { Validation.IsValid: true }
                    && generation.InactivatedAtUtc is not null
                    && generation.RetiredAtUtc is null);
                break;
            case MaterializationGenerationState.Retired:
                RequireValidControl(
                    generation.RetiredAtUtc is not null
                    && generation.LastRetirement is not null);
                break;
            default:
                throw InvalidControlState();
        }

        RequireValidControl(
            generation.Retained
                ? generation.LastCleanup is null
                : generation.State == MaterializationGenerationState.Retired
                    && generation.LastCleanup is not null);
    }

    static void ValidatePendingGenerationOperation(GenerationState generation)
    {
        if (generation.PendingBatch is { } batch)
        {
            RequireValidControl(
                generation.State is MaterializationGenerationState.Loading or MaterializationGenerationState.Active
                && IsDefined(batch.BatchId.Value)
                && IsDefined(batch.RequestFingerprint.Value)
                && IsDefined(batch.WorkerFence.Value)
                && IsDefined(batch.StartedRevision.Value)
                && batch.WorkerFence.Ordinal <= generation.LatestWorkerFence.Ordinal
                && (!batch.IsInitialized
                    ? batch.PreexistingMutationIds.IsEmpty && batch.PreexistingPendingMutationIds.IsEmpty
                    : !batch.PreexistingMutationIds.IsDefault && !batch.PreexistingPendingMutationIds.IsDefault)
                && HasUniqueDefinedValues(batch.PreexistingMutationIds)
                && HasUniqueDefinedValues(batch.PreexistingPendingMutationIds));
            if (batch.Completion is null)
            {
                RequireValidControl(batch.StartedRevision == generation.Revision);
            }
            else
            {
                RequireValidControl(
                    batch.Completion.BatchId == batch.BatchId
                    && batch.Completion.GenerationId == generation.GenerationId
                    && batch.Completion.GenerationRevision == generation.Revision
                    && batch.StartedRevision.Ordinal <= generation.Revision.Ordinal
                    && batch.StartedRevision.Ordinal + 1 >= generation.Revision.Ordinal);
            }
        }

        if (generation.PendingSeal is { } seal)
        {
            RequireValidControl(IsDefined(seal.SealId.Value) && IsDefined(seal.RequestFingerprint.Value));
            if (generation.State == MaterializationGenerationState.Loading)
            {
                RequireValidControl(
                    generation.SealReceipt is null
                    && seal.StartedRevision == generation.Revision);
            }
            else
            {
                RequireValidControl(
                    generation.State == MaterializationGenerationState.Sealed
                    && generation.SealReceipt is { } receipt
                    && receipt.SealId == seal.SealId
                    && seal.StartedRevision.Ordinal + 1 == generation.Revision.Ordinal);
            }
        }

        if (generation.PendingValidation is { } validation)
        {
            RequireValidControl(
                IsDefined(validation.ValidationId.Value)
                && IsDefined(validation.RequestFingerprint.Value)
                && generation.State is MaterializationGenerationState.Sealed or MaterializationGenerationState.Validated);
            if (generation.ValidationReceipt is { } receipt
                && receipt.ValidationId == validation.ValidationId
                && validation.StartedRevision.Ordinal + 1 == generation.Revision.Ordinal)
            {
                return;
            }
            RequireValidControl(
                generation.State == MaterializationGenerationState.Sealed
                && validation.StartedRevision == generation.Revision);
        }
    }

    static bool HasUniqueDefinedValues(ImmutableArray<MaterializationItemMutationId> values)
    {
        if (values.IsDefault)
        {
            return false;
        }
        HashSet<MaterializationItemMutationId> unique = [];
        foreach (var value in values)
        {
            if (!IsDefined(value.Value) || !unique.Add(value))
            {
                return false;
            }
        }
        return true;
    }

    static void RequireControlEnvelope(int formatVersion, string documentKind, string expectedKind) =>
        RequireValidControl(formatVersion == StateFormatVersion && documentKind == expectedKind);

    static void RequireValidControl(bool condition)
    {
        if (!condition)
        {
            throw InvalidControlState();
        }
    }

    static InvalidOperationException InvalidControlState() =>
        new("The Elasticsearch control index contains invalid materialization state.");

    static bool IsDefined(string? value) => !string.IsNullOrWhiteSpace(value);

    static bool IsUtc(DateTimeOffset value) => value.Offset == TimeSpan.Zero;

    static bool IsUtc(DateTimeOffset? value) => value is null || IsUtc(value.Value);

    async ValueTask<Stored<T>?> CreateControlAsync<T>(
        string id,
        T value,
        CancellationToken cancellationToken)
        where T : class
    {
        var result = await transport.CreateDocumentAsync(
            binding.ControlIndexName,
            id,
            SerializeControlDocument(value),
            policy.MaximumDiagnosticBytes,
            cancellationToken).ConfigureAwait(false);
        return result.Disposition switch
        {
            ElasticDocumentWriteDisposition.Applied when result.ConcurrencyToken is { } token => new(value, token),
            ElasticDocumentWriteDisposition.Conflict => null,
            _ => throw new InvalidOperationException("Elasticsearch did not durably create the control document.")
        };
    }

    async ValueTask<bool> ReserveOperationAsync(
        string operationDocumentId,
        MaterializationTargetIntentFingerprint requestFingerprint,
        CancellationToken cancellationToken)
    {
        OperationReservationState reservation = new(
            StateFormatVersion,
            OperationReservationDocumentKind,
            operationDocumentId,
            requestFingerprint);
        var reservationId = OperationReservationDocumentId(operationDocumentId);
        var created = await CreateControlAsync(reservationId, reservation, cancellationToken).ConfigureAwait(false);
        if (created is not null)
        {
            return true;
        }

        var retained = await ReadControlAsync<OperationReservationState>(reservationId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "A concurrent Elasticsearch operation reservation is unavailable.");
        return retained.Value == reservation;
    }

    async ValueTask<Stored<T>> ReplaceControlAsync<T>(
        Stored<T> current,
        T value,
        CancellationToken cancellationToken)
        where T : class
    {
        var id = value switch
        {
            TargetState => TargetDocumentId(),
            GenerationState generation => GenerationDocumentId(generation.GenerationId),
            _ => throw new ArgumentException("Only mutable target and generation state use CAS replacement.", nameof(value))
        };
        var result = await transport.ReplaceDocumentAsync(
            binding.ControlIndexName,
            id,
            SerializeControlDocument(value),
            current.Token,
            policy.MaximumDiagnosticBytes,
            cancellationToken).ConfigureAwait(false);
        if (result.Disposition != ElasticDocumentWriteDisposition.Applied
            || result.ConcurrencyToken is not { } token)
        {
            throw new InvalidOperationException(
                "The external Elasticsearch single-writer authority was violated by a concurrent control mutation.");
        }

        return new(value, token);
    }

    async ValueTask<TResult> ExecuteAsync<TResult>(
        OperationContext context,
        string operation,
        MaterializationGenerationId? generationId,
        Func<CancellationToken, ValueTask<TResult>> body,
        MaterializationGenerationId? relatedGenerationId = null)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        await localAdmission.WaitAsync(context.CancellationToken).ConfigureAwait(false);
        SemaphoreSlim? firstGenerationAdmission = null;
        SemaphoreSlim? secondGenerationAdmission = null;
        var firstGenerationAdmissionAcquired = false;
        var secondGenerationAdmissionAcquired = false;
        try
        {
            if (generationId is { } admittedGeneration)
            {
                var firstIndex = GenerationAdmissionIndex(admittedGeneration);
                var secondIndex = relatedGenerationId is { } related
                    ? GenerationAdmissionIndex(related)
                    : firstIndex;
                if (secondIndex < firstIndex)
                {
                    (firstIndex, secondIndex) = (secondIndex, firstIndex);
                }

                firstGenerationAdmission = generationAdmissions[firstIndex];
                await firstGenerationAdmission.WaitAsync(context.CancellationToken).ConfigureAwait(false);
                firstGenerationAdmissionAcquired = true;
                if (secondIndex != firstIndex)
                {
                    secondGenerationAdmission = generationAdmissions[secondIndex];
                    await secondGenerationAdmission.WaitAsync(context.CancellationToken).ConfigureAwait(false);
                    secondGenerationAdmissionAcquired = true;
                }
            }
        }
        catch
        {
            if (secondGenerationAdmissionAcquired)
                secondGenerationAdmission!.Release();
            if (firstGenerationAdmissionAcquired)
                firstGenerationAdmission!.Release();
            localAdmission.Release();
            throw;
        }
        var started = Stopwatch.GetTimestamp();
        using var activity = ElasticMaterializationTelemetry.Activities.StartActivity(
            ElasticMaterializationTelemetry.OperationActivityName,
            ActivityKind.Client);
        TagList tags = new()
        {
            { ElasticMaterializationTelemetry.TargetIdTagName, Descriptor.Id.Value },
            { ElasticMaterializationTelemetry.MaterializationIdTagName, Descriptor.MaterializationId.Value },
            { ElasticMaterializationTelemetry.OperationTagName, operation },
            { ElasticMaterializationTelemetry.BindingFingerprintTagName, binding.Fingerprint.Value },
            { ElasticMaterializationTelemetry.CapabilityProfileTagName, Descriptor.Capabilities.Id.Value }
        };
        activity?.SetTag(ElasticMaterializationTelemetry.TargetIdTagName, Descriptor.Id.Value);
        activity?.SetTag(ElasticMaterializationTelemetry.MaterializationIdTagName, Descriptor.MaterializationId.Value);
        activity?.SetTag(ElasticMaterializationTelemetry.OperationTagName, operation);
        activity?.SetTag(ElasticMaterializationTelemetry.GenerationIdTagName, generationId?.Value);
        activity?.SetTag(ElasticMaterializationTelemetry.BindingFingerprintTagName, binding.Fingerprint.Value);
        activity?.SetTag(
            ElasticMaterializationTelemetry.CapabilityProfileTagName,
            Descriptor.Capabilities.Id.Value);
        try
        {
            var result = await body(context.CancellationToken).ConfigureAwait(false);
            if (GetDisposition(result) is { } disposition)
            {
                tags.Add(ElasticMaterializationTelemetry.DispositionTagName, disposition);
                activity?.SetTag(ElasticMaterializationTelemetry.DispositionTagName, disposition);
            }
            ElasticMaterializationTelemetry.Operations.Add(1, tags);
            return result;
        }
        catch (Exception exception)
        {
            var failureCode = exception is ElasticMaterializationTransportException transportFailure
                ? transportFailure.ErrorType
                : exception.GetType().Name;
            activity?.SetStatus(ActivityStatusCode.Error, failureCode);
            activity?.SetTag(ElasticMaterializationTelemetry.FailureCodeTagName, failureCode);
            tags.Add(ElasticMaterializationTelemetry.FailureCodeTagName, failureCode);
            if (exception is ElasticMaterializationTransportException providerFailure)
            {
                tags.Add(ElasticMaterializationTelemetry.RetryableTagName, providerFailure.Retryable);
                activity?.SetTag(ElasticMaterializationTelemetry.RetryableTagName, providerFailure.Retryable);
                if (providerFailure.StatusCode is { } statusCode)
                {
                    tags.Add(ElasticMaterializationTelemetry.HttpStatusCodeTagName, statusCode);
                    activity?.SetTag(ElasticMaterializationTelemetry.HttpStatusCodeTagName, statusCode);
                }
            }
            ElasticMaterializationTelemetry.Operations.Add(1, tags);
            throw;
        }
        finally
        {
            ElasticMaterializationTelemetry.OperationDuration.Record(
                Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                tags);
            if (secondGenerationAdmissionAcquired)
            {
                secondGenerationAdmission!.Release();
            }
            if (firstGenerationAdmissionAcquired)
            {
                firstGenerationAdmission!.Release();
            }
            localAdmission.Release();
        }
    }

    int GenerationAdmissionIndex(MaterializationGenerationId generationId)
    {
        var hash = (uint)StringComparer.Ordinal.GetHashCode(generationId.Value);
        return checked((int)(hash % (uint)generationAdmissions.Length));
    }

    static string? GetDisposition<TResult>(TResult result) => result switch
    {
        MaterializationBatchResult batch => batch.Disposition.ToString(),
        MaterializationGenerationOperationResult generation => generation.Disposition.ToString(),
        MaterializationSealResult seal => seal.Disposition.ToString(),
        MaterializationValidationResult validation => validation.Disposition.ToString(),
        MaterializationPromotionResult promotion => promotion.Disposition.ToString(),
        MaterializationCleanupResult cleanup => cleanup.Disposition.ToString(),
        _ => null
    };

    ElasticJsonObject CreateControlIndexBody() => ElasticMaterializationWireJson.CreateControlIndexBody(
        MarkerAlias(MaterializationTargetRevision.Initial, promotionFence: null),
        ElasticMaterializationTargetBinding.MaximumIndexedIdentityCharacters);

    ElasticJsonObject CreateGenerationIndexBody(
        MaterializationGenerationId generationId,
        string ownerAlias) => ElasticMaterializationWireJson.CreateGenerationIndexBody(
            binding.Fingerprint.Value,
            binding.IndexTemplate.Fingerprint.Value,
            generationId.Value,
            ownerAlias,
            ElasticMaterializationTargetBinding.MaximumIndexedIdentityCharacters);

    static ElasticJsonObject PendingCountQuery(MaterializationGenerationId generationId) =>
        ElasticMaterializationWireJson.FilteredQuery(
            PendingMutationCountQuery,
            ElasticMaterializationWireJson.StringTermQuery("generationId", generationId.Value));

    string TargetDocumentId() => DocumentId("target", Descriptor.Id.Value);

    string GenerationDocumentId(MaterializationGenerationId generationId) =>
        DocumentId("generation", generationId.Value);

    string OperationDocumentId(string kind, string operationId) =>
        DocumentId(kind, operationId);

    string OperationReservationDocumentId(string operationDocumentId) =>
        DocumentId(OperationReservationDocumentKind, operationDocumentId);

    string MutationDocumentId(MaterializationGenerationId generationId, MaterializationItemMutationId mutationId) =>
        DocumentId("mutation", generationId.Value, mutationId.Value);

    string PendingMutationDocumentId(
        MaterializationGenerationId generationId,
        MaterializationItemMutationId mutationId) =>
        DocumentId("pending", generationId.Value, mutationId.Value);

    static string DataDocumentId(MaterializationItemId itemId) =>
        DocumentId("item", itemId.Value);

    string MarkerAlias(MaterializationTargetRevision revision, MaterializationPromotionFence? promotionFence)
    {
        var targetHash = binding.Fingerprint.Value[..16];
        var fence = promotionFence?.Value ?? "0";
        return $".cohesive-mat-{targetHash}-r{revision.Value}-f{fence}";
    }

    string GenerationOwnerAlias(
        MaterializationGenerationId generationId,
        MaterializationTargetIntentFingerprint beginFingerprint)
    {
        var canonical = string.Concat(
            binding.Fingerprint.Value,
            "\n",
            generationId.Value,
            "\n",
            beginFingerprint.Value);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        return $".cohesive-mat-owner-{hash}";
    }

    static string DocumentId(string kind, params string[] components)
    {
        StringBuilder canonical = new(256);
        AppendCanonical(canonical, kind);
        foreach (var component in components)
        {
            AppendCanonical(canonical, component);
        }
        return kind + "-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    static void AppendCanonical(StringBuilder builder, string value)
    {
        builder.Append(value.Length.ToString(CultureInfo.InvariantCulture));
        builder.Append(':');
        builder.Append(value);
        builder.Append(';');
    }

    static void RequireDefined(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A materialization identity cannot be default.", parameterName);
        }
    }

    static void RequireIndexedIdentity(string value, string parameterName)
    {
        RequireDefined(value, parameterName);
        if (value.Length > ElasticMaterializationTargetBinding.MaximumIndexedIdentityCharacters)
        {
            throw new ArgumentException(
                $"An Elasticsearch-indexed materialization identity cannot exceed {ElasticMaterializationTargetBinding.MaximumIndexedIdentityCharacters} Unicode characters.",
                parameterName);
        }
    }

    sealed record Stored<T>(T Value, ElasticDocumentConcurrencyToken Token) where T : class;

    sealed record TargetState(
        int FormatVersion,
        string DocumentKind,
        string BindingFingerprint,
        MaterializationTargetId TargetId,
        MaterializationId MaterializationId,
        MaterializationTargetRevision Revision,
        MaterializationGenerationId? ActiveGenerationId,
        MaterializationPromotionFence? LatestPromotionFence,
        DateTimeOffset? LatestPromotionAtUtc,
        string MarkerAlias,
        PendingFence? PendingFence,
        PendingPromotion? PendingPromotion,
        MaterializationPromotionReceipt? LastPromotionReceipt);

    sealed record PendingFence(
        MaterializationPromotionFence Fence,
        string ExpectedMarkerAlias,
        string NextMarkerAlias);

    sealed record PendingPromotion(
        MaterializationTargetIntentFingerprint RequestFingerprint,
        MaterializationPromotionReceipt Receipt,
        string ExpectedMarkerAlias,
        string NextMarkerAlias,
        string? ExpectedReadIndex,
        string NextReadIndex);

    sealed record PendingBatch(
        MaterializationBatchId BatchId,
        MaterializationTargetIntentFingerprint RequestFingerprint,
        MaterializationWorkerFence WorkerFence,
        MaterializationGenerationRevision StartedRevision,
        bool IsInitialized,
        ImmutableArray<MaterializationItemMutationId> PreexistingMutationIds,
        ImmutableArray<MaterializationItemMutationId> PreexistingPendingMutationIds,
        MaterializationBatchResult? Completion);

    sealed record GenerationState(
        int FormatVersion,
        string DocumentKind,
        string BindingFingerprint,
        MaterializationId MaterializationId,
        MaterializationGenerationId GenerationId,
        ExecutionDefinitionFingerprint DefinitionFingerprint,
        MaterializationTargetIntentFingerprint BeginFingerprint,
        bool Retained,
        bool IsProvisioned,
        string IndexName,
        MaterializationGenerationState State,
        MaterializationGenerationRevision Revision,
        MaterializationWorkerFence LatestWorkerFence,
        bool HasPermanentFailures,
        MaterializationSealReceipt? SealReceipt,
        MaterializationValidationReceipt? ValidationReceipt,
        DateTimeOffset CreatedAtUtc,
        DateTimeOffset? InactivatedAtUtc,
        DateTimeOffset? RetiredAtUtc,
        PendingBatch? PendingBatch,
        PendingSeal? PendingSeal,
        PendingValidation? PendingValidation,
        RetirementCompletion? LastRetirement,
        CleanupCompletion? LastCleanup);

    sealed record PendingSeal(
        MaterializationSealId SealId,
        MaterializationTargetIntentFingerprint RequestFingerprint,
        MaterializationGenerationRevision StartedRevision);

    sealed record PendingValidation(
        MaterializationValidationId ValidationId,
        MaterializationTargetIntentFingerprint RequestFingerprint,
        MaterializationGenerationRevision StartedRevision);

    sealed record RetirementCompletion(
        MaterializationRetirementId RetirementId,
        MaterializationTargetIntentFingerprint RequestFingerprint);

    sealed record CleanupCompletion(
        MaterializationCleanupId CleanupId,
        MaterializationTargetIntentFingerprint RequestFingerprint);

    sealed record ItemMetadata(
        string GenerationId,
        string ItemId,
        string MutationId,
        string MutationFingerprint,
        long Version,
        bool Deleted);

    sealed record ItemDocument(
        [property: JsonPropertyName(ElasticMaterializationTargetBinding.MetadataField)] ItemMetadata Metadata,
        [property: JsonPropertyName(ElasticMaterializationTargetBinding.ValueField)] ObservationValue? Value);

    sealed record MutationReceiptState(
        int FormatVersion,
        string DocumentKind,
        MaterializationGenerationId GenerationId,
        MaterializationItemMutationId MutationId,
        MaterializationTargetIntentFingerprint MutationFingerprint);

    sealed record PendingMutationState(
        int FormatVersion,
        string DocumentKind,
        MaterializationGenerationId GenerationId,
        MaterializationItemId ItemId,
        MaterializationItemMutationId MutationId,
        MaterializationItemVersion Version,
        MaterializationTargetIntentFingerprint MutationFingerprint);

    sealed record OperationReservationState(
        int FormatVersion,
        string DocumentKind,
        string OperationDocumentId,
        MaterializationTargetIntentFingerprint RequestFingerprint);

    sealed record BatchReceiptState(
        int FormatVersion,
        string DocumentKind,
        MaterializationTargetIntentFingerprint RequestFingerprint,
        MaterializationBatchResult Result);

    sealed record SealReceiptState(
        int FormatVersion,
        string DocumentKind,
        MaterializationTargetIntentFingerprint RequestFingerprint,
        MaterializationSealReceipt Receipt,
        MaterializationGenerationSnapshot Generation);

    sealed record ValidationReceiptState(
        int FormatVersion,
        string DocumentKind,
        MaterializationTargetIntentFingerprint RequestFingerprint,
        MaterializationValidationReceipt Receipt,
        MaterializationGenerationSnapshot Generation);

    sealed record PromotionReceiptState(
        int FormatVersion,
        string DocumentKind,
        MaterializationTargetIntentFingerprint RequestFingerprint,
        MaterializationPromotionReceipt Receipt);

    sealed record RetirementReceiptState(
        int FormatVersion,
        string DocumentKind,
        MaterializationRetirementId RetirementId,
        MaterializationTargetIntentFingerprint RequestFingerprint,
        MaterializationGenerationSnapshot Generation);

    sealed record CleanupReceiptState(
        int FormatVersion,
        string DocumentKind,
        MaterializationCleanupId CleanupId,
        MaterializationTargetIntentFingerprint RequestFingerprint);

    sealed record SealContent(
        MaterializationSealFingerprint Fingerprint,
        long VisibleItemCount);

    sealed class BatchWork(
        MaterializationItemMutation mutation,
        MaterializationTargetIntentFingerprint fingerprint)
    {
        internal MaterializationItemMutation Mutation { get; } = mutation;

        internal MaterializationTargetIntentFingerprint Fingerprint { get; } = fingerprint;

        internal bool DataAlreadyApplied { get; set; }

        internal bool DataAppliedThisAttempt { get; set; }

        internal bool ReceiptAlreadyApplied { get; set; }

        internal bool PendingAlreadyApplied { get; set; }

        internal bool ReceiptPresentAtBatchStart { get; set; }

        internal bool PendingPresentAtBatchStart { get; set; }

        internal bool VersionConflictResolvesPending { get; set; }

        internal int? DataOperationOrdinal { get; set; }

        internal int? ReceiptOperationOrdinal { get; set; }

        internal MaterializationItemOutcomeDisposition? InitialDisposition { get; set; }

        internal MaterializationItemOutcomeDisposition? FinalDisposition { get; set; }

        internal string? Code { get; set; }

        internal string? Message { get; set; }
    }
}
