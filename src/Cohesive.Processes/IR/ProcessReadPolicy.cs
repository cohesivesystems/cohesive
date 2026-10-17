using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Cohesive.Processes.IR;

/// <summary>Bounded execution policy for an observational process read.</summary>
/// <remarks>Attempts include the first call. Only explicitly selected structured failure codes permit retry;
/// unclassified exceptions and caller cancellation propagate. Timeout covers all attempts and delays and requests cooperative
/// cancellation, waiting for the host to quiesce. Durable progress requires a history-backed interpreter;
/// activity redelivery may repeat an interrupted observational read, but does not reset its logical attempt budget.
/// Durable deadline exhaustion is a structured operation failure; the activation-local interpreter cancels its activation.</remarks>
public sealed record ProcessReadPolicy
{
    /// <summary>Declares a finite read budget without selecting a persistence or scheduling implementation.</summary>
    /// <param name="timeout">Optional positive total deadline, including backoff.</param>
    /// <param name="maxAttempts">Positive maximum number of logical attempts, including the first.</param>
    /// <param name="retryDelay">Nonnegative fixed delay between retryable attempts.</param>
    /// <param name="retryableFailureCodes">Explicit adapter-classified structured failures eligible for retry.</param>
    /// <param name="progress">Required retention of attempt progress and the original deadline.</param>
    /// <exception cref="ArgumentOutOfRangeException">A budget or duration is outside its supported range.</exception>
    /// <exception cref="ArgumentException">Retry codes are empty, duplicated, or absent for multiple attempts.</exception>
    [JsonConstructor]
    public ProcessReadPolicy(TimeSpan? timeout = null, int maxAttempts = 1,
        TimeSpan retryDelay = default, ImmutableArray<string> retryableFailureCodes = default,
        ProcessReadProgress progress = ProcessReadProgress.ActivationLocal)
    {
        if (!Enum.IsDefined(progress)) throw new ArgumentOutOfRangeException(nameof(progress));
        Progress = progress;
        if (maxAttempts < 1) throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        if (timeout is { } duration && (duration <= TimeSpan.Zero || duration.TotalMilliseconds > uint.MaxValue - 1))
            throw new ArgumentOutOfRangeException(nameof(timeout));
        if (retryDelay < TimeSpan.Zero || retryDelay.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(retryDelay));
        var codes = retryableFailureCodes.IsDefault ? ImmutableArray<string>.Empty : retryableFailureCodes;
        if (codes.Any(string.IsNullOrWhiteSpace) || codes.Distinct(StringComparer.Ordinal).Count() != codes.Length
            || (maxAttempts > 1 && codes.IsEmpty))
            throw new ArgumentException("Retries require distinct, nonempty classified failure codes.", nameof(retryableFailureCodes));
        Timeout = timeout; MaxAttempts = maxAttempts; RetryDelay = retryDelay;
        RetryableFailureCodes = codes.Sort(StringComparer.Ordinal);
    }
    /// <summary>Required attempt retention. Durable progress cannot be realized by an ephemeral executor.</summary>
    public ProcessReadProgress Progress { get; }
    /// <summary>Total cooperative budget across all attempts, including queue time and backoff on durable targets.</summary>
    public TimeSpan? Timeout { get; }
    /// <summary>Maximum logical attempts. A durable activity may be redelivered after an interrupted read.</summary>
    public int MaxAttempts { get; }
    /// <summary>Fixed delay between selected failures.</summary>
    public TimeSpan RetryDelay { get; }
    /// <summary>Adapter-classified failures explicitly approved for retry.</summary>
    public ImmutableArray<string> RetryableFailureCodes { get; }
}

/// <summary>Required lifetime of observational read attempt evidence.</summary>
public enum ProcessReadProgress
{
    /// <summary>Attempts and deadline are retained only by the current in-memory activation.</summary>
    ActivationLocal = 0,
    /// <summary>Attempts, backoff, deadline and completed observation survive orchestration replay.</summary>
    Durable = 1
}
