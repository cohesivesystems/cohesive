using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Cohesive.Model.Serialization;

namespace Cohesive.Control;

/// <summary>Portable fixed-point unit used by control values and measurements.</summary>
public enum ControlUnit
{
    /// <summary>Dimensionless item, worker, request, or work count.</summary>
    Count = 0,

    /// <summary>Byte count.</summary>
    Bytes = 1,

    /// <summary>One ten-thousandth of a ratio; 10,000 basis points is one whole.</summary>
    BasisPoints = 2,

    /// <summary>Duration in milliseconds.</summary>
    Milliseconds = 3,

    /// <summary>Item count per second.</summary>
    ItemsPerSecond = 4,

    /// <summary>Byte count per second.</summary>
    BytesPerSecond = 5,

    /// <summary>One thousandth of a provider request unit.</summary>
    MilliRequestUnits = 6
}

/// <summary>Closed family of independently bounded operational actuators.</summary>
public enum ControlActuatorKind
{
    /// <summary>Maximum concurrently admitted work.</summary>
    Concurrency = 0,

    /// <summary>Maximum item count in one batch.</summary>
    BatchItems = 1,

    /// <summary>Maximum encoded bytes in one batch.</summary>
    BatchBytes = 2,

    /// <summary>Maximum admitted items per second.</summary>
    ItemRate = 3,

    /// <summary>Maximum admitted bytes per second.</summary>
    ByteRate = 4,

    /// <summary>Maximum buffered items before upstream work must wait.</summary>
    BufferedItems = 5,

    /// <summary>Maximum buffered bytes before upstream work must wait.</summary>
    BufferedBytes = 6
}

/// <summary>Invariant-preserving runtime cut at which one actuator kind may change.</summary>
public enum ControlApplicationPointKind
{
    /// <summary>Boundary before admitting more concurrent work; decreases drain already-started work.</summary>
    WorkAdmissionBoundary = 0,

    /// <summary>Boundary between complete item/byte batches.</summary>
    BatchBoundary = 1,

    /// <summary>Boundary between caller-owned rate windows.</summary>
    RateWindowBoundary = 2,

    /// <summary>Boundary before admitting more work into a finite buffer.</summary>
    BufferAdmissionBoundary = 3
}

/// <summary>Source of one non-overridable hard operating constraint.</summary>
public enum ControlHardLimitOrigin
{
    /// <summary>The semantic or compiled plan declared the constraint.</summary>
    Semantic = 0,

    /// <summary>A compiler proved the constraint while lowering the plan.</summary>
    Compiler = 1,

    /// <summary>A target adapter declared a physical capability boundary.</summary>
    Adapter = 2,

    /// <summary>A deployment supplied an operational capacity boundary.</summary>
    Deployment = 3,

    /// <summary>An explicit local declaration narrowed the otherwise effective range.</summary>
    ExplicitTightening = 4
}

/// <summary>Logical stage regulated by one control loop.</summary>
public enum ControlStageKind
{
    /// <summary>Source acquisition or intake.</summary>
    Source = 0,

    /// <summary>Transformation or computation.</summary>
    Transform = 1,

    /// <summary>Target write or publication.</summary>
    Target = 2
}

/// <summary>Closed typed measurements understood by the reference controller.</summary>
public enum ControlMetricKind
{
    /// <summary>CPU utilization as basis points in the inclusive range 0 through 10,000.</summary>
    ProcessorUtilization = 0,

    /// <summary>Memory utilization as basis points in the inclusive range 0 through 10,000.</summary>
    MemoryUtilization = 1,

    /// <summary>Operation latency in milliseconds.</summary>
    Latency = 2,

    /// <summary>Successful item throughput per second.</summary>
    ItemThroughput = 3,

    /// <summary>Successful byte throughput per second.</summary>
    ByteThroughput = 4,

    /// <summary>Rejected-operation ratio as basis points in the inclusive range 0 through 10,000.</summary>
    RejectionRatio = 5,

    /// <summary>Outstanding lag expressed as an item count.</summary>
    LagItems = 6,

