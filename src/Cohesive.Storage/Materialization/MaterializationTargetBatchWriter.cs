using System.Collections.Immutable;
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
    /// <param name="maximumBulkItems">Maximum mutations in one target request.</param>
    /// <param name="maximumBulkBytes">Maximum canonical target-intent bytes in one request.</param>
    /// <param name="maximumAttempts">Maximum target attempts for each selected chunk.</param>
    /// <param name="createBatchId">Deterministic identity projection from chunk index and zero-based retry index.</param>
    /// <param name="afterBulkObservation">
    /// Optional observation invoked after exact result validation and before the outcome is interpreted.
    /// </param>
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
        int maximumBulkItems,
        long maximumBulkBytes,
        int maximumAttempts,
        Func<int, int, MaterializationBatchId> createBatchId,
        Func<OperationContext, MaterializationApplyBatchRequest, MaterializationBatchResult, ValueTask>?
            afterBulkObservation = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(createBatchId);
        MaterializationContract.RequireDefinedIdentity(generation.Value, nameof(generation));
        MaterializationContract.RequireDefinedIdentity(workerFence.Value, nameof(workerFence));
        if (mutations.IsDefault)
            throw new ArgumentException("Target mutations cannot be default.", nameof(mutations));
        if (maximumBulkItems <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumBulkItems),
                maximumBulkItems,
                "A target bulk-item bound must be positive.");
        }
        if (maximumBulkBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumBulkBytes),
                maximumBulkBytes,
                "A target bulk-byte bound must be positive.");
        }
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

        var offset = 0;
        var chunkIndex = 0;
        while (offset < mutations.Length)
        {
            var lowerCount = 1;
            var upperCount = Math.Min(maximumBulkItems, mutations.Length - offset);
            ImmutableArray<MaterializationItemMutation>? selected = null;
            while (lowerCount <= upperCount)
            {
                var count = lowerCount + ((upperCount - lowerCount) / 2);
                var candidate = mutations.Slice(offset, count);
                var request = new MaterializationApplyBatchRequest(
                    batchId: createBatchId(chunkIndex, 0),
                    generationId: generation,
                    workerFence: workerFence,
                    mutations: candidate);
                if (MaterializationTargetIntentFingerprinter.TryAnalyzeBatch(
                        request,
                        maximumBulkBytes,
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
                    $"One output mutation cannot fit the {maximumBulkBytes}-byte target bound.");
            }

            var chunk = selected.Value;
            var pending = chunk;
            for (var retry = 0; retry < maximumAttempts; retry++)
            {
                var request = new MaterializationApplyBatchRequest(
                    batchId: createBatchId(chunkIndex, retry),
                    generationId: generation,
                    workerFence: workerFence,
                    mutations: pending);
                var result = await target.ApplyBatchAsync(context, request).ConfigureAwait(false);
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
                {
                    await afterBulkObservation(context, request, result).ConfigureAwait(false);
                }

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

                HashSet<MaterializationItemMutationId>? retryable = null;
                StringBuilder? permanentFailures = null;
                foreach (var outcome in result.Outcomes)
                {
                    if (outcome.Disposition is MaterializationItemOutcomeDisposition.Applied
                        or MaterializationItemOutcomeDisposition.Replayed)
                    {
                        continue;
                    }
                    if (outcome.Disposition == MaterializationItemOutcomeDisposition.RetryableRejected)
                    {
                        retryable ??= new(result.Outcomes.Length);
                        retryable.Add(outcome.MutationId);
                        continue;
                    }

                    permanentFailures ??= new();
                    if (permanentFailures.Length > 0)
                        permanentFailures.Append(' ');
                    permanentFailures.Append(outcome.Code).Append(": ").Append(outcome.Message);
                }

                if (retryable is null && permanentFailures is null)
                    break;
                if (permanentFailures is not null)
                {
                    return new(MaterializationTargetWriteDisposition.Failed, permanentFailures.ToString());
                }
                if (retry + 1 >= maximumAttempts)
                {
                    return new(
                        MaterializationTargetWriteDisposition.Failed,
                        "The target retry budget was exhausted for one or more output mutations.");
                }

                var retryBuilder = ImmutableArray.CreateBuilder<MaterializationItemMutation>(retryable!.Count);
                foreach (var mutation in pending)
                {
                    if (retryable.Contains(mutation.MutationId))
                        retryBuilder.Add(mutation);
                }
                pending = retryBuilder.MoveToImmutable();
            }

            offset += chunk.Length;
            chunkIndex++;
        }

        return new(MaterializationTargetWriteDisposition.Applied, null);
    }
}
