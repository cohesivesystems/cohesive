using Cohesive.Api;

namespace Cohesive.Api.Execution;

/// <summary>Transport-neutral SDK boundary for the canonical execution-control API catalog.</summary>
/// <remarks>
/// Implementations bind the canonical endpoint handles to an execution authority. HTTP, CLI, generated clients,
/// and direct SDK callers share this exact dispatch contract rather than introducing transport-specific command
/// DTOs or lifecycle semantics.
/// </remarks>
public interface IExecutionControlApiDispatcher
{
    /// <summary>Canonical semantic endpoint catalog bound by this dispatcher.</summary>
    ExecutionControlApiCatalog Catalog { get; }

    /// <summary>Dispatches one canonical API request through its exact endpoint handle.</summary>
    /// <param name="context">Explicit cancellation, time, identity, and tracing context.</param>
    /// <param name="endpoint">Exact endpoint handle owned by <see cref="Catalog"/>.</param>
    /// <param name="request">Canonical request value declared by <paramref name="endpoint"/>.</param>
    /// <param name="invocation">Trusted server-side authorization, timing, and provenance evidence.</param>
    /// <returns>The exact declared result variant and safe response body.</returns>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Trusted evidence or request structure is invalid.</exception>
    /// <exception cref="InvalidOperationException">
    /// The endpoint is not owned by this dispatcher or an authoritative integration returns incoherent evidence.
    /// </exception>
    /// <exception cref="OperationCanceledException">Dispatch is cancelled.</exception>
    ValueTask<ExecutionApiDispatchResult> DispatchAsync(
        OperationContext context,
        ApiEndpoint endpoint,
        object request,
        ExecutionApiInvocationContext invocation);
}
