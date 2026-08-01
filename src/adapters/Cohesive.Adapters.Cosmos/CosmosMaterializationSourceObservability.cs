using System.Collections.Immutable;
using System.Net;
using Cohesive.Control;
using Cohesive.Storage.Materialization;

namespace Cohesive.Adapters.Cosmos;

/// <summary>Kind of bounded Cosmos materialization source operation observed by the adapter.</summary>
public enum CosmosMaterializationSourceOperationKind
{
    /// <summary>Reads one bounded baseline page.</summary>
    BaselineRead = 0,

    /// <summary>Reads one bounded change-feed page.</summary>
    ChangeRead = 1,

    /// <summary>Captures a durable current change-feed boundary.</summary>
    CaptureCurrentPosition = 2
}

/// <summary>Operational disposition of one Cosmos materialization source request.</summary>
public enum CosmosMaterializationSourceDisposition
{
    /// <summary>The requested source boundary was exhausted.</summary>
    Complete = 0,

    /// <summary>A bounded page was returned and another read is required to prove source catch-up.</summary>
    Partial = 1,

    /// <summary>The adapter advanced past filtered provider records without delivering a semantic change.</summary>
    Progressed = 2,

    /// <summary>The change feed reached its currently visible boundary.</summary>
    CaughtUp = 3,

    /// <summary>Cosmos rejected the request because provisioned capacity was unavailable.</summary>
    Throttled = 4,

    /// <summary>A retryable provider failure prevented a page result.</summary>
    RetryableFailure = 5,

    /// <summary>A terminal provider failure prevented a page result.</summary>
    TerminalFailure = 6,

    /// <summary>The caller canceled the operation.</summary>
    Canceled = 7
}

/// <summary>Stable retry classification for a Cosmos materialization source failure.</summary>
public enum CosmosMaterializationFailureKind
{
    /// <summary>Cosmos rejected the operation with HTTP 429 and supplied capacity retry evidence.</summary>
    Throttled = 0,

    /// <summary>A transient transport or service failure may succeed on a later engine-controlled attempt.</summary>
    Transient = 1,

    /// <summary>A full-fidelity position is invalid or older than the configured retention horizon.</summary>
    PositionUnavailable = 2,

    /// <summary>Replaying an intra-page cursor observed different provider content and must fail closed.</summary>
    ReplayConflict = 3,

    /// <summary>A required full-fidelity document image or identity was unavailable.</summary>
    ChangeEvidenceUnavailable = 4,

    /// <summary>The request is not retryable without changing configuration, authorization, or semantic input.</summary>
    Terminal = 5
}

