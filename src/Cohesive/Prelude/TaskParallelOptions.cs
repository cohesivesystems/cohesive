namespace Cohesive.Prelude;

/// <summary>
/// Options controlling throttled task execution.
/// </summary>
public readonly struct TaskParallelOptions
{
    /// <summary>
    /// Creates a new task parallel execution option set.
    /// </summary>
    /// <param name="maxConcurrency">The maximum number of operations to run concurrently.</param>
    /// <param name="preserveOrder">Whether results should be written in source order.</param>
    /// <param name="failFast">Whether processing should stop after the first observed failure.</param>
    public TaskParallelOptions(int maxConcurrency, bool preserveOrder = true, bool failFast = true)
    {
        MaxConcurrency = maxConcurrency;
        PreserveOrder = preserveOrder;
        FailFast = failFast;
    }

    /// <summary>
    /// The maximum number of operations to run concurrently.
    /// </summary>
    public int MaxConcurrency { get; }

    /// <summary>
    /// Whether results should be written in source order.
    /// </summary>
    public bool PreserveOrder { get; }

    /// <summary>
    /// Whether processing should stop after the first observed failure.
    /// </summary>
    public bool FailFast { get; }
}
