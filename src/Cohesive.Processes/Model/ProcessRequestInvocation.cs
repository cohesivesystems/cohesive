namespace Cohesive.Processes.Model;

/// <summary>
/// Explicit request invocation for execute effect request nodes.
/// </summary>
public sealed record ProcessRequestInvocation(
    EffectRequest Request,
    ProcessEntityRef? ContinuationEntity = null
);