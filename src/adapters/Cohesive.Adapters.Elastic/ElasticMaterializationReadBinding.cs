using Cohesive.Relations.Authoring;
using Cohesive.Relations.Execution;
using Cohesive.Storage.Materialization;
using Microsoft.Extensions.DependencyInjection;

namespace Cohesive.Adapters.Elastic;

/// <summary>Declarative Elasticsearch serving target using a keyed, host-owned HTTP client and native admission policy.</summary>
public sealed class ElasticMaterializationReadBinding : IMaterializationReadTargetBinding
{
    readonly Func<HttpClient, PreparedMaterialization, CancellationToken, Task<ElasticMaterializationTarget>> bind;
    /// <summary>Declares the existing target and its native metadata admission, with no IO.</summary>
    /// <param name="id">Existing target identity; its Value keys the host's HttpClient registration.</param>
    /// <param name="bind">Native template/cluster admission. Must retain caller ownership and perform no generation writes.</param>
    public ElasticMaterializationReadBinding(MaterializationTargetId id,
        Func<HttpClient, PreparedMaterialization, CancellationToken, Task<ElasticMaterializationTarget>> bind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id.Value); ArgumentNullException.ThrowIfNull(bind);
        Id = id; this.bind = bind;
    }
    /// <summary>Display label for Elasticsearch serving; target identity and admitted capabilities carry semantics.</summary>
    public const string BackendName = "Elasticsearch";
    /// <inheritdoc />
    public string Backend => BackendName;
    /// <inheritdoc />
    public MaterializationTargetId Id { get; }
    /// <inheritdoc />
    public async Task<IMaterializationTarget> BindAsync(IServiceProvider services, PreparedMaterialization materialization,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services); ArgumentNullException.ThrowIfNull(materialization);
        cancellationToken.ThrowIfCancellationRequested();
        var target = await bind(services.GetRequiredKeyedService<HttpClient>(Id.Value), materialization, cancellationToken).ConfigureAwait(false);
        if (target.Descriptor.Id != Id || target.Descriptor.MaterializationId != materialization.Document.Definition.Id)
            throw new InvalidOperationException("Native target differs from its declared read binding identity.");
        return target;
    }
    /// <inheritdoc />
    public Task<Func<int, long, IRelationQueryReader<string, TResult?>>> PrepareReadersAsync<TResult>(
        RelationQuery<string, TResult?> query, RootedRelation<TResult> relation, BoundMaterialization runtime,
        MaterializationShadowComparison comparison, CancellationToken cancellationToken = default) where TResult : class =>
        ElasticMaterializationReadSelection.PrepareAsync(query, relation, runtime, comparison, cancellationToken);
}