    /// <summary>Outstanding lag expressed as elapsed milliseconds.</summary>
    LagDuration = 7,

    /// <summary>Buffer utilization as basis points in the inclusive range 0 through 10,000.</summary>
    BackpressureUtilization = 8,

    /// <summary>Provider request-unit consumption expressed in exact fixed-point milli request units.</summary>
    RequestUnitConsumption = 9,

    /// <summary>Exact number of work items awaiting admission or processing at the observed boundary.</summary>
    QueueDepth = 10,

    /// <summary>Exact number of items represented by one completed or rejected batch operation.</summary>
    BatchItems = 11,

    /// <summary>Exact canonical bytes represented by one completed or rejected batch operation.</summary>
    BatchBytes = 12
}

/// <summary>Statistic represented by one measurement value.</summary>
public enum ControlStatisticKind
{
    /// <summary>Most recent value in the observation window.</summary>
    Last = 0,

    /// <summary>Arithmetic mean over the observation window.</summary>
    Mean = 1,

    /// <summary>Ninety-fifth percentile over the observation window.</summary>
    P95 = 2,

    /// <summary>Maximum value observed in the window.</summary>
    Maximum = 3,

    /// <summary>Sum of values observed in the window.</summary>
    Sum = 4
}

/// <summary>Availability of one explicitly attempted measurement.</summary>
public enum ControlMeasurementAvailability
{
    /// <summary>A concrete measured value is available.</summary>
    Available = 0,

    /// <summary>The adapter attempted the measurement but could not produce a trustworthy value.</summary>
    Unavailable = 1
}

/// <summary>Declares which side of an objective's hysteresis band represents pressure.</summary>
public enum ControlObjectiveDirection
{
    /// <summary>Values at or above the congestion boundary indicate pressure.</summary>
    HigherIsCongested = 0,

    /// <summary>Values at or below the congestion boundary indicate pressure.</summary>
    LowerIsCongested = 1
}

/// <summary>Catalog of the canonical fixed-point unit for each typed control concept.</summary>
public static class ControlUnitCatalog
{
    /// <summary>Gets the required unit of an actuator.</summary>
    /// <param name="actuator">Actuator whose unit is requested.</param>
    /// <returns>The actuator's one canonical portable unit.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="actuator"/> is unsupported.</exception>
    public static ControlUnit ForActuator(ControlActuatorKind actuator) => actuator switch
    {
        ControlActuatorKind.Concurrency or ControlActuatorKind.BatchItems or ControlActuatorKind.BufferedItems =>
            ControlUnit.Count,
        ControlActuatorKind.BatchBytes or ControlActuatorKind.BufferedBytes => ControlUnit.Bytes,
        ControlActuatorKind.ItemRate => ControlUnit.ItemsPerSecond,
        ControlActuatorKind.ByteRate => ControlUnit.BytesPerSecond,
        _ => throw new ArgumentOutOfRangeException(nameof(actuator), actuator, "Unsupported control actuator.")
    };

    /// <summary>Gets the required unit of a measurement.</summary>
    /// <param name="metric">Measurement kind whose unit is requested.</param>
    /// <returns>The measurement's one canonical portable unit.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="metric"/> is unsupported.</exception>
    public static ControlUnit ForMetric(ControlMetricKind metric) => metric switch
    {
        ControlMetricKind.ProcessorUtilization
            or ControlMetricKind.MemoryUtilization
            or ControlMetricKind.RejectionRatio
            or ControlMetricKind.BackpressureUtilization => ControlUnit.BasisPoints,
        ControlMetricKind.Latency or ControlMetricKind.LagDuration => ControlUnit.Milliseconds,
        ControlMetricKind.ItemThroughput => ControlUnit.ItemsPerSecond,
        ControlMetricKind.ByteThroughput => ControlUnit.BytesPerSecond,
        ControlMetricKind.LagItems
            or ControlMetricKind.QueueDepth
            or ControlMetricKind.BatchItems => ControlUnit.Count,
        ControlMetricKind.BatchBytes => ControlUnit.Bytes,
        ControlMetricKind.RequestUnitConsumption => ControlUnit.MilliRequestUnits,
        _ => throw new ArgumentOutOfRangeException(nameof(metric), metric, "Unsupported control metric.")
    };
}

