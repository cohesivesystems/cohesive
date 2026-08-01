using System.Collections.Immutable;
using Cohesive.Control;
using Cohesive.Storage.Materialization;

namespace Cohesive.Adapters.Elastic;

/// <summary>Operational disposition of one Elasticsearch materialization batch request.</summary>
public enum ElasticMaterializationTargetDisposition
{
    /// <summary>Every requested item was applied or reused from durable idempotency evidence.</summary>
    Complete = 0,

    /// <summary>The exact prior batch result was reused without another physical batch write.</summary>
    Replayed = 1,

    /// <summary>A complete result contains at least one rejected or failed item outcome.</summary>
    PartiallyRejected = 2,

    /// <summary>The batch identity was previously bound to different canonical content.</summary>
    IdentityConflict = 3,

    /// <summary>The addressed generation does not exist.</summary>
    GenerationNotFound = 4,

    /// <summary>The addressed generation exists but cannot accept writes.</summary>
    GenerationNotWritable = 5,

    /// <summary>The canonical batch exceeds one or more declared target limits.</summary>
    LimitExceeded = 6,

    /// <summary>A newer materialization worker fence superseded the request.</summary>
    StaleFence = 7,

    /// <summary>A retryable provider or adapter failure prevented a semantic batch result.</summary>
    RetryableFailure = 8,

    /// <summary>A terminal provider or adapter failure prevented a semantic batch result.</summary>
    TerminalFailure = 9,

    /// <summary>The caller canceled the request before a semantic batch result was available.</summary>
    Canceled = 10
}

/// <summary>Eligibility classification governing whether batch evidence may influence adaptive Control.</summary>
public enum ElasticMaterializationTargetControlEvidenceKind
{
    /// <summary>The observation contains a representative physical pressure sample.</summary>
    PressureSample = 0,

    /// <summary>The complete batch result was reused without a physical request.</summary>
    BatchReplay = 1,

    /// <summary>A physical batch result included one or more durable item-level replay outcomes.</summary>
    MixedItemReplay = 2,

    /// <summary>The operation did not produce evidence representative of adaptive target pressure.</summary>
    IneligibleOperation = 3
}

