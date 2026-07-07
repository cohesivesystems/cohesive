namespace Cohesive.Processes.Model;

/// <summary>
/// Base type for transaction conflict handling policies.
/// </summary>
public abstract record OnConflictPolicy
{
    /// <summary>
    /// Fails immediately on conflict.
    /// </summary>
    public static OnConflictPolicy Fail() => new FailOnConflictPolicy();

    /// <summary>
    /// Retries conflicts with backoff.
    /// </summary>
    public static OnConflictPolicy RetryWithBackoff(int maxAttempts, TimeSpan? initialDelay = null)
        => new RetryWithBackoffPolicy(maxAttempts, initialDelay);

    /// <summary>
    /// Escalates conflicts to saga mode.
    /// </summary>
    public static OnConflictPolicy ConvertToSaga() => new ConvertToSagaOnConflictPolicy();
}

/// <summary>
/// Retries conflicts with exponential backoff.
/// </summary>
public sealed record RetryWithBackoffPolicy : OnConflictPolicy
{
    /// <summary>
    /// Creates retry policy.
    /// </summary>
    public RetryWithBackoffPolicy(int maxAttempts, TimeSpan? initialDelay = null)
    {
        if (maxAttempts <= 0)
            throw new SemanticRuleViolationException("RetryWithBackoff requires maxAttempts > 0.");

        MaxAttempts = maxAttempts;
        InitialDelay = initialDelay ?? TimeSpan.FromMilliseconds(25);
    }

    /// <summary>
    /// Maximum attempts.
    /// </summary>
    public int MaxAttempts { get; }

    /// <summary>
    /// Initial delay for attempt 2.
    /// </summary>
    public TimeSpan InitialDelay { get; }
}

/// <summary>
/// Fails immediately on conflicts.
/// </summary>
public sealed record FailOnConflictPolicy : OnConflictPolicy;

/// <summary>
/// Converts conflict handling to saga escalation.
/// </summary>
public sealed record ConvertToSagaOnConflictPolicy : OnConflictPolicy;

/// <summary>
/// Custom conflict policy using user callback.
/// </summary>
public sealed record CustomOnConflictPolicy(
    Func<OperationContext, ProcessConflictContext, Task<ConflictResolutionDecision>> ResolveAsync
) : OnConflictPolicy;

/// <summary>
/// Conflict callback context.
/// </summary>
public sealed record ProcessConflictContext(
    ProcessTransactionScope Scope,
    int Attempt,
    ProcessConcurrencyConflictException Conflict
);

/// <summary>
/// Conflict decisions returned by custom policies.
/// </summary>
public enum ConflictResolutionDecision
{
    Retry = 0,
    Fail = 1,
    ConvertToSaga = 2
}
