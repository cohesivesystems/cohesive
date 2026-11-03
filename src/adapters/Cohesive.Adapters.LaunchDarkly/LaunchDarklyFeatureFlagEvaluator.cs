using Cohesive.Configuration;
using LaunchDarkly.Sdk;
using LaunchDarkly.Sdk.Server.Interfaces;

namespace Cohesive.Adapters.LaunchDarkly;

/// <summary>Uses a shared native LaunchDarkly client and explicit invocation-to-targeting projection.</summary>
/// <typeparam name="TContext">Invocation context from which the host selects trusted targeting attributes.</typeparam>
/// <remarks>The host owns client lifetime and refresh policy. SDK evaluation errors fail the invocation rather than
/// silently using the SDK's fallback value. Native last-known cached values retain the SDK's freshness policy.</remarks>
public sealed class LaunchDarklyFeatureFlagEvaluator<TContext> : IFeatureFlagEvaluator<TContext>
{
    readonly ILdClient client;
    readonly Func<TContext, Context> targeting;
    /// <summary>Attaches the native singleton and a concurrent, invocation-local targeting projection.</summary>
    /// <param name="client">Host-owned LaunchDarkly client; never disposed by this adapter.</param>
    /// <param name="targeting">Projects only explicitly permitted attributes; no reflection or ambient tenant inference.</param>
    public LaunchDarklyFeatureFlagEvaluator(ILdClient client, Func<TContext, Context> targeting)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.targeting = targeting ?? throw new ArgumentNullException(nameof(targeting));
    }
    /// <inheritdoc />
    public ValueTask<bool> IsEnabledAsync(string flag, TContext context, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(flag);
        cancellationToken.ThrowIfCancellationRequested();
        var decision = client.BoolVariationDetail(flag, targeting(context), false);
        if (decision.Reason.Kind == EvaluationReasonKind.Error)
            throw new InvalidOperationException($"Feature flag '{flag}' evaluation failed: {decision.Reason.ErrorKind}.");
        return ValueTask.FromResult(decision.Value);
    }
}
