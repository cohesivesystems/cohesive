using System.Collections.Immutable;
using Cohesive.Adapters.Postgres.Infra;
using Cohesive.Api.Execution.Services;
using Cohesive.Infra;
using Cohesive.Infra.Realization;
using Cohesive.Storage;

namespace Cohesive.Adapters.Postgres.Services;

/// <summary>One native database's endpoint preparation and model-owned persistence policy.</summary>
public sealed class PostgresStorageComposition : IServiceStorageComposition
{
    readonly PostgresPersistenceBindings bindings;
    /// <inheritdoc />
    public ImmutableArray<Cohesive.Model.ValueContract> Observations { get; }
    /// <inheritdoc />
    public ImmutableArray<Cohesive.Relations.Compilation.RelationQueryCompilationRequest> Queries { get; }
    /// <summary>Stable runtime provenance authority, defaulting to di/ plus the database identity.</summary>
    public string Authority { get; }
    /// <summary>Prepares native evidence from exact endpoint selections without opening a connection.</summary>
    /// <param name="bindings">Declared mappings, receipt, history and outbox policy.</param>
    /// <param name="plan">Canonical endpoint query requirements.</param>
    /// <param name="authority">Native runtime authority; defaults to di/ followed by the declared database identity.</param>
    public PostgresStorageComposition(PostgresPersistenceBindings bindings, ServiceImplementationPlan plan, string? authority = null)
    {
        this.bindings = bindings ?? throw new ArgumentNullException(nameof(bindings));
        Observations = bindings.Observations;
        Queries = [.. plan.QueryDependencies.Queries.Select(query => query.Request)];
        Authority = authority ?? "di/" + bindings.Database.Value;
        ArgumentException.ThrowIfNullOrWhiteSpace(Authority);
        Evidence = bindings.PrepareService(plan, new InfrastructureTargetFacilityId("postgres/" + bindings.Database.Value + "/implementation"));
    }
    /// <summary>Native evidence shared with infrastructure association.</summary>
    public PostgresPreparedQueryEvidence Evidence { get; }
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
