namespace Cohesive.Configuration;

/// <summary>Evaluates a live flag using invocation-local context and a host-owned provider.</summary>
/// <typeparam name="TContext">Explicit targeting input; adapters may map it to their native context.</typeparam>
/// <remarks>Implementations must support concurrent calls, retain no invocation context, and propagate provider
/// failures. Refresh and last-known-value policy belong to the native provider. A decision is not authorization.</remarks>
public interface IFeatureFlagEvaluator<in TContext>
{
    /// <summary>Captures one flag decision for an invocation.</summary>
    /// <param name="flag">Configured flag identity.</param>
    /// <param name="context">Invocation-local targeting input, never cached by Cohesive.</param>
    /// <param name="cancellationToken">Cancels evaluation before dispatch.</param>
    /// <returns>The provider's current enabled/disabled decision.</returns>
    /// <exception cref="OperationCanceledException">Evaluation was cancelled.</exception>
    ValueTask<bool> IsEnabledAsync(string flag, TContext context, CancellationToken cancellationToken = default);
}
