using System.Text.Json.Serialization;

namespace Cohesive.Api.Services;

/// <summary>Recovery lifetime required of a Process invocation, independent of entity persistence.</summary>
public enum ServiceProcessLifetime
{
    /// <summary>Invocation-local execution without restart or background continuation.</summary>
    Ephemeral = 1,
    /// <summary>Execution admitted through a durable recovery authority.</summary>
    Durable = 2
}

/// <summary>The event promised by a service invocation before returning its normal response.</summary>
public enum ServiceProcessCompletion
{
    /// <summary>Return a receipt after durable admission, without promising terminal completion.</summary>
    Admission = 1,
    /// <summary>Await terminal execution within a cooperative cancellation budget.</summary>
    Terminal = 2
}

/// <summary>Portable invocation policy; neither lifetime nor completion implies whole-Process atomicity.</summary>
/// <remarks>Consistency remains owned by Process requirements and qualified storage/host bindings. A target must
/// reject unsupported policies rather than weaken them. A terminal deadline requests cancellation; it does not
/// authorize background continuation, rollback, or automatic retries after an uncertain write.</remarks>
public sealed record ServiceProcessExecution
{
    /// <summary>Declares lifetime and completion separately, with a required finite terminal budget.</summary>
    /// <exception cref="ArgumentOutOfRangeException">An enum is unknown or the budget cannot be represented by a timer.</exception>
    /// <exception cref="ArgumentException">Ephemeral admission or a budget inconsistent with completion is requested.</exception>
    [JsonConstructor]
    public ServiceProcessExecution(ServiceProcessLifetime lifetime, ServiceProcessCompletion completion, TimeSpan? timeout = null)
    {
        if (!Enum.IsDefined(lifetime)) throw new ArgumentOutOfRangeException(nameof(lifetime));
        if (!Enum.IsDefined(completion)) throw new ArgumentOutOfRangeException(nameof(completion));
        if (completion == ServiceProcessCompletion.Admission && lifetime != ServiceProcessLifetime.Durable)
            throw new ArgumentException("Admission-only completion requires a durable execution authority.", nameof(completion));
        if ((completion == ServiceProcessCompletion.Terminal) != timeout.HasValue)
            throw new ArgumentException("Only terminal completion requires an explicit cancellation budget.", nameof(timeout));
        if (timeout is { } budget && (budget <= TimeSpan.Zero || budget.TotalMilliseconds > uint.MaxValue - 1))
            throw new ArgumentOutOfRangeException(nameof(timeout));
        Lifetime = lifetime;
        Completion = completion;
        Timeout = timeout;
    }

    /// <summary>Required recovery lifetime, independent of response timing.</summary>
    public ServiceProcessLifetime Lifetime { get; }
    /// <summary>Promised completion boundary.</summary>
    public ServiceProcessCompletion Completion { get; }
    /// <summary>Terminal execution cancellation budget; does not forcibly preempt an uncooperative host.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TimeSpan? Timeout { get; }
}
