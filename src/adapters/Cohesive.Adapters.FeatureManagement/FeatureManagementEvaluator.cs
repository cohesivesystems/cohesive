using Cohesive.Configuration;
using Microsoft.FeatureManagement;

namespace Cohesive.Adapters.FeatureManagement;

/// <summary>Live contextual evaluation through Microsoft's host-owned feature manager, including Azure App Configuration.</summary>
/// <typeparam name="TContext">Invocation context supplied to native contextual filters.</typeparam>
/// <remarks>Register the manager rather than its snapshot service. Configure refresh on the native configuration provider.
/// Missing flags follow the native manager's disabled policy. Exceptions propagate without selecting a fallback.</remarks>
public sealed class FeatureManagementEvaluator<TContext> : IFeatureFlagEvaluator<TContext>
{
    readonly IVariantFeatureManager manager;
    /// <summary>Attaches a host-owned manager. Cohesive neither creates nor disposes native resources.</summary>
    /// <param name="manager">Concurrent live manager registered by AddFeatureManagement.</param>
    public FeatureManagementEvaluator(IVariantFeatureManager manager) => this.manager = manager ?? throw new ArgumentNullException(nameof(manager));
    /// <inheritdoc />
    public async ValueTask<bool> IsEnabledAsync(string flag, TContext context, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(flag);
        cancellationToken.ThrowIfCancellationRequested();
        return await manager.IsEnabledAsync(flag, context, cancellationToken).ConfigureAwait(false);
    }
}