/// <summary>Catalog of the invariant-preserving application-point kind required by each actuator.</summary>
public static class ControlApplicationPointCatalog
{
    /// <summary>Gets the required runtime cut kind for an actuator change.</summary>
    /// <param name="actuator">Actuator whose application boundary is requested.</param>
    /// <returns>The exact invariant-preserving application-point kind.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="actuator"/> is unsupported.</exception>
    public static ControlApplicationPointKind ForActuator(ControlActuatorKind actuator) => actuator switch
    {
        ControlActuatorKind.Concurrency => ControlApplicationPointKind.WorkAdmissionBoundary,
        ControlActuatorKind.BatchItems or ControlActuatorKind.BatchBytes => ControlApplicationPointKind.BatchBoundary,
        ControlActuatorKind.ItemRate or ControlActuatorKind.ByteRate => ControlApplicationPointKind.RateWindowBoundary,
        ControlActuatorKind.BufferedItems or ControlActuatorKind.BufferedBytes => ControlApplicationPointKind.BufferAdmissionBoundary,
        _ => throw new ArgumentOutOfRangeException(nameof(actuator), actuator, "Unsupported control actuator.")
    };
}

/// <summary>Non-negative portable fixed-point quantity with an explicit unit.</summary>
public readonly record struct ControlQuantity
{
    /// <summary>Largest integer exactly portable through common JSON runtimes.</summary>
    public const long MaximumPortableValue = 9_007_199_254_740_991;

    /// <summary>Creates a typed fixed-point quantity.</summary>
    /// <param name="value">Non-negative integral value in <paramref name="unit"/>.</param>
    /// <param name="unit">Explicit portable unit.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="value"/> is negative or not portable, or <paramref name="unit"/> is unsupported.
    /// </exception>
    [JsonConstructor]
    public ControlQuantity(long value, ControlUnit unit)
    {
        if (value is < 0 or > MaximumPortableValue)
            throw new ArgumentOutOfRangeException(nameof(value), value, "A control quantity must be non-negative and portable to JSON runtimes.");
        if (!Enum.IsDefined(unit))
            throw new ArgumentOutOfRangeException(nameof(unit), unit, "Unsupported control unit.");

        Value = value;
        Unit = unit;
    }

    /// <summary>Non-negative fixed-point value.</summary>
    [JsonConverter(typeof(StringEncodedInt64JsonConverter))]
    public long Value { get; }

    /// <summary>Explicit fixed-point unit.</summary>
    public ControlUnit Unit { get; }
}

/// <summary>One actuator value in a multidimensional operating point.</summary>
public sealed record ControlActuatorValue
{
    /// <summary>Creates an actuator value.</summary>
    /// <param name="actuator">Operational actuator.</param>
    /// <param name="quantity">Positive value in the actuator's canonical unit.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="actuator"/> is unsupported.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="quantity"/> has the wrong unit or is zero.
    /// </exception>
    [JsonConstructor]
    public ControlActuatorValue(ControlActuatorKind actuator, ControlQuantity quantity)
    {
        var expectedUnit = ControlUnitCatalog.ForActuator(actuator);
        if (quantity.Unit != expectedUnit)
            throw new ArgumentException($"Actuator '{actuator}' requires unit '{expectedUnit}'.", nameof(quantity));
        if (quantity.Value == 0)
            throw new ArgumentException("An effective actuator value must be positive.", nameof(quantity));

        Actuator = actuator;
        Quantity = quantity;
    }

    /// <summary>Operational actuator.</summary>
    public ControlActuatorKind Actuator { get; }

    /// <summary>Positive value in the actuator's canonical unit.</summary>
    public ControlQuantity Quantity { get; }
}

