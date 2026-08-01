using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace Cohesive.Storage.Materialization;

/// <summary>Terminal semantic outcome of one bounded sequence of target batches.</summary>
internal enum MaterializationTargetWriteDisposition
{
    /// <summary>Every mutation was applied or replayed.</summary>
    Applied = 0,

    /// <summary>At least one mutation cannot fit the caller's canonical byte bound.</summary>
    BoundaryExceeded = 1,

    /// <summary>A stable batch identity was replayed with different canonical content.</summary>
    IdentityConflict = 2,

    /// <summary>A newer target worker fence superseded the writer.</summary>
    StaleFence = 3,

    /// <summary>At least one mutation failed permanently or exhausted its retry budget.</summary>
    Failed = 4
}

/// <summary>Terminal evidence from the shared bounded target writer.</summary>
/// <param name="Disposition">Semantic outcome of the write.</param>
/// <param name="Message">Failure explanation, or <see langword="null"/> on success.</param>
internal readonly record struct MaterializationTargetWriteResult(
    MaterializationTargetWriteDisposition Disposition,
    string? Message);

/// <summary>Currently applied target batch bounds read at one exact batch boundary.</summary>
internal readonly record struct MaterializationTargetBatchOperatingLimits(int MaximumItems, long MaximumBytes)
{
    internal void Validate()
    {
        if (MaximumItems <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaximumItems), MaximumItems, "A target item bound must be positive.");
        if (MaximumBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaximumBytes), MaximumBytes, "A target byte bound must be positive.");
    }
}

