using Cohesive.Relations.Authoring;
using Cohesive.Relations.Execution;

namespace Cohesive.Storage.Materialization;

/// <summary>Offline source declaration that can admit native materialization ports for serving reads.</summary>
public interface IMaterializationReadSourceBinding
{
    /// <summary>Canonical materialization and impact preparation; available without native resources.</summary>
    PreparedMaterialization Materialization { get; }
    /// <summary>Validates runtime configuration without native IO and prepares exact source admission against a borrowed target.</summary>
    /// <param name="services">Host-owned native clients and configuration.</param>
    /// <returns>Native admission taking the target and cancellation token; it executes neither rebuild nor CDC.</returns>
    Func<IMaterializationTarget, CancellationToken, Task<BoundMaterialization>> Prepare(IServiceProvider services);
}

/// <summary>Provider read interpretation with an explicit target identity and host-owned native admission.</summary>
public interface IMaterializationReadTargetBinding
{
    /// <summary>Provider display label, not a capability attestation.</summary>
    string Backend { get; }
    /// <summary>Existing target identity, also used by its native registration.</summary>
    MaterializationTargetId Id { get; }
    /// <summary>Admits native target metadata; must not create or promote generations.</summary>
    /// <param name="services">Host-owned native resources.</param><param name="materialization">Exact source declaration.</param>
    /// <param name="cancellationToken">Startup cancellation.</param><returns>Borrowed target matching Id.</returns>
    Task<IMaterializationTarget> BindAsync(IServiceProvider services, PreparedMaterialization materialization, CancellationToken cancellationToken = default);
    /// <summary>Admits query/rebuild affinity and returns bounded eventual readers without fallback.</summary>
    /// <typeparam name="TResult">Rooted result projection.</typeparam>
    /// <param name="query">Selected keyed read.</param><param name="relation">Shared rooted relation.</param>
    /// <param name="runtime">Exact admitted native source and target plan.</param><param name="comparison">Validated trusted operator evidence.</param>
    /// <param name="cancellationToken">Startup cancellation.</param><returns>Factory accepting row and response-byte bounds.</returns>
    Task<Func<int, long, IRelationQueryReader<string, TResult?>>> PrepareReadersAsync<TResult>(
        RelationQuery<string, TResult?> query, RootedRelation<TResult> relation, BoundMaterialization runtime,
        MaterializationShadowComparison comparison, CancellationToken cancellationToken = default) where TResult : class;
}