/// <summary>Inclusive hard range for one operational actuator.</summary>
public sealed record ControlRange
{
    /// <summary>Creates an inclusive actuator range.</summary>
    /// <param name="actuator">Bounded actuator.</param>
    /// <param name="minimum">Positive inclusive minimum.</param>
    /// <param name="maximum">Positive inclusive maximum.</param>
    /// <exception cref="ArgumentException">A unit is incompatible, a bound is zero, or the range is inverted.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="actuator"/> is unsupported.</exception>
    [JsonConstructor]
    public ControlRange(
        ControlActuatorKind actuator,
        ControlQuantity minimum,
        ControlQuantity maximum)
    {
        var unit = ControlUnitCatalog.ForActuator(actuator);
        if (minimum.Unit != unit || maximum.Unit != unit)
            throw new ArgumentException($"Range for '{actuator}' requires unit '{unit}'.", nameof(minimum));
        if (minimum.Value == 0 || maximum.Value == 0)
            throw new ArgumentException("Control range bounds must be positive.", nameof(minimum));
        if (minimum.Value > maximum.Value)
            throw new ArgumentException("A control range minimum cannot exceed its maximum.", nameof(minimum));

        Actuator = actuator;
        Minimum = minimum;
        Maximum = maximum;
    }

    /// <summary>Bounded actuator.</summary>
    public ControlActuatorKind Actuator { get; }

    /// <summary>Inclusive minimum.</summary>
    public ControlQuantity Minimum { get; }

    /// <summary>Inclusive maximum.</summary>
    public ControlQuantity Maximum { get; }

    /// <summary>Tests whether a value is inside the inclusive range.</summary>
    /// <param name="value">Actuator value to test.</param>
    /// <returns><see langword="true"/> when actuator, unit, and value are inside the range.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    public bool Contains(ControlActuatorValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Actuator == Actuator
            && value.Quantity.Unit == Minimum.Unit
            && value.Quantity.Value >= Minimum.Value
            && value.Quantity.Value <= Maximum.Value;
    }
}

/// <summary>One attributable, non-overridable hard operating constraint.</summary>
public sealed record ControlHardLimit
{
    /// <summary>Creates a hard constraint.</summary>
    /// <param name="range">Inclusive range asserted by the authority.</param>
    /// <param name="origin">Kind of authority establishing the range.</param>
    /// <param name="authority">Stable identity and version of the semantic, compiler, adapter, or deployment evidence.</param>
    /// <exception cref="ArgumentNullException"><paramref name="range"/> or <paramref name="authority"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="authority"/> is empty or white space.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="origin"/> is unsupported.</exception>
    [JsonConstructor]
    public ControlHardLimit(ControlRange range, ControlHardLimitOrigin origin, string authority)
    {
        Range = Guard.RequireNotNull(range);
        if (!Enum.IsDefined(origin))
            throw new ArgumentOutOfRangeException(nameof(origin), origin, "Unsupported hard-limit origin.");
        Origin = origin;
        Authority = Guard.RequireNotNullOrWhiteSpace(authority);
    }

    /// <summary>Inclusive range asserted by this authority.</summary>
    public ControlRange Range { get; }

    /// <summary>Kind of authority establishing the range.</summary>
    public ControlHardLimitOrigin Origin { get; }

    /// <summary>Stable identity and version of the supporting authority.</summary>
    public string Authority { get; }
}

/// <summary>Normalized intersection of semantic, compiler, adapter, and deployment hard limits.</summary>
public sealed record ControlHardLimits
{
    /// <summary>Creates and validates a normalized hard-limit set.</summary>
    /// <param name="constraints">One or more attributable constraints.</param>
    /// <exception cref="ArgumentException">
    /// The collection is empty, contains null or duplicate evidence, or has an empty intersection for an actuator.
    /// </exception>
    [JsonConstructor]
    public ControlHardLimits(ImmutableArray<ControlHardLimit> constraints)
    {
        if (constraints.IsDefaultOrEmpty)
            throw new ArgumentException("Control hard limits require at least one constraint.", nameof(constraints));
        if (constraints.Any(static constraint => constraint is null))
            throw new ArgumentException("Control hard limits cannot contain null constraints.", nameof(constraints));

        var duplicates = constraints
            .GroupBy(static constraint => (constraint.Range.Actuator, constraint.Origin, constraint.Authority))
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicates is not null)
            throw new ArgumentException("A hard-limit authority cannot repeat an actuator constraint.", nameof(constraints));

