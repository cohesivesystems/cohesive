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

    /// <summary>Selects an eventual materialized reader; evidence and native admission are deferred until host preparation.</summary>
    /// <typeparam name="TResult">Rooted result projection.</typeparam>
    /// <param name="storage">Canonical repository/source owner and live rollback interpretation.</param>
    /// <param name="query">Selected keyed query.</param><param name="relation">Shared live/materialized root authority.</param>
    /// <param name="source">Materialization source declaration and configured native admission.</param>
    /// <param name="target">Native serving target declaration.</param>
    /// <param name="comparisonSection">IConfiguration section containing ComparisonFile; only loaded when selected.</param>
    /// <returns>Offline composition describing the same target used during runtime binding.</returns>
    /// <remarks>Comparison files are trusted local artifacts. Native clients stay host-owned. No generation promotion,
    /// CDC execution, rebuild, or silent fallback occurs. The existing SelectQuery seam owns bounds and consistency checks.</remarks>
    public static IServiceStorageComposition SelectMaterializedQuery<TResult>(this IServiceStorageComposition storage,
        RelationQuery<string, TResult?> query, RootedRelation<TResult> relation,
        IMaterializationReadSourceBinding source, IMaterializationReadTargetBinding target, string comparisonSection) where TResult : class
        => Select(storage, query, relation, source, target, comparisonSection, flag: null);

    /// <summary>Prepares live and materialized interpretations, then evaluates a live flag once per read.</summary>
    /// <typeparam name="TResult">Rooted result projection.</typeparam>
    /// <param name="storage">Canonical live source.</param><param name="query">Canonical keyed query.</param>
    /// <param name="relation">Shared rooted relation.</param><param name="source">Materialization source declaration.</param>
    /// <param name="target">Materialized serving target.</param><param name="comparisonSection">Comparison artifact configuration section.</param>
    /// <param name="flag">Flag key; true serves materialized reads, false live reads.</param>
    /// <returns>Composition requiring explicit eventual-consistency admission and both native readers at startup.</returns>
    /// <exception cref="ArgumentException">The flag or comparison section is empty.</exception>
    public static IServiceStorageComposition SelectMaterializedQueryByFeatureFlag<TResult>(this IServiceStorageComposition storage,
        RelationQuery<string, TResult?> query, RootedRelation<TResult> relation,
        IMaterializationReadSourceBinding source, IMaterializationReadTargetBinding target, string comparisonSection,
        string flag) where TResult : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(flag);
        return Select(storage, query, relation, source, target, comparisonSection, flag);
    }

    static IServiceStorageComposition Select<TResult>(IServiceStorageComposition storage,
        RelationQuery<string, TResult?> query, RootedRelation<TResult> relation,
        IMaterializationReadSourceBinding source, IMaterializationReadTargetBinding target, string comparisonSection,
        string? flag) where TResult : class
    {
        ArgumentNullException.ThrowIfNull(storage); ArgumentNullException.ThrowIfNull(query); ArgumentNullException.ThrowIfNull(relation);
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(comparisonSection);
        var materialization = source.Materialization;
        var targetId = target.Id;
        var binding = new ServiceQueryReadBinding(target.Backend, targetId.Value, RelationQueryConsistency.Eventual);
        return flag is null ? storage.SelectQuery(query, binding, Prepare)
            : storage.SelectQueryByFeatureFlag(query, flag, binding, Prepare);

        async ValueTask<Func<int, long, IRelationQueryReader<string, TResult?>>> Prepare(IServiceProvider services, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var section = services.GetRequiredService<IConfiguration>().GetSection(comparisonSection);
            var path = section[ComparisonFileKey]
                ?? throw new InvalidOperationException($"{section.Path}:{ComparisonFileKey} is required for materialized reads.");
            var comparison = await MaterializationReadSelection.LoadAsync(path, materialization, ct).ConfigureAwait(false);
            var bindSource = source.Prepare(services);
            var nativeTarget = await target.BindAsync(services, materialization, ct).ConfigureAwait(false);
            if (nativeTarget.Descriptor.Id != targetId)
                throw new InvalidOperationException("Native target differs from the declared serving resource.");
            var runtime = await bindSource(nativeTarget, ct).ConfigureAwait(false);
            return await target.PrepareReadersAsync(query, relation, runtime, comparison, ct).ConfigureAwait(false);
        }
    }
}
