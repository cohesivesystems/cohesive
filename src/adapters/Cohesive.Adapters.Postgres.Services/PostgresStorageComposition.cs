using System.Collections.Immutable;
using Cohesive.Adapters.Postgres.Infra;
using Cohesive.Api.Execution.Services;
using Cohesive.Infra;
using Cohesive.Infra.Realization;
using Cohesive.Storage;

namespace Cohesive.Adapters.Postgres.Services;

/// <summary>One native database's entity placement and model-owned persistence policy, independent of API modules.</summary>
public sealed class PostgresStorageComposition : IServiceStorageComposition
{
    readonly PostgresPersistenceBindings bindings;
    /// <inheritdoc />
    public ImmutableArray<Cohesive.Model.ValueContract> Observations { get; }
    /// <inheritdoc />
    public ImmutableArray<Cohesive.Model.QualifiedShapeId> Shapes { get; }
    /// <summary>Stable runtime provenance authority, defaulting to di/ plus the database identity.</summary>
    public string Authority { get; }
    /// <summary>Declares native placement without selecting any API queries or opening a connection.</summary>
    /// <param name="bindings">Declared mappings, receipt, history and outbox policy.</param>
    /// <param name="authority">Native runtime authority; defaults to di/ followed by the declared database identity.</param>
    public PostgresStorageComposition(PostgresPersistenceBindings bindings, string? authority = null)
    {
        this.bindings = bindings ?? throw new ArgumentNullException(nameof(bindings));
        Observations = bindings.Observations;
        Shapes = bindings.Shapes;
        Authority = authority ?? "di/" + bindings.Database.Value;
        ArgumentException.ThrowIfNullOrWhiteSpace(Authority);
    }
    /// <summary>Native evidence shared with infrastructure association.</summary>
    /// <param name="queries">Selections routed to this database.</param><returns>Offline native evidence.</returns>
    public PostgresPreparedQueryEvidence PrepareEvidence(IEnumerable<ServiceQuerySelection> queries) =>
        bindings.PrepareService(queries, new InfrastructureTargetFacilityId("postgres/" + bindings.Database.Value + "/implementation"));
    /// <inheritdoc />
    public IServiceStorageComposition PrepareQueries(ImmutableArray<ServiceQuerySelection> queries)
    {
        if (!queries.IsEmpty) _ = PrepareEvidence(queries);
        return this;
    }
    /// <inheritdoc />
    public ImmutableArray<object> Bindings => [bindings];
    /// <inheritdoc />
    public async ValueTask<IServicePersistence> BindAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        var prepared = await bindings.PrepareAsync(services, Authority, cancellationToken).ConfigureAwait(false);
        return prepared.ForServices();
    }
}