        Constraints = [.. constraints
            .OrderBy(static constraint => constraint.Range.Actuator)
            .ThenBy(static constraint => constraint.Origin)
            .ThenBy(static constraint => constraint.Authority, StringComparer.Ordinal)];

        foreach (var actuator in Constraints.Select(static constraint => constraint.Range.Actuator).Distinct())
            _ = GetEffectiveRangeCore(Constraints, actuator);
    }

    /// <summary>Attributable constraints in deterministic actuator, origin, and authority order.</summary>
    public ImmutableArray<ControlHardLimit> Constraints { get; }

    /// <summary>Gets the effective intersection for an actuator.</summary>
    /// <param name="actuator">Actuator whose hard range is requested.</param>
    /// <returns>The inclusive intersection of every constraint for <paramref name="actuator"/>.</returns>
    /// <exception cref="ArgumentException">No constraint exists for <paramref name="actuator"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="actuator"/> is unsupported.</exception>
    public ControlRange GetEffectiveRange(ControlActuatorKind actuator)
    {
        _ = ControlUnitCatalog.ForActuator(actuator);
        return GetEffectiveRangeCore(Constraints, actuator);
    }

    /// <summary>Compares normalized hard limits structurally.</summary>
    /// <param name="other">Hard limits to compare.</param>
    /// <returns><see langword="true"/> when every normalized constraint is equal.</returns>
    public bool Equals(ControlHardLimits? other) =>
        ReferenceEquals(this, other)
        || other is not null && Constraints.SequenceEqual(other.Constraints);

    /// <summary>Returns a structural hash code.</summary>
    /// <returns>A hash derived from every normalized constraint.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var constraint in Constraints)
            hash.Add(constraint);
        return hash.ToHashCode();
    }

    static ControlRange GetEffectiveRangeCore(
        ImmutableArray<ControlHardLimit> constraints,
        ControlActuatorKind actuator)
    {
        var found = false;
        long minimum = 0;
        long maximum = ControlQuantity.MaximumPortableValue;
        foreach (var constraint in constraints)
        {
            if (constraint.Range.Actuator != actuator)
                continue;

            found = true;
            minimum = Math.Max(minimum, constraint.Range.Minimum.Value);
            maximum = Math.Min(maximum, constraint.Range.Maximum.Value);
        }

        if (!found)
            throw new ArgumentException($"No hard limit is declared for actuator '{actuator}'.", nameof(actuator));
        if (minimum > maximum)
        {
            throw new ArgumentException(
                $"Hard-limit evidence for actuator '{actuator}' has an empty intersection.",
                nameof(constraints));
        }

        var unit = ControlUnitCatalog.ForActuator(actuator);
        return new(actuator, new(minimum, unit), new(maximum, unit));
    }
}

/// <summary>Normalized multidimensional operational setting selected within hard bounds.</summary>
public sealed record ControlOperatingPoint
{
    /// <summary>Creates a normalized operating point.</summary>
    /// <param name="values">One or more unique actuator values.</param>
    /// <exception cref="ArgumentException">The collection is empty or contains null or duplicate actuators.</exception>
    [JsonConstructor]
    public ControlOperatingPoint(ImmutableArray<ControlActuatorValue> values)
    {
        if (values.IsDefaultOrEmpty)
            throw new ArgumentException("A control operating point requires at least one actuator value.", nameof(values));
        if (values.Any(static value => value is null))
            throw new ArgumentException("A control operating point cannot contain null values.", nameof(values));
        if (values.GroupBy(static value => value.Actuator).Any(static group => group.Count() > 1))
            throw new ArgumentException("A control operating point cannot repeat an actuator.", nameof(values));

        Values = [.. values.OrderBy(static value => value.Actuator)];
    }

