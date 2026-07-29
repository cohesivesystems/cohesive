using System.Text.Json.Serialization;
using Cohesive.Model.Serialization;

namespace Cohesive.Control;

/// <summary>Outcome of a pure bounded-admission check.</summary>
public enum ControlAdmissionDisposition
{
    /// <summary>The candidate may be admitted immediately.</summary>
    Admitted = 0,

    /// <summary>The candidate must wait until existing work or window usage drains.</summary>
    Deferred = 1,

    /// <summary>The current batch is complete and the candidate must be retried in the next batch.</summary>
    Boundary = 2,

    /// <summary>The candidate cannot fit even when admitted in isolation.</summary>
    Unfulfillable = 3
}

/// <summary>Count-and-byte usage owned and measured by an admission runtime.</summary>
/// <remarks>
/// This value deliberately contains no clock or window identity. A caller establishes the batch, buffer,
/// or one-second rate window whose current usage it supplies to <see cref="ControlBoundedAdmission"/>.
/// </remarks>
public readonly record struct ControlWorkloadUsage
{
    /// <summary>Zero workload usage.</summary>
    public static ControlWorkloadUsage Zero { get; } = new(itemCount: 0, byteCount: 0);

    /// <summary>Creates count-and-byte workload usage.</summary>
    /// <param name="itemCount">Non-negative number of represented items.</param>
    /// <param name="byteCount">Non-negative number of represented encoded bytes.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A count is negative or exceeds <see cref="ControlQuantity.MaximumPortableValue"/>.
    /// </exception>
    [JsonConstructor]
    public ControlWorkloadUsage(long itemCount, long byteCount)
    {
        RequirePortableNonNegative(itemCount, nameof(itemCount));
        RequirePortableNonNegative(byteCount, nameof(byteCount));
        ItemCount = itemCount;
        ByteCount = byteCount;
    }

    /// <summary>Number of represented items.</summary>
    [JsonConverter(typeof(StringEncodedInt64JsonConverter))]
    public long ItemCount { get; }

    /// <summary>Number of represented encoded bytes.</summary>
    [JsonConverter(typeof(StringEncodedInt64JsonConverter))]
    public long ByteCount { get; }

    internal static void RequirePortableNonNegative(long value, string parameterName)
    {
        if (value is < 0 or > ControlQuantity.MaximumPortableValue)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                "Control admission usage must be non-negative and portable to JSON runtimes.");
        }
    }
}

/// <summary>Allocation-free result of one bounded-admission check.</summary>
public readonly record struct ControlAdmissionDecision
{
    /// <summary>Creates a validated admission decision.</summary>
    /// <param name="disposition">Admission outcome.</param>
    /// <param name="constrainedBy">Actuator that admitted or constrained the candidate.</param>
    /// <exception cref="ArgumentOutOfRangeException">An enum value is unsupported.</exception>
    public ControlAdmissionDecision(
        ControlAdmissionDisposition disposition,
        ControlActuatorKind constrainedBy)
    {
        if (!Enum.IsDefined(disposition))
        {
            throw new ArgumentOutOfRangeException(nameof(disposition), disposition, "Unsupported admission disposition.");
        }

        _ = ControlUnitCatalog.ForActuator(constrainedBy);

        Disposition = disposition;
        ConstrainedBy = constrainedBy;
    }

    /// <summary>Admission outcome.</summary>
    public ControlAdmissionDisposition Disposition { get; }

    /// <summary>Actuator that admitted or constrained the candidate.</summary>
    /// <remarks>
    /// Successful decisions identify the primary actuator governing the check. When two limits reject the
    /// same candidate, count is the deterministic primary attribution; both limits are nevertheless enforced.
    /// </remarks>
    public ControlActuatorKind ConstrainedBy { get; }

    /// <summary>Whether the caller may incorporate or start the candidate.</summary>
    [JsonIgnore]
    public bool AdmitsCandidate => Disposition == ControlAdmissionDisposition.Admitted;
}