/// <summary>
/// Applies target mutations under exact item-and-canonical-byte bounds while preserving retry and idempotency
/// semantics shared by baseline and incremental materialization execution.
/// </summary>
internal static class MaterializationTargetBatchWriter
{
    /// <summary>Applies one immutable mutation sequence as the largest fitting deterministic batches.</summary>
    /// <param name="context">Explicit cancellation and tracing context.</param>
    /// <param name="target">Concrete target receiving the mutations.</param>
    /// <param name="generation">Loading or active generation receiving the mutations.</param>
    /// <param name="workerFence">Current target ownership fence.</param>
    /// <param name="mutations">Ordered mutations to apply.</param>
    /// <param name="resolveLimits">Reads the currently applied bounds at every exact batch boundary.</param>
    /// <param name="maximumAttempts">Maximum target attempts for each selected chunk.</param>
    /// <param name="createBatchId">
    /// Deterministic identity projection from a canonical digest of the exact selected mutation content and each
    /// mutation's zero-based attempt ordinal.
    /// </param>
    /// <param name="afterBulkObservation">
    /// Optional observation invoked after exact result validation and before the outcome is interpreted.
    /// </param>
    /// <param name="acquireAdmission">Optional target-stage admission acquired immediately before each physical request.</param>
    /// <returns>Terminal semantic write evidence.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="context"/>, <paramref name="target"/>, or <paramref name="createBatchId"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="mutations"/> is default.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A supplied operating bound is not positive.</exception>
    /// <exception cref="OperationCanceledException">The operation is cancelled.</exception>
    internal static async ValueTask<MaterializationTargetWriteResult> ApplyAsync(
        OperationContext context,
        IMaterializationTarget target,
        MaterializationGenerationId generation,
        MaterializationWorkerFence workerFence,
        ImmutableArray<MaterializationItemMutation> mutations,
        Func<OperationContext, ValueTask<MaterializationTargetBatchOperatingLimits>> resolveLimits,
        int maximumAttempts,
        Func<string, MaterializationBatchId> createBatchId,
        Func<OperationContext, MaterializationApplyBatchRequest, MaterializationBatchResult, ValueTask>?
            afterBulkObservation = null,
        Func<OperationContext, ValueTask<IAsyncDisposable?>>? acquireAdmission = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(resolveLimits);
        ArgumentNullException.ThrowIfNull(createBatchId);
        MaterializationContract.RequireDefinedIdentity(generation.Value, nameof(generation));
        MaterializationContract.RequireDefinedIdentity(workerFence.Value, nameof(workerFence));
        if (mutations.IsDefault)
            throw new ArgumentException("Target mutations cannot be default.", nameof(mutations));
        if (maximumAttempts <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumAttempts),
                maximumAttempts,
                "A target attempt bound must be positive.");
        }
        context.ThrowIfCancellationRequested();
        if (mutations.IsEmpty)
            return new(MaterializationTargetWriteDisposition.Applied, null);

        List<PendingMutation> pending = new(mutations.Length);
        foreach (var mutation in mutations)
            pending.Add(new(mutation, Attempt: 0));

        while (pending.Count > 0)
        {
            var limits = await resolveLimits(context).ConfigureAwait(false);
            limits.Validate();
            var lowerCount = 1;
            var upperCount = Math.Min(limits.MaximumItems, pending.Count);
            ImmutableArray<PendingMutation>? selected = null;
            while (lowerCount <= upperCount)
            {
                var count = lowerCount + ((upperCount - lowerCount) / 2);
                var candidateBuilder = ImmutableArray.CreateBuilder<PendingMutation>(count);
                var mutationBuilder = ImmutableArray.CreateBuilder<MaterializationItemMutation>(count);
                for (var index = 0; index < count; index++)
                {
                    candidateBuilder.Add(pending[index]);
                    mutationBuilder.Add(pending[index].Mutation);
                }
                var candidate = candidateBuilder.MoveToImmutable();
                var candidateMutations = mutationBuilder.MoveToImmutable();
                var candidateRequest = new MaterializationApplyBatchRequest(
                    batchId: createBatchId(ContentIdentity(candidate)),
                    generationId: generation,
                    workerFence: workerFence,
                    mutations: candidateMutations);
                if (MaterializationTargetIntentFingerprinter.TryAnalyzeBatch(
                        candidateRequest,
                        limits.MaximumBytes,
                        out _,
                        out _))
                {
                    selected = candidate;
                    lowerCount = count + 1;
                }
                else
                {
                    upperCount = count - 1;
                }
            }

            if (selected is null)
            {
                return new(
                    MaterializationTargetWriteDisposition.BoundaryExceeded,
                    $"One output mutation cannot fit the {limits.MaximumBytes}-byte target bound.");
            }

            var batch = selected.Value;
            var batchMutations = ImmutableArray.CreateBuilder<MaterializationItemMutation>(batch.Length);
            foreach (var item in batch)
                batchMutations.Add(item.Mutation);
            var exactMutations = batchMutations.MoveToImmutable();
            var request = new MaterializationApplyBatchRequest(
                batchId: createBatchId(ContentIdentity(batch)),
                generationId: generation,
                workerFence: workerFence,
                mutations: exactMutations);
            var admissionLease = acquireAdmission is null
                ? null
                : await acquireAdmission(context).ConfigureAwait(false);
            MaterializationBatchResult result;
            try
            {
                result = await target.ApplyBatchAsync(context, request).ConfigureAwait(false);
            }
            finally
            {
                if (admissionLease is not null)
                    await admissionLease.DisposeAsync().ConfigureAwait(false);
            }
            try
            {
                result.ValidateAgainst(request);
            }
            catch (ArgumentException exception)
            {
                return new(
                    MaterializationTargetWriteDisposition.Failed,
                    $"The target returned inexact per-item evidence: {exception.Message}");
            }

            if (afterBulkObservation is not null)
                await afterBulkObservation(context, request, result).ConfigureAwait(false);

            if (result.Disposition == MaterializationBatchDisposition.IdentityConflict)
            {
                return new(
                    MaterializationTargetWriteDisposition.IdentityConflict,
                    $"The target found different canonical content for replayed batch '{request.BatchId.Value}'.");
            }
            if (result.Disposition == MaterializationBatchDisposition.StaleFence)
            {
                return new(
                    MaterializationTargetWriteDisposition.StaleFence,
                    "The target rejected a stale generation worker fence.");
            }

            Dictionary<MaterializationItemMutationId, MaterializationItemOutcomeDisposition> outcomes =
                new(result.Outcomes.Length);
            StringBuilder? permanentFailures = null;
            var hasItemIdentityConflict = false;
            foreach (var outcome in result.Outcomes)
            {
                outcomes.Add(outcome.MutationId, outcome.Disposition);
                if (outcome.Disposition == MaterializationItemOutcomeDisposition.IdempotencyConflict)
                    hasItemIdentityConflict = true;
                if (outcome.Disposition is MaterializationItemOutcomeDisposition.Applied
                    or MaterializationItemOutcomeDisposition.Replayed
                    or MaterializationItemOutcomeDisposition.RetryableRejected)
                {
                    continue;
                }

                permanentFailures ??= new();
                if (permanentFailures.Length > 0)
                    permanentFailures.Append(' ');
                permanentFailures.Append(outcome.Code).Append(": ").Append(outcome.Message);
            }
            if (hasItemIdentityConflict)
            {
                return new(
                    MaterializationTargetWriteDisposition.IdentityConflict,
                    "The target found different canonical content for a replayed mutation identity.");
            }
            if (permanentFailures is not null)
                return new(MaterializationTargetWriteDisposition.Failed, permanentFailures.ToString());

            pending.RemoveRange(index: 0, count: batch.Length);
            List<PendingMutation>? failed = null;
            foreach (var item in batch)
            {
                if (outcomes[item.Mutation.MutationId] != MaterializationItemOutcomeDisposition.RetryableRejected)
                    continue;
                if (item.Attempt + 1 >= maximumAttempts)
                {
                    return new(
                        MaterializationTargetWriteDisposition.Failed,
                        "The target retry budget was exhausted for one or more output mutations.");
                }

                failed ??= new(batch.Length);
                failed.Add(item with { Attempt = item.Attempt + 1 });
            }
            if (failed is not null)
                pending.InsertRange(index: 0, failed);
        }

        return new(MaterializationTargetWriteDisposition.Applied, null);

        static string ContentIdentity(ImmutableArray<PendingMutation> batch)
        {
            using MaterializationStableIdentity.DigestBuilder builder = new();
            builder.Append("materialization-target-batch-content/v1");
            foreach (var item in batch)
            {
                var fingerprint = MaterializationTargetIntentFingerprinter.Compute(item.Mutation);
                builder.Append(item.Mutation.MutationId.Value);
                builder.Append(fingerprint.Algorithm);
                builder.Append(fingerprint.Canonicalization);
                builder.Append(fingerprint.Value);
                builder.Append(item.Attempt.ToString(CultureInfo.InvariantCulture));
            }
            return builder.Complete();
        }
    }

    readonly record struct PendingMutation(MaterializationItemMutation Mutation, int Attempt);
}
