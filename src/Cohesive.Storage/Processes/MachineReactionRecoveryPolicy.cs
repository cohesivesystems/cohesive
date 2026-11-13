using Cohesive.Execution;

namespace Cohesive.Storage.Processes;

/// <summary>Validated authorization and retry policy for background reaction recovery.</summary>
public sealed class MachineReactionRecoveryPolicy
{
    /// <summary>Creates an immutable recovery policy.</summary>
    /// <param name="authorize">Fresh permission to recover a retained start; discovery never grants permission.</param>
    /// <param name="maximumAttempts">Positive physical dispatch budget, default five.</param>
    /// <param name="retryDelay">Nonnegative delay after failure or an interrupted claim's expiry; default thirty seconds.</param>
    /// <exception cref="ArgumentNullException">Authorization is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The budget or delay is invalid.</exception>
    public MachineReactionRecoveryPolicy(Func<OperationContext, ProcessStartRequest, ValueTask<bool>> authorize,
        int maximumAttempts = 5, TimeSpan? retryDelay = null)
    {
        Authorize = authorize ?? throw new ArgumentNullException(nameof(authorize));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumAttempts);
        MaximumAttempts = maximumAttempts;
        RetryDelay = retryDelay ?? TimeSpan.FromSeconds(30);
        if (RetryDelay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(retryDelay));
    }

    /// <summary>Fresh recovery authorization evaluated before execution or inspection.</summary>
    public Func<OperationContext, ProcessStartRequest, ValueTask<bool>> Authorize { get; }
    /// <summary>Maximum physical dispatches admitted by background recovery.</summary>
    public int MaximumAttempts { get; }
    /// <summary>Delay before another background dispatch may be admitted.</summary>
    public TimeSpan RetryDelay { get; }
}
