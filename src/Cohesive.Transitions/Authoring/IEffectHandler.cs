namespace Cohesive.Transitions.Authoring;

/// <summary>
/// Typed effect handler that executes a request and returns a response.
/// </summary>
public interface IEffectHandler<in TRequest, TResult>
    where TRequest : IEffectRequest<TResult>
{
    /// <summary>
    /// Executes the effect request.
    /// </summary>
    Task<TResult> HandleAsync(OperationContext context, TRequest request);
}