    /// <summary>Actuator values in deterministic actuator order.</summary>
    public ImmutableArray<ControlActuatorValue> Values { get; }

    /// <summary>Gets a required actuator value.</summary>
    /// <param name="actuator">Actuator to locate.</param>
    /// <returns>The exact effective value.</returns>
    /// <exception cref="KeyNotFoundException">The operating point does not contain <paramref name="actuator"/>.</exception>
    public ControlActuatorValue Get(ControlActuatorKind actuator)
    {
        foreach (var value in Values)
        {
            if (value.Actuator == actuator)
                return value;
        }

        throw new KeyNotFoundException($"Operating point does not contain actuator '{actuator}'.");
    }

    /// <summary>Creates a new operating point with one actuator replaced.</summary>
    /// <param name="value">Replacement value for an actuator already present.</param>
    /// <returns>A normalized operating point with exactly one replacement.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="KeyNotFoundException">The actuator is not present.</exception>
    public ControlOperatingPoint With(ControlActuatorValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var builder = ImmutableArray.CreateBuilder<ControlActuatorValue>(Values.Length);
        var replaced = false;
        foreach (var current in Values)
        {
            if (current.Actuator == value.Actuator)
            {
                builder.Add(value);
                replaced = true;
            }
            else
            {
                builder.Add(current);
            }
        }

        if (!replaced)
            throw new KeyNotFoundException($"Operating point does not contain actuator '{value.Actuator}'.");
        return new(builder.MoveToImmutable());
    }

    /// <summary>Compares normalized operating points structurally.</summary>
    /// <param name="other">Operating point to compare.</param>
    /// <returns><see langword="true"/> when every actuator value is equal.</returns>
    public bool Equals(ControlOperatingPoint? other) =>
        ReferenceEquals(this, other)
        || other is not null && Values.SequenceEqual(other.Values);

    /// <summary>Returns a structural hash code.</summary>
    /// <returns>A hash derived from every actuator value.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var value in Values)
            hash.Add(value);
        return hash.ToHashCode();
    }
}

/// <summary>Exclusive capacity allocation that lets controlled work consume only its unreserved surplus.</summary>
/// <remarks>
/// <see cref="Capacity"/> is the exclusive allocation already assigned to this loop. A compiler or runtime that
/// shares one physical pool among loops must arbitrate that pool before constructing each loop's budget; budgets
/// from independent loop definitions must not be summed as if they were coordinated reservations.
/// </remarks>
public sealed record ControlWorkloadBudget
{
    /// <summary>Creates an attributable workload budget.</summary>
    /// <param name="actuator">Capacity dimension governed by the budget.</param>
    /// <param name="capacity">Total positive capacity exclusively allocated to this loop.</param>
    /// <param name="reserved">Capacity reserved for other workload classes.</param>
    /// <param name="origin">Authority kind that established the budget.</param>
    /// <param name="authority">Stable identity and version of the authority.</param>
    /// <exception cref="ArgumentException">
    /// Units are incompatible, capacity is zero, reservation consumes all capacity, or <paramref name="authority"/>
    /// is empty or white space.
    /// </exception>
    /// <exception cref="ArgumentNullException"><paramref name="authority"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="actuator"/> or <paramref name="origin"/> is unsupported.
    /// </exception>
    [JsonConstructor]
    public ControlWorkloadBudget(
        ControlActuatorKind actuator,
        ControlQuantity capacity,
        ControlQuantity reserved,
        ControlHardLimitOrigin origin,
        string authority)
    {
        var unit = ControlUnitCatalog.ForActuator(actuator);
        if (capacity.Unit != unit || reserved.Unit != unit)
            throw new ArgumentException($"Budget for '{actuator}' requires unit '{unit}'.", nameof(capacity));
        if (capacity.Value == 0)
            throw new ArgumentException("A workload budget requires positive capacity.", nameof(capacity));
        if (reserved.Value >= capacity.Value)
            throw new ArgumentException("Reserved capacity must leave positive surplus for the controlled workload.", nameof(reserved));
        if (!Enum.IsDefined(origin))
            throw new ArgumentOutOfRangeException(nameof(origin), origin, "Unsupported workload-budget origin.");

        Actuator = actuator;
        Capacity = capacity;
        Reserved = reserved;
        Origin = origin;
        Authority = Guard.RequireNotNullOrWhiteSpace(authority);
    }

