using Cohesive.Execution;

namespace Cohesive.Processes.Execution;

/// <summary>Projects one canonical child-bearing Request into exact Process-start admission evidence.</summary>
/// <remarks>
/// The Request and its <see cref="RequestEnvelope.ChildTarget"/> remain semantic authority. Physical runtimes
/// supply their attributable authorization evidence while sharing the same command, idempotency, definition,
/// continuation, input, and provenance projection.
/// </remarks>
public static class ProcessChildStartProjection
{
    /// <summary>Creates exact child Process-start evidence from one canonical Request.</summary>
    /// <param name="request">Canonical parent Request carrying the exact child target and input.</param>
    /// <param name="target">Exact child target retained by <paramref name="request"/>.</param>
    /// <param name="authorization">Physical interpreter authorization attributable to this child start.</param>
    /// <param name="acceptedAtUtc">Explicit UTC child-start admission time.</param>
    /// <returns>A start receipt pinned to the child definition and interpreter-derived continuation.</returns>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// The target differs from the Request target, authorization crosses authority scope, or the time is not UTC.
    /// </exception>
    public static ProcessStartReceipt Create(
        RequestEnvelope request,
        ProcessChildRequestTarget target,
        ProcessControlAuthorizationContext authorization,
        DateTimeOffset acceptedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(authorization);
        if (request.ChildTarget != target)
        {
            throw new ArgumentException(
                "Child start projection requires the exact target retained by the Request.",
                nameof(target));
        }
        if (authorization.AuthorityScope != request.Context.AuthorityScope)
        {
            throw new ArgumentException(
                "Child start authorization must retain the Request authority scope.",
                nameof(authorization));
        }
        if (acceptedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Child start admission time must use the UTC offset.", nameof(acceptedAtUtc));
        }

        var start = new ProcessStartRequest(
            ProcessStartRequest.CurrentSchemaVersion,
            target.Definition,
            new(
                CommandId(request),
                IdempotencyKey(request),
                target.Continuation.ProcessInstanceId,
                authorization,
                acceptedAtUtc,
                request.Context.Provenance),
            target.Continuation,
            request.Payload);
        return new(start, acceptedAtUtc);
    }

    /// <summary>Determines whether retained start evidence is the exact projection of one child Request.</summary>
    /// <param name="retained">Previously admitted child start.</param>
    /// <param name="request">Canonical parent Request.</param>
    /// <param name="target">Exact child target retained by <paramref name="request"/>.</param>
    /// <param name="authorization">Physical interpreter authorization used for the projection.</param>
    /// <returns><see langword="true"/> only for complete exact start evidence.</returns>
    public static bool Matches(
        ProcessStartReceipt retained,
        RequestEnvelope request,
        ProcessChildRequestTarget target,
        ProcessControlAuthorizationContext authorization)
    {
        ArgumentNullException.ThrowIfNull(retained);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(authorization);
        return request.ChildTarget == target
            && retained.Request.Definition == target.Definition
            && retained.Request.InitialContinuation == target.Continuation
            && retained.Request.Context.CommandId == CommandId(request)
            && retained.Request.Context.IdempotencyKey == IdempotencyKey(request)
            && retained.Request.Context.ProcessInstanceId == target.Continuation.ProcessInstanceId
            && retained.Request.Context.Authorization == authorization
            && retained.Request.Context.Provenance == request.Context.Provenance
            && retained.Request.Input == request.Payload;
    }

    static ProcessControlCommandId CommandId(RequestEnvelope request) =>
        new($"process-child-start/{request.Context.EmissionId.Value}");

    static ProcessControlIdempotencyKey IdempotencyKey(RequestEnvelope request) =>
        new($"process-child-start/{request.Context.IdempotencyKey.Value}");
}
