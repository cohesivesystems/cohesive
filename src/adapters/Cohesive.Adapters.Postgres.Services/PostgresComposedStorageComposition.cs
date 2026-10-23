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
    /// <inheritdoc />
    public ImmutableArray<Cohesive.Model.ValueContract> Observations { get; }
    /// <inheritdoc />
    public ImmutableArray<Cohesive.Model.QualifiedShapeId> Shapes { get; }
    /// <summary>Stable runtime provenance authority, defaulting to di/ plus the database identity.</summary>
    public string Authority { get; }
    readonly Func<int, long, PostgresRelationQueryComposedPolicy> policy;
    /// <inheritdoc />
    public IServiceStorageComposition PrepareQueries(ImmutableArray<ServiceQuerySelection> queries) =>
        queries.IsEmpty ? this : new SelectedComposition(this, PrepareEvidence(queries));
    /// <summary>Derives offline evidence for explicit query selections without modifying this declaration.</summary>
    /// <param name="queries">Queries assigned by complete source placement.</param><returns>Prepared composed query evidence.</returns>
    public PostgresComposedServicePreparation PrepareEvidence(ImmutableArray<ServiceQuerySelection> queries) =>
        PostgresComposedServiceExtensions.PrepareCore(postgres, queries, new(sources.Select(source => source.Source)), policy);
    /// <inheritdoc />
    public ImmutableArray<object> Bindings { get; }

    internal PostgresComposedStorageComposition(PostgresPersistenceBindings postgres,
        ImmutableArray<IEntityStorageBinding> sources, Func<int, long, PostgresRelationQueryComposedPolicy> policy, string? authority)
    {
        ArgumentNullException.ThrowIfNull(postgres); ArgumentNullException.ThrowIfNull(policy);
        this.postgres = postgres;
        this.policy = policy;
        this.sources = sources;
        Observations = [.. postgres.Observations, .. sources.Select(source => source.Observation)];
        Shapes = [.. postgres.Shapes, .. sources.Select(source => source.Source.Shape)];
        Authority = authority ?? "di/" + postgres.Database.Value;
        ArgumentException.ThrowIfNullOrWhiteSpace(Authority);
        Bindings = [postgres, .. sources];
    }

    /// <inheritdoc />
    public ValueTask<IServicePersistence> BindAsync(IServiceProvider services, CancellationToken cancellationToken = default) =>
        BindCore(services, null, cancellationToken);

    async ValueTask<IServicePersistence> BindCore(IServiceProvider services, PostgresComposedServicePreparation? preparation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        var registrations = new EntityStorageRegistration[sources.Length];
        for (var index = 0; index < registrations.Length; index++)
            registrations[index] = await sources[index].BindAsync(services, cancellationToken).ConfigureAwait(false);
        var runtime = await postgres.PrepareAsync(services, Authority, cancellationToken).ConfigureAwait(false);
        if (preparation is not null) return preparation.BindCore(runtime.Persistence, runtime.Receipts, registrations, runtime.History, runtime.Outbox);
        var builder = new ServicePersistenceBuilder(runtime.ForServices());
        foreach (var registration in registrations) builder.Repository(registration.Repository);
        return builder.Build();
    }

    sealed class SelectedComposition(PostgresComposedStorageComposition owner, PostgresComposedServicePreparation preparation) : IServiceStorageComposition
    {
        public ImmutableArray<Cohesive.Model.ValueContract> Observations => owner.Observations;
        public ImmutableArray<Cohesive.Model.QualifiedShapeId> Shapes => owner.Shapes;
        public ImmutableArray<object> Bindings => owner.Bindings;
        public IServiceStorageComposition PrepareQueries(ImmutableArray<ServiceQuerySelection> queries) => owner.PrepareQueries(queries);
        public ValueTask<IServicePersistence> BindAsync(IServiceProvider services, CancellationToken cancellationToken = default) =>
            owner.BindCore(services, preparation, cancellationToken);
    }
}