/// <summary>
/// Typed adapter evidence from which a materialization runtime can construct a revision-fenced Control observation.
/// </summary>
/// <remarks>
/// The adapter intentionally does not create <see cref="ControlObservation"/> because it does not own the loop,
/// definition fingerprint, epoch, or expected controller revision. Request charge remains Cosmos-specific evidence;
/// portable pressure signals are projected into <see cref="Measurements"/>. Request-unit consumption is represented
/// in fixed-point milli request units, rounded to the nearest milli request unit with midpoint values rounded away
/// from zero and saturated at <see cref="ControlQuantity.MaximumPortableValue"/>, so canonical Control evidence never
/// depends on floating-point serialization.
/// Queue depth is intentionally absent because the Cosmos SDK and the adapter admission gates do not expose an exact
/// queued-work count at this operation boundary.
/// </remarks>
public sealed record CosmosMaterializationSourceObservation
{
    /// <summary>Creates one typed source-operation observation.</summary>
    /// <param name="operation">Observed operation kind.</param>
    /// <param name="disposition">Operational result.</param>
    /// <param name="scope">Exact materialization source scope.</param>
    /// <param name="startedAtUtc">UTC operation start.</param>
    /// <param name="completedAtUtc">UTC operation completion.</param>
    /// <param name="itemCount">Non-negative semantic item or delivery count.</param>
    /// <param name="canonicalByteCount">Non-negative canonical semantic byte count.</param>
    /// <param name="requestCharge">
    /// Non-negative Cosmos request-unit charge observed from completed SDK responses, or <see langword="null"/> when
    /// the provider did not expose trustworthy charge evidence.
    /// </param>
    /// <param name="measurements">Typed portable Control measurements.</param>
    /// <param name="evidenceReference">Non-sensitive adapter evidence reference.</param>
    /// <param name="controlEvidenceReference">Occurrence-safe identity for a runtime-owned Control observation.</param>
    /// <param name="statusCode">Provider HTTP status when available.</param>
    /// <param name="subStatusCode">Provider substatus when available.</param>
    /// <param name="retryAfter">Provider retry delay when available.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="scope"/>, <paramref name="evidenceReference"/>, or
    /// <paramref name="controlEvidenceReference"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">A timestamp, evidence reference, or measurement collection is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="operation"/> or <paramref name="disposition"/> is unsupported; an item count, byte count, or
    /// request charge is negative; the request charge is non-finite; or <paramref name="retryAfter"/> is negative.
    /// </exception>
    public CosmosMaterializationSourceObservation(
        CosmosMaterializationSourceOperationKind operation,
        CosmosMaterializationSourceDisposition disposition,
        MaterializationSourceScope scope,
        DateTimeOffset startedAtUtc,
        DateTimeOffset completedAtUtc,
        long itemCount,
        long canonicalByteCount,
        double? requestCharge,
        ImmutableArray<ControlMeasurement> measurements,
        string evidenceReference,
        string controlEvidenceReference,
        HttpStatusCode? statusCode = null,
        int? subStatusCode = null,
        TimeSpan? retryAfter = null)
    {
        if (!Enum.IsDefined(operation))
            throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unsupported Cosmos source operation.");
        if (!Enum.IsDefined(disposition))
            throw new ArgumentOutOfRangeException(nameof(disposition), disposition, "Unsupported Cosmos source disposition.");
        if (startedAtUtc.Offset != TimeSpan.Zero || completedAtUtc.Offset != TimeSpan.Zero || completedAtUtc < startedAtUtc)
            throw new ArgumentException("Cosmos source observation times must be chronological UTC values.", nameof(startedAtUtc));
        if (itemCount < 0)
            throw new ArgumentOutOfRangeException(nameof(itemCount), itemCount, "An observed item count cannot be negative.");
        if (canonicalByteCount < 0)
            throw new ArgumentOutOfRangeException(nameof(canonicalByteCount), canonicalByteCount, "An observed byte count cannot be negative.");
        if (requestCharge is { } charge && (!double.IsFinite(charge) || charge < 0))
            throw new ArgumentOutOfRangeException(nameof(requestCharge), requestCharge, "A Cosmos request charge must be finite and non-negative.");
        if (measurements.IsDefault || measurements.Any(static measurement => measurement is null))
            throw new ArgumentException("Control measurements must be a non-default collection without null entries.", nameof(measurements));
        if (disposition is (
                CosmosMaterializationSourceDisposition.Canceled
                    or CosmosMaterializationSourceDisposition.TerminalFailure)
            && measurements.Any(static measurement =>
                measurement.Availability != ControlMeasurementAvailability.Unavailable))
        {
            throw new ArgumentException(
                "A canceled or terminal Cosmos operation cannot carry Control-eligible measurements.",
                nameof(measurements));
        }
        if (retryAfter < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(retryAfter), retryAfter, "A provider retry delay cannot be negative.");

        Operation = operation;
        Disposition = disposition;
        Scope = Guard.RequireNotNull(scope);
        StartedAtUtc = startedAtUtc;
        CompletedAtUtc = completedAtUtc;
        ItemCount = itemCount;
        CanonicalByteCount = canonicalByteCount;
        RequestCharge = requestCharge;
        Measurements = measurements;
        EvidenceReference = Guard.RequireNotNullOrWhiteSpace(evidenceReference);
        ControlEvidenceReference = Guard.RequireNotNullOrWhiteSpace(controlEvidenceReference);
        StatusCode = statusCode;
        SubStatusCode = subStatusCode;
        RetryAfter = retryAfter;
    }

