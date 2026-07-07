namespace Cohesive.Processes.Runtime;

/// <summary>
/// Scheduled or deferred effect request.
/// </summary>
public sealed record ProcessPendingEffect(
    EffectRequest Request,
    ProcessEntityRef? ContinuationEntity
);