using Cohesive.Configuration;
using Cohesive.Relations.Execution;
using Microsoft.Extensions.DependencyInjection;

namespace Cohesive.Api.Execution.Services;

/// <summary>Closed authoring choice between a native reader, an alternate, or a live flag.</summary>
/// <typeparam name="TInput">Query input, independent of the provider's targeting type.</typeparam>
public abstract class ServiceReadSelection<TInput>
{
    private protected ServiceReadSelection() { }
    /// <summary>Uses only the native reader; alternate declarations and native preparation are not evaluated.</summary>
    public static ServiceReadSelection<TInput> Native { get; } = new FixedChoice();
    /// <summary>Uses only the alternate reader.</summary>
    public static ServiceReadSelection<TInput> Alternate { get; } = new FixedChoice();

    /// <summary>Declares a flag and an invocation-local projection into an independent targeting contract.</summary>
    /// <typeparam name="TContext">Provider targeting type.</typeparam>
    /// <param name="flag">Provider key; true selects the alternate.</param>
    /// <param name="targeting">Prepares a concurrent projection once using host-owned services. The returned
    /// function runs per invocation, so it may read trusted request identity through a captured accessor.</param>
    /// <returns>A flag choice whose provider and existence are checked before native reader preparation.</returns>
    /// <exception cref="ArgumentException">The flag is empty.</exception>
    public static ServiceReadSelection<TInput> ByFeatureFlag<TContext>(string flag,
        Func<IServiceProvider, Func<TInput, TContext>> targeting)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(flag); ArgumentNullException.ThrowIfNull(targeting);
        return new FlagChoice<TContext>(flag, targeting);
    }

    /// <summary>Lowers validated configuration to a fixed or flag choice without application branching.</summary>
    /// <typeparam name="TContext">Provider targeting type.</typeparam>
    /// <param name="policy">Fixed source and optional flag; materialized reads require explicit consent.</param>
    /// <param name="targeting">Target projection factory, used only for a flag choice.</param>
    /// <returns>Native, alternate or contextual flag selection.</returns>
    /// <exception cref="ArgumentException">The policy is invalid.</exception>
    public static ServiceReadSelection<TInput> FromPolicy<TContext>(RelationQueryReadPolicy policy,
        Func<IServiceProvider, Func<TInput, TContext>> targeting)
    {
        ArgumentNullException.ThrowIfNull(policy); ArgumentNullException.ThrowIfNull(targeting); policy.Validate();
        return policy.FeatureFlag is { } flag ? ByFeatureFlag(flag, targeting)
            : policy.Source == RelationQueryReadSource.Live ? Native : Alternate;
    }

    internal virtual string? Flag => null;
    internal bool IsNative => ReferenceEquals(this, Native);
    internal virtual ValueTask<Func<TInput, CancellationToken, ValueTask<bool>>> PrepareAsync(IServiceProvider services, CancellationToken ct) =>
        throw new InvalidOperationException("Only flag choices prepare an evaluator.");

    sealed class FixedChoice : ServiceReadSelection<TInput>;
    sealed class FlagChoice<TContext>(string flag, Func<IServiceProvider, Func<TInput, TContext>> targeting) : ServiceReadSelection<TInput>
    {
        internal override string Flag => flag;
        internal override async ValueTask<Func<TInput, CancellationToken, ValueTask<bool>>> PrepareAsync(IServiceProvider services, CancellationToken ct)
        {
            var evaluator = services.GetRequiredService<IFeatureFlagEvaluator<TContext>>();
            await evaluator.RequireFlagAsync(flag, ct).ConfigureAwait(false);
            var project = targeting(services) ?? throw new InvalidOperationException("Targeting factory returned no projection.");
            return (input, token) => evaluator.IsEnabledAsync(flag, project(input), token);
        }
    }
}