    /// <summary>Capacity dimension governed by the budget.</summary>
    public ControlActuatorKind Actuator { get; }

    /// <summary>Total capacity exclusively allocated to this loop.</summary>
    public ControlQuantity Capacity { get; }

    /// <summary>Capacity unavailable to the controlled workload.</summary>
    public ControlQuantity Reserved { get; }

    /// <summary>Authority kind that established the budget.</summary>
    public ControlHardLimitOrigin Origin { get; }

    /// <summary>Stable identity and version of the authority.</summary>
    public string Authority { get; }

    /// <summary>Gets capacity available after reservation.</summary>
    /// <returns>A positive quantity in the actuator's canonical unit.</returns>
    [JsonIgnore]
    public ControlQuantity Available => new(Capacity.Value - Reserved.Value, Capacity.Unit);
}

/// <summary>One typed measurement or explicit unavailable-measurement result.</summary>
public sealed record ControlMeasurement
{
    /// <summary>Creates a typed measurement result.</summary>
    /// <param name="metric">Semantic measurement kind.</param>
    /// <param name="statistic">Statistic over the observation window.</param>
    /// <param name="availability">Whether a trustworthy value was produced.</param>
    /// <param name="value">Measured value when available.</param>
    /// <param name="sampleCount">Number of source samples represented by an available value.</param>
    /// <param name="failureCode">Stable adapter-owned failure code when unavailable.</param>
    /// <exception cref="ArgumentException">
    /// Availability, value, sample count, failure code, unit, or bounded-ratio semantics conflict.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="metric"/>, <paramref name="statistic"/>, or <paramref name="availability"/> is unsupported,
    /// or <paramref name="sampleCount"/> is outside the portable non-negative range.
    /// </exception>
    [JsonConstructor]
    public ControlMeasurement(
        ControlMetricKind metric,
        ControlStatisticKind statistic,
        ControlMeasurementAvailability availability,
        ControlQuantity? value = null,
        long sampleCount = 0,
        string? failureCode = null)
    {
        var expectedUnit = ControlUnitCatalog.ForMetric(metric);
        if (!Enum.IsDefined(statistic))
            throw new ArgumentOutOfRangeException(nameof(statistic), statistic, "Unsupported control statistic.");
        if (!Enum.IsDefined(availability))
            throw new ArgumentOutOfRangeException(nameof(availability), availability, "Unsupported measurement availability.");
        if (sampleCount is < 0 or > ControlQuantity.MaximumPortableValue)
            throw new ArgumentOutOfRangeException(nameof(sampleCount), sampleCount, "A sample count must be non-negative and portable.");

        failureCode = failureCode.TrimmedEmptyOrWhiteSpaceAs();
        if (availability == ControlMeasurementAvailability.Available)
        {
            if (value is not { } measured)
                throw new ArgumentException("An available measurement requires a value.", nameof(value));
            if (measured.Unit != expectedUnit)
                throw new ArgumentException($"Metric '{metric}' requires unit '{expectedUnit}'.", nameof(value));
            if (sampleCount == 0)
                throw new ArgumentException("An available measurement requires at least one sample.", nameof(sampleCount));
            if (failureCode is not null)
                throw new ArgumentException("An available measurement cannot carry a failure code.", nameof(failureCode));
            if (expectedUnit == ControlUnit.BasisPoints && measured.Value > 10_000)
                throw new ArgumentException("A ratio measurement cannot exceed 10,000 basis points.", nameof(value));
        }
        else
        {
            if (value is not null || sampleCount != 0 || failureCode is null)
            {
                throw new ArgumentException(
                    "An unavailable measurement requires only a stable failure code.",
                    nameof(availability));
            }
        }

        Metric = metric;
        Statistic = statistic;
        Availability = availability;
        Value = value;
        SampleCount = sampleCount;
        FailureCode = failureCode;
    }

