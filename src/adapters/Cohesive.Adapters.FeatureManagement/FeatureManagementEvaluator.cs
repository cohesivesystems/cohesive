using Cohesive.Configuration;
using Microsoft.FeatureManagement;

namespace Cohesive.Adapters.FeatureManagement;

/// <summary>Live contextual evaluation through Microsoft's host-owned feature manager, including Azure App Configuration.</summary>
/// <typeparam name="TContext">Invocation context supplied to native contextual filters.</typeparam>
/// <remarks>Register the manager rather than its snapshot service. Configure refresh on the native configuration provider.
/// Missing flags fail during admission and evaluation. Exceptions propagate without selecting a fallback.</remarks>
public sealed class FeatureManagementEvaluator<TContext> : IFeatureFlagEvaluator<TContext>
{
    readonly IVariantFeatureManager manager;
    readonly IFeatureDefinitionProvider definitions;
    /// <summary>Attaches a host-owned manager. Cohesive neither creates nor disposes native resources.</summary>
    /// <param name="manager">Concurrent live manager registered by AddFeatureManagement.</param>
    /// <param name="definitions">The same host-owned definition provider used by the manager.</param>
    public FeatureManagementEvaluator(IVariantFeatureManager manager, IFeatureDefinitionProvider definitions)
    {
        this.manager = manager ?? throw new ArgumentNullException(nameof(manager));
        this.definitions = definitions ?? throw new ArgumentNullException(nameof(definitions));
    }
    /// <inheritdoc />
    public async ValueTask RequireFlagAsync(string flag, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(flag);
        cancellationToken.ThrowIfCancellationRequested();
        if (await definitions.GetFeatureDefinitionAsync(flag).WaitAsync(cancellationToken).ConfigureAwait(false) is null)
            throw new FeatureFlagNotFoundException(flag);
    }
    /// <inheritdoc />
    public async ValueTask<bool> IsEnabledAsync(string flag, TContext context, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(flag);
        cancellationToken.ThrowIfCancellationRequested();
        await RequireFlagAsync(flag, cancellationToken).ConfigureAwait(false);
        return await manager.IsEnabledAsync(flag, context, cancellationToken).ConfigureAwait(false);
    }
}