/// <summary>Pure admission checks that enforce a selected operating point without owning runtime mechanisms.</summary>
/// <remarks>
/// The caller retains ownership of work, counters, windows, queues, and clocks. A non-admitted decision never
/// consumes or drops the candidate. Applying a lower concurrency or buffer target therefore drains existing
/// work naturally and prevents only new admissions.
/// </remarks>
public static class ControlBoundedAdmission
{
    /// <summary>Checks whether one new operation may start at the selected concurrency.</summary>
    /// <param name="operatingPoint">Operating point containing the concurrency target.</param>
    /// <param name="inFlight">Current non-negative number of started operations that have not completed.</param>
    /// <returns>
    /// <see cref="ControlAdmissionDisposition.Admitted"/> when <paramref name="inFlight"/> is below the target;
    /// otherwise <see cref="ControlAdmissionDisposition.Deferred"/>. Existing operations are never preempted.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="operatingPoint"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="inFlight"/> is negative or not portable.</exception>
    /// <exception cref="KeyNotFoundException">The operating point has no concurrency value.</exception>
    public static ControlAdmissionDecision CheckConcurrency(
        ControlOperatingPoint operatingPoint,
        long inFlight)
    {
        ArgumentNullException.ThrowIfNull(operatingPoint);
        ControlWorkloadUsage.RequirePortableNonNegative(inFlight, nameof(inFlight));
        var target = operatingPoint.Get(ControlActuatorKind.Concurrency).Quantity.Value;
        return new(
            inFlight < target ? ControlAdmissionDisposition.Admitted : ControlAdmissionDisposition.Deferred,
            ControlActuatorKind.Concurrency);
    }

    /// <summary>Checks whether one encoded item may be appended to the current batch.</summary>
    /// <param name="operatingPoint">Operating point containing item-count and byte-count batch limits.</param>
    /// <param name="currentBatch">Current batch usage, which remains caller-owned.</param>
    /// <param name="itemByteCount">Non-negative encoded byte count of the candidate item.</param>
    /// <param name="resultingBatch">
    /// Usage after an admitted append; otherwise exactly <paramref name="currentBatch"/>, so the candidate remains
    /// available to retry or reject explicitly.
    /// </param>
    /// <returns>
    /// Admitted when both projected limits hold; Boundary when a candidate that fits alone would cross either
    /// current-batch boundary; or Unfulfillable when the candidate cannot fit in an empty batch.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="operatingPoint"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="itemByteCount"/> is negative or not portable.</exception>
    /// <exception cref="KeyNotFoundException">The operating point lacks either batch limit.</exception>
    public static ControlAdmissionDecision CheckBatchItem(
        ControlOperatingPoint operatingPoint,
        ControlWorkloadUsage currentBatch,
        long itemByteCount,
        out ControlWorkloadUsage resultingBatch)
    {
        ArgumentNullException.ThrowIfNull(operatingPoint);
        ControlWorkloadUsage.RequirePortableNonNegative(itemByteCount, nameof(itemByteCount));

        var itemLimit = operatingPoint.Get(ControlActuatorKind.BatchItems).Quantity.Value;
        var byteLimit = operatingPoint.Get(ControlActuatorKind.BatchBytes).Quantity.Value;
        resultingBatch = currentBatch;

        if (itemLimit < 1)
        {
            return new(ControlAdmissionDisposition.Unfulfillable, ControlActuatorKind.BatchItems);
        }

        if (itemByteCount > byteLimit)
        {
            return new(ControlAdmissionDisposition.Unfulfillable, ControlActuatorKind.BatchBytes);
        }

        if (WouldExceed(currentBatch.ItemCount, addition: 1, itemLimit))
        {
            return new(ControlAdmissionDisposition.Boundary, ControlActuatorKind.BatchItems);
        }

        if (WouldExceed(currentBatch.ByteCount, itemByteCount, byteLimit))
        {
            return new(ControlAdmissionDisposition.Boundary, ControlActuatorKind.BatchBytes);
        }

        resultingBatch = new(currentBatch.ItemCount + 1, currentBatch.ByteCount + itemByteCount);
        return new(ControlAdmissionDisposition.Admitted, ControlActuatorKind.BatchItems);
    }

