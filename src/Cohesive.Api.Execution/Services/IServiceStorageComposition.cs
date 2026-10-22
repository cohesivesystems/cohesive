using Cohesive.Storage;

namespace Cohesive.Api.Execution.Services;

/// <summary>Offline storage composition that can prepare service persistence from host-owned native resources.</summary>
public interface IServiceStorageComposition : IStorageComposition
{
    /// <summary>Exact entity observation contracts supplied by this composition.</summary>
    System.Collections.Immutable.ImmutableArray<Cohesive.Model.ValueContract> Observations { get; }
    /// <summary>Exact query requests selected and prepared by this composition.</summary>
    System.Collections.Immutable.ImmutableArray<Cohesive.Relations.Compilation.RelationQueryCompilationRequest> Queries { get; }

    /// <summary>Validates native metadata and prepares persistence without allocating or disposing clients.</summary>
    /// <param name="services">Native resources registered under their existing infrastructure connection identities.</param>
    /// <param name="cancellationToken">Cancels preparation; no traffic may be admitted until it completes.</param>
    /// <returns>Shared repositories and query readers for one runtime lifetime.</returns>
    ValueTask<IServicePersistence> BindAsync(IServiceProvider services, CancellationToken cancellationToken = default);
}