/// <summary>
/// Typed adapter evidence from which a materialization runtime can construct a revision-fenced Control observation.
/// </summary>
/// <remarks>
/// The adapter intentionally does not create <see cref="ControlObservation"/> because it does not own the loop,
/// definition fingerprint, epoch, or expected controller revision. Batch item and byte counts describe the exact
/// canonical input intent. Item throughput is available only when both a complete semantic result and a nonzero
/// observation window make it derivable and is conservatively truncated to whole items per second. Per-batch
/// rejection is rounded to the nearest basis point. Byte throughput and queue depth are intentionally absent: partial
/// item outcomes do not identify successful canonical bytes, and the local admission gates expose no exact waiter
/// count.
/// </remarks>
public sealed record ElasticMaterializationTargetObservation
{
    /// <summary>Creates typed evidence for one Elasticsearch materialization batch operation.</summary>
    /// <param name="disposition">Operational result.</param>
    /// <param name="targetId">Stable physical target identity.</param>
    /// <param name="materializationId">Stable logical materialization identity.</param>
    /// <param name="generationId">Addressed generation identity.</param>
    /// <param name="batchId">Addressed batch identity.</param>
    /// <param name="startedAtUtc">UTC operation start, including local admission wait.</param>
    /// <param name="completedAtUtc">UTC operation completion.</param>
    /// <param name="itemCount">Exact positive input item count.</param>
    /// <param name="canonicalByteCount">Exact non-negative canonical input byte count.</param>
    /// <param name="successfulItemCount">Exact applied or replayed item count when a semantic result is available.</param>
    /// <param name="measurements">Typed portable Control measurements.</param>
    /// <param name="controlEvidenceKind">Eligibility classification for adaptive Control.</param>
    /// <param name="evidenceReference">Non-sensitive adapter evidence reference.</param>
    /// <param name="controlEvidenceReference">Occurrence-safe identity for a runtime-owned Control observation.</param>
    /// <param name="failureCode">Stable provider, adapter, or item outcome code when available.</param>
    /// <param name="statusCode">Provider HTTP status when available.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="evidenceReference"/> or <paramref name="controlEvidenceReference"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// An identity, timestamp, evidence reference, count relationship, or measurement collection is invalid.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="disposition"/> or <paramref name="controlEvidenceKind"/> is unsupported; the item count is not
    /// positive; the byte count is negative; or <paramref name="statusCode"/> is not an HTTP status code.
    /// </exception>
    public ElasticMaterializationTargetObservation(
        ElasticMaterializationTargetDisposition disposition,
        MaterializationTargetId targetId,
        MaterializationId materializationId,
        MaterializationGenerationId generationId,
        MaterializationBatchId batchId,
        DateTimeOffset startedAtUtc,
        DateTimeOffset completedAtUtc,
        long itemCount,
        long canonicalByteCount,
        long? successfulItemCount,
        ImmutableArray<ControlMeasurement> measurements,
        ElasticMaterializationTargetControlEvidenceKind controlEvidenceKind,
        string evidenceReference,
        string controlEvidenceReference,
        string? failureCode = null,
        int? statusCode = null)
    {
        if (!Enum.IsDefined(disposition))
        {
            throw new ArgumentOutOfRangeException(
                nameof(disposition),
                disposition,
                "Unsupported Elasticsearch materialization target disposition.");
        }
        if (!Enum.IsDefined(controlEvidenceKind))
        {
            throw new ArgumentOutOfRangeException(
                nameof(controlEvidenceKind),
                controlEvidenceKind,
                "Unsupported Elasticsearch materialization Control-evidence kind.");
        }
        RequireDefined(targetId.Value, nameof(targetId));
        RequireDefined(materializationId.Value, nameof(materializationId));
        RequireDefined(generationId.Value, nameof(generationId));
        RequireDefined(batchId.Value, nameof(batchId));
        if (startedAtUtc.Offset != TimeSpan.Zero
            || completedAtUtc.Offset != TimeSpan.Zero
            || completedAtUtc < startedAtUtc)
        {
            throw new ArgumentException(
                "Elasticsearch target observation times must be chronological UTC values.",
                nameof(startedAtUtc));
        }
        if (itemCount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(itemCount),
                itemCount,
                "An observed target batch requires at least one item.");
        }
        if (canonicalByteCount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(canonicalByteCount),
                canonicalByteCount,
                "An observed byte count cannot be negative.");
        }
        if (successfulItemCount is < 0 || successfulItemCount > itemCount)
        {
            throw new ArgumentException(
                "A successful item count must be non-negative and no greater than the input item count.",
                nameof(successfulItemCount));
        }
        var resultAvailable = disposition is not (
            ElasticMaterializationTargetDisposition.RetryableFailure
                or ElasticMaterializationTargetDisposition.TerminalFailure
                or ElasticMaterializationTargetDisposition.Canceled);
        if (resultAvailable != successfulItemCount.HasValue)
        {
            throw new ArgumentException(
                "A semantic result disposition requires a successful item count, while a failed or canceled operation must omit it.",
                nameof(successfulItemCount));
        }
        if ((disposition is ElasticMaterializationTargetDisposition.Complete
                or ElasticMaterializationTargetDisposition.Replayed)
            && successfulItemCount != itemCount)
        {
            throw new ArgumentException(
                "A complete or replayed result requires every input item to be successful.",
                nameof(successfulItemCount));
        }
        if (disposition == ElasticMaterializationTargetDisposition.PartiallyRejected
            && successfulItemCount == itemCount)
        {
            throw new ArgumentException(
                "A partially rejected result requires at least one unsuccessful item.",
                nameof(successfulItemCount));
        }
        if ((disposition is ElasticMaterializationTargetDisposition.IdentityConflict
                or ElasticMaterializationTargetDisposition.GenerationNotFound
                or ElasticMaterializationTargetDisposition.GenerationNotWritable
                or ElasticMaterializationTargetDisposition.LimitExceeded
                or ElasticMaterializationTargetDisposition.StaleFence)
            && successfulItemCount != 0)
        {
            throw new ArgumentException(
                "A rejected batch-level disposition cannot report successful items.",
                nameof(successfulItemCount));
        }
        if (measurements.IsDefaultOrEmpty)
        {
            throw new ArgumentException(
                "Control measurements must be a non-empty collection.",
                nameof(measurements));
        }
        for (var firstIndex = 0; firstIndex < measurements.Length; firstIndex++)
        {
            var firstMeasurement = measurements[firstIndex];
            if (firstMeasurement is null)
            {
                throw new ArgumentException(
                    "Control measurements cannot contain null entries.",
                    nameof(measurements));
            }
            for (var secondIndex = firstIndex + 1; secondIndex < measurements.Length; secondIndex++)
            {
                var secondMeasurement = measurements[secondIndex];
                if (secondMeasurement is null)
                {
                    throw new ArgumentException(
                        "Control measurements cannot contain null entries.",
                        nameof(measurements));
                }
                if (firstMeasurement.Metric == secondMeasurement.Metric
                    && firstMeasurement.Statistic == secondMeasurement.Statistic)
                {
                    throw new ArgumentException(
                        "Control measurements cannot repeat a metric and statistic pair.",
                        nameof(measurements));
                }
            }
        }
        if (disposition == ElasticMaterializationTargetDisposition.Replayed
            && controlEvidenceKind != ElasticMaterializationTargetControlEvidenceKind.BatchReplay)
        {
            throw new ArgumentException(
                "A replayed batch disposition requires batch-replay Control evidence.",
                nameof(controlEvidenceKind));
        }
        if (controlEvidenceKind == ElasticMaterializationTargetControlEvidenceKind.BatchReplay
            && disposition != ElasticMaterializationTargetDisposition.Replayed)
        {
            throw new ArgumentException(
                "Batch-replay Control evidence requires a replayed batch disposition.",
                nameof(controlEvidenceKind));
        }
        if (controlEvidenceKind == ElasticMaterializationTargetControlEvidenceKind.MixedItemReplay
            && disposition is not (
                ElasticMaterializationTargetDisposition.Complete
                    or ElasticMaterializationTargetDisposition.PartiallyRejected))
        {
            throw new ArgumentException(
                "Mixed item-replay Control evidence requires a complete or partially rejected semantic result.",
                nameof(controlEvidenceKind));
        }
        if (disposition is (
                ElasticMaterializationTargetDisposition.Canceled
                    or ElasticMaterializationTargetDisposition.TerminalFailure)
            && controlEvidenceKind != ElasticMaterializationTargetControlEvidenceKind.IneligibleOperation)
        {
            throw new ArgumentException(
                "A canceled or terminal batch requires ineligible Control evidence.",
                nameof(controlEvidenceKind));
        }
        if (disposition is (
                ElasticMaterializationTargetDisposition.IdentityConflict
                    or ElasticMaterializationTargetDisposition.GenerationNotFound
                    or ElasticMaterializationTargetDisposition.GenerationNotWritable
                    or ElasticMaterializationTargetDisposition.LimitExceeded
                    or ElasticMaterializationTargetDisposition.StaleFence)
            && controlEvidenceKind != ElasticMaterializationTargetControlEvidenceKind.IneligibleOperation)
        {
            throw new ArgumentException(
                "A batch-level semantic rejection requires ineligible Control evidence.",
                nameof(controlEvidenceKind));
        }
        if (controlEvidenceKind == ElasticMaterializationTargetControlEvidenceKind.IneligibleOperation
            && disposition is not (
                ElasticMaterializationTargetDisposition.PartiallyRejected
                    or ElasticMaterializationTargetDisposition.IdentityConflict
                    or ElasticMaterializationTargetDisposition.GenerationNotFound
                    or ElasticMaterializationTargetDisposition.GenerationNotWritable
                    or ElasticMaterializationTargetDisposition.LimitExceeded
                    or ElasticMaterializationTargetDisposition.StaleFence
                    or ElasticMaterializationTargetDisposition.RetryableFailure
                    or ElasticMaterializationTargetDisposition.TerminalFailure
                    or ElasticMaterializationTargetDisposition.Canceled))
        {
            throw new ArgumentException(
                "Ineligible Control evidence requires a partial rejection, cancellation, failure, or batch-level semantic rejection.",
                nameof(controlEvidenceKind));
        }
        if (controlEvidenceKind != ElasticMaterializationTargetControlEvidenceKind.PressureSample
            && measurements.Any(static measurement =>
                measurement.Availability != ControlMeasurementAvailability.Unavailable))
        {
            throw new ArgumentException(
                "Replay and ineligible operation evidence cannot carry Control-eligible measurements.",
                nameof(measurements));
        }
        if (statusCode is < 100 or > 599)
        {
            throw new ArgumentOutOfRangeException(
                nameof(statusCode),
                statusCode,
                "A provider status must be an HTTP status code.");
        }

        Disposition = disposition;
        TargetId = targetId;
        MaterializationId = materializationId;
        GenerationId = generationId;
        BatchId = batchId;
        StartedAtUtc = startedAtUtc;
        CompletedAtUtc = completedAtUtc;
        ItemCount = itemCount;
        CanonicalByteCount = canonicalByteCount;
        SuccessfulItemCount = successfulItemCount;
        Measurements = measurements;
        ControlEvidenceKind = controlEvidenceKind;
        EvidenceReference = Guard.RequireNotNullOrWhiteSpace(evidenceReference);
        ControlEvidenceReference = Guard.RequireNotNullOrWhiteSpace(controlEvidenceReference);
        FailureCode = failureCode is null ? null : Guard.RequireNotNullOrWhiteSpace(failureCode);
        if ((disposition is ElasticMaterializationTargetDisposition.Complete
                or ElasticMaterializationTargetDisposition.Replayed)
            && FailureCode is not null)
        {
            throw new ArgumentException(
                "A successful target observation cannot carry a failure code.",
                nameof(failureCode));
        }
        if (disposition is not (
                ElasticMaterializationTargetDisposition.Complete
                    or ElasticMaterializationTargetDisposition.Replayed
                    or ElasticMaterializationTargetDisposition.Canceled)
            && FailureCode is null)
        {
            throw new ArgumentException(
                "A rejected or failed target observation requires a stable failure code.",
                nameof(failureCode));
        }
        StatusCode = statusCode;
    }

    static void RequireDefined(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException(
                "An Elasticsearch target observation requires non-default identities.",
                parameterName);
        }
    }

    /// <summary>Operational result.</summary>
    public ElasticMaterializationTargetDisposition Disposition { get; }

    /// <summary>Stable physical target identity.</summary>
    public MaterializationTargetId TargetId { get; }

    /// <summary>Stable logical materialization identity.</summary>
    public MaterializationId MaterializationId { get; }

    /// <summary>Addressed generation identity.</summary>
    public MaterializationGenerationId GenerationId { get; }

    /// <summary>Addressed batch identity.</summary>
    public MaterializationBatchId BatchId { get; }

    /// <summary>UTC operation start, including local admission wait.</summary>
    public DateTimeOffset StartedAtUtc { get; }

    /// <summary>UTC operation completion.</summary>
    public DateTimeOffset CompletedAtUtc { get; }

    /// <summary>Exact positive canonical input item count.</summary>
    public long ItemCount { get; }

    /// <summary>Exact canonical input byte count.</summary>
    public long CanonicalByteCount { get; }

    /// <summary>Exact applied or replayed item count, or <see langword="null"/> when no semantic result exists.</summary>
    public long? SuccessfulItemCount { get; }

    /// <summary>Portable typed pressure measurements ready for a runtime-owned Control observation envelope.</summary>
    public ImmutableArray<ControlMeasurement> Measurements { get; }

    /// <summary>Eligibility classification governing whether the evidence may influence adaptive Control.</summary>
    public ElasticMaterializationTargetControlEvidenceKind ControlEvidenceKind { get; }

    /// <summary>Non-sensitive adapter evidence reference.</summary>
    public string EvidenceReference { get; }

    /// <summary>Occurrence-safe identity to use when wrapping measurements in a Control observation.</summary>
    public string ControlEvidenceReference { get; }

    /// <summary>Stable provider, adapter, or item outcome code when available.</summary>
    public string? FailureCode { get; }

    /// <summary>Provider HTTP status when available.</summary>
    public int? StatusCode { get; }
}

/// <summary>Explicit sink for typed Elasticsearch materialization target observations.</summary>
public interface IElasticMaterializationTargetObserver
{
    /// <summary>Observes one completed, failed, or canceled bounded target batch operation.</summary>
    /// <param name="observation">Typed operation evidence.</param>
    /// <remarks>
    /// Calls for independently admitted generations may be concurrent. An observer MUST be thread-safe, SHOULD return
    /// promptly, and MUST NOT throw. The target suppresses non-fatal observer failures so advisory evidence cannot
    /// alter durable materialization semantics.
    /// </remarks>
    void Observe(ElasticMaterializationTargetObservation observation);
}