    /// <summary>Checks finite buffer capacity before accepting caller-owned incoming work.</summary>
    /// <param name="operatingPoint">Operating point containing buffered item-count and byte-count limits.</param>
    /// <param name="buffered">Current buffer usage.</param>
    /// <param name="incoming">Positive incoming usage retained by the caller until admitted.</param>
    /// <returns>
    /// Admitted only when both projected buffer limits hold; Deferred while existing usage can drain; or Unfulfillable
    /// when the indivisible candidate exceeds an empty buffer's absolute item or byte limit.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="operatingPoint"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="incoming"/> represents no items and no bytes.</exception>
    /// <exception cref="KeyNotFoundException">The operating point lacks either buffer limit.</exception>
    public static ControlAdmissionDecision CheckBuffer(
        ControlOperatingPoint operatingPoint,
        ControlWorkloadUsage buffered,
        ControlWorkloadUsage incoming)
    {
        ArgumentNullException.ThrowIfNull(operatingPoint);
        RequirePositiveCandidate(incoming, nameof(incoming));
        var itemLimit = operatingPoint.Get(ControlActuatorKind.BufferedItems).Quantity.Value;
        var byteLimit = operatingPoint.Get(ControlActuatorKind.BufferedBytes).Quantity.Value;

        if (incoming.ItemCount > itemLimit)
            return new(ControlAdmissionDisposition.Unfulfillable, ControlActuatorKind.BufferedItems);
        if (incoming.ByteCount > byteLimit)
            return new(ControlAdmissionDisposition.Unfulfillable, ControlActuatorKind.BufferedBytes);

        if (WouldExceed(buffered.ItemCount, incoming.ItemCount, itemLimit))
        {
            return new(ControlAdmissionDisposition.Deferred, ControlActuatorKind.BufferedItems);
        }

        if (WouldExceed(buffered.ByteCount, incoming.ByteCount, byteLimit))
        {
            return new(ControlAdmissionDisposition.Deferred, ControlActuatorKind.BufferedBytes);
        }

        return new(ControlAdmissionDisposition.Admitted, ControlActuatorKind.BufferedItems);
    }

    /// <summary>Checks one-second item and byte rate usage supplied by the caller.</summary>
    /// <param name="operatingPoint">Operating point containing item-per-second and byte-per-second limits.</param>
    /// <param name="windowUsage">Usage already admitted in the caller's current one-second window.</param>
    /// <param name="incoming">Positive usage proposed for that same window.</param>
    /// <returns>
    /// Admitted only when both projected rate limits hold; Deferred until the next window when that can succeed; or
    /// Unfulfillable when the indivisible candidate exceeds an empty window's absolute item or byte limit.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="operatingPoint"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="incoming"/> represents no items and no bytes.</exception>
    /// <exception cref="KeyNotFoundException">The operating point lacks either rate limit.</exception>
    public static ControlAdmissionDecision CheckRate(
        ControlOperatingPoint operatingPoint,
        ControlWorkloadUsage windowUsage,
        ControlWorkloadUsage incoming)
    {
        ArgumentNullException.ThrowIfNull(operatingPoint);
        RequirePositiveCandidate(incoming, nameof(incoming));
        var itemLimit = operatingPoint.Get(ControlActuatorKind.ItemRate).Quantity.Value;
        var byteLimit = operatingPoint.Get(ControlActuatorKind.ByteRate).Quantity.Value;

        if (incoming.ItemCount > itemLimit)
            return new(ControlAdmissionDisposition.Unfulfillable, ControlActuatorKind.ItemRate);
        if (incoming.ByteCount > byteLimit)
            return new(ControlAdmissionDisposition.Unfulfillable, ControlActuatorKind.ByteRate);

        if (WouldExceed(windowUsage.ItemCount, incoming.ItemCount, itemLimit))
        {
            return new(ControlAdmissionDisposition.Deferred, ControlActuatorKind.ItemRate);
        }

        if (WouldExceed(windowUsage.ByteCount, incoming.ByteCount, byteLimit))
        {
            return new(ControlAdmissionDisposition.Deferred, ControlActuatorKind.ByteRate);
        }

        return new(ControlAdmissionDisposition.Admitted, ControlActuatorKind.ItemRate);
    }

    static bool WouldExceed(long current, long addition, long maximum) =>
        current > maximum || addition > maximum - current;

    static void RequirePositiveCandidate(ControlWorkloadUsage incoming, string parameterName)
    {
        if (incoming.ItemCount == 0 && incoming.ByteCount == 0)
        {
            throw new ArgumentException("An admission candidate must represent positive work.", parameterName);
        }
    }
}
