using System.Globalization;
using System.Text.Json.Serialization;
using Cohesive.Model.Serialization;

namespace Cohesive.Processes.Distribution;

/// <summary>Stable identity of one logical worker-pool admission and policy boundary.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct ProcessWorkerPoolId
{
    /// <summary>Creates a worker-pool identity.</summary>
    /// <param name="value">Stable provider-neutral pool identity.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white space.</exception>
    [JsonConstructor]
    public ProcessWorkerPoolId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw provider-neutral pool identity.</summary>
    public string Value { get; }

    /// <summary>Returns the raw pool identity.</summary>
    /// <returns>The value supplied at construction.</returns>
    public override string ToString() => Value;
}

/// <summary>Stable logical identity of one distributable canonical Process work unit.</summary>
/// <remarks>Delivery, recovery, and retry retain this identity.</remarks>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct ProcessWorkId
{
    /// <summary>Creates a logical work identity.</summary>
    /// <param name="value">Stable identity retained across physical delivery attempts.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white space.</exception>
    [JsonConstructor]
    public ProcessWorkId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw stable logical work identity.</summary>
    public string Value { get; }

    /// <summary>Returns the raw work identity.</summary>
    /// <returns>The value supplied at construction.</returns>
    public override string ToString() => Value;
}

/// <summary>Stable key that deduplicates semantically identical work submission intent.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct ProcessWorkIdempotencyKey
{
    /// <summary>Creates a work-submission idempotency key.</summary>
    /// <param name="value">Stable caller or compiler assigned idempotency identity.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white space.</exception>
    [JsonConstructor]
    public ProcessWorkIdempotencyKey(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw idempotency identity.</summary>
    public string Value { get; }

    /// <summary>Returns the raw idempotency identity.</summary>
    /// <returns>The value supplied at construction.</returns>
    public override string ToString() => Value;
}

/// <summary>Unique identity of one concrete worker-process incarnation.</summary>
/// <remarks>A process restart must register a new incarnation even when it represents the same logical host.</remarks>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct ProcessWorkerIncarnationId
{
    /// <summary>Creates a worker-incarnation identity.</summary>
    /// <param name="value">Unique identity for one worker-process lifetime.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white space.</exception>
    [JsonConstructor]
    public ProcessWorkerIncarnationId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw worker-incarnation identity.</summary>
    public string Value { get; }

    /// <summary>Returns the raw incarnation identity.</summary>
    /// <returns>The value supplied at construction.</returns>
    public override string ToString() => Value;
}

/// <summary>Stable identity of one physical dispatch or competing-consumer delivery attempt.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct ProcessWorkDispatchId
{
    /// <summary>Creates a dispatch identity.</summary>
    /// <param name="value">Stable identity of one physical work delivery attempt.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white space.</exception>
    [JsonConstructor]
    public ProcessWorkDispatchId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw physical dispatch identity.</summary>
    public string Value { get; }

    /// <summary>Returns the raw dispatch identity.</summary>
    /// <returns>The value supplied at construction.</returns>
    public override string ToString() => Value;
}

/// <summary>Stable identity of one claim request, retained across exact provider retries.</summary>
/// <remarks>
/// Concurrent lanes of one worker use distinct request identities. Repeating the same identity recovers the exact
/// claim created by an outcome-ambiguous store call rather than competing for another work unit.
/// </remarks>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct ProcessWorkClaimRequestId
{
    /// <summary>Creates a claim-request identity.</summary>
    /// <param name="value">Stable identity of one worker-lane claim request.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white space.</exception>
    [JsonConstructor]
    public ProcessWorkClaimRequestId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw claim-request identity.</summary>
    public string Value { get; }

    /// <summary>Returns the raw claim-request identity.</summary>
    /// <returns>The value supplied at construction.</returns>
    public override string ToString() => Value;
}

/// <summary>Monotonic ownership fence of one logical distributed work unit.</summary>
/// <remarks>
/// Reclaiming work creates a greater fence and makes every earlier claim stale. This fences ownership of one
/// distributed logical work unit; it is intentionally not interchangeable with the Storage runtime's
/// <c>ProcessWorkerFence</c>, which fences a Process aggregate activation lease.
/// </remarks>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct ProcessWorkFence
{
    /// <summary>Creates a positive canonical work fence.</summary>
    /// <param name="value">Canonical invariant-culture positive 64-bit integer string.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="value"/> is not a canonical positive 64-bit integer string.
    /// </exception>
    [JsonConstructor]
    public ProcessWorkFence(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var ordinal)
            || ordinal <= 0
            || !string.Equals(value, ordinal.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A work fence must be a canonical positive 64-bit integer string.",
                nameof(value));
        }

        Value = value;
        Ordinal = ordinal;
    }

    /// <summary>Canonical positive fence string.</summary>
    public string Value { get; }

    /// <summary>Positive numeric fence used for ordering and monotonic increment.</summary>
    [JsonIgnore]
    public long Ordinal { get; }

    /// <summary>Returns the canonical fence string.</summary>
    /// <returns>The canonical positive fence supplied at construction.</returns>
    public override string ToString() => Value;

    internal ProcessWorkFence Next() =>
        new(checked(Ordinal + 1).ToString(CultureInfo.InvariantCulture));
}
