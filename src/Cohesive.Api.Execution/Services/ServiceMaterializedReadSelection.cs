using Cohesive.Relations.Authoring;
using Cohesive.Relations.Execution;
using Cohesive.Storage.Materialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Cohesive.Api.Execution.Services;

/// <summary>Declarative serving selection composed from independent materialization source and target adapters.</summary>
public static class ServiceMaterializedReadSelection
{
    /// <summary>Required configuration key within the declared comparison section: a trusted local comparison artifact path.</summary>
    public const string ComparisonFileKey = "ComparisonFile";

    /// <summary>Declares fixed or flag-based materialized serving, reusing the native reader for live choice.</summary>
    /// <typeparam name="TResult">Rooted projection.</typeparam>
    /// <param name="storage">Canonical source and native reader.</param><param name="query">Canonical query.</param>
    /// <param name="relation">Shared rooted relation.</param>
    /// <param name="source">Declares the materialization source only if the selection can use it; no native IO here.</param>
    /// <param name="target">Materialized serving target.</param><param name="comparisonSection">Trusted comparison artifact section.</param>
    /// <param name="selection">Fixed native/alternate or contextual flag choice; defaults to alternate.</param>
    /// <returns>Immutable composition whose native binding admits the flag and both readers before traffic.</returns>
    public static IServiceStorageComposition SelectMaterializedQuery<TResult>(this IServiceStorageComposition storage,
        RelationQuery<string, TResult?> query, RootedRelation<TResult> relation,
        Func<IMaterializationReadSourceBinding> source, IMaterializationReadTargetBinding target, string comparisonSection,
        ServiceReadSelection<string>? selection = null) where TResult : class
    {
        ArgumentNullException.ThrowIfNull(storage); ArgumentNullException.ThrowIfNull(query); ArgumentNullException.ThrowIfNull(relation);
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(comparisonSection);
        selection ??= ServiceReadSelection<string>.Alternate;
        if (selection.IsNative) return storage;
        var sourceBinding = source() ?? throw new InvalidOperationException("Materialization source declaration returned null.");
        var materialization = sourceBinding.Materialization;
        var targetId = target.Id;
        var binding = new ServiceQueryReadBinding(target.Backend, targetId.Value, RelationQueryConsistency.Eventual);
        return storage.SelectQuery(query, binding, Prepare, selection);

        async ValueTask<Func<int, long, IRelationQueryReader<string, TResult?>>> Prepare(IServiceProvider services, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var section = services.GetRequiredService<IConfiguration>().GetSection(comparisonSection);
            var path = section[ComparisonFileKey]
                ?? throw new InvalidOperationException($"{section.Path}:{ComparisonFileKey} is required for materialized reads.");
            var comparison = await MaterializationReadSelection.LoadAsync(path, materialization, ct).ConfigureAwait(false);
            var bindSource = sourceBinding.Prepare(services);
            var nativeTarget = await target.BindAsync(services, materialization, ct).ConfigureAwait(false);
            if (nativeTarget.Descriptor.Id != targetId)
                throw new InvalidOperationException("Native target differs from the declared serving resource.");
            var runtime = await bindSource(nativeTarget, ct).ConfigureAwait(false);
            return await target.PrepareReadersAsync(query, relation, runtime, comparison, ct).ConfigureAwait(false);
        }
    }
}
