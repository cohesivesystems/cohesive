namespace Cohesive.Transitions.Authoring;

/// <summary>
/// Typed effect handler that executes a request and returns a response.
/// </summary>
/// <remarks>
/// Compatibility handler retained for the flat Transition effect surface through ARI-218. New execution adapters
/// should interpret exact canonical interaction contracts instead of dispatching by request-name strings.
/// </remarks>
public interface IEffectHandler<in TRequest, TResult>
    where TRequest : IEffectRequest<TResult>
{
    /// <summary>
    /// Executes the effect request.
    /// </summary>
    Task<TResult> HandleAsync(OperationContext context, TRequest request);
}
