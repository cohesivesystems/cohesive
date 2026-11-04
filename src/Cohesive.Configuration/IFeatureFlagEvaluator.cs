namespace Cohesive.Configuration;

/// <summary>Evaluates a live flag using invocation-local context and a host-owned provider.</summary>
/// <typeparam name="TContext">Explicit targeting input; adapters may map it to their native context.</typeparam>
/// <remarks>Implementations must support concurrent calls, retain no invocation context, and propagate provider
/// failures. Refresh and last-known-value policy belong to the native provider. A decision is not authorization.</remarks>
public interface IFeatureFlagEvaluator<in TContext>
{
    /// <summary>Requires the named flag to exist before dependent native resources are prepared.</summary>
    /// <param name="flag">Flag identity to admit; no request targeting context is required.</param>
    /// <param name="cancellationToken">Cancels admission.</param>
    /// <returns>Completion when the provider has usable configuration containing this flag.</returns>
    /// <exception cref="FeatureFlagNotFoundException">The provider does not contain the flag.</exception>
    /// <exception cref="OperationCanceledException">Admission was cancelled.</exception>
    ValueTask RequireFlagAsync(string flag, CancellationToken cancellationToken = default);

    /// <summary>Captures one flag decision for an invocation.</summary>
    /// <param name="flag">Configured flag identity.</param>
    /// <param name="context">Invocation-local targeting input, never cached by Cohesive.</param>
    /// <param name="cancellationToken">Cancels evaluation before dispatch.</param>
    /// <returns>The provider's current enabled/disabled decision.</returns>
    /// <exception cref="FeatureFlagNotFoundException">The flag is absent from the provider.</exception>
    /// <exception cref="OperationCanceledException">Evaluation was cancelled.</exception>
    ValueTask<bool> IsEnabledAsync(string flag, TContext context, CancellationToken cancellationToken = default);
}
