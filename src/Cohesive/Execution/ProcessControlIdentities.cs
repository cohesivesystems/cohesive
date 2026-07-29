using System.Globalization;
using System.Text.Json.Serialization;
using Cohesive.Model.Serialization;

namespace Cohesive.Execution;

/// <summary>Stable identity of one protocol-neutral Process lifecycle command.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct ProcessControlCommandId
{
    /// <summary>Creates a Process control-command identity.</summary>
    /// <param name="value">Stable command identity retained across transport retry and replay.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white-space.</exception>
    [JsonConstructor]
    public ProcessControlCommandId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw stable command identity.</summary>
    public string Value { get; }

    /// <summary>Returns the raw stable command identity.</summary>
    /// <returns>The value supplied at construction.</returns>
    public override string ToString() => Value;
}

/// <summary>Stable logical deduplication key for one Process lifecycle command.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct ProcessControlIdempotencyKey
{
    /// <summary>Creates a Process control-command idempotency key.</summary>
    /// <param name="value">Stable key reused for logically equivalent command submissions.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white-space.</exception>
    [JsonConstructor]
    public ProcessControlIdempotencyKey(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw logical deduplication key.</summary>
    public string Value { get; }

    /// <summary>Returns the raw logical deduplication key.</summary>
    /// <returns>The value supplied at construction.</returns>
    public override string ToString() => Value;
}

/// <summary>Stable identity of one invariant-preserving Process safe point.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct ProcessSafePointId
{
    /// <summary>Creates a Process safe-point identity.</summary>
    /// <param name="value">Stable identity derived by the Process interpreter.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white-space.</exception>
    [JsonConstructor]
    public ProcessSafePointId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw stable safe-point identity.</summary>
    public string Value { get; }

    /// <summary>Returns the raw safe-point identity.</summary>
    /// <returns>The value supplied at construction.</returns>
    public override string ToString() => Value;
}

/// <summary>
/// Monotonic semantic revision and optimistic fence for one Process control state.
/// </summary>
/// <remarks>
/// This revision fences lifecycle decisions; it is intentionally distinct from an external-operation ownership
/// fence and from a physical Storage record version. The canonical string encoding preserves the full positive
/// 64-bit range across JSON hosts.
/// </remarks>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct ProcessControlRevision : IComparable<ProcessControlRevision>
{
    /// <summary>Initial semantic control revision.</summary>
    public static ProcessControlRevision Initial { get; } = new("1");

    /// <summary>Creates a positive canonical Process control revision.</summary>
    /// <param name="value">Canonical invariant-culture positive 64-bit integer string.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="value"/> is not the canonical encoding of a positive 64-bit integer.
    /// </exception>
    [JsonConstructor]
    public ProcessControlRevision(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var ordinal)
            || ordinal <= 0
            || !string.Equals(value, ordinal.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A Process control revision must be a canonical positive 64-bit integer string.",
                nameof(value));
        }

        Value = value;
        Ordinal = ordinal;
    }

    /// <summary>Canonical positive revision string.</summary>
    public string Value { get; }

    internal long Ordinal { get; }

    /// <summary>Returns the canonical revision string.</summary>
    /// <returns>The canonical positive revision supplied at construction.</returns>
    public override string ToString() => Value;

    /// <summary>Compares semantic control revisions by their positive numeric ordinal.</summary>
    /// <param name="other">Revision to compare with this value.</param>
    /// <returns>
    /// A negative value when this revision precedes <paramref name="other"/>, zero when equal, or a positive value
    /// when this revision follows <paramref name="other"/>.
    /// </returns>
    public int CompareTo(ProcessControlRevision other) => Ordinal.CompareTo(other.Ordinal);

    internal ProcessControlRevision Next()
    {
        var next = checked(Ordinal + 1);
        return new(next.ToString(CultureInfo.InvariantCulture));
    }
}
