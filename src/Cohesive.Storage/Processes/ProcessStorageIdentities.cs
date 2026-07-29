using System.Globalization;
using System.Text.Json.Serialization;
using Cohesive.Model.Serialization;

namespace Cohesive.Storage.Processes;

/// <summary>Stable idempotency identity of one atomic Process-storage commit.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct ProcessCommitId
{
    /// <summary>Creates a Process commit identity.</summary>
    /// <param name="value">Stable identity reused when an ambiguous commit is retried.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white-space.</exception>
    [JsonConstructor]
    public ProcessCommitId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw stable commit identity.</summary>
    public string Value { get; }

    /// <summary>Returns the raw stable commit identity.</summary>
    /// <returns>The value supplied at construction.</returns>
    public override string ToString() => Value;
}

/// <summary>Deterministic fingerprint of one complete Process commit intent.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct ProcessCommitFingerprint
{
    /// <summary>Creates a Process commit fingerprint.</summary>
    /// <param name="value">Versioned algorithm name and lowercase digest.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white-space.</exception>
    [JsonConstructor]
    public ProcessCommitFingerprint(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Versioned deterministic fingerprint value.</summary>
    public string Value { get; }

    /// <summary>Returns the versioned deterministic fingerprint.</summary>
    /// <returns>The value supplied at construction.</returns>
    public override string ToString() => Value;
}

/// <summary>Deterministic fingerprint of one complete canonical Process continuation.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct ProcessContinuationFingerprint
{
    /// <summary>Creates a Process continuation fingerprint.</summary>
    /// <param name="value">Versioned algorithm name and lowercase digest.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white-space.</exception>
    [JsonConstructor]
    public ProcessContinuationFingerprint(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Versioned deterministic fingerprint value.</summary>
    public string Value { get; }

    /// <summary>Returns the versioned deterministic fingerprint.</summary>
    /// <returns>The value supplied at construction.</returns>
    public override string ToString() => Value;
}

/// <summary>Monotonic physical compare-and-swap revision of one stored Process aggregate.</summary>
/// <remarks>
/// This physical revision is intentionally distinct from semantic Process-control revisions, durable-operation
/// fences, and provider-specific ETags.
/// </remarks>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct ProcessStorageRevision
{
    /// <summary>Initial revision of a newly persisted Process aggregate.</summary>
    public static ProcessStorageRevision Initial { get; } = new("1");

    /// <summary>Creates a positive canonical Process-storage revision.</summary>
    /// <param name="value">Canonical invariant-culture positive 64-bit integer string.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="value"/> is not the canonical encoding of a positive 64-bit integer.
    /// </exception>
    [JsonConstructor]
    public ProcessStorageRevision(string value)
    {
        Value = RequirePositiveOrdinal(value, nameof(value), out var ordinal);
        Ordinal = ordinal;
    }

    /// <summary>Canonical positive revision string.</summary>
    public string Value { get; }

    /// <summary>Positive numeric revision used for ordering and compare-and-swap.</summary>
    [JsonIgnore]
    public long Ordinal { get; }

    /// <summary>Returns the canonical revision string.</summary>
    /// <returns>The canonical positive revision supplied at construction.</returns>
    public override string ToString() => Value;

    internal ProcessStorageRevision Next() =>
        new(checked(Ordinal + 1).ToString(CultureInfo.InvariantCulture));

    internal static string RequirePositiveOrdinal(string value, string parameterName, out long ordinal)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out ordinal)
            || ordinal <= 0
            || !string.Equals(value, ordinal.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The value must be a canonical positive 64-bit integer string.",
                parameterName);
        }

        return value;
    }
}

/// <summary>Monotonic ownership fence of a Process activation worker.</summary>
/// <remarks>
/// Reclaiming an expired lease creates a greater fence. Every prior owner becomes stale even if it later resumes.
/// </remarks>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct ProcessWorkerFence
{
    /// <summary>Creates a positive canonical worker fence.</summary>
    /// <param name="value">Canonical invariant-culture positive 64-bit integer string.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="value"/> is not the canonical encoding of a positive 64-bit integer.
    /// </exception>
    [JsonConstructor]
    public ProcessWorkerFence(string value)
    {
        Value = ProcessStorageRevision.RequirePositiveOrdinal(value, nameof(value), out var ordinal);
        Ordinal = ordinal;
    }

    /// <summary>Canonical positive fence string.</summary>
    public string Value { get; }

    /// <summary>Positive numeric fence used for ordering.</summary>
    [JsonIgnore]
    public long Ordinal { get; }

    /// <summary>Returns the canonical fence string.</summary>
    /// <returns>The canonical positive fence supplied at construction.</returns>
    public override string ToString() => Value;
}
