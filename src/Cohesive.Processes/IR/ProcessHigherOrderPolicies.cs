using System.Text.Json.Serialization;

namespace Cohesive.Processes.IR;

/// <summary>Semantic purpose for invoking a child Process.</summary>
public enum ProcessChildPurpose
{
    /// <summary>No purpose was declared; invalid in canonical Process IR.</summary>
    Unspecified = 0,

    /// <summary>The child performs ordinary coordinated work.</summary>
    Work = 1,

    /// <summary>The child performs explicitly authored compensation.</summary>
    Compensation = 2,

    /// <summary>The child performs explicitly authored reconciliation.</summary>
    Reconciliation = 3
}

/// <summary>How parent cancellation affects a child Process invocation.</summary>
public enum ProcessChildCancellationPolicy
{
    /// <summary>No cancellation behavior was declared; invalid in canonical Process IR.</summary>
    Unspecified = 0,

    /// <summary>Request cancellation of child work when cancellation of the parent is accepted.</summary>
    Propagate = 1,

    /// <summary>Leave child work independently active after cancellation of the parent is accepted.</summary>
    Detach = 2
}

/// <summary>How one bounded partition-work occurrence responds to a terminal child failure.</summary>
public enum ProcessPartitionFailurePolicy
{
    /// <summary>No failure behavior was declared; invalid in canonical Process IR.</summary>
    Unspecified = 0,

    /// <summary>Stop admitting sibling work and close the occurrence as soon as one child fails.</summary>
    FailFast = 1,

    /// <summary>
    /// Continue admitting bounded sibling work, retain every terminal outcome, and close only after every child
    /// settles.
    /// </summary>
    AwaitAll = 2
}

/// <summary>One named concurrent-work limit within a bounded partition-work occurrence.</summary>
public sealed record ProcessCapacityDomainLimit
{
    /// <summary>Creates one capacity-domain limit.</summary>
    /// <param name="identity">Stable domain identity produced by the partition-local capacity expression.</param>
    /// <param name="maximumParallelism">Maximum simultaneously active children assigned to the domain.</param>
    [JsonConstructor]
    public ProcessCapacityDomainLimit(string identity, int maximumParallelism)
    {
        Identity = identity;
        MaximumParallelism = maximumParallelism;
    }

    /// <summary>Stable domain identity produced by the partition-local capacity expression.</summary>
    public string Identity { get; }

    /// <summary>Maximum simultaneously active children assigned to this domain.</summary>
    public int MaximumParallelism { get; }
}

/// <summary>Finite limits for one bounded partition-work occurrence.</summary>
public sealed record ProcessWorkLimits
{
    /// <summary>Creates explicit bounded-work limits.</summary>
    /// <param name="maximumItems">Maximum number of distinct partition items admitted by one occurrence.</param>
    /// <param name="maximumStartsPerActivation">Maximum child starts produced by one finite activation.</param>
    /// <param name="maximumParallelism">Maximum simultaneously active child Process invocations.</param>
    [JsonConstructor]
    public ProcessWorkLimits(
        int maximumItems,
        int maximumStartsPerActivation,
        int maximumParallelism)
    {
        MaximumItems = maximumItems;
        MaximumStartsPerActivation = maximumStartsPerActivation;
        MaximumParallelism = maximumParallelism;
    }

    /// <summary>Maximum number of distinct partition items admitted by one occurrence.</summary>
    public int MaximumItems { get; }

    /// <summary>Maximum child starts produced by one finite activation.</summary>
    public int MaximumStartsPerActivation { get; }

    /// <summary>Maximum simultaneously active child Process invocations.</summary>
    public int MaximumParallelism { get; }
}

/// <summary>Finite progress limits for recurrence across durable activations.</summary>
public sealed record ProcessRecurrencePolicy
{
    /// <summary>Creates explicit recurrence limits.</summary>
    /// <param name="maximumOccurrences">Maximum number of repeat decisions admitted by one recurrence occurrence.</param>
    /// <param name="maximumUnchangedProgressOccurrences">
    /// Maximum consecutive repeat decisions permitted without a change to the authored progress value.
    /// </param>
    [JsonConstructor]
    public ProcessRecurrencePolicy(
        int maximumOccurrences,
        int maximumUnchangedProgressOccurrences)
    {
        MaximumOccurrences = maximumOccurrences;
        MaximumUnchangedProgressOccurrences = maximumUnchangedProgressOccurrences;
    }

    /// <summary>Maximum number of repeat decisions admitted by one recurrence occurrence.</summary>
    public int MaximumOccurrences { get; }

    /// <summary>Maximum consecutive repeat decisions permitted without observable progress.</summary>
    public int MaximumUnchangedProgressOccurrences { get; }
}