    /// <summary>Observed operation kind.</summary>
    public CosmosMaterializationSourceOperationKind Operation { get; }

    /// <summary>Operational result.</summary>
    public CosmosMaterializationSourceDisposition Disposition { get; }

    /// <summary>Exact source scope.</summary>
    public MaterializationSourceScope Scope { get; }

    /// <summary>UTC operation start.</summary>
    public DateTimeOffset StartedAtUtc { get; }

    /// <summary>UTC operation completion.</summary>
    public DateTimeOffset CompletedAtUtc { get; }

    /// <summary>Semantic item or delivery count.</summary>
    public long ItemCount { get; }

    /// <summary>Canonical semantic byte count.</summary>
    public long CanonicalByteCount { get; }

    /// <summary>Cosmos request-unit charge aggregated across completed SDK responses, when available.</summary>
    /// <remarks>
    /// This provider-native value is retained for diagnostics. Portable Control consumers should use the
    /// <see cref="ControlMetricKind.RequestUnitConsumption"/> measurement in <see cref="Measurements"/>.
    /// </remarks>
    public double? RequestCharge { get; }

    /// <summary>Portable typed pressure measurements ready for a runtime-owned Control observation envelope.</summary>
    public ImmutableArray<ControlMeasurement> Measurements { get; }

    /// <summary>Non-sensitive adapter evidence reference.</summary>
    public string EvidenceReference { get; }

    /// <summary>Occurrence-safe identity to use when wrapping measurements in a Control observation.</summary>
    public string ControlEvidenceReference { get; }

    /// <summary>Provider HTTP status when available.</summary>
    public HttpStatusCode? StatusCode { get; }

    /// <summary>Provider substatus when available.</summary>
    public int? SubStatusCode { get; }

    /// <summary>Provider retry delay when available.</summary>
    public TimeSpan? RetryAfter { get; }
}

/// <summary>Explicit sink for typed Cosmos materialization observations.</summary>
public interface ICosmosMaterializationSourceObserver
{
    /// <summary>Observes one completed, failed, or canceled bounded source operation.</summary>
    /// <param name="observation">Typed operation evidence.</param>
    /// <remarks>
    /// Calls for independently admitted scopes may be concurrent. An observer MUST be thread-safe, SHOULD return
    /// promptly, and MUST NOT throw. The source suppresses non-fatal observer failures so advisory evidence cannot
    /// alter cursor or materialization semantics.
    /// </remarks>
    void Observe(CosmosMaterializationSourceObservation observation);
}

/// <summary>Sanitized typed exception from a failed Cosmos materialization source operation.</summary>
public sealed class CosmosMaterializationSourceException : Exception
{
    /// <summary>Creates a sanitized provider failure.</summary>
    /// <param name="message">Non-sensitive human-facing failure summary.</param>
    /// <param name="failureKind">Stable retry and recovery classification.</param>
    /// <param name="observation">Typed operation evidence emitted for the failure.</param>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> or <paramref name="observation"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="message"/> is empty or white space.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="failureKind"/> is unsupported.</exception>
    public CosmosMaterializationSourceException(
        string message,
        CosmosMaterializationFailureKind failureKind,
        CosmosMaterializationSourceObservation observation)
        : base(Guard.RequireNotNullOrWhiteSpace(message))
    {
        if (!Enum.IsDefined(failureKind))
            throw new ArgumentOutOfRangeException(nameof(failureKind), failureKind, "Unsupported Cosmos materialization failure kind.");
        FailureKind = failureKind;
        Observation = Guard.RequireNotNull(observation);
    }

    /// <summary>Stable retry and recovery classification.</summary>
    public CosmosMaterializationFailureKind FailureKind { get; }

    /// <summary>Typed non-sensitive provider and Control evidence.</summary>
    public CosmosMaterializationSourceObservation Observation { get; }
}
