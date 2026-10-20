using System.Collections.Immutable;
using Cohesive.Api.Execution.Services;
using Cohesive.Storage;

namespace Cohesive.Adapters.Postgres.Services;

/// <summary>DI-ready heterogeneous storage with prepared query evidence and all native source declarations.</summary>
/// <remarks>Offline-only PostgresComposedServicePreparation is not a storage composition. This type is produced
/// by ComposeServices, which supplies the native declarations required for host binding.</remarks>
public sealed class PostgresComposedStorageComposition : IServiceStorageComposition
{
    readonly PostgresPersistenceBindings postgres;
    readonly ImmutableArray<IEntityStorageBinding> sources;
    /// <summary>Stable runtime provenance authority, defaulting to di/ plus the database identity.</summary>
    public string Authority { get; }
    /// <summary>Offline evidence and explicit native binding, shared with infrastructure projections.</summary>
    public PostgresComposedServicePreparation Preparation { get; }
    /// <inheritdoc />
    public ImmutableArray<object> Bindings { get; }

    internal PostgresComposedStorageComposition(PostgresPersistenceBindings postgres,
        PostgresComposedServicePreparation preparation, ImmutableArray<IEntityStorageBinding> sources, string? authority)
    {
        this.postgres = postgres;
        Preparation = preparation;
        this.sources = sources;
        Authority = authority ?? "di/" + postgres.Database.Value;
        ArgumentException.ThrowIfNullOrWhiteSpace(Authority);
        if (preparation.Sources.Keys.Any(id => !sources.Any(source => source.Source.Source.Id == id)))
            throw new ArgumentException("Composition requires a native binding for every prepared source.", nameof(sources));
        Bindings = [postgres, .. sources];
    }

    /// <inheritdoc />
    public async ValueTask<IServicePersistence> BindAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        var registrations = new EntityStorageRegistration[sources.Length];
        for (var index = 0; index < registrations.Length; index++)
            registrations[index] = await sources[index].BindAsync(services, cancellationToken).ConfigureAwait(false);
        var runtime = await postgres.PrepareAsync(services, Authority, cancellationToken).ConfigureAwait(false);
        return Preparation.BindCore(runtime.Persistence, runtime.Receipts, registrations, runtime.History, runtime.Outbox);
    }
}