    /// <summary>Semantic measurement kind.</summary>
    public ControlMetricKind Metric { get; }

    /// <summary>Statistic over the observation window.</summary>
    public ControlStatisticKind Statistic { get; }

    /// <summary>Whether a trustworthy value was produced.</summary>
    public ControlMeasurementAvailability Availability { get; }

    /// <summary>Measured value when available.</summary>
    public ControlQuantity? Value { get; }

    /// <summary>Number of represented samples, or zero when unavailable.</summary>
    [JsonConverter(typeof(StringEncodedInt64JsonConverter))]
    public long SampleCount { get; }

    /// <summary>Stable failure code when unavailable.</summary>
    public string? FailureCode { get; }
}

/// <summary>Soft pressure objective with an explicit hysteresis band.</summary>
public sealed record ControlObjective
{
    /// <summary>Creates a pressure objective.</summary>
    /// <param name="metric">Required measurement kind.</param>
    /// <param name="statistic">Required window statistic.</param>
    /// <param name="direction">Whether higher or lower measured values indicate congestion.</param>
    /// <param name="recoveryBoundary">Inclusive boundary on the healthy side of the hysteresis band.</param>
    /// <param name="congestionBoundary">Inclusive boundary on the congested side of the hysteresis band.</param>
    /// <exception cref="ArgumentException">
    /// Units are incompatible, a ratio exceeds one whole, or the hysteresis band is empty or inverted.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="metric"/>, <paramref name="statistic"/>, or <paramref name="direction"/> is unsupported.
    /// </exception>
    [JsonConstructor]
    public ControlObjective(
        ControlMetricKind metric,
        ControlStatisticKind statistic,
        ControlObjectiveDirection direction,
        ControlQuantity recoveryBoundary,
        ControlQuantity congestionBoundary)
    {
        var unit = ControlUnitCatalog.ForMetric(metric);
        if (!Enum.IsDefined(statistic))
            throw new ArgumentOutOfRangeException(nameof(statistic), statistic, "Unsupported control statistic.");
        if (!Enum.IsDefined(direction))
            throw new ArgumentOutOfRangeException(nameof(direction), direction, "Unsupported control-objective direction.");
        if (recoveryBoundary.Unit != unit || congestionBoundary.Unit != unit)
            throw new ArgumentException($"Objective for '{metric}' requires unit '{unit}'.", nameof(recoveryBoundary));
        if (unit == ControlUnit.BasisPoints
            && (recoveryBoundary.Value > 10_000 || congestionBoundary.Value > 10_000))
        {
            throw new ArgumentException("A ratio objective cannot exceed 10,000 basis points.", nameof(congestionBoundary));
        }
        if (direction == ControlObjectiveDirection.HigherIsCongested
                && recoveryBoundary.Value >= congestionBoundary.Value
            || direction == ControlObjectiveDirection.LowerIsCongested
                && recoveryBoundary.Value <= congestionBoundary.Value)
        {
            throw new ArgumentException(
                "A pressure objective requires a non-empty hysteresis band ordered from its healthy side toward congestion.",
                nameof(recoveryBoundary));
        }

        Metric = metric;
        Statistic = statistic;
        Direction = direction;
        RecoveryBoundary = recoveryBoundary;
        CongestionBoundary = congestionBoundary;
    }

    /// <summary>Required measurement kind.</summary>
    public ControlMetricKind Metric { get; }

    /// <summary>Required window statistic.</summary>
    public ControlStatisticKind Statistic { get; }

    /// <summary>Whether higher or lower measured values indicate congestion.</summary>
    public ControlObjectiveDirection Direction { get; }

    /// <summary>Inclusive boundary on the healthy side of the hysteresis band.</summary>
    public ControlQuantity RecoveryBoundary { get; }

    /// <summary>Inclusive boundary on the congested side of the hysteresis band.</summary>
    public ControlQuantity CongestionBoundary { get; }
}
